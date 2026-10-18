// ============================================================
// HomePanel.cs —— 主界面入口。
// 界面不负责开战斗域，只向唯一的 GameFlow 提交「进入选关」请求。
// ============================================================
using RevGame.Flow;
using Revolution;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]
    public sealed class HomePanel : RevUIPanel
    {
        [RevBind]
        private Button _btnPlay;
        protected override void OnBindView()
        {
            _btnPlay.onClick.AddListener(OnPlay);
        }

        private void OnPlay()
        {
            RevServices.GetRequired<GameFlow>().OpenLevels();
        }

        protected override void OnRelease()
        {
            _btnPlay.onClick.RemoveListener(OnPlay);
        }
    }
}
