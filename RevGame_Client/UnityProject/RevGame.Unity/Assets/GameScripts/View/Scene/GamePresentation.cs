// ============================================================
// GamePresentation.cs —— 场景中的大流程表现接线。
// GameFlow 只切状态和域；本组件负责显示/隐藏棋盘、打开局外面板、适配相机。
// 大厅不保留战斗画面，离开战斗时 BoardView.OnDisable 会归还所有棋子和格子。
// ============================================================
using RevGame.Core;
using RevGame.Flow;
using RevGame.UI;
using Revolution;
using UnityEngine;
using System;
using UnityEngine.UI;

namespace RevGame.View
{
    public sealed class GamePresentation : MonoBehaviour
    {
        [SerializeField]
        private GameObject boardRoot;
        [SerializeField]
        private Camera boardCamera;
        [Tooltip("由编辑器工具接好的背景。直接引用 Sprite，运行时不读取外部美术文件夹。")]
        [SerializeField]
        private SpriteRenderer backgroundRenderer;
        /// <summary>
        /// 是否让背景按相机自动铺满。默认关闭：尊重场景里手动设置的位置与（可能非等比的）缩放，
        /// 每帧只同步图集 UV；只有需要背景跟随任意屏幕比例自动铺满时才在 Inspector 勾上。
        /// </summary>
        [Tooltip("勾上=按相机自动铺满背景（等比缩放并居中对齐相机）；关闭=保留场景里手动设置的尺寸，只同步图集UV")]
        [SerializeField]
        private bool fitBackgroundToCamera;
        private bool _released;
        /// <summary>当前屏幕的异步装配版本；快速切换时旧面板加载回调不能覆盖新屏幕。</summary>
        private int _screenVersion;
        private GameScreen _readyScreen = GameScreen.Exit;
        private string _loadError;
        // 材质属于整个场景，不能归入Battle组；否则第一次返回大厅就会被战斗域卸载。
        private bool _backgroundMaterialRequested;
        /// <summary>动态背景也属于交付屏障；不能只等棋盘完成就把仍未取得材质的背景露给玩家。</summary>
        private bool _backgroundReady;
        private string _backgroundError;
        private Material _originalBackgroundMaterial;
        /// <summary>背景只有一张，允许独立参数块；棋子/弹道仍共享参数以保留合批。</summary>
        private MaterialPropertyBlock _backgroundProperties;
        private Vector4 _lastBackgroundUv;
        private static readonly int ArtUvRectId = Shader.PropertyToID("_ArtUvRect");
        private void OnEnable()
        {
            // 相机真正渲染前再核对一次布局：拖动窗口时不能等 0.25 秒计时器才适配新尺寸。
            // Game 与 Simulator 的目标尺寸也可能不同，使用当前相机的像素大小，不用上次窗口的快照。
            Camera.onPreCull += OnCameraRendering;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraRendering;
        }

        private void OnCameraRendering(Camera camera)
        {
            if (camera == boardCamera) { FitCamera(); }
        }
        private void Start()
        {
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreen, owner: this);
            // GameBootstrap 在配置表就绪后发出首次 ScreenChanged，此前不提前开放操作。
            FitCamera();
            LoadBackgroundMaterial();
        }

        /// <summary>两段式逻辑路径通过框架加载，编辑器直读和正式AB加载共用此入口；不改用户选好的背景Sprite。</summary>
        private void LoadBackgroundMaterial()
        {
            if (backgroundRenderer == null) { _backgroundReady = true; return; }
            if (_backgroundMaterialRequested) { return; }
            _backgroundMaterialRequested = true;
            _originalBackgroundMaterial = backgroundRenderer.sharedMaterial;
            RevResManager.LoadAsync(RevResPath.Material_Background, "ArcaneScene", typeof(Material), handle =>
            {
                // 场景已退出时只释放迟到结果；不能给销毁中的Renderer写材质。
                if (_released) { return; } // OnDestroy已经配对释放请求，迟到回调不能再减一次引用计数。
                if (!handle.IsLoaded)
                {
                    _backgroundError = "场景动态材质加载失败：" + handle.ErrorReason;
                    RevLog.Error(_backgroundError, "RevGame");
                    return;
                }
                if (backgroundRenderer != null)
                {
                    backgroundRenderer.sharedMaterial = handle.Content as Material;
                    _backgroundReady = backgroundRenderer.sharedMaterial != null;
                }
            }, RevResGroup.Scene);
        }

