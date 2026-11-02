using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using Unity.Profiling;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽棋盘表现根，整块经RevPool租用。盘内复用PieceView，外围只用一个动态网格，
    /// 弹道、飘字、出生/死亡等战斗反馈全部复用六章资源（见 EndlessArena.CombatFx），落子提示见 EndlessArena.Placement。
    /// 伤害在Service中已完成，少画一个光效绝不会少一次伤害。退出先使回调过期再归还棋子。
    /// </summary>
    [RevPoolPrefab("Endless", "EndlessArena", Group = RevResGroup.Battle)]
    public sealed partial class EndlessArena : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private PieceArtConfig art;
        [SerializeField] private MeshRenderer hordeRenderer;
        [SerializeField] private MeshRenderer gridRenderer;
        [SerializeField] private MeshRenderer backgroundRenderer;
        private readonly Dictionary<int, PieceView> _pieces = new Dictionary<int, PieceView>(81);
        private readonly HashSet<int> _pending = new HashSet<int>();
        private readonly List<int> _remove = new List<int>(81);
        private Mesh _hordeMesh;
        private int _hordeDrawCount;
        private Vector3[] _vertices;
        private Vector2[] _uv;
        private Color[] _colors;
        private Sprite[] _frames;
        private Vector4[] _frameUvs;
        private MaterialPropertyBlock _hordeProperties, _gridProperties, _backgroundProperties;
        private int _generation, _bindingVersion = -1;
        private bool _active;
        private Camera _camera;
        // 开发机观测窗口：预分配30秒左右样本，统计时才复制/排序，热帧不格式化报告。
        private readonly float[] _frameSamples = new float[1800];
        private int _frameCount;
        private long _gcBytes;
        private bool _gcObserved;
        private ProfilerRecorder _gcRecorder;
        private float _shakeOffset;
        /// <summary>技能命中时的额外震屏幅度，随时间衰减到 0。</summary>
        private float _shakeBoost;
        /// <summary>已经绑定过 Boss 外观的棋子编号，避免每帧重复绑定。</summary>
        private int _bossArtBound = -1;
        public bool Ready
        {
            get;
            private set;
        }
        public string Error
        {
            get;
            private set;
        }
        private static readonly Color[] Palette =
        {
            new Color(0.2f, 1, 1), new Color(1, 0.22f, 0.03f), new Color(1, 0.85f, 0.25f),
            new Color(0.75f, 0.3f, 1), new Color(0.3f, 0.7f, 1), new Color(0.2f, 1, 0.45f), new Color(1, 0.08f, 0.15f)
        };
        public void OnPoolGet()
        {
            _generation++;
            _heartFrame = -1;
            _heartRatio = -1;
            _active = true;
            Ready = false;
            Error = "";
            _camera = Camera.main;
            _frameCount = 0;
            _gcBytes = 0;
            _gcObserved = false;
            _gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            RevSound.StopBgm(.6f);
            _music = new EndlessMusicPlaylist(gameObject);
            _frames = art.Find(PieceKind.Swordman, StarTier.One, GameSide.Enemy).idleFrames;
            _frameUvs = new Vector4[_frames.Length];
            if (_hordeMesh == null)
            {
                int count = EndlessConfig.Count("HordeLimit");
                _hordeMesh = CreateMesh(count, out _vertices, out _uv, out _colors);
                hordeRenderer.GetComponent<MeshFilter>().sharedMesh = _hordeMesh;
                _hordeProperties = new MaterialPropertyBlock();
                _gridProperties = new MaterialPropertyBlock();
                _backgroundProperties = new MaterialPropertyBlock();
            }
            RefreshAtlas();
            BeginFeel();
            EndlessService service = RevServices.Get<EndlessService>();
            if (service?.Run != null)
            {
                BeginCombatFx(service);
            }
        }
        /// <summary>手牌上显示的棋子头像。用棋子待机第一帧，它所在的图集进局时已经加载好。</summary>
        public Sprite PieceIcon(PieceKind kind)
        {
            Sprite[] frames = art.Find(kind, StarTier.One, GameSide.Player).idleFrames;
            return frames != null && frames.Length > 0 ? frames[0] : null;
        }
        private static Mesh CreateMesh(int count, out Vector3[] vertices, out Vector2[] uv, out Color[] colors)
        {
            vertices = new Vector3[count * 4];
            uv = new Vector2[count * 4];
            colors = new Color[count * 4];
            int[] indices = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                int v = i * 4, t = i * 6;
                indices[t] = v;
                indices[t + 1] = v + 1;
                indices[t + 2] = v + 2;
                indices[t + 3] = v;
                indices[t + 4] = v + 2;
                indices[t + 5] = v + 3;
            }
            var mesh = new Mesh
            {
                name = "无尽有限批量网格"
            };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = indices;
            mesh.bounds = new Bounds(new Vector3(4, 4, 0), new Vector3(40, 40, 10));
            return mesh;
        }
        private void RefreshAtlas()
        {
            if (_bindingVersion == GameSpriteAtlasService.BindingVersion)
            {
                return;
            }
            _bindingVersion = GameSpriteAtlasService.BindingVersion;
            for (int i = 0; i < _frames.Length; i++)
            {
                _frameUvs[i] = UnityEngine.Sprites.DataUtility.GetOuterUV(_frames[i]);
            }
            _hordeProperties.SetTexture("_MainTex", _frames[0].texture);
            hordeRenderer.SetPropertyBlock(_hordeProperties);
        }
        private void Update()
        {
            if (!_active)
            {
                return;
            }
            EndlessService service = RevServices.Get<EndlessService>();
            if (service?.Run == null)
            {
                return;
            }
            RefreshAtlas();
            EndlessRun run = service.Run;
            if (run.phase == EndlessPhase.Fighting && run.seconds > 2.5f && _frameCount < _frameSamples.Length)
            {
                _frameSamples[_frameCount++] = Time.unscaledDeltaTime * 1000;
                if (_gcRecorder.Valid)
                {
                    long allocated = _gcRecorder.LastValue;
                    _gcBytes += allocated;
                    _gcObserved |= allocated > 0;
                }
            }
            if (!_fxBegun)
            {
                // 竞技场在服务开局前就被取出（例如自动化先取池）时，第一帧补做初始化。
                BeginCombatFx(service);
            }
            // 顺序有讲究：先读死亡回执，SyncPieces 才知道消失的棋子该溶解；出生视图要在画批量网格前确定，网格才知道隐藏谁。
            ReadDeaths(service);
            SyncPieces(service);
            // 死亡回执和棋盘移除在服务的同一步里发生，本帧没对上的编号（视图还没加载出来）不再保留。
            _dyingIds.Clear();
            UpdateCombatFx(service);
            UpdateHorde(run);
            UpdateHordeBars(run);
            UpdateAuras(service);
            UpdateGuard(service);
            UpdatePlacement(service);
            UpdateHeart(service);
            _shakeBoost = Mathf.MoveTowards(_shakeBoost, 0, Time.unscaledDeltaTime * .5f);
            _shakeOffset = service.Run.shake ? Mathf.Sin(Time.unscaledTime * 62) * _shakeBoost : 0;
            EndlessUnit boss = service.BossUnit();
            UpdateBossFx(service, Time.unscaledDeltaTime);
            BindBossArt(service, boss);
            _gridProperties.SetVector(ShockId, _shock);
            _gridProperties.SetFloat("_Clock", run.seconds);
            _gridProperties.SetFloat("_Theme", run.circuit % 4);
            _gridProperties.SetFloat("_Heart", run.heart / EndlessConfig.Value("HeartHp"));
            _gridProperties.SetVector("_GateWarning", new Vector4(run.gateWarnings[0] > 0 ? 1 : 0, run.gateWarnings[1] > 0 ? 1 : 0, run.gateWarnings[2] > 0 ? 1 : 0, run.gateWarnings[3] > 0 ? 1 : 0));
            Vector4 breaker = new Vector4(-10, -10, 0, 0);
            for (int i = 0; i < service.Units.Count; i++)
            {
                var u = service.Units[i];
                if (u.BreakEyeAt > 0)
                {
                    var victim = service.Board.FindPiece(u.BreakEyeTarget);
                    if (victim != null)
                    {
                        breaker = new Vector4(victim.Pos.X, victim.Pos.Y, 1, 0);
                    }
                }
            }
            _gridProperties.SetVector("_BreakCell", breaker);
            gridRenderer.SetPropertyBlock(_gridProperties);
            _backgroundProperties.SetFloat("_EffectTime", run.seconds);
            _backgroundProperties.SetFloat("_Theme", run.circuit % 4);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
            if (_camera != null)
            {
                _camera.orthographic = true;
                // 棋盘放在屏幕正中并尽量放大；窄屏时再拉远，保证两侧的敌潮看得见。
                _camera.orthographicSize = Mathf.Max(6.4f, 11f / Mathf.Max(0.5f, _camera.aspect));
                _camera.transform.position = new Vector3(4 + _shakeOffset, 4, -10);
                // 原画等比覆盖相机；宽屏裁上下、窄屏裁左右，建筑不会被拉伸。
                float height = Mathf.Max(_camera.orthographicSize * 2, _camera.orthographicSize * 2 * _camera.aspect / (16f / 9f));
                backgroundRenderer.transform.localScale = new Vector3(height * (16f / 9f), height, 1);
            }
            if (Input.GetMouseButtonDown(0) && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            {
                Vector3 world = _camera.ScreenToWorldPoint(Input.mousePosition);
                service.ClickCell(Mathf.RoundToInt(world.x), Mathf.RoundToInt(world.y));
            }
            Ready = _pieces.Count == service.Units.Count && _pending.Count == 0 && _frames[0].texture != null && HeartReady;
        }
        private void SyncPieces(EndlessService service)
        {
            _remove.Clear();
            foreach (var pair in _pieces)
            {
                if (service.Find(pair.Key) == null)
                {
                    // 死亡的播六章溶解，三合一被吃掉的直接回池（见 RetirePieceView）。
                    RetirePieceView(pair.Key, pair.Value);
                    _remove.Add(pair.Key);
                }
            }
            for (int i = 0; i < _remove.Count; i++)
            {
                _pieces.Remove(_remove[i]);
            }
            for (int i = 0; i < service.Units.Count; i++)
            {
                var unit = service.Units[i];
                var p = unit.Piece;
                if (!_pieces.TryGetValue(p.Id, out PieceView view))
                {
                    if (!_pending.Contains(p.Id))
                    {
                        RequestPiece(p.Id);
                    }
                    continue;
                }
                view.Bind(p.Id, p.Def.Kind, p.Def.Star, p.Side);
                CheckUpgrade(view, p.Id, p.Def.Star);
                view.SetBodySize(p.Size, p.IsBoss, false);
                Vector2 pos = service.ActorPosition(unit);
                view.MoveTo(new Vector3(pos.x, pos.y, -0.5f));
                view.SetHp(p.HpRatio);
                view.SetLocked(p.IsLocked);
                view.SetEyeGarrison(p.IsEyeGarrison);
            }
        }
        private void RequestPiece(int id)
        {
            _pending.Add(id);
            int version = _generation;
            GamePoolRequests.GetAsync<PieceView>(view =>
            {
                if (!_active || version != _generation)
                {
                    if (view != null)
                    {
                        RevPool.Return(view);
                    }
                    return;
                }
                _pending.Remove(id);
                if (view == null)
                {
                    Error = "棋子预制体加载失败";
                    return;
                }
                var p = RevServices.GetRequired<EndlessService>().Find(id)?.Piece;
                if (p == null)
                {
                    // 加载期间已经阵亡或被合掉：不画一颗不存在的棋子。
                    RevPool.Return(view);
                    _dyingIds.Remove(id);
                    return;
                }
                _pieces[id] = view;
                Vector2 pos = RevServices.GetRequired<EndlessService>().ActorPosition(RevServices.GetRequired<EndlessService>().Find(id));
                view.Bind(p.Id, p.Def.Kind, p.Def.Star, p.Side);
                view.SetBodySize(p.Size, p.IsBoss, false);
                view.SnapTo(new Vector3(pos.x, pos.y, -0.5f));
                _stars[id] = p.Def.Star;
                // 新落子、援军、入侵者进盘，都和六章落子一样从身体里烧起来。
                PlayPieceBirth(view);
            }
            , transform);
        }
        private void UpdateHorde(EndlessRun run)
        {
            EndlessHordeComponents horde = RevServices.GetRequired<EndlessService>().Horde;
            int frame = (int)(run.seconds * 5) % _frames.Length;
            // 图集分页可能让帧换页；每批只使用当前统一帧的真实纹理，UV与纹理总是配对。
            _hordeProperties.SetTexture("_MainTex", _frames[frame].texture);
            hordeRenderer.SetPropertyBlock(_hordeProperties);
            int active = horde.ActiveCount;
            for (int cursor = 0; cursor < active; cursor++)
            {
                int i = horde.ActiveSlot(cursor);
                EndlessHorde h = horde[i];
                // 正在播出生燃烧的杂兵由 PieceView 代画，网格里先隐藏，避免同一只怪画两遍。
                float half = h.active && !HordeDrawnByView(i) ? (h.kind >= 6 ? .8f : HordeQuadHalf) : 0;
                // 颜色就是信息：金色=献祭潮（杀了伤领主），绿色=毒囊（杀了会爆炸）。
                Color color = h.frozenUntil > run.seconds ? Palette[4] : h.burnUntil > run.seconds ? new Color(1, 0.55f, 0.25f)
                    : h.pustule ? new Color(.45f, 1, .35f) : h.surge ? new Color(1, .84f, .3f) : Color.white;
                if (h.kind > 0 && !h.surge && h.frozenUntil <= run.seconds && h.burnUntil <= run.seconds)
                {
                    color = h.kind >= 5 ? new Color(1, .35f, .4f) : h.kind == 3 ? Palette[3] : h.kind == 4 ? Palette[2] : Color.white;
                }
                Quad(_vertices, _uv, _colors, cursor, new Vector2(h.x, h.y), half, half, _frameUvs[frame], color, -0.3f);
            }
            // 顶点区只写当前实体，清掉上一帧尾部；渲染不再为1024个空槽重复写颜色和UV。
            for (int i = active * 4; i < _hordeDrawCount * 4; i++)
            {
                _vertices[i] = Vector3.zero;
            }
            _hordeDrawCount = active;
            _hordeMesh.vertices = _vertices;
            _hordeMesh.uv = _uv;
            _hordeMesh.colors = _colors;
        }
        private static void Quad(Vector3[] vertices, Vector2[] uv, Color[] colors, int index, Vector2 center, float x, float y, Vector4 rect, Color color, float z)
        {
            int v = index * 4;
            vertices[v] = new Vector3(center.x - x, center.y - y, z);
            vertices[v + 1] = new Vector3(center.x + x, center.y - y, z);
            vertices[v + 2] = new Vector3(center.x + x, center.y + y, z);
            vertices[v + 3] = new Vector3(center.x - x, center.y + y, z);
            uv[v] = new Vector2(rect.x, rect.y);
            uv[v + 1] = new Vector2(rect.z, rect.y);
            uv[v + 2] = new Vector2(rect.z, rect.w);
            uv[v + 3] = new Vector2(rect.x, rect.w);
            for (int n = 0; n < 4; n++)
            {
                colors[v + n] = color;
            }
        }
        public void OnPoolReturn()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            if (_gcRecorder.Valid)
            {
                _gcRecorder.Dispose();
            }
            ReleaseBossFx();
            ReleaseCombatFx();
            ReleaseFeel();
            ReleaseGuard();
            ResetPlacement();
            _generation++;
            foreach (var pair in _pieces)
            {
                RevPool.Return(pair.Value);
            }
            _pieces.Clear();
            _pending.Clear();
            Ready = false;
        }
        private void OnDestroy()
        {
            OnPoolReturn();
            if (_hordeMesh != null)
            {
                Destroy(_hordeMesh);
            }
            DestroyFeelMeshes();
            DestroyGuardMeshes();
        }
        /// <summary>只读观测，GC计数全零时标记未验证，不以零宣称无分配；Editor帧时不能替代手机。</summary>
        public object Metrics()
        {
            float[] sorted = new float[_frameCount];
            Array.Copy(_frameSamples, sorted, _frameCount);
            Array.Sort(sorted);
            float seconds = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                seconds += sorted[i] / 1000;
            }
            return new Dictionary<string, object>
            {
                ["samples"] = _frameCount, ["seconds"] = seconds,
                ["p95Ms"] = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .95f))],
                ["p99Ms"] = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99f))],
                ["gcCounterObservedPositive"] = _gcObserved, ["gcBytesPerSecond"] = seconds > 0 ? _gcBytes / seconds : 0,
                ["scope"] = "Unity Editor整帧，含UI与工具；非目标手机验收"
            };
        }
    }
}
