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
        public Image First, Second, FirstHealth, SecondHealth, FirstEnergy, SecondEnergy;
        public Text Clock, Banner, Names, FirstHealthText, SecondHealthText;
        public Button Exit, Reconnect, Jump, Jab, Skill, Ultimate;
        public DuelActorAnimator FirstAnimator, SecondAnimator;
        public DuelStrikeEffects Effects;
        private int _seenFrame = -1;
        private int _presentedResult;
        private int _clickedAction;
        private int _pointerDirection;
        private bool _clickedJump;
        private Vector2 _firstPosition, _secondPosition;

        private void Start()
        {
            Exit.onClick.AddListener(ReturnHome);
            Reconnect.onClick.AddListener(ReconnectServer);
            Jab.onClick.AddListener(() => _clickedAction = 1);
            Skill.onClick.AddListener(() => _clickedAction = 2);
            Ultimate.onClick.AddListener(() => _clickedAction = 3);
            Jump.onClick.AddListener(() => _clickedJump = true);
        }

        private void ReconnectServer()
        {
            var service = RevServices.GetRequired<PvpService>();
            service.StartServer(service.Endpoint, service.Nickname);
        }

        public void MovePointer(int direction)
        {
            _pointerDirection = direction;
        }

        private void ReturnHome()
        {
            RevServices.GetRequired<GameFlow>().ReturnHome();
        }

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

        private static void PaintActor(Image actor, Vector2 position, int facing)
        {
            var rect = actor.rectTransform;
            rect.anchoredPosition = position;
            rect.localScale = new Vector3(facing < 0 ? -1 : 1, 1, 1);
        }

        private void Flash(Image image)
        {
            image.DOKill();
            image.color = new Color(1, .54f, .46f);
            image.DOColor(Color.white, .16f).SetUpdate(true).SetLink(gameObject);
        }
    }
}
