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
        // HUD 设计框，等比适配屏幕安全区。
        public RectTransform Hud;
        // 桌面键盘图例所在的底板，触屏设备只显示圆形操作键。
        public GameObject KeyboardGuide;
        // 桌面键盘说明里的文字组件。确定本机座位后写入对应角色的技能名。
        public Text KeyboardLegend;
        // 依次对应 K、L、I 的三个圆键文字。
        public Text[] SkillCaptions;
        // 依次对应 J、K、L、I 的四个圆键图标。
        public Image[] SkillGlyphs;
        // 黑方按钮使用绯焰招式图标，顺序与 SkillGlyphs 一致。
        public Sprite[] EmberSkillGlyphs;
        // 白方按钮使用鸦鸦招式图标，切换席位只改变显示。
        public Sprite[] YayaSkillGlyphs;
        // 短时按键回执，说明当前输入、受击或能量状态。
        public Text InputFeedback;
        // 黑方玩家名，独立排版以限制过长昵称。
        public Text FirstName;
        // 白方玩家名。
        public Text SecondName;
        // 白方血条旁的头像组件。
        public Image SecondPortrait;
        // 头像可显示的五张原画差分，顺序为待机、专注、微笑、受击、倒地。
        public Sprite[] YayaPortraits;
        // 左手圆形摇杆，移动方向与键盘共用同一输入缓冲。
        public DuelMoveStick MoveStick;
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
        // 待下一次 Update 消费的一次跳跃点按。
        private bool _clickedJump;
        // 输入提示结束的本地非缩放时间。
        private float _inputFeedbackUntil;
        // 最近一次绘制按钮说明的座位，避免每次刷新 HUD 都重设图标和字形。
        private GoSide _hudSeat;
        // 最后使用的表情索引。只有已确认状态发生变化时才更换头像。
        private int _yayaExpression = -1;
        // 千分格到 HUD 局部坐标的换算系数，权威和预测都用同一个比例。
        private const float ActorScale = .138f;
        // 两个站位小于一步的距离时保持已有镜像，防止重叠或相互穿过时连续翻面。
        private const float FacingHoldDistance = 140 * ActorScale;
        // 当前真正显示的黑方朝向。
        private int _firstDrawFacing = 1;
        // 当前真正显示的白方朝向。
        private int _secondDrawFacing = -1;
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
        // 实际 HUD 回调进入次数，用来检查一次鼠标按下与抬起只产生一次意图。
        internal int HudPressCountForCheck { get; private set; }
        // 最近一次 HUD 操作的招式槽。0 是跳跃。
        internal int LastHudActionForCheck { get; private set; }
