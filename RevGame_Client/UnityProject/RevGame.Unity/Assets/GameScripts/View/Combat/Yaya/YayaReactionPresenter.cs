// 场景根拥有局内鸦鸦的图片目录/图集/池租约。只有触发才加载，结束显示和退出归还，旧异步不能写入新局。
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Endless;
using RevGame.Flow;
using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.View
{
    /// <summary>局内场景表现所有者，默认没有立绘，不用UI面板承载；一次最多一条互动，不排长队。</summary>
    public sealed class YayaReactionPresenter : MonoBehaviour
    {
        /// <summary>构图比例允许美术调整；区域位于左下侧栏，发生UI/棋子占用时选邻近空位，找不到则暂不显示。</summary>
        [Tooltip("六章Viewport区域（0～1），连同气泡和弹性外扩必须避开棋盘与手牌。")]
        [SerializeField] private Rect campaignViewport = new Rect(.023f, .018f, .20f, .39f);
        /// <summary>无尽左外侧空区，避开敌潮出生横线；只移动表现，不修改敌人路径。</summary>
        [SerializeField] private Rect endlessViewport = new Rect(.012f, .145f, .145f, .48f);
        /// <summary>加载代次与本域模式，过期回调只清理自己的加载结果。</summary>
        private int _generation, _mode;
        /// <summary>Session从域进入到退出，Loading只在一次互动资源准备期间为true。</summary>
        private bool _session, _loading;
        /// <summary>一条互动持有的目录/图集/池实例句柄，结束展示归还；不会卸载剧情仍使用的共享图片。</summary>
        private RevResHandle _catalogHandle;
        private GameSpriteAtlasService.ScopedAtlasLease[] _atlases = Array.Empty<GameSpriteAtlasService.ScopedAtlasLease>();
        private YayaReactionView _view;
        /// <summary>正在显示的Excel台词值快照；摄像机和安全区域仅属于本场景。</summary>
        private YayaReactionLine _line;
        private Camera _camera;
        private Rect _area;
        /// <summary>下次遮挡检查的非缩放时间秒；检查复用角点缓冲与本次UI数组。</summary>
        private float _nextLayoutCheck;
        private readonly Vector3[] _corners = new Vector3[4];
        /// <summary>一次弹出扫描当前UI，低频遮挡复检复用同一数组，避免每次LateUpdate重新分配Find结果。</summary>
        private Graphic[] _graphics = Array.Empty<Graphic>();
        /// <summary>资源正在加载或已有互动显示，不允许叠出第二个立绘。</summary>
        public bool Busy => _loading || _view != null;
        /// <summary>加载失败或空间不足原因，GM可查询；空间不足不会影响战斗规则。</summary>
        public string LastError { get; private set; } = "";
        /// <summary>本场景实际开始播放次数，已加载或请求accepted不算成功落屏。</summary>
        public int ShownCount
        {
            get; private set;
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>仅开发态保存最近三次真正落屏的非缩放时间秒，不包含资源请求；固定数组不会随长局增长。</summary>
        private readonly float[] _recentStarts = new float[3];
        /// <summary>最近一次实际停留参数与可见总时长，用于验收1.5秒停留和动画结束；被暂停收回时总时长可能较短。</summary>
        private float _lastHoldSeconds, _lastVisibleSeconds;
#endif
        /// <summary>域进入时只接线，不预载立绘；先清上一会话，保证重进没有旧Tween或目录引用。</summary>
        public void BeginSession(int mode)
        {
            EndSession();
            _mode = mode;
            _session = true;
            _camera = Camera.main;
            LastError = "";
        }
        /// <summary>只接受本域短反馈；不在观察者回调里阻塞，加载后复查状态和安全区域。</summary>
        public void Show(YayaReactionLine line)
        {
            if (!_session || Busy || !CanDisplay())
            {
                return;
            }
            _line = line;
            _graphics = UnityEngine.Object.FindObjectsOfType<Graphic>();
            _loading = true;
            int version = ++_generation;
            ShowAsync(line, version);
        }
        /// <summary>Mono动作的薄异步适配，所有等待用RevTask；异常唯一出口，代次检查覆盖每个await之后。</summary>
        private async void ShowAsync(YayaReactionLine line, int version)
        {
            try
            {
                _catalogHandle = RevResManager.LoadAsync("Config/Yaya", "YayaExpressionCatalog", typeof(YayaExpressionCatalog), null, RevResGroup.Battle);
                while (IsCurrent(version) && _catalogHandle.IsLoading)
                {
                    await RevTask.Yield();
                }
                if (!IsCurrent(version))
                {
                    return;
                }
                var catalog = _catalogHandle.Content as YayaExpressionCatalog;
                if (catalog == null)
                {
                    throw new InvalidOperationException("鸦鸦互动图片目录加载失败：" + _catalogHandle.ErrorReason);
                }
                _atlases = new GameSpriteAtlasService.ScopedAtlasLease[catalog.AtlasNames.Length];
                for (int i = 0; i < catalog.AtlasNames.Length; i++)
                {
                    _atlases[i] = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas(catalog.AtlasNames[i]);
                    await _atlases[i].WaitReadyAsync();
                    if (!IsCurrent(version))
                    {
                        return;
                    }
                }
                if (!IsCurrent(version))
                {
                    return;
                }
                if (!CanDisplay() || !TryFindArea(out _area))
                {
                    LastError = "左下方暂时没有不遮挡的互动区域";
                    Hide();
                    return;
                }
                YayaReactionView loaded = null;
                RevPool.GetAsync<YayaReactionView>(view =>
                {
                    // 场景已退出才加载完成的池对象只能归还，不能遗留在新场景，也不能再写局部等待变量。
                    if (!IsCurrent(version))
                    {
                        if (view != null)
                        {
                            RevPool.Return(view);
                            // 只清刚还回的空闲对象，不能销毁可能已被新场景复用的活动池对象。
                            RevPool.Clear<YayaReactionView>();
                        }
                        return;
                    }
                    loaded = view;
                });
                float deadline = Time.realtimeSinceStartup + 30;
                while (loaded == null && IsCurrent(version) && Time.realtimeSinceStartup < deadline)
                {
                    await RevTask.Yield();
                }
                if (!IsCurrent(version))
                {
                    if (loaded != null)
                    {
                        RevPool.Return(loaded);
                    }
                    return;
                }
                if (loaded == null)
                {
                    throw new InvalidOperationException("鸦鸦互动预制体加载失败或超时");
                }
                if (!CanDisplay() || !TryFindArea(out _area))
                {
                    RevPool.Return(loaded);
                    Hide();
                    return;
                }
                _view = loaded;
                _loading = false;
                // 场景归属为当前场景；Show回池不挂在UIRoot，场景根生命周期负责清理。
                _view.Play(catalog.Find(line.Expression), line, _camera, _area, Hide);
                ShownCount++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                _recentStarts[0] = _recentStarts[1];
                _recentStarts[1] = _recentStarts[2];
                _recentStarts[2] = Time.unscaledTime;
                _lastHoldSeconds = _view.HoldSeconds;
#endif
            }
            catch (Exception error)
            {
                if (IsCurrent(version))
                {
                    LastError = error.Message;
                    RevLog.Exception(error, "鸦鸦互动表现失败", "RevGame");
                    Hide();
                }
            }
        }
        /// <summary>场景退出/新互动都会改变代次，旧目录和Prefab完成后只能归还，不得覆盖新请求。</summary>
        private bool IsCurrent(int version)
        {
            return _session && version == _generation && this != null;
        }
        /// <summary>表现层也检查阶段，防止异步加载期间从战斗切到准备后仍迟到弹出。</summary>
        private bool CanDisplay()
        {
            GameFlow flow = RevServices.Get<GameFlow>();
            if (flow == null || flow.IsGameplayBlocked)
            {
                return false;
            }
            return _mode == 1 ? RevServices.GetRequired<RevGame.Services.IBattleService>().Phase == GamePhase.Battle : RevServices.GetRequired<EndlessService>().Run.phase == EndlessPhase.Fighting;
        }
        /// <summary>按低频检查当前占用，若新弹窗/敌人进入此区则立即收回；不改变棋子或UI的位置。</summary>
        private void LateUpdate()
        {
            if (_view == null || Time.unscaledTime < _nextLayoutCheck)
            {
                return;
            }
            _nextLayoutCheck = Time.unscaledTime + .2f;
            GameFlow flow = RevServices.Get<GameFlow>();
            if (flow == null || !CanDisplay() || HasOverlap(_area))
            {
                Hide();
            }
        }
        /// <summary>有限候选靠左下侧边，面积包含Back弹性外扩；任何候选遮挡则丢弃，绝不盖棋盘换显示。</summary>
        private bool TryFindArea(out Rect chosen)
        {
            chosen = _mode == 1 ? campaignViewport : endlessViewport;
            if (_camera == null)
            {
                return false;
            }
            // 无尽敌潮出生x=-3，左侧最外条带仍是场景空白；窄而高的区域放大人物但不盖出生横线。
            Rect preferred = chosen;
            for (int size = 0; size < 3; size++)
            {
                float shrink = 1 - size * .16f;
                for (int row = 0; row < 3; row++)
                {
                    var area = new Rect(preferred.x, preferred.y + row * .05f, preferred.width * shrink, preferred.height * shrink);
                    if (!HasOverlap(area))
                    {
                        chosen = area;
                        return true;
                    }
                }
            }
            return false;
        }
        /// <summary>只在显示/低频监测时读UI矩形，忽略背景板但检查按钮、文本与棋盘；动画外扩仍留边距。</summary>
        private bool HasOverlap(Rect area)
        {
            Rect expanded = new Rect(area.x - area.width * .035f, area.y - area.height * .035f, area.width * 1.07f, area.height * 1.07f);
            Rect safe = GameBattleLayout.SafeViewport();
            if (expanded.xMin < safe.xMin || expanded.yMin < safe.yMin || expanded.xMax > safe.xMax || expanded.yMax > safe.yMax)
            {
                return true;
            }
            Rect board = _mode == 1 ? GameBattleLayout.Calculate(new Vector2(_camera.pixelWidth, _camera.pixelHeight), safe).Board : new Rect();
            if (_mode == 1)
            {
                board = new Rect(board.x / _camera.pixelWidth, board.y / _camera.pixelHeight, board.width / _camera.pixelWidth, board.height / _camera.pixelHeight);
            }
            else
            {
                Vector3 low = _camera.WorldToViewportPoint(new Vector3(-.5f, -.5f, 0));
                Vector3 high = _camera.WorldToViewportPoint(new Vector3(8.5f, 8.5f, 0));
                board = Rect.MinMaxRect(low.x, low.y, high.x, high.y);
            }
            if (expanded.Overlaps(board))
            {
                return true;
            }
            foreach (Graphic graphic in _graphics)
            {
                if (graphic == null || !graphic.isActiveAndEnabled || graphic.GetComponentInParent<YayaReactionView>() != null || !(graphic is Text) && graphic.GetComponent<Selectable>() == null)
                {
                    continue;
                }
                if (graphic is Text text && string.IsNullOrEmpty(text.text))
                {
                    continue;
                }
                graphic.rectTransform.GetWorldCorners(_corners);
                Canvas canvas = graphic.canvas;
                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);
                Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, _corners[2]);
                Rect rect = Rect.MinMaxRect(a.x / _camera.pixelWidth, a.y / _camera.pixelHeight, b.x / _camera.pixelWidth, b.y / _camera.pixelHeight);
                if (expanded.Overlaps(rect))
                {
                    return true;
                }
            }
            // 无尽敌潮由实例化网格绘制，不一定有SpriteRenderer；用真实位置占用检查，避免立绘盖住外侧敌人。
            if (_mode == 2)
            {
                var service = RevServices.GetRequired<EndlessService>();
                var horde = service.Horde;
                for (int i = 0; i < horde.Length; i++)
                {
                    if (!horde[i].active)
                    {
                        continue;
                    }
                    Vector3 point = _camera.WorldToViewportPoint(new Vector3(horde[i].x, horde[i].y, 0));
                    // 图像有面积，不能只看中心点；预留半格外扩，使立绘不盖住敌人身体边缘。
                    Vector3 edge = _camera.WorldToViewportPoint(new Vector3(horde[i].x + .5f, horde[i].y + .5f, 0));
                    Rect body = new Rect(point.x - (edge.x - point.x), point.y - (edge.y - point.y), (edge.x - point.x) * 2, (edge.y - point.y) * 2);
                    if (expanded.Overlaps(body))
                    {
                        return true;
                    }
                }
                foreach (var actor in service.Units)
                {
                    if (!actor.Outside || !actor.Piece.IsAlive)
                    {
                        continue;
                    }
                    float half = actor.Piece.Size * .5f;
                    Vector3 low = _camera.WorldToViewportPoint(new Vector3(actor.Center.x - half, actor.Center.y - half, 0));
                    Vector3 high = _camera.WorldToViewportPoint(new Vector3(actor.Center.x + half, actor.Center.y + half, 0));
                    if (expanded.Overlaps(Rect.MinMaxRect(low.x, low.y, high.x, high.y)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        /// <summary>结束显示先令加载过期，再归池、清目录引用和图集租约；空闲小Prefab保留到离开域，Sprite已清空。</summary>
        public void Hide()
        {
            _generation++;
            _loading = false;
            if (_view != null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                _lastVisibleSeconds = _view.VisibleSeconds;
#endif
                RevPool.Return(_view, 0);
                _view = null;
            }
            // 空闲实例Sprite已清空，保留一个小Prefab池复用，避免每次互动重建Canvas；离开本域再销毁池。
            if (_catalogHandle != null)
            {
                RevResManager.Release("Config/Yaya", "YayaExpressionCatalog");
                _catalogHandle = null;
            }
            foreach (var atlas in _atlases)
            {
                atlas?.Dispose();
            }
            _atlases = Array.Empty<GameSpriteAtlasService.ScopedAtlasLease>();
        }
        /// <summary>域关闭/场景销毁时释放空闲Prefab池，材质与字体租约不留到大厅。</summary>
        public void EndSession()
        {
            _session = false;
            Hide();
            _graphics = Array.Empty<Graphic>();
            RevPool.DestroyPool<YayaReactionView>();
        }
        /// <summary>场景真正销毁的兜底，EndSession允许重复调用，不多还资源引用。</summary>
        private void OnDestroy()
        {
            EndSession();
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>只读实际显示、资源租约和非遮挡检查，GM不可直接改Visible凑断言。</summary>
        public object Snapshot()
        {
            return new Dictionary<string, object> { ["mode"] = _mode, ["visible"] = _view != null && _view.Visible, ["settled"] = _view != null && _view.Settled, ["busy"] = Busy, ["lineId"] = _line.Id, ["band"] = _line.Band, ["expression"] = _line.Expression, ["text"] = _line.Text, ["shown"] = ShownCount, ["error"] = LastError, ["speechFits"] = _view != null && _view.SpeechFits, ["overlap"] = _view != null && HasOverlap(_area), ["areaX"] = _area.x, ["areaY"] = _area.y, ["areaWidth"] = _area.width, ["areaHeight"] = _area.height, ["catalogHeld"] = _catalogHandle != null, ["atlasHeld"] = _atlases.Length > 0,
                ["recentStarts"] = _recentStarts, ["cadenceReady"] = ShownCount >= 3 && _recentStarts[2] - _recentStarts[0] <= 6,
                ["holdSeconds"] = _lastHoldSeconds, ["lastVisibleSeconds"] = _lastVisibleSeconds, ["visibleSeconds"] = _view != null ? _view.VisibleSeconds : 0 };
        }
#endif
    }
}