        private void FitCamera()
        {
            // 棋盘必须同时放得进横向与纵向；窄窗口不能仅固定 orthographicSize 导致两侧被裁。
            if (boardCamera == null)
            {
                return;
            }

            float aspect = Mathf.Max(0.1f, boardCamera.aspect);
            // 与手牌/HUD 使用相同安全区域。这里用像素计算，UI 用画布单位计算，比例完全一致。
            Vector2 screenSize = new Vector2(boardCamera.pixelWidth, boardCamera.pixelHeight);
            if (screenSize.x < 1f || screenSize.y < 1f) { return; }
            GameBattleLayout.Regions areas = GameBattleLayout.Calculate(screenSize, GameBattleLayout.SafeViewport());
            float boardFraction = areas.Board.height / screenSize.y;
            float boardWidth = Consts.BoardSize * Consts.CellSize;
            boardCamera.orthographicSize = boardWidth / (2f * boardFraction);
            Vector2 center = areas.Board.center;
            Vector3 cameraPosition = boardCamera.transform.position;
            cameraPosition.x = (0.5f - center.x / screenSize.x) * boardCamera.orthographicSize * 2f * aspect;
            cameraPosition.y = (0.5f - center.y / screenSize.y) * boardCamera.orthographicSize * 2f;
            boardCamera.transform.position = cameraPosition;
            if (backgroundRenderer != null && backgroundRenderer.sprite != null)
            {
                UpdateBackgroundAtlasUv();
                // ★ 只有显式勾了“自动铺满”才写 transform：本方法每帧 pre-cull 都会跑，
                //   无条件覆盖会让美术在场景里手调的尺寸一进 Play 就被改掉（含非等比缩放），手调永远留不住。
                if (fitBackgroundToCamera)
                {
                    // ★ 旧场景曾把背景设成Sliced；它实际绘制size，而旧代码却按sprite.bounds计算，导致留白。
                    // 统一使用完整矩形Sprite，缩放才与下面的源图尺寸一一对应；背景不参与安全区内缩。
                    backgroundRenderer.drawMode = SpriteDrawMode.Simple;
                    // ★ 用同一个比例同时缩放宽和高，背景中的房屋才不会被拉扁。
                    // 取宽、高需要的缩放中较大的一个，保证填满窗口；多出来的部分由相机自然裁掉。
                    Vector2 sourceSize = backgroundRenderer.sprite.bounds.size;
                    float height = boardCamera.orthographicSize * 2f;
                    float scale = Mathf.Max(height * aspect / sourceSize.x, height / sourceSize.y) * 1.02f;
                    backgroundRenderer.transform.localScale = Vector3.one * scale;
                    cameraPosition = boardCamera.transform.position;
                    // 背景Pivot不一定在中心，扣掉缩放后的bounds.center，确保四边总围绕相机而非图片锚点。
                    Vector3 pivotOffset = backgroundRenderer.sprite.bounds.center * scale;
                    backgroundRenderer.transform.position = new Vector3(cameraPosition.x - pivotOffset.x, cameraPosition.y - pivotOffset.y, 5f);
                }
            }
        }

        /// <summary>
        /// 图集异步绑定后Sprite对象没换，但UV会改变。每次取四个数比较，只有变化才上传参数。
        /// 不访问sprite.uv数组（它会分配），也不访问renderer.material（它会克隆材质）。
        /// </summary>
        private void UpdateBackgroundAtlasUv()
        {
            Vector4 outer = UnityEngine.Sprites.DataUtility.GetOuterUV(backgroundRenderer.sprite);
            Vector4 rect = new Vector4(outer.x, outer.y, outer.z - outer.x, outer.w - outer.y);
            if (_backgroundProperties != null && rect == _lastBackgroundUv) { return; }
            if (_backgroundProperties == null) { _backgroundProperties = new MaterialPropertyBlock(); }
            _lastBackgroundUv = rect;
            backgroundRenderer.GetPropertyBlock(_backgroundProperties);
            _backgroundProperties.SetVector(ArtUvRectId, rect);
            backgroundRenderer.SetPropertyBlock(_backgroundProperties);
        }

        private void OnScreen(GameScreen screen)
        {
            int version = ++_screenVersion;
            _readyScreen = GameScreen.Exit;
            _loadError = null;
            if (boardRoot != null) { boardRoot.SetActive(screen == GameScreen.Battle); }
            RevUI.Close<HomePanel>();
            RevUI.Close<LevelSelectPanel>();
            if (screen == GameScreen.Home || screen == GameScreen.LevelSelect)
            {
                ShowMenuAsync(screen, version);
            }
            else if (screen == GameScreen.Battle && boardRoot == null)
            {
                _loadError = "战斗场景没有接上棋盘根节点";
            }
        }

