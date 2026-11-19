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
        /// <summary>外围敌人的连续绘制缓冲。保留最大容量，但只向 GPU 提交需要该网格画出的敌人。</summary>
        private EndlessQuadBatch _hordeBatch;
        /// <summary>图集当前绑定的纹理页。使用同一页的帧只更新顶点 UV，不重复提交材质参数块。</summary>
        private Texture _hordeTexture;
        /// <summary>外围敌人 Shader 的主图集采样属性。</summary>
        private static readonly int HordeTextureId = Shader.PropertyToID("_MainTex");
        private Sprite[] _frames;
        private Vector4[] _frameUvs;
        /// <summary>外围网格的共享材质参数。只记录实际绑定的图集页，回池清空。</summary>
        private MaterialPropertyBlock _hordeProperties;
        /// <summary>棋盘上的局内时钟、棋心生命和门口预警，每帧从当前无尽局更新。</summary>
        private MaterialPropertyBlock _gridProperties;
        /// <summary>本竞技场背景的材质参数，在表现更新中写入，不修改共享材质资产。</summary>
        private MaterialPropertyBlock _backgroundProperties;
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
        /// <summary>出池初始化本局观测和资源。网格只在首次出池构建，后续沿用已空的绘制范围。</summary>
        public void OnPoolGet()
        {
            _generation++;
            _heartFrame = -1;
            _heartRatio = -1;
            _active = true;
            Ready = false;
            _bindingVersion = -1;
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
            if (_hordeBatch == null)
            {
                int count = EndlessConfig.Count("HordeLimit");
                _hordeBatch = new EndlessQuadBatch(count, "无尽外围敌人批量网格");
                hordeRenderer.GetComponent<MeshFilter>().sharedMesh = _hordeBatch.Mesh;
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
        /// <summary>图集预载完成后，源 Sprite 的纹理还可能等到下一帧才绑定。全部帧可画后才提交绑定版本。</summary>
        private bool RefreshAtlas()
        {
            if (_bindingVersion == GameSpriteAtlasService.BindingVersion)
            {
                return true;
            }
            for (int i = 0; i < _frames.Length; i++)
            {
                if (_frames[i] == null || _frames[i].texture == null)
                {
                    return false;
                }
                _frameUvs[i] = UnityEngine.Sprites.DataUtility.GetOuterUV(_frames[i]);
            }
            BindHordeTexture(_frames[0].texture);
            _bindingVersion = GameSpriteAtlasService.BindingVersion;
            return true;
        }
        /// <summary>在表现帧消费正式无尽快照。先处理出生和死亡，再更新网格，避免与实体视图重复绘制。</summary>
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
            bool atlasReady = RefreshAtlas();
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
            if (atlasReady)
            {
                UpdateHorde(service.Horde, run);
            }
            UpdateHordeBars(service.Horde);
            UpdateAuras(service);
            UpdateGuard(service);
            UpdatePlacement(service);
            UpdateCardUpgradeVisual(service);
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
                // 两侧操作区与棋盘使用同一个 16:9 安全设计框，震屏在此基础上叠加。
                Vector2 pixels = new Vector2(_camera.pixelWidth, _camera.pixelHeight);
                Rect viewport = RevGame.UI.GameBattleLayout.BoardViewport(pixels);
                _camera.orthographicSize = 6.4f / viewport.height;
                Vector2 offset = (Vector2.one * .5f - viewport.center) * (_camera.orthographicSize * 2f);
                _camera.transform.position = new Vector3(4 + offset.x * _camera.aspect + _shakeOffset, 4 + offset.y, -10);
                // 原画等比覆盖相机；宽屏裁上下、窄屏裁左右，建筑不会被拉伸。
                float height = Mathf.Max(_camera.orthographicSize * 2, _camera.orthographicSize * 2 * _camera.aspect / (16f / 9f));
                backgroundRenderer.transform.localScale = new Vector3(height * (16f / 9f), height, 1);
                // 背景跟随画幅中心覆盖超出的两侧，震屏仍由棋盘相机表现。
                Vector3 backgroundPosition = backgroundRenderer.transform.position;
                backgroundPosition.x = _camera.transform.position.x - _shakeOffset;
                backgroundPosition.y = _camera.transform.position.y;
                backgroundRenderer.transform.position = backgroundPosition;
            }
            if (Input.GetMouseButtonDown(0) && (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            {
                Vector3 world = _camera.ScreenToWorldPoint(Input.mousePosition);
                service.ClickCell(Mathf.RoundToInt(world.x), Mathf.RoundToInt(world.y));
            }
            Ready = _pieces.Count == service.Units.Count && _pending.Count == 0 && atlasReady && HeartReady;
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
        /// <summary>按活动槽的原顺序写外围敌人。出生期间由单独视图绘制，跳过这些槽不会影响相邻可见敌人的排序。</summary>
        private void UpdateHorde(EndlessHordeComponents horde, EndlessRun run)
        {
            int frame = (int)(run.seconds * 5) % _frames.Length;
            // 取当前动画帧的真实纹理和 UV。只有跨页时才重新提交材质参数。
            BindHordeTexture(_frames[frame].texture);
            int active = horde.ActiveCount;
            int visible = 0;
            for (int cursor = 0; cursor < active; cursor++)
            {
                int i = horde.ActiveSlot(cursor);
                EndlessHorde h = horde[i];
                // 正在播出生燃烧的杂兵由 PieceView 代画，网格里先隐藏，避免同一只怪画两遍。
                if (!h.active || HordeDrawnByView(i))
                {
                    continue;
                }
                float half = h.kind >= 6 ? .8f : HordeQuadHalf;
                // 颜色就是信息：金色=献祭潮（杀了伤领主），绿色=毒囊（杀了会爆炸）。
                Color color = h.frozenUntil > run.seconds ? Palette[4] : h.burnUntil > run.seconds ? new Color(1, 0.55f, 0.25f)
                    : h.pustule ? new Color(.45f, 1, .35f) : h.surge ? new Color(1, .84f, .3f) : Color.white;
                if (h.kind > 0 && !h.surge && h.frozenUntil <= run.seconds && h.burnUntil <= run.seconds)
                {
                    color = h.kind >= 5 ? new Color(1, .35f, .4f) : h.kind == 3 ? Palette[3] : h.kind == 4 ? Palette[2] : Color.white;
                }
                _hordeBatch.Write(visible++, h.x, h.y, half, half, -.3f, _frameUvs[frame], color);
            }
            _hordeBatch.Submit(visible);
        }

        /// <summary>动画换到不同图集页或本局首次绑定时提交属性。Uv 会由 UpdateHorde 按同一帧同步。</summary>
        private void BindHordeTexture(Texture texture)
        {
            if (_hordeTexture == texture)
            {
                return;
            }
            _hordeTexture = texture;
            _hordeProperties.SetTexture(HordeTextureId, texture);
            hordeRenderer.SetPropertyBlock(_hordeProperties);
        }
        /// <summary>结束本局竞技场寿命。先停止表现并收起批次，再归还棋子和音效，保证重进时看不到上一局的敌人。</summary>
        public void OnPoolReturn()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            _hordeBatch?.Submit(0);
            _hordeProperties?.Clear();
            _hordeTexture = null;
            if (hordeRenderer != null)
            {
                hordeRenderer.SetPropertyBlock(null);
            }
            if (_gcRecorder.Valid)
            {
                _gcRecorder.Dispose();
            }
            ReleaseBossFx();
            ReleaseCombatFx();
            ReleaseFeel();
            ReleaseGuard();
            ResetPlacement();
            ResetCardUpgradeVisual();
            _generation++;
            foreach (var pair in _pieces)
            {
                RevPool.Return(pair.Value);
            }
            _pieces.Clear();
            _pending.Clear();
            Ready = false;
        }
        /// <summary>竞技场真正销毁时释放自建 Mesh。正常回池只清可绘制范围以复用容量。</summary>
        private void OnDestroy()
        {
            OnPoolReturn();
            if (_hordeBatch != null)
            {
                Destroy(_hordeBatch.Mesh);
            }
            DestroyFeelMeshes();
            DestroyGuardMeshes();
            DestroyCardUpgradeMesh();
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
