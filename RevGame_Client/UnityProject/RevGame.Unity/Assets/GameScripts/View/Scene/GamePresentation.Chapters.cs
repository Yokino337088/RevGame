// 章节背景仅属于场景表现：每张原画使用自己的材质与 Shader，美术参数在对应材质 Inspector 中调节。
// 图片与材质按章成对租用；换章先提交完整新画面再释放旧引用，取消/退出也必须配对归还。
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
            [Tooltip("材质在 Material/Background 中，原画名加 _Material。主题专属参数直接在该材质上调，不由场景的公共参数覆盖。")]
            [InspectorName("独立材质逻辑名")] public string materialName;
            [Tooltip("必须与该章节专用 Shader 的声明名称一致，避免换图后误用其它章节的效果。")]
            [InspectorName("独立 Shader 名称")] public string shaderName;

            public ChapterBackgroundStyle(string name)
            {
                textureName = name;
                materialName = name + "_Material";
                shaderName = "RevGame/Background/" + name;
            }
        }
        // 章节与图像的对应是美术绑定，不是策划关卡规则；参数在场景中序列化，修改无需重编代码。
        [Header("第 1 章 · 原始教学背景（独立图集与专用材质）")]
        [SerializeField]
        private ChapterBackgroundStyle tutorialBackground = new ChapterBackgroundStyle("SceneBackdrop_0fc7e394")
        {
            materialName = "ArcaneScene",
            shaderName = "RevGame/Background/Chapter01_Tutorial"
        };
        [Header("第 2 章 · 魔法村落（明朗阳光 / 绿金流光 / 萤火）")]
        [SerializeField] private ChapterBackgroundStyle villageBackground = new ChapterBackgroundStyle("Chapter02_Village");
        [Header("第 3 章 · 魔法湖泊（波动 / 银蓝反光 / 焦散）")]
        [SerializeField] private ChapterBackgroundStyle lakeBackground = new ChapterBackgroundStyle("Chapter03_Lake");
        [Header("第 4 章 · 死亡沼泽（使用湖面 v1 / 黏稠暗流 / 泥泡）")]
        [SerializeField] private ChapterBackgroundStyle swampBackground = new ChapterBackgroundStyle("Chapter04_Swamp");
        [Header("第 5 章 · 冰雪极地（冰封扫光 / 寒雾 / 双层飘雪）")]
        [SerializeField] private ChapterBackgroundStyle iceBackground = new ChapterBackgroundStyle("Chapter05_Ice");
        [Header("第 6 章 · 象牙金神殿（迷幻螺纹 / 慢旋符文 / 呼吸法阵）")]
        [SerializeField] private ChapterBackgroundStyle templeBackground = new ChapterBackgroundStyle("Chapter06_Temple");
        [Header("背景可读性与换章演出")]
        [Tooltip("棋盘内减弱光束、雪花及扭曲，外围保留完整效果；不会给棋盘盖黑色遮罩。")]
        [Range(0f, 1f)][SerializeField] private float backgroundBoardProtection = 0.82f;
        [Tooltip("换章短暂的柔和光晕过渡，纯表现时长，不暂停额外的玩法时间。")]
        [Range(0.1f, 2f)][SerializeField] private float backgroundArrivalSeconds = 0.85f;

        public const string ChapterBackgroundRoot = "ChapterBackground";
        /// <summary>只在场景装配时借棋盘组件；准备门禁高频读取不反复GetComponent。</summary>
        private BoardView _backgroundBoard;
        public bool IsChapterBackgroundReady
        {
            get
            {
                // Boss回合首次加载关键帧也属于画面就绪，不能在美术还没到时消耗准备/战斗时间。
                return _chapterReady && (_backgroundBoard == null || _backgroundBoard.IsBossArtReady);
            }
        }
        public int BackgroundChapter
        {
            get
            {
                return _activeBackgroundChapter;
            }
        }
        public string BackgroundTextureName
        {
            get
            {
                return _currentBackground == null ? "Original" : _currentBackground.Name;
            }
        }
        public string BackgroundLoadError
        {
            get
            {
                return _backgroundError;
            }
        }
        public SpriteRenderer BackgroundRenderer
        {
            get
            {
                return backgroundRenderer;
            }
        }
        private sealed class BackgroundLease
        {
            public string Name;
            public string MaterialName;
            public Texture2D Texture;
            public Sprite Sprite;
            public Material Material;
            public bool Finished;
            public bool MaterialFinished;
            public bool TextureRequested;
            public bool MaterialRequested;
            public GameSpriteAtlasService.TutorialSpriteLease Atlas;
            public bool Released;
            public string Error;
        }
        private BackgroundLease _pendingBackground;
        private BackgroundLease _currentBackground;
        private bool _chapterReady = true;
        private int _requestedBackgroundChapter;
        private int _activeBackgroundChapter;
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
            _backgroundBoard = boardRoot == null ? null : boardRoot.GetComponent<BoardView>();

            if (backgroundRenderer != null)
            {
                _originalBackgroundMaterial = backgroundRenderer.sharedMaterial;
                _originalBackgroundScale = backgroundRenderer.transform.localScale;
                _originalBackgroundPosition = backgroundRenderer.transform.position;
            }
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnChapterPrepare, owner: this);
        }
        private void OnChapterPrepare(int round)
        {
            GameFlow flow = RevServices.Get<GameFlow>();

            if (flow != null && flow.Screen == GameScreen.Battle)
            {
                ApplyChapterBackground(RevServices.GetRequired<IBattleService>().Chapter);
            }
        }

        private ChapterBackgroundStyle BackgroundStyle(int chapter)
        {
            switch (chapter)
            {
                case 1:
                    return tutorialBackground;
                case 2:
                    return villageBackground;
                case 3:
                    return lakeBackground;
                case 4:
                    return swampBackground;
                case 5:
                    return iceBackground;
                case 6:
                    return templeBackground;
                default:
                    throw new ArgumentOutOfRangeException(nameof(chapter), "背景章节只允许 1~6");
            }
        }
        private void ApplyChapterBackground(int chapter)
        {
            if (_released || backgroundRenderer == null || chapter == _requestedBackgroundChapter)
            {
                return;
            }
            int version = ++_backgroundVersion;
            _requestedBackgroundChapter = chapter;
            _backgroundError = null;
            _chapterReady = false;
            ReleaseBackground(_pendingBackground);
            _pendingBackground = null;
            LoadChapterBackgroundAsync(chapter, version);
        }
        private async void LoadChapterBackgroundAsync(int chapter, int version)
        {
            BackgroundLease lease = null;
            try
            {
                ChapterBackgroundStyle style = BackgroundStyle(chapter);

                if (style == null || string.IsNullOrWhiteSpace(style.textureName) || string.IsNullOrWhiteSpace(style.materialName) || string.IsNullOrWhiteSpace(style.shaderName))
                {
                    throw new InvalidOperationException("章节背景的图片与材质逻辑名不能为空：" + chapter);
                }
                lease = new BackgroundLease { Name = style.textureName, MaterialName = style.materialName };
                _pendingBackground = lease;

                // 标记“已请求”后再调用：编辑器缓存命中可能同步回调，退出只释放实际发起的请求。
                if (chapter == 1)
                {
                    lease.Atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireTutorialSprite();
                    LoadTutorialSpriteAsync(lease);
                }
                else
                {
                    lease.TextureRequested = true;
                    RevResManager.LoadAsync(ChapterBackgroundRoot, lease.Name, typeof(Texture2D), handle =>
                    {
                        if (lease.Released || _released)
                        {
                            return;
                        }
                        lease.Finished = true;
                        lease.Texture = handle.Content as Texture2D;

                        if (!handle.IsLoaded || lease.Texture == null)
                        {
                            lease.Error = "背景图片加载失败：" + lease.Name + " / " + handle.ErrorReason;
                        }
                    }, RevResGroup.Scene);
                }
                lease.MaterialRequested = true;
                RevResManager.LoadAsync(RevResPath.Material_Background, lease.MaterialName, typeof(Material), handle =>
                {
                    if (lease.Released || _released)
                    {
                        return;
                    }
                    lease.MaterialFinished = true;
                    lease.Material = handle.Content as Material;

                    if (!handle.IsLoaded || lease.Material == null)
                    {
                        lease.Error = "章节独立材质加载失败：" + lease.MaterialName + " / " + handle.ErrorReason;
                    }
                }, RevResGroup.Scene);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!_released && version == _backgroundVersion && (!lease.Finished || !lease.MaterialFinished))
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        throw new TimeoutException("章节背景加载超时：" + chapter);
                    }
                    await RevTask.Yield();
                }
                if (_released || version != _backgroundVersion)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(lease.Error))
                {
                    throw new InvalidOperationException(lease.Error);
                }
                if (!string.IsNullOrEmpty(_backgroundError))
                {
                    throw new InvalidOperationException(_backgroundError);
                }
                if (lease.Material == null || lease.Material.shader == null || !lease.Material.shader.isSupported
                    || lease.Material.shader.name != style.shaderName)
                {
                    throw new InvalidOperationException("章节独立 Shader 未就绪、与材质不匹配或不支持当前平台：" + lease.MaterialName);
                }
                if (chapter != 1)
                {
                    lease.Sprite = Sprite.Create(lease.Texture, new Rect(0, 0, lease.Texture.width, lease.Texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    lease.Sprite.name = lease.Name;
                }
                // 先让 Renderer 与参数块指向新图，再销毁旧 Sprite、Release 旧图；不能在渲染器还持有它时卸包。
                BackgroundLease oldLease = _currentBackground;
                _currentBackground = lease;
                _pendingBackground = null;
                _activeBackgroundChapter = chapter;
                backgroundRenderer.sprite = lease.Sprite;
                backgroundRenderer.sharedMaterial = lease.Material;

                if (chapter == 1)
                {
                    backgroundRenderer.transform.localScale = _originalBackgroundScale;
                    backgroundRenderer.transform.position = _originalBackgroundPosition;
                }
                _backgroundArrivalTime = Time.unscaledTime;
                UploadChapterStyle();
                FitCamera();
                ReleaseBackground(oldLease);

                // 新Renderer已接管，等旧Sprite的Destroy落地后再清理零引用包。
                // 章节交接时做一次，不把昂贵的闲置资源扫描放进战斗Update；有其他使用者的包由引用计数保留。
                if (oldLease != null)
                {
                    await RevTask.Yield();

                    if (_released || version != _backgroundVersion)
                    {
                        return;
                    }
                    RevResManager.FlushUnused();
                }
                _chapterReady = true;
            }
            catch (Exception error)
            {
                if (!_released && version == _backgroundVersion)
                {
                    _backgroundError = error.Message;
                    RevLog.Exception(error, "章节背景交接失败", "RevGame");
                    GameFlow flow = RevServices.Get<GameFlow>();

                    if (flow != null && !flow.IsTransitioning)
                    {
                        flow.ShowChapterBackgroundFailure(error.Message);
                    }
                }
            }
            finally
            {
                if (lease != null && !ReferenceEquals(lease, _currentBackground))
                {
                    ReleaseBackground(lease);
                }
            }
        }
        /// <summary>第一章只在被选中时向项目图集服务租一张 Sprite，不再由大厅提前预热。</summary>
        private async void LoadTutorialSpriteAsync(BackgroundLease lease)
        {
            try
            {
                Sprite sprite = await lease.Atlas.LoadAsync(lease.Name);

                if (lease.Released || _released)
                {
                    if (sprite != null)
                    {
                        Destroy(sprite);
                    }
                    return;
                }
                lease.Sprite = sprite;

                if (sprite == null)
                {
                    lease.Error = "第一章图集租约已取消";
                }
            }
            catch (Exception error) { lease.Error = "第一章图集加载失败：" + error.Message; }
            finally { lease.Finished = true; }
        }
        private void UploadChapterStyle()
        {
            if (_backgroundProperties == null)
            {
                _backgroundProperties = new MaterialPropertyBlock();
            }
            // 独立 Texture2D 不参与图集；显式设置 _ChapterTex，规避 SpriteRenderer 覆盖 _MainTex 的平台差异。
            _backgroundProperties.Clear();

            if (_activeBackgroundChapter > 1)
            {
                _backgroundProperties.SetTexture(ChapterTextureId, _currentBackground.Texture);
            }
            // Shader/速度/颜色等由当前独立材质决定；参数块只持有场景时钟、图片与棋盘布局。
            _backgroundProperties.SetFloat(EffectTimeId, Time.unscaledTime);
            _backgroundProperties.SetFloat("_BoardProtection", backgroundBoardProtection);
            _backgroundProperties.SetFloat("_Aspect", _currentBackground.Sprite.rect.width / _currentBackground.Sprite.rect.height);
            _backgroundProperties.SetFloat(PreviewTimeId, -1f);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
            _lastBackgroundUv = Vector4.zero;

            if (_activeBackgroundChapter == 1)
            {
                UpdateBackgroundAtlasUv();
            }
        }

        private void UpdateChapterFrame(GameBattleLayout.Regions areas, Vector2 screenSize)
        {
            if (_currentBackground == null)
            {
                return;
            }
            _backgroundProperties.SetFloat(EffectTimeId, Time.unscaledTime);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _backgroundProperties.SetFloat("_Strength", _previewBackgroundStrength >= 0f ? _previewBackgroundStrength : _currentBackground.Material.GetFloat("_Strength"));
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
                backgroundRenderer.sprite = null;
                backgroundRenderer.sharedMaterial = _originalBackgroundMaterial;
                backgroundRenderer.transform.localScale = _originalBackgroundScale;
                backgroundRenderer.transform.position = _originalBackgroundPosition;
                backgroundRenderer.SetPropertyBlock(null);
            }
            ReleaseBackground(_currentBackground);
            _currentBackground = null;
            _activeBackgroundChapter = 0;
            _lastBackgroundUv = Vector4.zero;
            _backgroundProperties = null;
        }
        private static void ReleaseBackground(BackgroundLease lease)
        {
            if (lease == null || lease.Released)
            {
                return;
            }
            lease.Released = true;

            // 两种 Sprite 都由本租约拥有：第一章是 GetSprite 的副本，其余章是动态完整矩形。
            if (lease.Sprite != null)
            {
                Destroy(lease.Sprite);
            }
            lease.Sprite = null;
            lease.Texture = null;
            lease.Material = null;

            if (lease.TextureRequested)
            {
                RevResManager.Release(ChapterBackgroundRoot, lease.Name);
            }
            if (lease.MaterialRequested)
            {
                RevResManager.Release(RevResPath.Material_Background, lease.MaterialName);
            }
            lease.Atlas?.Dispose();
            lease.Atlas = null;
        }
        private void DisposeChapterBackground()
        {
            ++_backgroundVersion;
            RestoreOriginalBackground();
            ReleaseBackground(_pendingBackground);
            _pendingBackground = null;
        }
        private void OnValidate()
        {
            if (Application.isPlaying && _currentBackground != null && backgroundRenderer != null)
            {
                UploadChapterStyle();
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private float _previewBackgroundStrength = -1f;
        // 固定渲染时间仅用于两帧 GPU 像素差异验收，不修改战斗计时、章节或存档。
        public void SetBackgroundPreviewStrength(float strength)
        {
            _previewBackgroundStrength = strength;
        }
        public void SetBackgroundPreviewSpeed(float speed)
        {
            if (_currentBackground == null || _backgroundProperties == null)
            {
                return;
            }
            _backgroundProperties.SetFloat("_Speed", speed >= 0f ? speed : _currentBackground.Material.GetFloat("_Speed"));
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }
        public void SetBackgroundPreviewTime(float seconds)
        {
            if (_backgroundProperties == null)
            {
                return;
            }
            _backgroundProperties.SetFloat(PreviewTimeId, seconds);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }
#endif
    }
}
