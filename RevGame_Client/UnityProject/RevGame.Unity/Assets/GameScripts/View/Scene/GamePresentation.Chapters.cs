// 章节背景仅属于场景表现：美术参数留在 Inspector，不改变关卡数值、棋子或 UI 材质。
// 五张大图独立按需加载，不放进启动预热图集；换章先提交新画面再释放旧引用。
using System;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GamePresentation
    {
        [Serializable]
        public sealed class ChapterBackgroundStyle
        {
            [Header("图片替换（资源系统逻辑名，不填磁盘路径）")]
            [Tooltip("图片位于 Assets/GameRes/ChapterBackground，替换同名 PNG 后重新打包即可；更改逻辑名需重新进入战斗。")]
            [InspectorName("背景图片逻辑名")] public string textureName;
            [Header("动态效果（纯美术表现参数，不影响玩法）")]
            [InspectorName("特效总强度（0 = 原画）")] [Range(0f, 1f)] public float strength = 0.8f;
            [InspectorName("流动速度（0 = 静止）")] [Range(0f, 2f)] public float speed = 0.4f;
            [InspectorName("原画扰动幅度")] [Range(0f, 0.04f)] public float distortion = 0.008f;
            [InspectorName("反光 / 光束强度")] [Range(0f, 1.5f)] public float glow = 0.65f;
            [InspectorName("萤火 / 气泡 / 雪花 / 金粉")] [Range(0f, 1f)] public float particles = 0.5f;
            [Header("主题配色（光束 / 波纹 / 雪花，不重染整张原画）")]
            [InspectorName("原画色调（白色保持原色）")] public Color tint = Color.white;
            [InspectorName("主特效颜色")] public Color accent = Color.white;
            [InspectorName("辅特效颜色")] public Color secondary = Color.white;

            public ChapterBackgroundStyle(string name, float rate, float warp, float light, float motes, Color primary, Color other)
            {
                textureName = name; speed = rate; distortion = warp; glow = light; particles = motes;
                accent = primary; secondary = other;
            }
        }

        // 章节与图像的对应是美术绑定，不是策划关卡规则；参数在场景中序列化，修改无需重编代码。
        [Header("第 2 章 · 魔法村落（明朗阳光 / 绿金流光 / 萤火）")]
        [SerializeField] private ChapterBackgroundStyle villageBackground = new ChapterBackgroundStyle("Chapter02_Village", 0.48f, 0.002f, 0.65f, 0.55f, new Color(1f, 0.82f, 0.35f), new Color(0.45f, 1f, 0.67f));
        [Header("第 3 章 · 魔法湖泊（波动 / 银蓝反光 / 焦散）")]
        [SerializeField] private ChapterBackgroundStyle lakeBackground = new ChapterBackgroundStyle("Chapter03_Lake", 0.7f, 0.012f, 0.85f, 0.35f, new Color(0.55f, 0.9f, 1f), new Color(0.2f, 0.65f, 1f));
        [Header("第 4 章 · 死亡沼泽（使用湖面 v1 / 黏稠暗流 / 泥泡）")]
        [SerializeField] private ChapterBackgroundStyle swampBackground = new ChapterBackgroundStyle("Chapter04_Swamp", 0.19f, 0.016f, 0.5f, 0.5f, new Color(0.78f, 0.61f, 0.27f), new Color(0.4f, 0.53f, 0.18f));
        [Header("第 5 章 · 冰雪极地（冰封扫光 / 寒雾 / 双层飘雪）")]
        [SerializeField] private ChapterBackgroundStyle iceBackground = new ChapterBackgroundStyle("Chapter05_Ice", 0.42f, 0.0015f, 0.65f, 0.8f, new Color(0.7f, 0.93f, 1f), new Color(0.35f, 0.65f, 1f));
        [Header("第 6 章 · 象牙金神殿（神圣光束 / 光晕 / 金粉）")]
        [SerializeField] private ChapterBackgroundStyle templeBackground = new ChapterBackgroundStyle("Chapter06_Temple", 0.28f, 0.001f, 0.9f, 0.6f, new Color(1f, 0.86f, 0.48f), new Color(1f, 0.96f, 0.78f));
        [Header("背景可读性与换章演出")]
        [Tooltip("棋盘内减弱光束、雪花及扭曲，外围保留完整效果；不会给棋盘盖黑色遮罩。")]
        [Range(0f, 1f)] [SerializeField] private float backgroundBoardProtection = 0.82f;
        [Tooltip("换章短暂的柔和光晕过渡，纯表现时长，不暂停额外的玩法时间。")]
        [Range(0.1f, 2f)] [SerializeField] private float backgroundArrivalSeconds = 0.85f;

        public const string ChapterBackgroundRoot = "ChapterBackground";
        public const string ChapterBackgroundMaterialName = "ChapterAtmosphere";
        public bool IsChapterBackgroundReady { get { return _chapterReady && _backgroundReady; } }
        public int BackgroundChapter { get { return _activeBackgroundChapter; } }
        public string BackgroundTextureName { get { return _currentBackground == null ? "Original" : _currentBackground.Name; } }
        public string BackgroundLoadError { get { return _backgroundError; } }
        public SpriteRenderer BackgroundRenderer { get { return backgroundRenderer; } }

        private sealed class BackgroundLease
        {
            public string Name;
            public Texture2D Texture;
            public bool Finished;
            public bool Released;
            public string Error;
        }

        private BackgroundLease _pendingBackground;
        private BackgroundLease _currentBackground;
        private Sprite _originalBackgroundSprite;
        private Sprite _chapterSprite;
        private Material _legacyBackgroundMaterial;
        private Material _chapterMaterial;
        private bool _chapterMaterialRequested;
        private bool _chapterMaterialFinished;
        private bool _chapterReady = true;
        private int _requestedBackgroundChapter;
        private int _activeBackgroundChapter = 1;
        private int _backgroundVersion;
        private float _backgroundArrivalTime;
        private Vector3 _originalBackgroundScale;
        private Vector3 _originalBackgroundPosition;
        private static readonly int EffectTimeId = Shader.PropertyToID("_EffectTime");
        private static readonly int TransitionId = Shader.PropertyToID("_Transition");
        private static readonly int BoardRectId = Shader.PropertyToID("_BoardRect");
        private static readonly int ChapterTextureId = Shader.PropertyToID("_ChapterTex");
        private static readonly int PreviewTimeId = Shader.PropertyToID("_PreviewTime");

        private void InitializeChapterBackground()
        {
            if (backgroundRenderer != null)
            {
                _originalBackgroundSprite = backgroundRenderer.sprite;
                _originalBackgroundScale = backgroundRenderer.transform.localScale;
                _originalBackgroundPosition = backgroundRenderer.transform.position;
            }
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnChapterPrepare, owner: this);
        }

        private void OnChapterPrepare(int round)
        {
            GameFlow flow = RevServices.Get<GameFlow>();
            if (flow != null && flow.Screen == GameScreen.Battle) { ApplyChapterBackground(RevServices.GetRequired<IBattleService>().Chapter); }
        }

        private ChapterBackgroundStyle BackgroundStyle(int chapter)
        {
            switch (chapter)
            {
                case 2: return villageBackground;
                case 3: return lakeBackground;
                case 4: return swampBackground;
                case 5: return iceBackground;
                case 6: return templeBackground;
                default: throw new ArgumentOutOfRangeException(nameof(chapter), "背景章节只允许 2~6");
            }
        }

        private void SetLegacyBackgroundMaterial(Material material)
        {
            _legacyBackgroundMaterial = material;
            if (_requestedBackgroundChapter <= 1 && backgroundRenderer != null) { backgroundRenderer.sharedMaterial = material; }
        }

        private void ApplyChapterBackground(int chapter)
        {
            if (_released || backgroundRenderer == null || chapter == _requestedBackgroundChapter) { return; }
            int version = ++_backgroundVersion;
            _requestedBackgroundChapter = chapter;
            _backgroundError = null;
            _chapterReady = false;
            ReleaseBackground(_pendingBackground);
            _pendingBackground = null;
            if (chapter == 1)
            {
                RestoreOriginalBackground();
                _chapterReady = true;
                return;
            }
            LoadChapterBackgroundAsync(chapter, version);
        }

        private void RequestChapterMaterial()
        {
            if (_chapterMaterialRequested) { return; }
            _chapterMaterialRequested = true;
            RevResManager.LoadAsync(RevResPath.Material_Background, ChapterBackgroundMaterialName, typeof(Material), handle =>
            {
                if (_released) { return; }
                _chapterMaterialFinished = true;
                _chapterMaterial = handle.Content as Material;
                if (!handle.IsLoaded || _chapterMaterial == null || _chapterMaterial.shader == null || !_chapterMaterial.shader.isSupported)
                {
                    _backgroundError = "章节背景材质加载失败：" + handle.ErrorReason;
                }
            }, RevResGroup.Scene);
        }

        private async void LoadChapterBackgroundAsync(int chapter, int version)
        {
            BackgroundLease lease = null;
            try
            {
                ChapterBackgroundStyle style = BackgroundStyle(chapter);
                if (style == null || string.IsNullOrWhiteSpace(style.textureName)) { throw new InvalidOperationException("章节背景逻辑名不能为空：" + chapter); }
                RequestChapterMaterial();
                lease = new BackgroundLease { Name = style.textureName };
                _pendingBackground = lease;
                RevResManager.LoadAsync(ChapterBackgroundRoot, lease.Name, typeof(Texture2D), handle =>
                {
                    if (lease.Released || _released) { return; }
                    lease.Finished = true;
                    lease.Texture = handle.Content as Texture2D;
                    if (!handle.IsLoaded || lease.Texture == null) { lease.Error = "背景图片加载失败：" + lease.Name + " / " + handle.ErrorReason; }
                }, RevResGroup.Scene);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!_released && version == _backgroundVersion && (!lease.Finished || !_chapterMaterialFinished))
                {
                    if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException("章节背景加载超时：" + chapter); }
                    await RevTask.Yield();
                }
                if (_released || version != _backgroundVersion) { return; }
                if (!string.IsNullOrEmpty(lease.Error)) { throw new InvalidOperationException(lease.Error); }
                if (!string.IsNullOrEmpty(_backgroundError)) { throw new InvalidOperationException(_backgroundError); }
                if (_chapterMaterial == null || _chapterMaterial.shader == null || !_chapterMaterial.shader.isSupported
                    || _chapterMaterial.shader.name != "RevGame/Background/ChapterAtmosphere")
                {
                    throw new InvalidOperationException("章节背景 Shader 未就绪或不支持当前平台");
                }
                Sprite sprite = Sprite.Create(lease.Texture, new Rect(0, 0, lease.Texture.width, lease.Texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                sprite.name = lease.Name;
                // 先让 Renderer 与参数块指向新图，再销毁旧 Sprite、Release 旧图；不能在渲染器还持有它时卸包。
                Sprite oldSprite = _chapterSprite;
                BackgroundLease oldLease = _currentBackground;
                _chapterSprite = sprite;
                _currentBackground = lease;
                _pendingBackground = null;
                _activeBackgroundChapter = chapter;
                backgroundRenderer.sprite = sprite;
                backgroundRenderer.sharedMaterial = _chapterMaterial;
                _backgroundArrivalTime = Time.unscaledTime;
                UploadChapterStyle();
                FitCamera();
                if (oldSprite != null) { Destroy(oldSprite); }
                ReleaseBackground(oldLease);
                _chapterReady = true;
            }
            catch (Exception error)
            {
                if (!_released && version == _backgroundVersion)
                {
                    _backgroundError = error.Message;
                    RevLog.Exception(error, "章节背景交接失败", "RevGame");
                    GameFlow flow = RevServices.Get<GameFlow>();
                    if (flow != null && !flow.IsTransitioning) { flow.ShowChapterBackgroundFailure(error.Message); }
                }
            }
            finally
            {
                if (lease != null && !ReferenceEquals(lease, _currentBackground)) { ReleaseBackground(lease); }
            }
        }

        private void UploadChapterStyle()
        {
            ChapterBackgroundStyle style = BackgroundStyle(_activeBackgroundChapter);
            if (_backgroundProperties == null) { _backgroundProperties = new MaterialPropertyBlock(); }
            // 独立 Texture2D 不参与图集；显式设置 _ChapterTex，规避 SpriteRenderer 覆盖 _MainTex 的平台差异。
            _backgroundProperties.Clear();
            _backgroundProperties.SetTexture(ChapterTextureId, _currentBackground.Texture);
            _backgroundProperties.SetFloat("_Theme", _activeBackgroundChapter);
            _backgroundProperties.SetFloat("_Strength", style.strength);
            _backgroundProperties.SetFloat("_Speed", style.speed);
            _backgroundProperties.SetFloat("_Distortion", style.distortion);
            _backgroundProperties.SetFloat("_Glow", style.glow);
            _backgroundProperties.SetFloat("_Particles", style.particles);
            _backgroundProperties.SetFloat("_BoardProtection", backgroundBoardProtection);
            _backgroundProperties.SetFloat("_Aspect", _currentBackground.Texture.width / (float)_currentBackground.Texture.height);
            _backgroundProperties.SetFloat(PreviewTimeId, -1f);
            _backgroundProperties.SetColor("_Tint", style.tint);
            _backgroundProperties.SetColor("_Accent", style.accent);
            _backgroundProperties.SetColor("_Secondary", style.secondary);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }

        private void UpdateChapterFrame(GameBattleLayout.Regions areas, Vector2 screenSize)
        {
            if (_activeBackgroundChapter <= 1 || _currentBackground == null) { return; }
            _backgroundProperties.SetFloat(EffectTimeId, Time.unscaledTime);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _backgroundProperties.SetFloat("_Strength", _previewBackgroundStrength >= 0f ? _previewBackgroundStrength : BackgroundStyle(_activeBackgroundChapter).strength);
#endif
            _backgroundProperties.SetFloat(TransitionId, Mathf.Clamp01((Time.unscaledTime - _backgroundArrivalTime) / Mathf.Max(0.1f, backgroundArrivalSeconds)));
            Rect board = areas.Board;
            _backgroundProperties.SetVector(BoardRectId, new Vector4(board.x / screenSize.x, board.y / screenSize.y, board.width / screenSize.x, board.height / screenSize.y));
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }

        private void RestoreOriginalBackground()
        {
            if (backgroundRenderer != null)
            {
                backgroundRenderer.sprite = _originalBackgroundSprite;
                backgroundRenderer.sharedMaterial = _legacyBackgroundMaterial ?? _originalBackgroundMaterial;
                backgroundRenderer.transform.localScale = _originalBackgroundScale;
                backgroundRenderer.transform.position = _originalBackgroundPosition;
                backgroundRenderer.SetPropertyBlock(null);
            }
            if (_chapterSprite != null) { Destroy(_chapterSprite); }
            _chapterSprite = null;
            ReleaseBackground(_currentBackground);
            _currentBackground = null;
            _activeBackgroundChapter = 1;
            _lastBackgroundUv = Vector4.zero;
            _backgroundProperties = null;
        }

        private static void ReleaseBackground(BackgroundLease lease)
        {
            if (lease == null || lease.Released) { return; }
            lease.Released = true;
            lease.Texture = null;
            RevResManager.Release(ChapterBackgroundRoot, lease.Name);
        }

        private void DisposeChapterBackground()
        {
            ++_backgroundVersion;
            RestoreOriginalBackground();
            ReleaseBackground(_pendingBackground);
            _pendingBackground = null;
            if (_chapterMaterialRequested)
            {
                RevResManager.Release(RevResPath.Material_Background, ChapterBackgroundMaterialName);
                _chapterMaterialRequested = false;
            }
            _chapterMaterial = null;
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _activeBackgroundChapter > 1 && _currentBackground != null && backgroundRenderer != null) { UploadChapterStyle(); }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private float _previewBackgroundStrength = -1f;
        // 固定渲染时间仅用于两帧 GPU 像素差异验收，不修改战斗计时、章节或存档。
        public void SetBackgroundPreviewStrength(float strength) { _previewBackgroundStrength = strength; }
        public void SetBackgroundPreviewTime(float seconds)
        {
            if (_activeBackgroundChapter <= 1 || _backgroundProperties == null) { return; }
            _backgroundProperties.SetFloat(PreviewTimeId, seconds);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }
#endif
    }
}