        /// <summary>局外面板在图集到齐之后才实例化；不能先显示白底，再等Unity稍后修复图片绑定。</summary>
        private async void ShowMenuAsync(GameScreen screen, int version)
        {
            try
            {
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(new[] { "UIMenus", "UIBattle" });
                if (_released || version != _screenVersion) { return; }
                RevUIPanel panel;
                if (screen == GameScreen.Home) { panel = await RevUI.OpenAsync<HomePanel>(); }
                else { panel = await RevUI.OpenAsync<LevelSelectPanel>(); }
                CheckOpened(panel, screen);
                if (!_released && version == _screenVersion) { _readyScreen = screen; }
            }
            catch (Exception error)
            {
                _loadError = error.Message;
                RevLog.Exception(error, "局外面板装配失败", "RevGame");
            }
        }

        /// <summary>先让棋盘正常OnDisable回收对象，再交给RevScene清池/卸场景，避免双重归还或旧弹幕残留。</summary>
        public void SuspendBattle()
        {
            if (boardRoot != null) { boardRoot.SetActive(false); }
        }

        /// <summary>
        /// 可见性屏障：等待81格、所有开局棋子、HUD和六张手牌完成装配，才允许过场淡出。
        /// 仅流程边界检查一次Image清单，不放进战斗Update；超时给出明确原因，不能无限等加载条。
        /// </summary>
        public async RevTask WaitUntilReadyAsync(GameScreen screen, Action<float> progress)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            BoardView board = boardRoot == null ? null : boardRoot.GetComponent<BoardView>();
            while (!_released)
            {
                if (!string.IsNullOrEmpty(_loadError)) { throw new InvalidOperationException(_loadError); }
                if (!string.IsNullOrEmpty(_backgroundError)) { throw new InvalidOperationException(_backgroundError); }
                if (screen == GameScreen.Battle)
                {
                    if (board == null) { throw new InvalidOperationException("GameScene缺少BoardView"); }
                    if (!string.IsNullOrEmpty(board.LoadError)) { throw new InvalidOperationException(board.LoadError); }
                    progress?.Invoke(board.CellCount / (float)(Consts.BoardSize * Consts.BoardSize) * 0.8f);
                    if (board.IsReady && _backgroundReady && RevUI.IsOpen<PreparePanel>() && RevUI.IsOpen<BattleHudPanel>()) { break; }
                }
                else if (_readyScreen == screen) { break; }
                if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException("画面装配超时：" + screen); }
                await RevTask.Yield();
            }
            if (_released) { throw new OperationCanceledException("场景已释放"); }
            // 图集注册与UGUI提交不是同一个步骤。刷新后让引擎完成一次真实Canvas重建和渲染，再撤遮挡。
            Image[] images = UnityEngine.Object.FindObjectsOfType<Image>();
            foreach (Image image in images) { image.SetAllDirty(); }
            Canvas.ForceUpdateCanvases();
            await RevTask.Yield();
            foreach (Image image in images)
            {
                if (image == null || image.sprite == null) { continue; }
                if (image.sprite.texture == null || image.mainTexture == Texture2D.whiteTexture)
                {
                    throw new InvalidOperationException("UI图集像素未绑定：" + image.name + " / " + image.sprite.name);
                }
            }
            if (screen == GameScreen.Battle)
            {
                CardItem[] cards = RevUI.Get<PreparePanel>().GetComponentsInChildren<CardItem>();
                if (cards.Length != 6) { throw new InvalidOperationException("开局手牌视图必须完整显示6张"); }
            }
            progress?.Invoke(1f);
            await RevTask.Yield();
        }

        private void CheckOpened(RevUIPanel panel, GameScreen expected)
        {
            if (panel == null)
            {
                RevLog.Error("局外面板加载失败：" + expected, "RevGame");
                return;
            }

            GameFlow flow = RevServices.Get<GameFlow>();
            if (_released || flow == null || flow.Screen != expected)
            {
                RevUI.Close(panel);
            }
        }

        private void OnDestroy()
        {
            _released = true;
            if (_backgroundMaterialRequested)
            {
                if (backgroundRenderer != null) { backgroundRenderer.sharedMaterial = _originalBackgroundMaterial; }
                RevResManager.Release(RevResPath.Material_Background, "ArcaneScene");
                _backgroundMaterialRequested = false;
            }
            RevEvent.RemoveAllByOwner(this);
            RevTimer.CancelAllOf(this);
            RevUI.Close<HomePanel>();
            RevUI.Close<LevelSelectPanel>();
        }
    }
}
