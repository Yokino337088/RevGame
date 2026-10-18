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
            if (backgroundRenderer == null || _backgroundMaterialRequested) { return; }
            _backgroundMaterialRequested = true;
            _originalBackgroundMaterial = backgroundRenderer.sharedMaterial;
            RevResManager.LoadAsync(RevResPath.Material_Background, "ArcaneScene", typeof(Material), handle =>
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
                UpdateBackgroundAtlasUv();
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
