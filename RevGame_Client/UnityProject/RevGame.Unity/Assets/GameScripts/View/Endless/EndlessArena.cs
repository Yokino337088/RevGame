using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using Unity.Profiling;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽棋盘表现根，整块经RevPool租用。盘内复用PieceView，外围只用一个动态网格，
    /// 弹道、飘字、出生/死亡等战斗反馈全部复用六章资源（见 EndlessArena.CombatFx），落子提示见 EndlessArena.Placement。
    /// 外围敌人的位置在表现帧插值，伤害和存档由 Service 持有。退出先使回调过期再归还棋子。
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
        /// <summary>本局敌潮的显示样本。只在世界提交变化时读取组件，渲染帧在样本上插值。</summary>
        private EndlessHordePresentation _hordePresentation;
        /// <summary>上次提交网格时的插值比例。静止时保持 1，可跳过重复位置提交。</summary>
        private float _hordeDrawBlend;
        /// <summary>上次提交敌潮时使用的图集绑定版本，-1 表示没有可画的纹理。</summary>
        private int _hordeDrawAtlas;
        /// <summary>最近构建的敌人身体方块数。表现帧按这段紧凑范围写位置。</summary>
        private int _hordeQuadCount;
        /// <summary>出生视图接管或归还槽位时置位，通知身体和血条同时重建紧凑绘制顺序。</summary>
        private bool _hordeLayoutDirty;
        /// <summary>身体与血条着色器的局部关键字。运行时只开在竞技场自有材质，正式资源保持 CPU 变体。</summary>
        private const string HordeGpuKeyword = "REVGAME_HORDE_GPU_INTERPOLATION";
        /// <summary>身体和血条共同使用的 0～1 插值比例。属性存在也用于识别实际装入的新版 Shader。</summary>
        private static readonly int HordeBlendId = Shader.PropertyToID("_HordeBlend");
        /// <summary>两份 CPU 材质来自预制体和资源包。切回原路径时恢复，资源所有权仍归预制体。</summary>
        private Material _hordeCpuMaterial;
        /// <summary>该竞技场的血条原材质。身体换路径时血条一起恢复，保证两者同一坐标约定。</summary>
        private Material _barCpuMaterial;
        /// <summary>竞技场自建的 GPU 身体变体，回池保留以供复用，真正销毁时释放。</summary>
        private Material _hordeGpuMaterial;
        /// <summary>竞技场自建的 GPU 血条变体，寿命和 GPU 身体材质一致。</summary>
        private Material _barGpuMaterial;
        /// <summary>GPU 血条的插值属性块。血条没有图集参数，与身体独立写回各自渲染器。</summary>
        private MaterialPropertyBlock _barGpuProperties;
        /// <summary>上一批次正在使用哪种顶点约定。开始或回池时均恢复 CPU 材质。</summary>
        private bool _hordeInterpolatesOnGpu;
#if UNITY_EDITOR
        /// <summary>Editor 低层回归可以强制选择有能力的 GPU 或 CPU，不修改正式设备策略。回池归零。</summary>
        private bool? _forceHordeGpuInterpolation;
        /// <summary>实测窗口中确实选到 GPU 端点方案的呈现帧数。</summary>
        private int _hordeGpuFrames;
        /// <summary>采样窗口中实际提交的位置字节数。</summary>
        private long _hordePositionBytes;
        /// <summary>采样窗口中实际提交的颜色及 UV 字节数。</summary>
        private long _hordeAppearanceBytes;
        /// <summary>用相同可见方块数按旧布局每帧整批上传推算的字节数，仅用于等工作量对照。</summary>
        private long _hordeWholeVertexBytes;
        /// <summary>采样窗口中存在中间位置的表现帧数，用于检查插值真正参加绘制。</summary>
        private int _hordeInterpolatedFrames;
        /// <summary>采样窗口中读到的敌潮提交数，供验证采样与表现的频率差异。</summary>
        private int _hordeCaptures;
        /// <summary>剑气、环绕盾牌、护盾泡泡和突刺在采样期间交给 Unity 的动态顶点字节。</summary>
        private long _effectUploadBytes;
        /// <summary>同样帧数按旧四批满容量写入的字节，不含固定纹理坐标或索引。</summary>
        private long _effectWholeBytes;
        /// <summary>四批每帧实际参与绘制的方块数之和，用来核对缩小的索引范围。</summary>
        private long _effectDrawnQuads;
        /// <summary>四批都没上传动态顶点的表现帧数。</summary>
        private int _effectNoUploadFrames;
