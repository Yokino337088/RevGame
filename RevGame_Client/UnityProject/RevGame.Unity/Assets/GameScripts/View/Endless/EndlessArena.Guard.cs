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

        private struct Thrust
        {
            public Vector2 From, To;
            public float StartedAt;
        }
        private readonly Thrust[] _thrusts = new Thrust[MaxThrusts];
        private int _thrustCursor;
        private Mesh _orbitMesh, _bubbleMesh, _thrustMesh;
        private Vector3[] _orbitVertices, _bubbleVertices, _thrustVertices;
        private Vector2[] _orbitUv, _bubbleUv, _thrustUv, _orbitData, _bubbleData, _thrustData;
        private Color[] _orbitColors, _bubbleColors, _thrustColors;
        private int _orbitLast = GuardCapacity, _bubbleLast = GuardCapacity;
        private readonly float[] _zoneMask = new float[81];
        private MaterialPropertyBlock _zoneProperties;
        private int _zoneVersion = -1, _blockRead;
        /// <summary>每枚棋子最近一次格挡的时间（unscaled 秒），用于护盾波动。</summary>
        private readonly Dictionary<int, float> _blockAt = new Dictionary<int, float>(16);
        private int _thrustsShown, _blocksShown;
        public int ShieldOrbitCount
        {
            get;
            private set;
        }
        public int BubbleCount
        {
            get;
            private set;
        }

        /// <summary>进入无尽：建网格（只建一次，随竞技场复用），对齐格挡回执序号。</summary>
        private void BeginGuard(EndlessService service)
        {
            if (_orbitMesh == null)
            {
                _orbitMesh = GuardMesh(GuardCapacity, shieldOrbitRenderer, "无尽盾牌环绕网格", out _orbitVertices, out _orbitUv, out _orbitColors, out _orbitData);
                _bubbleMesh = GuardMesh(GuardCapacity, bubbleRenderer, "无尽护盾泡泡网格", out _bubbleVertices, out _bubbleUv, out _bubbleColors, out _bubbleData);
                _thrustMesh = GuardMesh(MaxThrusts, thrustRenderer, "无尽刺客突刺网格", out _thrustVertices, out _thrustUv, out _thrustColors, out _thrustData);
                _zoneProperties = new MaterialPropertyBlock();
            }
            _zoneVersion = -1;
            _blockRead = service.BlockSerial;
            _blockAt.Clear();
            System.Array.Clear(_thrusts, 0, _thrusts.Length);
            _thrustsShown = _blocksShown = 0;
        }
        /// <summary>建一张批量网格：uv 固定 0~1 只写一次，uv2 存每块的参数（相位/数量/进度）。</summary>
        private static Mesh GuardMesh(int count, MeshRenderer renderer, string name, out Vector3[] vertices, out Vector2[] uv, out Color[] colors, out Vector2[] data)
        {
            Mesh mesh = CreateMesh(count, out vertices, out uv, out colors);
            mesh.name = name;
            for (int i = 0; i < uv.Length; i += 4)
            {
                uv[i] = new Vector2(0, 0);
                uv[i + 1] = new Vector2(1, 0);
                uv[i + 2] = new Vector2(1, 1);
                uv[i + 3] = new Vector2(0, 1);
            }
            mesh.uv = uv;
            data = new Vector2[count * 4];
            mesh.uv2 = data;
            renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
            return mesh;
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

        /// <summary>每枚己方盾战一块方块：盾牌数 = 星级 + 2，相位按编号错开。</summary>
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
                WriteQuad(_orbitVertices, _orbitColors, _orbitData, count, VisualCenter(p), half, -.45f, OrbitTint, new Vector2(Mathf.Repeat(p.Id * .618f, 1), (int)p.Def.Star + 2));
                count++;
            }
            ClearQuads(_orbitVertices, count, _orbitLast);
            _orbitLast = count;
            ShieldOrbitCount = count;
            _orbitMesh.vertices = _orbitVertices;
            _orbitMesh.colors = _orbitColors;
            _orbitMesh.uv2 = _orbitData;
        }

        /// <summary>防御区内的每枚己方棋子（不含石子）一块护盾泡泡；最近被格挡过的按衰减值波动。</summary>
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
                WriteQuad(_bubbleVertices, _bubbleColors, _bubbleData, count, VisualCenter(p), BubbleRadius * p.Size, -.55f, color, new Vector2(Mathf.Repeat(p.Id * .37f, 1), 0));
                count++;
            }
            ClearQuads(_bubbleVertices, count, _bubbleLast);
            _bubbleLast = count;
            BubbleCount = count;
            _bubbleMesh.vertices = _bubbleVertices;
            _bubbleMesh.colors = _bubbleColors;
            _bubbleMesh.uv2 = _bubbleData;
        }

        /// <summary>登记一道突刺（满了覆盖最旧的那道）。</summary>
        private void PushThrust(Vector2 from, Vector2 to)
        {
            _thrusts[_thrustCursor] = new Thrust { From = from, To = to, StartedAt = Time.unscaledTime };
            _thrustCursor = (_thrustCursor + 1) % MaxThrusts;
            _thrustsShown++;
        }
        /// <summary>每道突刺一个沿攻击线摆放的长方块，uv2.x 写播放进度；播完的收成一个点。</summary>
        private void UpdateThrusts()
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < MaxThrusts; i++)
            {
                Thrust t = _thrusts[i];
                float progress = (now - t.StartedAt) / ThrustSeconds;
                int v = i * 4;
                if (t.StartedAt <= 0 || progress >= 1)
                {
                    _thrustVertices[v] = _thrustVertices[v + 1] = _thrustVertices[v + 2] = _thrustVertices[v + 3] = Vector3.zero;
                    continue;
                }
                Vector2 direction = (t.To - t.From).normalized;
                Vector2 side = new Vector2(-direction.y, direction.x) * ThrustHalfWidth;
                // z=-0.7：在棋子（-0.5）前面，突刺从刺客身上扎出去。
                _thrustVertices[v] = new Vector3(t.From.x - side.x, t.From.y - side.y, -.7f);
                _thrustVertices[v + 1] = new Vector3(t.To.x - side.x, t.To.y - side.y, -.7f);
                _thrustVertices[v + 2] = new Vector3(t.To.x + side.x, t.To.y + side.y, -.7f);
                _thrustVertices[v + 3] = new Vector3(t.From.x + side.x, t.From.y + side.y, -.7f);
                for (int n = 0; n < 4; n++)
                {
                    _thrustColors[v + n] = ThrustTint;
                    _thrustData[v + n] = new Vector2(progress, 0);
                }
            }
            _thrustMesh.vertices = _thrustVertices;
            _thrustMesh.colors = _thrustColors;
            _thrustMesh.uv2 = _thrustData;
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
        private static void WriteQuad(Vector3[] vertices, Color[] colors, Vector2[] data, int index, Vector2 center, float half, float z, Color color, Vector2 parameters)
        {
            int v = index * 4;
            vertices[v] = new Vector3(center.x - half, center.y - half, z);
            vertices[v + 1] = new Vector3(center.x + half, center.y - half, z);
            vertices[v + 2] = new Vector3(center.x + half, center.y + half, z);
            vertices[v + 3] = new Vector3(center.x - half, center.y + half, z);
            for (int n = 0; n < 4; n++)
            {
                colors[v + n] = color;
                data[v + n] = parameters;
            }
        }
        /// <summary>把上一帧用过、这一帧不用的方块收成一个点（不产生像素）；只清变化的那一段。</summary>
        private static void ClearQuads(Vector3[] vertices, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                int v = i * 4;
                vertices[v] = vertices[v + 1] = vertices[v + 2] = vertices[v + 3] = Vector3.zero;
            }
        }

        /// <summary>验收用的只读计数。</summary>
        public object GuardSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["shieldOrbits"] = ShieldOrbitCount, ["bubbles"] = BubbleCount, ["thrusts"] = _thrustsShown, ["blocks"] = _blocksShown,
                ["zoneVisible"] = shieldZoneRenderer != null && shieldZoneRenderer.enabled
            };
        }

        /// <summary>退出无尽：清掉画面状态，网格随竞技场复用。</summary>
        private void ReleaseGuard()
        {
            _blockAt.Clear();
            System.Array.Clear(_thrusts, 0, _thrusts.Length);
            _orbitLast = _bubbleLast = GuardCapacity;
            _zoneVersion = -1;
            if (shieldZoneRenderer != null)
            {
                shieldZoneRenderer.enabled = false;
            }
        }
        private void DestroyGuardMeshes()
        {
            if (_orbitMesh != null)
            {
                Destroy(_orbitMesh);
                Destroy(_bubbleMesh);
                Destroy(_thrustMesh);
            }
        }
    }
}
