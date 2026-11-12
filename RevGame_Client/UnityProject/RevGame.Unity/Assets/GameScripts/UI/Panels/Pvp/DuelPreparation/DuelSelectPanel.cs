using DG.Tweening;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>双人实时选角。按钮只发送意图，锁定标志和倒计时由权威回执驱动。</summary>
    [RevUIPanel("UI/Panel/Pvp/DuelPrep", RevUILayer.Top, InBackStack = false, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class DuelSelectPanel : DuelPrepPanelBase
    {
        [RevBind("layout/timer")] private Text _timer; // 权威剩余秒数；双方就绪前显示等待符号。
        [RevBind("layout/prompt")] private Text _prompt; // 选角或等待另一席的文字提示。
        [RevBind("layout/roster/ember")] private Button _ember; // 绯焰角色卡。
        [RevBind("layout/roster/yaya")] private Button _yaya; // 鸦鸦角色卡。
        [RevBind("layout/roster/ember/seats")] private Text _emberSeats; // 该卡上当前两席的选择。
        [RevBind("layout/roster/yaya/seats")] private Text _yayaSeats;
        [RevBind("layout/confirm")] private Button _confirm; // 锁定本席最后一次预选。
        private int _paintedSeconds = -1; // 仅在数字跳变时触发一次提示动效。

        // 由服务消费同一条点击入口，面板不拥有角色选择。
        [RevButtonClick("layout/roster/ember")]
        private void PickEmber()
        {
            RevServices.GetRequired<PvpService>().SelectDuelRole(DuelRole.Ember, false);
        }

        // 选鸦鸦不影响对手，也不会抢占该角色。
        [RevButtonClick("layout/roster/yaya")]
        private void PickYaya()
        {
            RevServices.GetRequired<PvpService>().SelectDuelRole(DuelRole.Yaya, false);
        }

        // 确认携带本席预选，快速连点时也不依赖旧的网络回执。
        [RevButtonClick("layout/confirm")]
        private void Confirm()
        {
            var service = RevServices.GetRequired<PvpService>();
            service.SelectDuelRole(service.DuelPreviewFor(Data.LocalSeat), true);
        }

        // 正式服务通知对手撤销准备，流程负责退出。
        [RevButtonClick("layout/cancel")]
        private void Cancel()
        {
            RevServices.GetRequired<PvpService>().AbortDuelPreparation();
        }

        // 仅刷新允许状态、角色卡上的两席标志和倒计时。
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
            _prompt.text = !choosing ? "等待双方进入选角" : Data.CanPick ? "点选角色 · 确认后锁定" : "等待对方确认";
            _ember.interactable = _yaya.interactable = _confirm.interactable = Data.CanPick;
            _emberSeats.text = Seats(DuelRole.Ember);
            _yayaSeats.text = Seats(DuelRole.Yaya);
            DuelRole own = Data.LocalSeat == 1 ? Data.Black : Data.White;
            _ember.image.color = own == DuelRole.Ember ? Color.white : new Color(.69f, .73f, .77f);
            _yaya.image.color = own == DuelRole.Yaya ? Color.white : new Color(.69f, .73f, .77f);
        }

        // 同角可同时显示红色黑方和青色白方，色弱玩家仍可按编号辨认。
        private string Seats(DuelRole role)
        {
            string first = Data.Black == role ? "<color=#FFAE94>1P 黑方</color>" : "";
            string second = Data.White == role ? "<color=#8DEDE3>2P 白方</color>" : "";
            return first + "    " + second;
        }

        // 停掉最后几秒的数字弹跳，随后清理共用席位动画。
        protected override void OnClose()
        {
            _timer.rectTransform.DOKill();
            base.OnClose();
        }
    }
}
