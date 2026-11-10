using System.Collections.Generic;
using DG.Tweening;
using RevGame.Flow;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
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
        // 以 EventSystem 的指针 ID 区分同一按钮上的多根手指。
        private readonly HashSet<int> _leftPointers = new HashSet<int>();
        private readonly HashSet<int> _rightPointers = new HashSet<int>();
        // 兼容已有场景配方里的旧持久监听。运行期绑定会用实际指针替换它。
        private const int LegacyPointerId = int.MinValue;
        // 待下一次 Update 消费的一次跳跃点按。
        private bool _clickedJump;

        // 绑定场景按钮，只转发输入和流程请求。
        private void Start()
        {
            RevGame.UI.GameMobileLayout.EnableSceneHud(transform,
                "Left", "Right", "Jump", "Jab", "Skill", "Ultimate", "Back", "Reconnect",
                "BlackHealth", "WhiteHealth", "BlackEnergy", "WhiteEnergy", "BlackHp", "WhiteHp",
                "FighterNames", "Clock", "TimerPlate", "Banner");
            BindDirectionButton("Left", _leftPointers);
            BindDirectionButton("Right", _rightPointers);
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

        // 保留旧场景配方的方向绑定入口。正式运行时由 Start 更换为带指针 ID 的事件。
        public void MovePointer(int direction)
        {
            if (direction != 0)
            {
                (direction < 0 ? _leftPointers : _rightPointers).Add(LegacyPointerId);
            }
        }

        // 旧序列化回调与按下入口成对保留，便于已有场景继续导入。
        public void ReleasePointer(int direction)
        {
            if (direction == 0)
            {
                return;
            }
            (direction < 0 ? _leftPointers : _rightPointers).Remove(LegacyPointerId);
        }

        // 重用场景中已有方向按钮的事件触发器。攻击和跳跃继续走 Button.onClick。
        private void BindDirectionButton(string path, HashSet<int> pointers)
        {
            var button = transform.Find(path)?.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogError("决斗方向按钮缺少绑定：" + path, this);
                return;
            }
            var trigger = button.GetComponent<EventTrigger>() ?? button.gameObject.AddComponent<EventTrigger>();
            trigger.triggers.RemoveAll(entry => entry.eventID == EventTriggerType.PointerDown ||
                entry.eventID == EventTriggerType.PointerUp || entry.eventID == EventTriggerType.PointerExit);
            AddDirectionEvent(trigger, EventTriggerType.PointerDown, pointer =>
            {
                if (pointer.button == PointerEventData.InputButton.Left && button.IsInteractable())
                {
                    pointers.Add(pointer.pointerId);
                }
            });
            AddDirectionEvent(trigger, EventTriggerType.PointerUp, pointer => pointers.Remove(pointer.pointerId));
            AddDirectionEvent(trigger, EventTriggerType.PointerExit, pointer => pointers.Remove(pointer.pointerId));
        }

        // 保留 Unity 每根手指的原始事件数据，释放时只删除该触点。
        private static void AddDirectionEvent(EventTrigger trigger, EventTriggerType kind, System.Action<PointerEventData> listener)
        {
            var entry = new EventTrigger.Entry { eventID = kind, callback = new EventTrigger.TriggerEvent() };
            entry.callback.AddListener(data =>
            {
                if (data is PointerEventData pointer)
                {
                    listener(pointer);
                }
            });
            trigger.triggers.Add(entry);
        }

        // 窗口失焦时可能收不到 PointerUp。清除本地按住状态，回到窗口后重新接收输入。
        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                ClearHeldControls();
            }
        }

        // Android 切到后台时不保证先收到失焦或抬起事件。
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                ClearHeldControls();
            }
        }

        private void OnDisable()
        {
            ClearHeldControls();
        }

        // 只清还没封帧的意图。已经发送的帧仍沿原链路等待权威确认。
        private void ClearHeldControls()
        {
            _leftPointers.Clear();
            _rightPointers.Clear();
            _clickedJump = false;
            _clickedAction = 0;
            RevServices.Get<PvpService>()?.ClearDuelUnsealedInput();
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
            bool left = _leftPointers.Count > 0 || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            bool right = _rightPointers.Count > 0 || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
            int direction = (right ? 1 : 0) - (left ? 1 : 0);
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

        // 在独立播放时钟上取样。到包可以批量推进原生世界，画面仍按相邻已确认帧插值。
        private void LateUpdate()
        {
            var service = RevServices.GetRequired<PvpService>();
            if (!service.DuelDisplay.Sample(Time.unscaledDeltaTime, service.Mode != PvpConnectionMode.Local,
                out DuelSnapshot view, out DuelSnapshot next, out float fraction))
            {
                return;
            }
            int firstMove = next.FirstX.CompareTo(view.FirstX);
            int secondMove = next.SecondX.CompareTo(view.SecondX);
            if (next.Frame == view.Frame && view.Frame > 0 && view.Result == 0)
            {
                // 到达连续上界时位置停在最后一帧，姿态继续沿用已确认的运动，避免每次等包都切回待机。
                DuelSnapshot previous = service.DuelDisplay.Get(view.Frame - 1);
                firstMove = view.FirstX.CompareTo(previous.FirstX);
                secondMove = view.SecondX.CompareTo(previous.SecondX);
            }
            // 出招按权威朝向绘制。跑动朝向取自正在播放的位移区间，松手或转向时不会被旧目标拉住。
            int firstFacing = view.FirstAction == 0 && firstMove != 0 ? firstMove : view.FirstFacing;
            int secondFacing = view.SecondAction == 0 && secondMove != 0 ? secondMove : view.SecondFacing;
            Vector2 firstPosition = ActorPosition(view.FirstX, view.FirstY, next.FirstX, next.FirstY, fraction);
            Vector2 secondPosition = ActorPosition(view.SecondX, view.SecondY, next.SecondX, next.SecondY, fraction);
            if (_seenFrame != view.Frame)
            {
                // 正常跨帧时依序消费特效；重连或长停顿后只保留最近四帧的短时表现。
                for (int frame = Mathf.Max(_seenFrame + 1, view.Frame - 3); frame <= view.Frame; frame++)
                {
                    PaintDuelEvents(service.DuelDisplay.Get(frame));
                }
                _seenFrame = view.Frame;
                FirstHealth.fillAmount = Mathf.Clamp01(view.FirstHealth / (float)DuelConfig.Health);
                SecondHealth.fillAmount = Mathf.Clamp01(view.SecondHealth / (float)DuelConfig.Health);
                FirstEnergy.fillAmount = Mathf.Clamp01(view.FirstEnergy / (float)DuelConfig.Energy);
                SecondEnergy.fillAmount = Mathf.Clamp01(view.SecondEnergy / (float)DuelConfig.Energy);
                FirstHealthText.text = view.FirstHealth.ToString();
                SecondHealthText.text = view.SecondHealth.ToString();
                Names.text = service.BlackName + "                                      " + service.WhiteName;
                Clock.text = DuelConfig.FrameLimit == 0
                    ? "∞"
                    : Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - view.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            }
            PaintActor(First, firstPosition, firstFacing);
            PaintActor(Second, secondPosition, secondFacing);
            FirstAnimator.Paint(view.FirstHealth, view.FirstY, view.FirstAction, firstMove != 0, view.Frame, view.FirstAge, fraction);
            SecondAnimator.Paint(view.SecondHealth, view.SecondY, view.SecondAction, secondMove != 0, view.Frame, view.SecondAge, fraction);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            FirstAnimator.ObserveSprite(First.sprite);
            SecondAnimator.ObserveSprite(Second.sprite);
#endif
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

        // 命中特效和受击闪光读取同一逻辑帧，且与渲染频率无关。
        private void PaintDuelEvents(DuelSnapshot view)
        {
            Effects.Observe(0, view.FirstAction, view.Frame - view.FirstAge, view.FirstFacing,
                new Vector2(view.FirstX, view.FirstY) * .138f);
            Effects.Observe(1, view.SecondAction, view.Frame - view.SecondAge, view.SecondFacing,
                new Vector2(view.SecondX, view.SecondY) * .138f);
            if (view.FirstHit != 0)
            {
                Flash(Second);
            }
            if (view.SecondHit != 0)
            {
                Flash(First);
            }
        }

        // 逻辑坐标用千分格保存，只有绘制时才换算到屏幕单位。
        private static Vector2 ActorPosition(int x, int y, int nextX, int nextY, float fraction)
        {
            return new Vector2(Mathf.Lerp(x, nextX, fraction), Mathf.Lerp(y, nextY, fraction)) * .138f;
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
