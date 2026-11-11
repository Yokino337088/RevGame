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
        // 黑方角色图层，位置和镜像每次渲染时更新。
        public Image First;
        // 白方角色图层。场景配方在编辑器中绑定，运行时不重建。
        public Image Second;
        // 黑方已确认血量的条形填充。
        public Image FirstHealth;
        // 白方已确认血量的条形填充。
        public Image SecondHealth;
        // 黑方已确认能量的条形填充。
        public Image FirstEnergy;
        // 白方已确认能量的条形填充。
        public Image SecondEnergy;
        // 逻辑帧换算的剩余秒数。不限时时显示 ∞。
        public Text Clock;
        // 双方就绪、连接中断及权威胜负提示。
        public Text Banner;
        // 黑白两席的显示名称。
        public Text Names;
        // 黑方当前权威血量的整数文本。
        public Text FirstHealthText;
        // 白方当前权威血量的整数文本。
        public Text SecondHealthText;
        // 返回大厅入口，由 GameFlow 执行退房与切场景。
        public Button Exit;
        // 在线连接中断时供玩家手动重新连接。
        public Button Reconnect;
        // 跳跃按钮，点击只缓存一次输入边沿。
        public Button Jump;
        // 普攻按钮，对应原生招式槽 1。
        public Button Jab;
        // 技能按钮，对应原生招式槽 2。
        public Button Skill;
        // 技能二按钮，对应原生输入槽 4。
        public Button SkillTwo;
        // 大招按钮，对应原生招式槽 3。
        public Button Ultimate;
        // 黑方的表现状态机，消费跑动、起跳及权威招式姿态。
        public DuelActorAnimator FirstAnimator;
        // 白方的表现状态机，独立保存自己的播放与过渡进度。
        public DuelActorAnimator SecondAnimator;
        // 当前场景的只读招式表现消费者，由场景序列化注入。
        public DuelStrikeEffects Effects;
        // 双人擂台镜头，只消费已确认快照的坐标和招式事件。
        public DuelCameraRig CameraRig;
        // 黑方当前命中数标签，连段结束后隐藏文字。
        public Text FirstCombo;
        // 白方当前命中数标签。
        public Text SecondCombo;
        // 最近一次消费命中或招式事件的帧号，避免同一快照重复播放。
        private int _seenFrame = -1;
        // 已播放的终局码，避免结算文字每渲染帧重复弹动。
        private int _presentedResult;
        // HUD 点按后的招式编号，由下一次输入采样取走。
        private int _clickedAction;
        // 按住左移的 EventSystem 指针 ID。松开一根手指不能清掉其他触点。
        private readonly HashSet<int> _leftPointers = new HashSet<int>();
        // 按住右移的指针 ID。与左移同时按住时抵消为回中。
        private readonly HashSet<int> _rightPointers = new HashSet<int>();
        // 兼容已有场景配方里的旧持久监听。运行期绑定会用实际指针替换它。
        private const int LegacyPointerId = int.MinValue;
        // 待下一次 Update 消费的一次跳跃点按。
        private bool _clickedJump;
        // 千分格到 HUD 局部坐标的换算系数，权威和预测都用同一个比例。
        private const float ActorScale = .138f;
        // 小幅位置差在 85 毫秒内归零；跨越大半个预测窗时直接校正，避免拖着角色穿过场景。
        private const float CorrectionSeconds = .085f;
        // 大于 96 个 HUD 单位的纠偏不做缓动，通常来自掉线、跳帧或明显错误的猜测。
        private const float CorrectionSnapDistance = 96;
        // 上一个渲染帧是否由预测提供位置，用于检测何时退回权威插值。
        private bool _wasPredicting;
        // 上一次画出的本机坐标，用于把预测与权威的差值变成纯显示偏移。
        private Vector2 _lastLocalDrawn;
        // 正在衰减的显示偏移，单位为 HUD 局部坐标。
        private Vector2 _correctionOffset;
        // 当前纠偏剩余的显示秒数，不进入逻辑时钟。
        private float _correctionRemaining;
        // 已应用的校正版本，避免每个渲染帧都重新启动同一次纠偏。
        private int _seenCorrection;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 只有角色真正被绘制到权威地面之上时才记录预跳，便于自动化区分试算和落屏。
        internal bool SawPredictedJumpForCheck { get; private set; }
        // 原生确认本机离地后置位，保留到离场供跨帧断言读取。
        internal bool SawConfirmedJumpForCheck { get; private set; }
