// 性能面板只显示数据；采样、快捷键和测试会话由启动宿主的诊断服务管理。
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    public sealed class PerformanceMonitorFrame
    {
        public string Summary, Details;
    }

    [RevUIPanel("UI/Panel/Diagnostics", RevUILayer.Top, CacheMode = RevUICacheMode.DestroyOnClose, Mask = RevUIMaskMode.None, InBackStack = false)]
    public sealed class PerformanceMonitorPanel : RevUIPanel<PerformanceMonitorFrame>
    {
        [RevBind] private Text _summary;
        [RevBind] private Text _details;
        [RevBind] private RectTransform _card;
        private bool _expanded = true;

        protected override void OnBindView()
        {
            // 控件由真实Prefab绑定；这里不采样或访问业务服务。
        }

        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            _summary.text = Data.Summary;
            _details.text = Data.Details;
            _details.gameObject.SetActive(_expanded);
            _card.sizeDelta = new Vector2(620, _expanded ? 810 : 145);
        }

        [RevButtonClick("btnFold")]
        private void Fold()
        {
            _expanded = !_expanded;
            RefreshView();
        }

        [RevButtonClick("btnClose")]
        private void CloseMonitor()
        {
            CloseSelf();
        }
    }
}
