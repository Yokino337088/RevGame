using System.Collections.Generic;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>纯引导浮层：没有棋盘、棋子或代替手牌的按钮。所有Graphic不拦截点击，玩家直接操作下层真实界面。</summary>
    [RevUIPanel("UI/Panel/Story", RevUILayer.Guide, InBackStack = false, Mask = RevUIMaskMode.None)]
    public sealed class TutorialPanel : RevUIPanel
    {
        [RevBind] private Text _instruction;
        [RevBind] private Text _progress;
        [RevBind] private Text _liberty;
        [RevBind] private Image _focus;
        /// <summary>棋格专用共享材质，增强闪烁和淡色底光；由编辑器装配，不在运行时new材质。</summary>
        [SerializeField] private Material boardFocusMaterial;
        private Material _uiFocusMaterial;
        // 复用布局查询缓冲，所有权属于本面板；不修改真实按钮的位置。
        private readonly List<RectTransform> _controls = new List<RectTransform>();
        private readonly List<Rect> _occupied = new List<Rect>();

        private readonly Vector3[] _corners = new Vector3[4];

        protected override void OnBindView()
        {
            _instruction.supportRichText = false;
            _uiFocusMaterial = _focus.material;
        }
        protected override void OnOpen()
        {
            // 动画由共享Shader的时间驱动，不克隆材质，也不对原按钮做缩放或透明度动画。
            RevTimer.Every(0.1f, RefreshView, owner: this);
        }
        protected override void OnRefreshView()
        {
            var lesson = RevLogic.Get<TutorialLogic>();

            if (lesson == null || !lesson.Active)
            {
                _focus.enabled = false;
                return;
            }
            var flow = RevServices.GetRequired<GameFlow>();
            _instruction.text = flow.StoryContent.Find("lesson").Lines[(int)lesson.Step].Text;
            _progress.text = "团团的引导  " + ((int)lesson.Step + 1) + " / 5";
            _liberty.text = "敌气 " + lesson.EnemyLiberties + (lesson.EnemyLocked ? " · 封锁" : "");
            // 查询当前可见控件的实际边界，利用最大空隙放大提示；不覆盖手工调整过的真实UI布局。
            RectTransform root = (RectTransform)transform;
            var areas = GameBattleLayout.Calculate(root.rect.size, GameBattleLayout.SafeViewport());
            Rect box = FindHintSpace(root, areas.Right);
            var hint = (RectTransform)_instruction.transform.parent;
            hint.anchorMin = hint.anchorMax = Vector2.zero;
            hint.pivot = Vector2.zero;
            hint.anchoredPosition = box.position;
            hint.sizeDelta = box.size;
            var prepare = RevUI.Get<PreparePanel>();
            Transform target = prepare == null ? null : FindTarget(prepare.transform, lesson.Step == TutorialStep.SelectCard ? "card0" : "btnReady");
            bool uiTarget = lesson.Step == TutorialStep.SelectCard || lesson.Step == TutorialStep.Ready;
            Material targetMaterial = uiTarget ? _uiFocusMaterial : boardFocusMaterial;

            if (_focus.material != targetMaterial)
            {
                _focus.material = targetMaterial;
            }
            _focus.enabled = uiTarget ? target != null : Camera.main != null;

            if (!_focus.enabled)
            {
                return;
            }
            // 把真实按钮的世界四角换算到本浮层坐标；窗口尺寸变化后也能准确跟随，不写死屏幕像素。
            if (!uiTarget)
            {
                // 同一张透明魔法框投影到真实棋格上；只画提示，不创建第二张棋盘或拦截棋子点击。
                Vector3 world = lesson.Target.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                Camera camera = Camera.main;
                Canvas canvas = GetComponentInParent<Canvas>();
                Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Vector2 lower, upper;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, camera.WorldToScreenPoint(world - new Vector3(1f, 1f, 0f) * Consts.CellSize * 0.48f), uiCamera, out lower);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, camera.WorldToScreenPoint(world + Vector3.one * Consts.CellSize * 0.48f), uiCamera, out upper);
                SetFocus(root, lower, upper);
                return;
            }
            ((RectTransform)target).GetWorldCorners(_corners);
            Vector3 min = root.InverseTransformPoint(_corners[0]);
            Vector3 max = root.InverseTransformPoint(_corners[2]);
            SetFocus(root, min, max);
        }
        /// <summary>把目标边界扩出少量留白；中心透明，文字/棋子仍然可见且可以点击。</summary>
        private void SetFocus(RectTransform root, Vector2 min, Vector2 max)
        {
            _focus.rectTransform.anchorMin = _focus.rectTransform.anchorMax = root.pivot;
            _focus.rectTransform.pivot = Vector2.zero;
            _focus.rectTransform.anchoredPosition = (Vector2)min - Vector2.one * 5f;
            _focus.rectTransform.sizeDelta = (max - min) + Vector2.one * 10f;
        }
        /// <summary>在右栏寻找最大的连续竖向空隙，最多使用29%栏高，并为外缘特效留出安全间距。</summary>
        private Rect FindHintSpace(RectTransform root, Rect right)
        {
            _occupied.Clear();
            CollectOccupied(root, right, RevUI.Get<PreparePanel>());
            CollectOccupied(root, right, RevUI.Get<BattleHudPanel>());
            _occupied.Sort((a, b) => a.yMin.CompareTo(b.yMin));
            float cursor = right.yMin;
            float bestBottom = cursor, bestTop = cursor;
            for (int i = 0; i <= _occupied.Count; i++)
            {
                float next = i == _occupied.Count ? right.yMax : _occupied[i].yMin;

                if (next - cursor > bestTop - bestBottom)
                {
                    bestBottom = cursor;
                    bestTop = next;
                }
                if (i < _occupied.Count)
                {
                    cursor = Mathf.Max(cursor, _occupied[i].yMax);
                }
            }
            float padding = right.height * 0.018f;
            float height = Mathf.Min(right.height * 0.29f, Mathf.Max(0f, bestTop - bestBottom - padding * 2f));
            float center = Mathf.Clamp(right.yMin + right.height * 0.50f, bestBottom + padding + height * 0.5f, bestTop - padding - height * 0.5f);
            return new Rect(right.center.x - right.width * 0.49f, center - height * 0.5f, right.width * 0.98f, height);
        }
        /// <summary>只读实际按钮和状态面板的矩形；停用按钮不占空间，不通过猜测旧版锚点来避让。</summary>
        private void CollectOccupied(RectTransform root, Rect right, Component panel)
        {
            if (panel == null)
            {
                return;
            }
            _controls.Clear();
            panel.GetComponentsInChildren<RectTransform>(false, _controls);
            foreach (var rect in _controls)
            {
                if (!rect.gameObject.activeInHierarchy || (rect.GetComponent<Button>() == null && rect.name != "infoPlate"))
                {
                    continue;
                }
                rect.GetWorldCorners(_corners);
                Vector2 min = (Vector2)root.InverseTransformPoint(_corners[0]) + Vector2.Scale(root.rect.size, root.pivot);
                Vector2 max = (Vector2)root.InverseTransformPoint(_corners[2]) + Vector2.Scale(root.rect.size, root.pivot);

                if (max.x <= right.xMin || min.x >= right.xMax || max.y <= right.yMin || min.y >= right.yMax)
                {
                    continue;
                }
                _occupied.Add(Rect.MinMaxRect(min.x, Mathf.Max(min.y, right.yMin), max.x, Mathf.Min(max.y, right.yMax)));
            }
        }
        private static Transform FindTarget(Transform parent, string name)
        {
            if (parent.name == name)
            {
                return parent;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindTarget(parent.GetChild(i), name);

                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }
        protected override void OnClose()
        {
            StopEffects();
        }
        protected override void OnRelease()
        {
            StopEffects();
        }
        private void StopEffects()
        {
            RevTimer.CancelAllOf(this);
        }
    }
}
