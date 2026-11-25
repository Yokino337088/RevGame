using System;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>大厅首先打开的四选一入口。未选择模式前不创建Socket、不加载对局场景。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed class PvpEntryPanel : RevUIPanel
    {
        /// <summary>首次装配时让漩涡铺满屏幕，菜单底板和控件仍使用设备安全框。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            BindTutorialChoice();
        }

        /// <summary>入口的转场接单锁，防止连点打开两个页面。</summary>
        private bool _entering;
        /// <summary>首次询问时隐藏的普通模式与设置按钮。</summary>
        private GameObject[] _modeButtons;
        /// <summary>教学确认窗与入口面板同寿命。</summary>
        private GameObject _tutorialChoice;
        /// <summary>在常规模式菜单保留的教学入口。</summary>
        private Button _tutorialReplay;
        [RevButtonClick("agentSettings")]
        private void AgentSettings()
        {
            PvpAgentSettingsPanel.OpenSettings();
        }

        [RevButtonClick("server")]
        private void Server()
        {
            Enter("Server");
        }
        [RevButtonClick("lan")]
        private void Lan()
        {
            Enter("Lan");
        }
        [RevButtonClick("computer")]
        private void Computer()
        {
            Enter("Computer");
        }
        [RevButtonClick("local")]
        private void Local()
        {
            Enter("Local");
        }

        /// <summary>只在大厅打开对战入口时询问首次玩家。</summary>
        protected override void OnOpen()
        {
            _entering = false;
            var flow = RevServices.GetRequired<GameFlow>();
            ShowTutorialChoice(flow.Screen == GameScreen.Home && flow.ShouldPromptPvpTutorial);
        }

        /// <summary>从既有菜单底板、字体及按钮拼装提示窗。各按钮只注册一次。</summary>
        private void BindTutorialChoice()
        {
            RectTransform veil = PvpTutorialControls.Rect("tutorialChoice", transform, 0, 0, 1, 1);
            _tutorialChoice = veil.gameObject;
            // 蒙层挡住下面的按钮。普通菜单也会隐藏，以免键盘焦点绕过询问。
            veil.gameObject.AddComponent<Image>().color = new Color(.07f, .09f, .13f, .82f);
            var plate = Instantiate(transform.Find("background").GetComponent<Image>(), veil, false);
            PvpTutorialControls.SetRect(plate.rectTransform, .15f, .12f, .85f, .88f);
            RectTransform popup = plate.rectTransform;
            var heading = Instantiate(transform.Find("heading").GetComponent<Text>(), popup, false);
            PvpTutorialControls.SetRect(heading.rectTransform, .12f, .71f, .88f, .87f);
            heading.text = "开始九路围棋";
            var subtitle = Instantiate(transform.Find("subtitle").GetComponent<Text>(), popup, false);
            PvpTutorialControls.SetRect(subtitle.rectTransform, .19f, .59f, .81f, .72f);
            subtitle.text = "可以先跟鸦鸦练几手，也可以直接选对战方式。";
            Button local = transform.Find("local").GetComponent<Button>();
            Button computer = transform.Find("computer").GetComponent<Button>();
            Button back = transform.Find("back").GetComponent<Button>();
            PvpTutorialControls.Button("tutorialBeginner", computer, popup, "跟鸦鸦学", .25f, .43f, .75f, .56f, OpenTutorial);
            PvpTutorialControls.Button("tutorialExpert", local, popup, "直接选对战方式", .25f, .28f, .75f, .41f, ChooseExpert);
            PvpTutorialControls.Button("tutorialLater", back, popup, "返回大厅", .37f, .13f, .63f, .24f, Back);
            _tutorialReplay = PvpTutorialControls.Button("tutorialReplay", back, transform,
                "围棋教学", .31f, .085f, .54f, .171f, OpenTutorial);
            _modeButtons = new[]
            {
                transform.Find("server").gameObject, transform.Find("lan").gameObject,
                computer.gameObject, local.gameObject, transform.Find("agentSettings").gameObject, back.gameObject
            };
            _tutorialChoice.SetActive(false);
        }

        /// <summary>在首次询问和常规模式菜单之间交割输入。</summary>
        private void ShowTutorialChoice(bool visible)
        {
            foreach (GameObject button in _modeButtons)
            {
                button.SetActive(!visible);
            }
            _tutorialReplay.gameObject.SetActive(!visible);
            _tutorialChoice.SetActive(visible);
            if (visible)
            {
                _tutorialChoice.transform.SetAsLastSibling();
            }
        }

        /// <summary>记录直接进入对战菜单的选择，之后仍可点围棋教学。</summary>
        private void ChooseExpert()
        {
            if (_entering)
            {
                return;
            }
            RevServices.GetRequired<GameFlow>().SkipPvpTutorial();
            ShowTutorialChoice(false);
        }

        /// <summary>将入口交给开场剧情，接单后关闭菜单。拒绝时保留原菜单供玩家重试。</summary>
        private void OpenTutorial()
        {
            if (_entering)
            {
                return;
            }
            var flow = RevServices.GetRequired<GameFlow>();
            _entering = flow.EnterPvpTutorial();
            if (_entering)
            {
                CloseSelf();
            }
            else
            {
                PvpNoticePanel.ShowMessage(flow.LastError);
            }
        }

        /// <summary>核对当前转场锁与首次询问，然后转发玩家选择的对战模式。</summary>
        private void Enter(string destination)
        {
            if (_entering || _tutorialChoice.activeSelf)
            {
                return;
            }
            var flow = RevServices.GetRequired<GameFlow>();
            if (flow.Screen == GameScreen.Pvp)
            {
                RevUI.Get<PvpPanel>().ShowDestination(destination);
                return;
            }
            _entering = flow.EnterPvp(destination);
            if (_entering)
            {
                CloseSelf();
            }
            else
            {
                PvpNoticePanel.ShowMessage(flow.LastError);
            }
        }

        /// <summary>关闭选择或返回大厅，关闭本次询问时保留未来的教学选择。</summary>
        [RevButtonClick("back")]
        private void Back()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (flow.Screen == GameScreen.Pvp)
            {
                flow.ReturnHome();
            }
            CloseSelf();
        }
    }

}