#endif
        /// <summary>图集当前绑定的纹理页。使用同一页的帧只更新顶点 UV，不重复提交材质参数块。</summary>
        private Texture _hordeTexture;
        /// <summary>外围敌人 Shader 的主图集采样属性。</summary>
        private static readonly int HordeTextureId = Shader.PropertyToID("_MainTex");
        private Sprite[] _frames;
        private Vector4[] _frameUvs;
        /// <summary>身体网格的本场材质参数，保留已绑定图集页及 GPU 插值比例，回池清空。</summary>
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
        /// <summary>出池准备敌潮与局部效果的网格，清本局采样计数。首帧按实际样本重建可见内容。</summary>
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
            _hordeLayoutDirty = true;
            _hordeDrawBlend = 1f;
            _hordeDrawAtlas = -1;
            _hordeQuadCount = 0;
#if UNITY_EDITOR
            _hordePositionBytes = 0;
            _hordeAppearanceBytes = 0;
            _hordeWholeVertexBytes = 0;
            _hordeInterpolatedFrames = 0;
            _hordeCaptures = 0;
            _hordeGpuFrames = 0;
            _effectUploadBytes = 0;
            _effectWholeBytes = 0;
            _effectDrawnQuads = 0;
            _effectNoUploadFrames = 0;
