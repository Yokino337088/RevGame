using DG.Tweening;
using RevGame.Flow;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>独立决斗场景的表现消费者。屏幕刷新只读快照，插值和特效不会写回战斗世界。</summary>
    public sealed class PvpDuelView : MonoBehaviour
    {
        // First/Second 是角色画面；FirstHealth/SecondHealth 是血条填充；FirstEnergy/SecondEnergy 是能量条填充。
        public Image First, Second, FirstHealth, SecondHealth, FirstEnergy, SecondEnergy;
        // Clock 显示剩余秒数，Banner 显示就绪或胜负，Names 显示两席名称，两个 HealthText 显示生命数值。
        public Text Clock, Banner, Names, FirstHealthText, SecondHealthText;
        // Exit 返回大厅，Reconnect 重新连接；Jump、Jab、Skill、Ultimate 分别送出跳跃和三类招式意图。
        public Button Exit, Reconnect, Jump, Jab, Skill, Ultimate;
        // 黑白两席各自的 Legacy Animation 状态机。
        public DuelActorAnimator FirstAnimator, SecondAnimator;
        // 当前场景的只读招式表现消费者，由场景序列化注入。
        public DuelStrikeEffects Effects;
        // 最近一次消费命中或招式事件的帧号，避免同一快照重复播放。
        private int _seenFrame = -1;
        // 已播放的终局码，避免结算文字每渲染帧重复弹动。
        private int _presentedResult;
        // HUD 点按后的招式编号，由下一次输入采样取走。
        private int _clickedAction;
        // 方向按钮按住时的 -1、0、1，松手或离开时归零。
        private int _pointerDirection;
        // 待下一次 Update 消费的一次跳跃点按。
        private bool _clickedJump;
        // 黑白角色的当前显示坐标，浮点插值只用于画面。
        private Vector2 _firstPosition, _secondPosition;

        // 绑定场景按钮，只转发输入和流程请求。
        private void Start()
        {
            Exit.onClick.AddListener(ReturnHome);
            Reconnect.onClick.AddListener(ReconnectServer);
            Jab.onClick.AddListener(() => _clickedAction = 1);
            Skill.onClick.AddListener(() => _clickedAction = 2);
            Ultimate.onClick.AddListener(() => _clickedAction = 3);
            Jump.onClick.AddListener(() => _clickedJump = true);
        }

        // 把当前端点和昵称交回 PvP 服务，由连接流程决定能否续局。
        private void ReconnectServer()
        {
            var service = RevServices.GetRequired<PvpService>();
            service.StartServer(service.Endpoint, service.Nickname);
        }

        // 由方向按钮的按下、抬起和移出事件维护当前触摸方向。
        public void MovePointer(int direction)
        {
            _pointerDirection = direction;
        }

        // 交给统一 GameFlow 退房和换场景。
        private void ReturnHome()
        {
            RevServices.GetRequired<GameFlow>().ReturnHome();
        }

        // 每个表现帧采样键盘及触摸，交给业务服务缓存到下一逻辑帧。
        private void Update()
        {
            var service = RevServices.GetRequired<PvpService>();
            int direction = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? -1 :
                Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : _pointerDirection;
            int action = Input.GetKeyDown(KeyCode.J) ? 1 : Input.GetKeyDown(KeyCode.K) ? 2 :
                Input.GetKeyDown(KeyCode.L) ? 3 : _clickedAction;
            _clickedAction = 0;
            service.SetDuelInput(direction, action, _clickedJump || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow));
            _clickedJump = false;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReturnHome();
            }
        }

        // 按最新只读快照更新角色、动画、条形 HUD 和结算。命中特效按逻辑帧去重。
        private void LateUpdate()
        {
            var service = RevServices.GetRequired<PvpService>();
            DuelSnapshot view = service.DuelSnapshot;
            if (_seenFrame != view.Frame)
            {
                _seenFrame = view.Frame;
                Effects.Observe(0, view.FirstAction, view.FirstAge, view.FirstFacing, First.rectTransform.anchoredPosition);
                Effects.Observe(1, view.SecondAction, view.SecondAge, view.SecondFacing, Second.rectTransform.anchoredPosition);
                if (view.FirstHit != 0)
                {
                    Flash(Second);
                }
                if (view.SecondHit != 0)
                {
                    Flash(First);
                }
            }
            // 逻辑坐标是千分格。屏幕宽度换算和插值使用浮点，因为它们只影响绘制。
            var firstTarget = new Vector2(view.FirstX, view.FirstY) * .138f;
            var secondTarget = new Vector2(view.SecondX, view.SecondY) * .138f;
            float blend = 1 - Mathf.Exp(-22 * Time.unscaledDeltaTime);
            _firstPosition = Vector2.Lerp(_firstPosition, firstTarget, blend);
            _secondPosition = Vector2.Lerp(_secondPosition, secondTarget, blend);
            PaintActor(First, _firstPosition, view.FirstFacing);
            PaintActor(Second, _secondPosition, view.SecondFacing);
            FirstAnimator.Paint(view.FirstHealth, view.FirstY, view.FirstAction, Mathf.Abs(_firstPosition.x - firstTarget.x) > 4);
            SecondAnimator.Paint(view.SecondHealth, view.SecondY, view.SecondAction, Mathf.Abs(_secondPosition.x - secondTarget.x) > 4);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            FirstAnimator.ObserveSprite(First.sprite);
            SecondAnimator.ObserveSprite(Second.sprite);
#endif
            FirstHealth.fillAmount = Mathf.Clamp01(view.FirstHealth / (float)DuelConfig.Health);
            SecondHealth.fillAmount = Mathf.Clamp01(view.SecondHealth / (float)DuelConfig.Health);
            FirstEnergy.fillAmount = Mathf.Clamp01(view.FirstEnergy / (float)DuelConfig.Energy);
            SecondEnergy.fillAmount = Mathf.Clamp01(view.SecondEnergy / (float)DuelConfig.Energy);
            FirstHealthText.text = view.FirstHealth.ToString();
            SecondHealthText.text = view.SecondHealth.ToString();
            Names.text = service.BlackName + "                                      " + service.WhiteName;
            Clock.text = Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - view.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            bool disconnected = service.Mode != PvpConnectionMode.Local && !service.Connected;
            Reconnect.gameObject.SetActive(disconnected && service.Mode == PvpConnectionMode.Server && service.DuelReconnectAvailable);
            Banner.text = service.DuelError.Length != 0 ? service.DuelError :
                disconnected ? "连接中断 · 决斗等待同步" :
                !service.DuelBothReady ? "等待双方进入决斗场" :
                view.Result == 0 ? (view.Frame < 18 ? "开打！" : "") :
                view.Result == 3 ? "平局" : (view.Result == (int)service.MySide ? "决斗胜利" : "决斗落败");
            if (_presentedResult == 0 && view.Result != 0)
            {
                _presentedResult = view.Result;
                Banner.transform.DOPunchScale(Vector3.one * .16f, .4f).SetUpdate(true).SetLink(gameObject);
            }
        }

        // 更新屏幕位置和镜像方向，只修改 RectTransform。
        private static void PaintActor(Image actor, Vector2 position, int facing)
        {
            var rect = actor.rectTransform;
            rect.anchoredPosition = position;
            rect.localScale = new Vector3(facing < 0 ? -1 : 1, 1, 1);
        }

        // 命中时做一次受击着色，旧 Tween 先取消，避免重复叠加。
        private void Flash(Image image)
        {
            image.DOKill();
            image.color = new Color(1, .54f, .46f);
            image.DOColor(Color.white, .16f).SetUpdate(true).SetLink(gameObject);
        }
    }
}