#endif

        // 绑定场景按钮，只转发输入和流程请求。
        private void Start()
        {
            RevGame.UI.GameMobileLayout.EnableSceneHud(transform,
                "Left", "Right", "Jump", "Jab", "Skill", "SkillTwo", "Ultimate", "Back", "Reconnect",
                "BlackHealth", "WhiteHealth", "BlackEnergy", "WhiteEnergy", "BlackHp", "WhiteHp",
                "FighterNames", "Clock", "TimerPlate", "Banner", "BlackCombo", "WhiteCombo");
            BindDirectionButton("Left", _leftPointers);
            BindDirectionButton("Right", _rightPointers);
            Exit.onClick.AddListener(ReturnHome);
            Reconnect.onClick.AddListener(ReconnectServer);
            Jab.onClick.AddListener(() => _clickedAction = 1);
            Skill.onClick.AddListener(() => _clickedAction = 2);
            SkillTwo.onClick.AddListener(() => _clickedAction = 4);
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

        // 场景退出或组件被禁用时，和失焦一样清除未发送按键与预测轨迹。
        private void OnDisable()
        {
            ClearHeldControls();
        }

        // 清除触控状态及本地预测。已发送的输入仍由权威处理，清理表现不会撤销它们。
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
                Input.GetKeyDown(KeyCode.L) ? 4 : Input.GetKeyDown(KeyCode.I) ? 3 : _clickedAction;
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
            // 镜头跟随两张已确认快照；预测只影响当前操作者的角色层坐标。
            float confirmedFirstX = firstPosition.x;
            float confirmedSecondX = secondPosition.x;
            float confirmedHeight = Mathf.Max(firstPosition.y, secondPosition.y);
            int firstAltitude = view.FirstY;
            int secondAltitude = view.SecondY;
            // 只替换自己这一席的运动表现。下方血量、出招、命中特效和结果继续消费原来的权威播放头。
            if (service.Mode != PvpConnectionMode.Local)
            {
                if (service.MySide == GoSide.Black)
                {
                    PaintLocalMotion(service, view.FirstAction, ref firstPosition, ref firstMove, ref firstFacing, ref firstAltitude);
                }
                else if (service.MySide == GoSide.White)
                {
                    PaintLocalMotion(service, view.SecondAction, ref secondPosition, ref secondMove, ref secondFacing, ref secondAltitude);
                }
            }
            if (_seenFrame != view.Frame)
            {
                // 正常跨帧时依序消费特效；重连或长停顿后只保留最近四帧的短时表现。
                for (int frame = Mathf.Max(_seenFrame + 1, view.Frame - 3); frame <= view.Frame; frame++)
                {
                    PaintDuelEvents(service.DuelDisplay.Get(frame), frame > 0 ? service.DuelDisplay.Get(frame - 1) : default);
                }
                _seenFrame = view.Frame;
                FirstHealth.fillAmount = Mathf.Clamp01(view.FirstHealth / (float)DuelConfig.Health);
                SecondHealth.fillAmount = Mathf.Clamp01(view.SecondHealth / (float)DuelConfig.Health);
                FirstEnergy.fillAmount = Mathf.Clamp01(view.FirstEnergy / (float)DuelConfig.Energy);
                SecondEnergy.fillAmount = Mathf.Clamp01(view.SecondEnergy / (float)DuelConfig.Energy);
                FirstHealthText.text = view.FirstHealth.ToString();
                SecondHealthText.text = view.SecondHealth.ToString();
                Names.text = "绯焰  ·  " + service.BlackName + "                           " + service.WhiteName + "  ·  绯焰";
                FirstCombo.text = view.FirstCombo > 1 ? view.FirstCombo + " HIT" : "";
                SecondCombo.text = view.SecondCombo > 1 ? view.SecondCombo + " HIT" : "";
                Clock.text = DuelConfig.FrameLimit == 0
                    ? "∞"
                    : Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - view.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            }
            PaintActor(First, firstPosition, firstFacing);
            PaintActor(Second, secondPosition, secondFacing);
            CameraRig.Paint(confirmedFirstX, confirmedSecondX, confirmedHeight, Time.unscaledDeltaTime);
            FirstAnimator.Paint(view.FirstHealth, firstAltitude, view.FirstAction, view.FirstStun, firstMove != 0, view.Frame, view.FirstAge, fraction);
            SecondAnimator.Paint(view.SecondHealth, secondAltitude, view.SecondAction, view.SecondStun, secondMove != 0, view.Frame, view.SecondAge, fraction);
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

        // 把预测修正限制在本机的绘制坐标。转向立即换姿态，收到不同结果时位置在很短时间内归位。
        private void PaintLocalMotion(PvpService service, int confirmedAction, ref Vector2 position, ref int move, ref int facing, ref int altitude)
        {
            float delta = Time.unscaledDeltaTime;
            bool predicting = service.TrySampleDuelMotion(delta, out DuelPredictedMotion predicted);
            if (predicting)
            {
                position = new Vector2(predicted.X, predicted.Y) * ActorScale;
                if (confirmedAction == 0)
                {
                    move = predicted.Movement;
                    altitude = Mathf.RoundToInt(predicted.Y);
                    if (move != 0)
                    {
                        facing = move;
                    }
                }
            }

            if (_wasPredicting && (!predicting || _seenCorrection != predicted.CorrectionVersion))
            {
                // 用上一帧已经显示的位置起步，禁止把偏移喂回预测或权威副本。
                Vector2 difference = _lastLocalDrawn - position;
                _correctionOffset = difference.sqrMagnitude < CorrectionSnapDistance * CorrectionSnapDistance
                    ? difference
                    : Vector2.zero;
                _correctionRemaining = CorrectionSeconds;
            }
            _correctionRemaining = Mathf.Max(0, _correctionRemaining - delta);
            position += _correctionOffset * (_correctionRemaining / CorrectionSeconds);
            _lastLocalDrawn = position;
            _wasPredicting = predicting;
            _seenCorrection = predicted.CorrectionVersion;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            DuelSnapshot confirmed = service.DuelSnapshot;
            int confirmedHeight = service.MySide == GoSide.Black ? confirmed.FirstY : confirmed.SecondY;
            SawPredictedJumpForCheck |= predicting && confirmedHeight == 0 && predicted.Y > 0 && position.y > 0;
            SawConfirmedJumpForCheck |= confirmedHeight > 0;
#endif
        }

        // 命中特效和受击闪光读取同一逻辑帧，且与渲染频率无关。
        private void PaintDuelEvents(DuelSnapshot view, DuelSnapshot previous)
        {
            Effects.Observe(0, view.FirstAction, view.Frame - view.FirstAge, view.FirstFacing,
                new Vector2(view.FirstX, view.FirstY) * ActorScale);
            Effects.Observe(1, view.SecondAction, view.Frame - view.SecondAge, view.SecondFacing,
                new Vector2(view.SecondX, view.SecondY) * ActorScale);
            if ((view.FirstAction == 3 && view.FirstAge == 0) || (view.SecondAction == 3 && view.SecondAge == 0))
            {
                CameraRig.FocusUltimate();
            }
            if (view.FirstHit != 0)
            {
                // 同帧对打可能已打断出招者。前一帧仍持有命中的动作编号。
                int action = view.FirstAction != 0 ? view.FirstAction : Mathf.Max(1, previous.FirstAction);
                Effects.Hit(0, action, view.FirstFacing, new Vector2(view.SecondX, view.SecondY) * ActorScale);
                CameraRig.Hit(action, view.FirstFacing);
                Flash(Second);
            }
            if (view.SecondHit != 0)
            {
                int action = view.SecondAction != 0 ? view.SecondAction : Mathf.Max(1, previous.SecondAction);
                Effects.Hit(1, action, view.SecondFacing, new Vector2(view.FirstX, view.FirstY) * ActorScale);
                CameraRig.Hit(action, view.SecondFacing);
                Flash(First);
            }
        }

        // 逻辑坐标用千分格保存，只有绘制时才换算到屏幕单位。
        private static Vector2 ActorPosition(int x, int y, int nextX, int nextY, float fraction)
        {
            return new Vector2(Mathf.Lerp(x, nextX, fraction), Mathf.Lerp(y, nextY, fraction)) * ActorScale;
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