#endif
            _gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            RevSound.StopBgm(.6f);
            _music = new EndlessMusicPlaylist(gameObject);
            _frames = art.Find(PieceKind.Swordman, StarTier.One, GameSide.Enemy).idleFrames;
            _frameUvs = new Vector4[_frames.Length];
            if (_hordeBatch == null)
            {
                int count = EndlessConfig.Count("HordeLimit");
                _hordeBatch = CreateHordeDrawBatch(count, "无尽外围敌人批量网格", TryPrepareHordeGpuMaterials());
                _hordePresentation = new EndlessHordePresentation(count, EndlessConfig.Value("SimulationStep"));
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

        /// <summary>仅对能力和 Shader 都足够的 Windows/安卓准备端点流。安卓可在后续升档，预留其能力与真正启用分开。</summary>
        private bool TryPrepareHordeGpuMaterials()
        {
#if UNITY_WEBGL
            return false;
#else
            bool android = Application.platform == RuntimePlatform.Android;
            bool native = Application.platform == RuntimePlatform.WindowsEditor
                || Application.platform == RuntimePlatform.WindowsPlayer || android;
            int shaderLevel = SystemInfo.graphicsShaderLevel;
            // 安卓开局档位保守，热状态与稳定帧窗口确认后才可升级。预留不会在开局提前切换渲染路径。
            bool prepare = Application.isEditor || GameBattleDevicePolicy.Current.AllowGpuInterpolation
                || (android && shaderLevel >= 45);
            if (!native || !prepare || shaderLevel < 30)
            {
                return false;
            }
            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D11 && api != GraphicsDeviceType.Direct3D12
                && api != GraphicsDeviceType.Vulkan && api != GraphicsDeviceType.OpenGLES3)
            {
                return false;
            }
            Material body = hordeRenderer.sharedMaterial;
            Material bars = hpBarRenderer.sharedMaterial;
            if (!HasHordeGpuVariant(body) || !HasHordeGpuVariant(bars))
            {
                return false;
            }
            try
            {
                _hordeCpuMaterial = body;
                _barCpuMaterial = bars;
                _hordeGpuMaterial = new Material(body) { name = "无尽敌潮 GPU 插值" };
                _hordeGpuMaterial.EnableKeyword(HordeGpuKeyword);
                _barGpuMaterial = new Material(bars) { name = "无尽血条 GPU 插值" };
                _barGpuMaterial.EnableKeyword(HordeGpuKeyword);
                _barGpuProperties = new MaterialPropertyBlock();
                return true;
            }
            catch (Exception error)
            {
                ReleaseHordeGpuMaterials();
                RevLog.Warn("无尽显卡材质装配失败，回退到原顶点路径：" + error.Message, "Endless");
                return false;
            }
#endif
        }

        /// <summary>核对真实资产声明的参数和局部变体，旧 AB 只要缺一项就走原顶点路径。</summary>
        private static bool HasHordeGpuVariant(Material material)
        {
            return material != null && material.HasProperty(HordeBlendId) && material.shader.isSupported
                && material.shader.keywordSpace.FindKeyword(HordeGpuKeyword).isValid;
        }

        /// <summary>端点流为可选能力。原生缓冲装配失败时用双流重试，原双流自身的错误仍交给场景入口。</summary>
        private EndlessQuadBatch CreateHordeDrawBatch(int count, string name, bool gpuCapable)
        {
            try
            {
                return new EndlessQuadBatch(count, name, gpuCapable);
            }
            catch (Exception error) when (gpuCapable)
            {
                ReleaseHordeGpuMaterials();
                RevLog.Warn("无尽显卡端点缓冲装配失败，回退到原顶点路径：" + error.Message, "Endless");
                return new EndlessQuadBatch(count, name);
            }
        }

        /// <summary>只有在未绑定 GPU 变体时调用。装配失败与竞技场真正销毁共用这份清理。</summary>
        private void ReleaseHordeGpuMaterials()
        {
            Destroy(_hordeGpuMaterial);
            Destroy(_barGpuMaterial);
            _hordeGpuMaterial = null;
            _barGpuMaterial = null;
            _barGpuProperties = null;
        }

        /// <summary>只有身体和血条都装好对应布局才可用 GPU。按实际入批可见身体数决定，少量实体保留原路径。</summary>
        private bool ShouldInterpolateHordeOnGpu()
        {
            if (!_hordeBatch.CanInterpolateOnGpu || !_barBatch.CanInterpolateOnGpu || _hordeGpuMaterial == null)
            {
                return false;
            }
#if UNITY_EDITOR
            if (_forceHordeGpuInterpolation.HasValue)
            {
                return _forceHordeGpuInterpolation.Value;
            }
#endif
            var policy = GameBattleDevicePolicy.Current;
            // 边界附近连续出生或死亡不用来回切换材质和上传布局。
            int threshold = _hordeInterpolatesOnGpu ? Math.Max(1, policy.GpuMinimumSprites * 3 / 4) : policy.GpuMinimumSprites;
            return policy.AllowGpuInterpolation && _hordeQuadCount >= threshold;
        }

#if UNITY_EDITOR
        /// <summary>隔离 A/B 用的 Editor 覆盖。true 仍要求现用 Shader 和设备能力，null 恢复正式设备策略。</summary>
        internal bool OverrideHordeGpuForChecks(bool? enabled)
        {
            if (!_active || (enabled == true && (_hordeGpuMaterial == null || _barGpuMaterial == null)))
            {
                return false;
            }
            _forceHordeGpuInterpolation = enabled;
            return true;
        }
