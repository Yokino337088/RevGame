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
        private bool _released;
        // 材质属于整个场景，不能归入Battle组；否则第一次返回大厅就会被战斗域卸载。
        private bool _backgroundMaterialRequested;
        private Material _originalBackgroundMaterial;
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
            if (backgroundRenderer == null || _backgroundMaterialRequested) { return; }
            _backgroundMaterialRequested = true;
            _originalBackgroundMaterial = backgroundRenderer.sharedMaterial;
            RevResManager.LoadAsync("Material/", "ArcaneScene", typeof(Material), handle =>
            {
                // 场景已退出时只释放迟到结果；不能给销毁中的Renderer写材质。
                if (_released) { return; } // OnDestroy已经配对释放请求，迟到回调不能再减一次引用计数。
                if (!handle.IsLoaded) { RevLog.Error("场景动态材质加载失败：" + handle.ErrorReason, "RevGame"); return; }
                if (backgroundRenderer != null) { backgroundRenderer.sharedMaterial = handle.Content as Material; }
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
                // ★ 用同一个比例同时缩放宽和高，背景中的房屋才不会被拉扁。
                // 取宽、高需要的缩放中较大的一个，保证填满窗口；多出来的部分由相机自然裁掉。
                Vector2 sourceSize = backgroundRenderer.sprite.bounds.size;
                float height = boardCamera.orthographicSize * 2f;
                float scale = Mathf.Max(height * aspect / sourceSize.x, height / sourceSize.y);
                backgroundRenderer.transform.localScale = Vector3.one * scale;
                cameraPosition = boardCamera.transform.position;
                backgroundRenderer.transform.position = new Vector3(cameraPosition.x, cameraPosition.y, 5f);
            }
        }

        private void OnScreen(GameScreen screen)
        {
            boardRoot.SetActive(screen == GameScreen.Battle);
            RevUI.Close<HomePanel>();
            RevUI.Close<LevelSelectPanel>();
            if (screen == GameScreen.Home)
            {
                RevUI.Open<HomePanel>(panel => CheckOpened(panel, screen));
            }
            else if (screen == GameScreen.LevelSelect)
            {
                RevUI.Open<LevelSelectPanel>(panel => CheckOpened(panel, screen));
            }
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
                RevResManager.Release("Material/", "ArcaneScene");
                _backgroundMaterialRequested = false;
            }
            RevEvent.RemoveAllByOwner(this);
            RevTimer.CancelAllOf(this);
            RevUI.Close<HomePanel>();
            RevUI.Close<LevelSelectPanel>();
        }
    }
}
