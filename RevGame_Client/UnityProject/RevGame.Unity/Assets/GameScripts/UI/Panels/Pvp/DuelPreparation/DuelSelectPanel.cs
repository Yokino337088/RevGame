using DG.Tweening;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>双人确认鸦鸦参战。按钮只发送意图，锁定标志和倒计时由权威回执驱动。</summary>
    [RevUIPanel("UI/Panel/Pvp/DuelPrep", RevUILayer.Top, InBackStack = false, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class DuelSelectPanel : DuelPrepPanelBase
    {
        [RevBind("layout/timer")] private Text _timer; // 权威剩余秒数；双方就绪前显示等待符号。
        [RevBind("layout/prompt")] private Text _prompt; // 选角或等待另一席的文字提示。
        [RevBind("layout/confirm")] private Button _confirm; // 确认本席已准备好使用鸦鸦。
        private int _paintedSeconds = -1; // 仅在数字跳变时触发一次提示动效。

        // 提交本席的鸦鸦确认。另一席独立确认后才进入加载。
        [RevButtonClick("layout/confirm")]
        private void Confirm()
        {
            RevServices.GetRequired<PvpService>().SelectDuelRole(DuelRole.Yaya, true);
        }

        // 正式服务通知对手撤销准备，流程负责退出。
        [RevButtonClick("layout/cancel")]
        private void Cancel()
        {
            RevServices.GetRequired<PvpService>().AbortDuelPreparation();
        }

        // 按权威阶段刷新确认按钮和倒计时，倒计时变更时才重播提醒。
        protected override void PaintCenter()
        {
            bool choosing = Data.State?.phase == DuelPrepPhase.Choosing;
            _timer.text = choosing ? Data.Seconds.ToString("00") : "—";
            bool urgent = choosing && Data.Seconds <= 5;
            _timer.color = urgent ? new Color(1f, .5f, .36f) : new Color(1f, .87f, .61f);
            if (urgent && _paintedSeconds != Data.Seconds)
            {
                _timer.rectTransform.DOKill();
                _timer.rectTransform.localScale = Vector3.one;
                _timer.rectTransform.DOPunchScale(Vector3.one * .12f, .24f, 1).SetUpdate(true);
            }
            _paintedSeconds = Data.Seconds;
            _prompt.text = !choosing ? "等待双方进入准备页" : Data.CanPick ? "双方使用鸦鸦" : "等待对方确认";
            _confirm.interactable = Data.CanPick;
        }

        // 停掉最后几秒的数字弹跳，随后清理共用席位动画。
        protected override void OnClose()
        {
            _timer.rectTransform.DOKill();
            base.OnClose();
        }
    }
}