#endif

        /// <summary>布局写好后再更换两份渲染材质。退出时先清索引范围，再把预制体原材质还给两个渲染器。</summary>
        private void SetHordeGpuMaterial(bool onGpu)
        {
            if (onGpu == _hordeInterpolatesOnGpu)
            {
                return;
            }
            hordeRenderer.sharedMaterial = onGpu ? _hordeGpuMaterial : _hordeCpuMaterial;
            hpBarRenderer.sharedMaterial = onGpu ? _barGpuMaterial : _barCpuMaterial;
            _hordeInterpolatesOnGpu = onGpu;
        }

        /// <summary>显卡路径通常只在表现帧提交一个归一化比例。图集仍绑定在原身体属性块里，避免切姿势丢图。</summary>
        private void SetHordeGpuBlend(float blend, bool atlasReady)
        {
            if (atlasReady && _hordeQuadCount > 0)
            {
                _hordeProperties.SetFloat(HordeBlendId, blend);
                hordeRenderer.SetPropertyBlock(_hordeProperties);
            }
            if (HordeBarCount > 0)
            {
                _barGpuProperties.SetFloat(HordeBlendId, blend);
                hpBarRenderer.SetPropertyBlock(_barGpuProperties);
            }
        }
        /// <summary>手牌上显示的棋子头像。用棋子待机第一帧，它所在的图集进局时已经加载好。</summary>
        public Sprite PieceIcon(PieceKind kind)
        {
            Sprite[] frames = art.Find(kind, StarTier.One, GameSide.Player).idleFrames;
            return frames != null && frames.Length > 0 ? frames[0] : null;
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
        /// <summary>在表现帧消费无尽状态。继续读取每条回执，外围和局部效果各自提交当前绘制范围并记录采样。</summary>
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
            bool sampling = run.phase == EndlessPhase.Fighting && run.seconds > 2.5f && _frameCount < _frameSamples.Length;
            if (sampling)
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
            // 本帧只采样一次已完成的逻辑状态。独立出生视图和两张网格共用同一姿态。
            EndlessHordeComponents horde = service.Horde;
            double now = Time.timeAsDouble;
            bool hordeChanged = _hordePresentation.Capture(horde, run, now);
            float hordeBlend = _hordePresentation.Blend(now);
            UpdateCombatFx(service, hordeChanged, hordeBlend);
            UpdateHorde(horde, run, atlasReady, hordeChanged, hordeBlend, sampling);
            UpdateAuras(service);
            UpdateGuard(service, sampling);
#if UNITY_EDITOR
            if (sampling)
            {
                SampleLocalEffects();
            }
#endif
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
        /// <summary>身体和血条共用插值。显卡路径只在样本或路径变化时上传端点，原路径保留每帧位置上传。</summary>
        private void UpdateHorde(EndlessHordeComponents horde, EndlessRun run, bool atlasReady, bool sampleChanged, float blend, bool sampling)
        {
            int atlas = atlasReady ? _bindingVersion : -1;
            bool appearanceChanged = sampleChanged || _hordeLayoutDirty || _hordeDrawAtlas != atlas;
            if (appearanceChanged)
            {
                RebuildHordeBatches(horde, run, atlasReady);
                _hordeDrawAtlas = atlas;
                _hordeLayoutDirty = false;
            }
            // 等图集时身体会沿用上一份完整网格。保持其旧顶点约定，直到新图和新几何能一起提交。
            bool onGpu = atlasReady ? ShouldInterpolateHordeOnGpu() : _hordeInterpolatesOnGpu;
            bool submit = appearanceChanged || _hordeDrawBlend != blend || onGpu != _hordeInterpolatesOnGpu;
            if (submit)
            {
                if (atlasReady)
                {
                    // 图集页异步绑定期间沿用上一张完整网格。新页可画后，位置和外观一起切换。
                    _hordeBatch.Submit(_hordeQuadCount, blend, appearanceChanged, onGpu);
                }
                _barBatch.Submit(HordeBarCount, blend, appearanceChanged, onGpu);
                SetHordeGpuMaterial(onGpu);
                if (onGpu)
                {
                    SetHordeGpuBlend(blend, atlasReady);
                }
                _hordeDrawBlend = blend;
            }
#if UNITY_EDITOR
            if (sampling)
            {
                int quads = (atlasReady ? _hordeBatch.SubmittedCount : 0) + HordeBarCount;
                _hordeWholeVertexBytes += quads * (EndlessQuadBatch.PositionBytesPerQuad + EndlessQuadBatch.AppearanceBytesPerQuad);
                if (submit)
                {
                    _hordePositionBytes += (atlasReady ? _hordeBatch.LastPositionBytes : 0) + _barBatch.LastPositionBytes;
                    _hordeAppearanceBytes += (atlasReady ? _hordeBatch.LastAppearanceBytes : 0) + _barBatch.LastAppearanceBytes;
                }
                _hordeGpuFrames += onGpu && quads > 0 ? 1 : 0;
                _hordeCaptures += sampleChanged ? 1 : 0;
                _hordeInterpolatedFrames += blend > 0f && blend < 1f && quads > 0 ? 1 : 0;
            }
#endif
        }

        /// <summary>按活动槽原顺序排布身体和血条。只在新样本或绘制归属变更时重建，保留原透明排序。</summary>
        private void RebuildHordeBatches(EndlessHordeComponents horde, EndlessRun run, bool atlasReady)
        {
            int frame = (int)(run.seconds * 5) % _frames.Length;
            if (atlasReady)
            {
                BindHordeTexture(_frames[frame].texture);
            }
            int active = horde.ActiveCount;
            int visible = 0;
            int bars = 0;
            for (int cursor = 0; cursor < active; cursor++)
            {
                int slot = horde.ActiveSlot(cursor);
                // 出生视图接管时同时隐藏网格身体、血条，避免重复绘制。
                if (HordeDrawnByView(slot))
                {
                    continue;
                }
                ref readonly EndlessHorde h = ref _hordePresentation.Read(slot);
                Vector2 from = _hordePresentation.Position(slot, 0f);
                Vector2 to = new Vector2(h.x, h.y);
                bool boss = h.kind >= 6;
                if (atlasReady)
                {
                    float half = boss ? .8f : HordeQuadHalf;
                    _hordeBatch.Write(visible++, from, to, half, half, -.3f, _frameUvs[frame], HordeColor(h, run.seconds));
                }
                if (h.hp < h.maxHp && h.maxHp > 0)
                {
                    WriteHordeBar(bars++, from, to, h, boss);
                }
            }
            _hordeQuadCount = visible;
            HordeBarCount = bars;
        }

        /// <summary>按最近逻辑秒取原有状态颜色。金色为献祭潮，绿色为毒囊，冰冻和灼烧沿用原优先级。</summary>
        private static Color HordeColor(in EndlessHorde horde, float seconds)
        {
            Color color = horde.frozenUntil > seconds ? Palette[4] : horde.burnUntil > seconds ? new Color(1, .55f, .25f)
                : horde.pustule ? new Color(.45f, 1, .35f) : horde.surge ? new Color(1, .84f, .3f) : Color.white;
            if (horde.kind > 0 && !horde.surge && horde.frozenUntil <= seconds && horde.burnUntil <= seconds)
            {
                color = horde.kind >= 5 ? new Color(1, .35f, .4f) : horde.kind == 3 ? Palette[3] : horde.kind == 4 ? Palette[2] : Color.white;
            }
            return color;
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
        /// <summary>停止表现，清两批索引与属性，恢复 CPU 材质。保留可复用网格和竞技场持有的 GPU 材质。</summary>
        public void OnPoolReturn()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            _hordePresentation?.Reset();
            _hordeBatch?.Submit(0);
            _barBatch?.Submit(0);
            SetHordeGpuMaterial(false);
#if UNITY_EDITOR
            _forceHordeGpuInterpolation = null;
#endif
            _hordeProperties?.Clear();
            _barGpuProperties?.Clear();
            _hordeTexture = null;
            if (hordeRenderer != null)
            {
                hordeRenderer.SetPropertyBlock(null);
            }
            if (hpBarRenderer != null)
            {
                hpBarRenderer.SetPropertyBlock(null);
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
        /// <summary>竞技场真正销毁时释放 GPU 专用材质与自建 Mesh。低能力设备没有这些专用材质。</summary>
        private void OnDestroy()
        {
            OnPoolReturn();
            if (_hordeBatch != null)
            {
                Destroy(_hordeBatch.Mesh);
            }
            ReleaseHordeGpuMaterials();
            DestroyFeelMeshes();
            DestroyGuardMeshes();
            DestroyCardUpgradeMesh();
        }
#if UNITY_EDITOR
        /// <summary>只在整帧采样窗口内累计四批实际 API 写入和旧画法的同帧数基线，不包含按需出现的升星环。</summary>
        private void SampleLocalEffects()
        {
            int bytes = _auraBatch.LastUploadBytes + _orbitBatch.LastUploadBytes + _bubbleBatch.LastUploadBytes + _thrustBatch.LastUploadBytes;
            _effectUploadBytes += bytes;
            _effectWholeBytes += _auraBatch.FullUploadBytes + _orbitBatch.FullUploadBytes + _bubbleBatch.FullUploadBytes + _thrustBatch.FullUploadBytes;
            _effectDrawnQuads += _auraBatch.DrawnCount + _orbitBatch.DrawnCount + _bubbleBatch.DrawnCount + _thrustBatch.DrawnCount;
            if (bytes == 0)
            {
                _effectNoUploadFrames++;
            }
        }
#endif

        /// <summary>查询时才排序帧样本。Editor 的字节只表示提交到网格接口的范围，整帧与显存带宽需分开解释。</summary>
        public object Metrics()
        {
            float[] sorted = new float[_frameCount];
            Array.Copy(_frameSamples, sorted, _frameCount);
            Array.Sort(sorted);
            float seconds = 0;
            int over33Ms = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                seconds += sorted[i] / 1000;
                if (sorted[i] > 1000f / 30f)
                {
                    over33Ms++;
                }
            }
            return new Dictionary<string, object>
            {
                ["samples"] = _frameCount, ["seconds"] = seconds,
                ["p95Ms"] = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .95f))],
                ["p99Ms"] = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99f))],
                ["maxMs"] = sorted.Length == 0 ? 0 : sorted[sorted.Length - 1],
                ["over33Ms"] = over33Ms,
                ["gcCounterObservedPositive"] = _gcObserved, ["gcBytesPerSecond"] = seconds > 0 ? _gcBytes / seconds : 0,
                ["scope"] = "Unity Editor整帧，含UI与工具；非目标手机验收",
#if UNITY_EDITOR
                ["hordePresentation"] = new Dictionary<string, object>
                {
                    ["positionBytes"] = _hordePositionBytes, ["appearanceBytes"] = _hordeAppearanceBytes,
                    ["wholeVertexBytesAtSameQuadCounts"] = _hordeWholeVertexBytes,
                    ["captures"] = _hordeCaptures, ["interpolatedFrames"] = _hordeInterpolatedFrames,
                    ["gpuFrames"] = _hordeGpuFrames, ["gpuInterpolation"] = _hordeInterpolatesOnGpu
                },
                ["guardSelection"] = GuardSelectionMetrics(),
                ["localEffects"] = new Dictionary<string, object>
                {
                    ["dynamicBytes"] = _effectUploadBytes,
                    ["fullBytesAtSameFrameCount"] = _effectWholeBytes,
                    ["drawnQuads"] = _effectDrawnQuads,
                    ["noUploadFrames"] = _effectNoUploadFrames
                }
#endif
            };
        }
    }
}
