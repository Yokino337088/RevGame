// ============================================================
// EndlessArena.Guard.cs —— 盾牌环绕、防御区、护盾格挡、刺客突刺的画面（新手先读这段）
//
// 四种效果都按“一种效果一张批量网格/面片、整场各 1 次绘制”来画，不为每枚棋子建 GameObject：
//   shieldOrbitRenderer  盾战身边三面公转盾牌（RevGame/Endless/ShieldOrbit，盾牌剪影 Kenney CC0）；
//   shieldZoneRenderer   覆盖 9×9 棋盘的防御区面片（RevGame/Endless/ShieldZone），81 格遮罩只在形状变化时上传；
//   bubbleRenderer       防御区内每枚己方棋子的淡蓝护盾泡泡（RevGame/Endless/ShieldBubble），格挡时波动；
//   thrustRenderer       刺客突刺尖刺（RevGame/Endless/Thrust，速度残影 Kenney CC0），最多同时 32 道。
// 规则全部在 EndlessService.Guard：这里只读防御区并集、格挡回执、突刺回执来画，少画一下不影响任何伤害。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pieces;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        [Tooltip("盾战环绕盾牌批量网格，由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer shieldOrbitRenderer;
        [Tooltip("盾战防御区面片（覆盖整块 9×9 棋盘），由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer shieldZoneRenderer;
        [Tooltip("防御区内棋子的护盾泡泡批量网格，由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer bubbleRenderer;
        [Tooltip("刺客突刺批量网格，由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer thrustRenderer;

        /// <summary>盾牌/护盾方块上限：盘面 81 格，结构容量。</summary>
        private const int GuardCapacity = 81;
        /// <summary>同时显示的突刺上限（表现预算）：超出时覆盖最旧的一道，伤害不受影响。</summary>
        private const int MaxThrusts = 32;
        /// <summary>一道突刺的播放时长、护盾格挡波动时长（秒，纯表现节奏）。</summary>
        private const float ThrustSeconds = .4f, BlockSeconds = .35f;
        /// <summary>突刺线半宽（格）、护盾泡泡相对棋子的半径、盾牌公转方块半宽（格，1.35 让盾牌离身体约 1 格），纯表现尺寸。</summary>
        private const float ThrustHalfWidth = .52f, BubbleRadius = .62f, OrbitHalf = 1.35f;
        private static readonly int ZoneId = Shader.PropertyToID("_Zone");
        private static readonly Color ThrustTint = new Color(.8f, .56f, 1f, 1f);
        private static readonly Color OrbitTint = new Color(.55f, .75f, 1f, 1f);

        /// <summary>一个已经命中的突刺外观。攻击方向只在收到回执时算一次，每帧只推进播放进度。</summary>
        private struct Thrust
        {
            /// <summary>攻击的画面起点，单位棋格。</summary>
            public Vector2 From;
            /// <summary>攻击的画面终点，单位棋格。</summary>
            public Vector2 To;
            /// <summary>攻击线的垂直偏移，长度等于半宽，单位棋格。</summary>
            public Vector2 Side;
            /// <summary>现实播放时间，单位秒。零表示没有正在播放的突刺。</summary>
            public float StartedAt;
        }
        private readonly Thrust[] _thrusts = new Thrust[MaxThrusts];
        private int _thrustCursor;
        /// <summary>环绕盾牌的运行时批次，回池保留容量。</summary>
        private EndlessEffectBatch _orbitBatch;
        /// <summary>护盾泡泡的运行时批次，回池保留容量。</summary>
        private EndlessEffectBatch _bubbleBatch;
        /// <summary>刺客突刺的运行时批次，保留原环形队列的绘制顺序。</summary>
        private EndlessEffectBatch _thrustBatch;
        private readonly float[] _zoneMask = new float[81];
        private MaterialPropertyBlock _zoneProperties;
        private int _zoneVersion = -1, _blockRead;
        /// <summary>每枚棋子最近一次格挡的时间（unscaled 秒），用于护盾波动。</summary>
        private readonly Dictionary<int, float> _blockAt = new Dictionary<int, float>(16);
        private int _thrustsShown, _blocksShown;
        /// <summary>本帧画出的环绕盾牌方块数，一枚盾战对应一个方块。</summary>
        public int ShieldOrbitCount
        {
            get;
            private set;
        }
        /// <summary>本帧画出的护盾泡泡方块数。</summary>
        public int BubbleCount
        {
            get;
            private set;
        }

        /// <summary>进入无尽：建网格（只建一次，随竞技场复用），对齐格挡回执序号。</summary>
        private void BeginGuard(EndlessService service)
        {
            _orbitBatch ??= BindEffectBatch(shieldOrbitRenderer, GuardCapacity, "无尽盾牌环绕网格");
            _bubbleBatch ??= BindEffectBatch(bubbleRenderer, GuardCapacity, "无尽护盾泡泡网格");
            _thrustBatch ??= BindEffectBatch(thrustRenderer, MaxThrusts, "无尽刺客突刺网格");
            _zoneProperties ??= new MaterialPropertyBlock();
            _zoneVersion = -1;
            _blockRead = service.BlockSerial;
            _blockAt.Clear();
            System.Array.Clear(_thrusts, 0, _thrusts.Length);
            _thrustsShown = _blocksShown = 0;
        }
        /// <summary>将批次挂到现有预制体的网格节点。先核对节点再分配，不改变原对象的材质和排序。</summary>
        private static EndlessEffectBatch BindEffectBatch(MeshRenderer renderer, int capacity, string name)
        {
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            if (filter == null)
            {
                throw new System.InvalidOperationException(name + "缺少预制体网格节点");
            }
            var batch = new EndlessEffectBatch(capacity, name);
            filter.sharedMesh = batch.Mesh;
            return batch;
        }

        /// <summary>每帧入口：读格挡回执，刷新四种画面。</summary>
        private void UpdateGuard(EndlessService service)
        {
            ReadBlocks(service);
            UpdateShieldZone(service);
            UpdateShieldOrbits(service);
            UpdateBubbles(service);
            UpdateThrusts();
        }

        private void ReadBlocks(EndlessService service)
        {
            int latest = service.BlockSerial;
            if (latest < _blockRead)
            {
                _blockRead = latest;
            }
            int first = Mathf.Max(_blockRead + 1, latest - service.BlockReceipts.Length + 1);
            float now = Time.unscaledTime;
            for (int serial = first; serial <= latest; serial++)
            {
                EndlessBlockReceipt block = service.BlockReceipts[serial % service.BlockReceipts.Length];
                if (block.serial == serial)
                {
                    _blockAt[block.id] = now;
                    _blocksShown++;
                }
            }
            if (latest > _blockRead)
            {
                PlayFeelSound(SoundHit);
            }
            _blockRead = latest;
        }

        /// <summary>防御区形状只在版本变化时上传一次 81 格遮罩；整块没有防御区时直接关掉渲染器，不画透明像素。</summary>
        private void UpdateShieldZone(EndlessService service)
        {
            if (_zoneVersion == service.ShieldZoneVersion)
            {
                return;
            }
            _zoneVersion = service.ShieldZoneVersion;
            bool any = false;
            for (int i = 0; i < 81; i++)
            {
                _zoneMask[i] = service.ShieldZone[i] ? 1 : 0;
                any |= service.ShieldZone[i];
            }
            shieldZoneRenderer.enabled = any;
            _zoneProperties.SetFloatArray(ZoneId, _zoneMask);
            shieldZoneRenderer.SetPropertyBlock(_zoneProperties);
        }

        /// <summary>每枚己方盾战写一个活动方块，保留星级和编号相位，原地旋转由 Shader 继续绘制。</summary>
        private void UpdateShieldOrbits(EndlessService service)
        {
            int count = 0;
            for (int i = 0; i < service.Units.Count && count < GuardCapacity; i++)
            {
                EndlessUnit unit = service.Units[i];
                Piece p = unit.Piece;
                if (unit.Stone || p.Side != GameSide.Player || !p.IsAlive || p.Def.Kind != PieceKind.Shield)
                {
                    continue;
                }
                float half = OrbitHalf + (p.Size - 1) * .5f;
                _orbitBatch.WriteSquare(count, VisualCenter(p), half, -.45f, OrbitTint, new Vector2(Mathf.Repeat(p.Id * .618f, 1), (int)p.Def.Star + 2));
                count++;
            }
            ShieldOrbitCount = count;
            _orbitBatch.Submit(count);
        }

        /// <summary>为防御区内的非石子棋子画泡泡。位置随缓动，最近一次格挡的闪光按原节奏衰减。</summary>
        private void UpdateBubbles(EndlessService service)
        {
            float now = Time.unscaledTime;
            int count = 0;
            for (int i = 0; i < service.Units.Count && count < GuardCapacity; i++)
            {
                EndlessUnit unit = service.Units[i];
                Piece p = unit.Piece;
                if (unit.Stone || !p.IsAlive || !service.InShieldZone(p))
                {
                    continue;
                }
                float hit = 0;
                if (_blockAt.TryGetValue(p.Id, out float at))
                {
                    hit = Mathf.Clamp01(1 - (now - at) / BlockSeconds);
                }
                Color color = Color.white;
                color.a = hit;
                _bubbleBatch.WriteSquare(count, VisualCenter(p), BubbleRadius * p.Size, -.55f, color, new Vector2(Mathf.Repeat(p.Id * .37f, 1), 0));
                count++;
            }
            BubbleCount = count;
            _bubbleBatch.Submit(count);
        }

        /// <summary>收到突刺回执时锁定攻击线的宽度方向。队列满后仍覆盖最旧的一道。</summary>
        private void PushThrust(Vector2 from, Vector2 to)
        {
            Vector2 direction = (to - from).normalized;
            _thrusts[_thrustCursor] = new Thrust
            {
                From = from,
                To = to,
                Side = new Vector2(-direction.y, direction.x) * ThrustHalfWidth,
                StartedAt = Time.unscaledTime
            };
            _thrustCursor = (_thrustCursor + 1) % MaxThrusts;
            _thrustsShown++;
        }
        /// <summary>沿环形队列的原有顺序收集正在播的突刺。结束的槽不参加索引范围，UV2.X 仍写播放进度。</summary>
        private void UpdateThrusts()
        {
            float now = Time.unscaledTime;
            int count = 0;
            for (int i = 0; i < MaxThrusts; i++)
            {
                Thrust t = _thrusts[i];
                float progress = (now - t.StartedAt) / ThrustSeconds;
                if (t.StartedAt <= 0 || progress >= 1)
                {
                    continue;
                }
                // z=-0.7：在棋子（-0.5）前面，突刺从刺客身上扎出去。
                _thrustBatch.WriteQuad(count++,
                    new Vector3(t.From.x - t.Side.x, t.From.y - t.Side.y, -.7f),
                    new Vector3(t.To.x - t.Side.x, t.To.y - t.Side.y, -.7f),
                    new Vector3(t.To.x + t.Side.x, t.To.y + t.Side.y, -.7f),
                    new Vector3(t.From.x + t.Side.x, t.From.y + t.Side.y, -.7f),
                    ThrustTint, new Vector2(progress, 0));
            }
            _thrustBatch.Submit(count);
        }

        /// <summary>
        /// 剑气/盾牌/护盾跟着棋子“画面上的位置”走：棋子移动时 PieceView 会平滑滑过去，
        /// 如果特效直接用逻辑格坐标，会比棋子先跳到新格子、看起来脱节。视图还没加载出来时退回逻辑坐标。
        /// </summary>
        private Vector2 VisualCenter(Piece p)
        {
            if (_pieces.TryGetValue(p.Id, out PieceView view) && view != null)
            {
                Vector3 position = view.transform.position;
                return new Vector2(position.x, position.y);
            }
            return EndlessService.Position(p);
        }
        /// <summary>验收用的只读计数。累计触发与当前实际索引范围分开，可以核对特效播完后的撤场。</summary>
        public object GuardSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["shieldOrbits"] = ShieldOrbitCount, ["bubbles"] = BubbleCount, ["thrusts"] = _thrustsShown, ["blocks"] = _blocksShown,
                ["zoneVisible"] = shieldZoneRenderer != null && shieldZoneRenderer.enabled,
                ["orbitIndices"] = _orbitBatch == null ? 0 : _orbitBatch.Mesh.GetIndexCount(0),
                ["bubbleIndices"] = _bubbleBatch == null ? 0 : _bubbleBatch.Mesh.GetIndexCount(0),
                ["thrustIndices"] = _thrustBatch == null ? 0 : _thrustBatch.Mesh.GetIndexCount(0),
                ["auraIndices"] = _auraBatch == null ? 0 : _auraBatch.Mesh.GetIndexCount(0)
            };
        }

        /// <summary>回池立即撤掉本局三个绘制范围与预警，保留网格容量给下一局。</summary>
        private void ReleaseGuard()
        {
            _blockAt.Clear();
            System.Array.Clear(_thrusts, 0, _thrusts.Length);
            _orbitBatch?.Submit(0);
            _bubbleBatch?.Submit(0);
            _thrustBatch?.Submit(0);
            ShieldOrbitCount = BubbleCount = 0;
            _zoneVersion = -1;
            if (shieldZoneRenderer != null)
            {
                shieldZoneRenderer.enabled = false;
            }
        }
        /// <summary>竞技场销毁后释放三张运行时网格；允许初始化中途失败，只释放实际持有的部分。</summary>
        private void DestroyGuardMeshes()
        {
            Destroy(_orbitBatch?.Mesh);
            Destroy(_bubbleBatch?.Mesh);
            Destroy(_thrustBatch?.Mesh);
        }
    }
}