#endif

        // 绑定场景按钮，只转发输入和流程请求。
        private void Start()
        {
            RevGame.UI.GameMobileLayout.EnableDuelHud(Hud);
            KeyboardGuide.SetActive(!UnityEngine.Device.Application.isMobilePlatform);
            Exit.onClick.AddListener(ReturnHome);
            Reconnect.onClick.AddListener(ReconnectServer);
            Jab.onClick.AddListener(() => QueueHudPress(1));
            Skill.onClick.AddListener(() => QueueHudPress(2));
            SkillTwo.onClick.AddListener(() => QueueHudPress(4));
            Ultimate.onClick.AddListener(() => QueueHudPress(3));
            Jump.onClick.AddListener(() => QueueHudPress(0));
        }

        // 只缓存 UGUI 按下事件，Update 仍负责与键盘合并并调用业务入口。
        private void QueueHudPress(int action)
        {
            if (action == 0)
            {
                _clickedJump = true;
            }
            else
            {
                _clickedAction = action;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            HudPressCountForCheck++;
            LastHudActionForCheck = action;
#endif
        }

        // 把当前端点和昵称交回 PvP 服务，由连接流程决定能否续局。
        private void ReconnectServer()
        {
            var service = RevServices.GetRequired<PvpService>();
            service.StartServer(service.Endpoint, service.Nickname);
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
            MoveStick?.ResetInput();
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
            bool left = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            bool right = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
            int keyboard = (right ? 1 : 0) - (left ? 1 : 0);
            // 键盘和触控都输出同一种离散意图。反向同时按住时归中。
            int direction = Mathf.Clamp(keyboard + MoveStick.Direction, -1, 1);
            int action = Input.GetKeyDown(KeyCode.J) ? 1 : Input.GetKeyDown(KeyCode.K) ? 2 :
                Input.GetKeyDown(KeyCode.L) ? 4 : Input.GetKeyDown(KeyCode.I) ? 3 : _clickedAction;
            _clickedAction = 0;
            bool jump = _clickedJump || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow);
            service.SetDuelInput(direction, action, jump);
            if (action != 0 || jump)
            {
                ShowInputFeedback(service, action, jump);
            }
            else if (_inputFeedbackUntil != 0 && Time.unscaledTime >= _inputFeedbackUntil)
            {
                _inputFeedbackUntil = 0;
                InputFeedback.text = "";
            }
            _clickedJump = false;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReturnHome();
            }
        }

        // 及时说明按键被接收或受什么条件限制。具体出招和命中仍等权威帧。
        private void ShowInputFeedback(PvpService service, int action, bool jump)
        {
            DuelSnapshot state = service.DuelSnapshot;
            bool black = service.MySide == GoSide.Black;
            string name = ActionCaption(action, !black);
            int energy = black ? state.FirstEnergy : state.SecondEnergy;
            int cost = action == 2 ? DuelConfig.SkillCost : action == 3 ? DuelConfig.UltimateCost : action == 4 ? DuelConfig.SkillTwoCost : 0;
            int stun = black ? state.FirstStun : state.SecondStun;
            bool jumpBlocked = jump && ((black ? state.FirstY : state.SecondY) > 0 ||
                (black ? state.FirstAction : state.SecondAction) != 0);
            string message = "已输入";
            if (state.Result != 0)
            {
                message = "对局已结束";
            }
            else if (!service.DuelBothReady || service.DuelError.Length != 0 ||
                (service.Mode != PvpConnectionMode.Local && !service.Connected))
            {
                message = "等待对局就绪";
            }
            else if (energy < cost)
            {
                message = $"能量不足，需 {cost}";
            }
            else if (action == 0 && service.Mode != PvpConnectionMode.Local && (stun > 0 || jumpBlocked))
            {
                message = "暂不能起跳";
            }
            else if (stun > 0 || (action == 0 ? jumpBlocked : !state.CanTakeAction(black, action)))
            {
                message = "已缓冲";
            }
            InputFeedback.text = name + "  ·  " + message;
            _inputFeedbackUntil = Time.unscaledTime + .85f;
        }

        // 确认席位后更新按键说明，黑白两边的同一按键继续提交相同的原生动作号。
        private void PaintSeatControls(GoSide side)
        {
            if (_hudSeat == side || side == GoSide.Empty || KeyboardLegend == null)
            {
                return;
            }
            _hudSeat = side;
            bool yaya = side == GoSide.White;
            Sprite[] glyphs = yaya ? YayaSkillGlyphs : EmberSkillGlyphs;
            SkillCaptions[0].text = ActionCaption(2, yaya);
            SkillCaptions[1].text = ActionCaption(4, yaya);
            SkillCaptions[2].text = ActionCaption(3, yaya);
            for (int i = 0; i < SkillGlyphs.Length; i++)
            {
                SkillGlyphs[i].sprite = glyphs[i];
            }
            KeyboardLegend.text = yaya
                ? "A/D 移动    SPACE 跳跃    J 普攻    K 鸦袭    L 掠羽    I 鸦宴"
                : "A/D 移动    SPACE 跳跃    J 普攻    K 贯枪    L 坠枪    I 赤阵";
        }

        // 动作名只影响本机显示，技能能量与取消窗口由原生动作槽决定。
        private static string ActionCaption(int action, bool yaya)
        {
            return action switch
            {
                1 => "普攻",
                2 => yaya ? "鸦袭" : "贯枪",
                3 => yaya ? "鸦宴" : "赤阵",
                4 => yaya ? "掠羽" : "坠枪",
                _ => "跳跃"
            };
        }

        // 在独立播放时钟上取样。到包可以批量推进原生世界，画面仍按相邻已确认帧插值。
        private void LateUpdate()
        {
            var service = RevServices.GetRequired<PvpService>();
            PaintSeatControls(service.MySide);
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
                FirstName.text = "绯焰  /  " + service.BlackName;
                SecondName.text = service.WhiteName + "  /  鸦鸦";
                PaintYayaExpression(view, secondMove != 0);
                FirstCombo.text = view.FirstCombo > 1 ? view.FirstCombo + " HIT" : "";
                SecondCombo.text = view.SecondCombo > 1 ? view.SecondCombo + " HIT" : "";
                Clock.text = DuelConfig.FrameLimit == 0
                    ? "∞"
                    : Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - view.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            }
            if (Mathf.Abs(confirmedFirstX - confirmedSecondX) >= FacingHoldDistance)
            {
                _firstDrawFacing = firstFacing;
                _secondDrawFacing = secondFacing;
            }
            PaintActor(First, firstPosition, _firstDrawFacing);
            PaintActor(Second, secondPosition, _secondDrawFacing);
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

        // 状态来自同一只读快照。移动和施法沿用 02 号的笑意，腿法命中使用 23 号。
        private void PaintYayaExpression(DuelSnapshot view, bool moving)
        {
            bool playful = view.SecondAction == 2 || view.SecondAction == 3 || view.SecondAction == 4 ||
                (moving && view.SecondAction == 0 && view.SecondY == 0);
            int expression = view.SecondHealth == 0 ? 4 : view.SecondStun > 0 ? 3 :
                playful ? 2 : view.SecondAction != 0 || view.SecondY > 0 ? 1 : 0;
            if (SecondPortrait != null && _yayaExpression != expression)
            {
                _yayaExpression = expression;
                SecondPortrait.sprite = YayaPortraits[expression];
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
            Vector2 first = new Vector2(view.FirstX, view.FirstY) * ActorScale;
            Vector2 second = new Vector2(view.SecondX, view.SecondY) * ActorScale;
            Effects.Observe(0, view.FirstAction, view.Frame - view.FirstAge, view.FirstFacing, first, second);
            Effects.Observe(1, view.SecondAction, view.Frame - view.SecondAge, view.SecondFacing, second, first);
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
