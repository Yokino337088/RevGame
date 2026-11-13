using DG.Tweening;
using RevGame.Flow;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>独立决斗场景的表现消费者。屏幕刷新只读快照，插值和特效不会写回战斗世界。</summary>
    public sealed partial class PvpDuelView : MonoBehaviour
    {
        // 黑方角色图层，位置和镜像每次渲染时更新。
        public Image First;
        // 白方角色图层。场景配方在编辑器中绑定，运行时不重建。
        public Image Second;
        // GameFlow 在专用 RevUI 面板就绪后交给当前场景，离场时自动失效。
        internal DuelHudPanel HudPanel { get; private set; }
        // 单局复用数据容器，权威快照或连接、头像发生变化时才提交给面板。
        private readonly DuelHudData _hudData = new DuelHudData();
        // 最近一条输入提示，定时清掉后统一在 LateUpdate 落屏。
        private string _inputFeedback = "";
        // 黑方的表现状态机，消费跑动、起跳及权威招式姿态。
        public DuelActorAnimator FirstAnimator;
        // 白方的表现状态机，独立保存自己的播放与过渡进度。
        public DuelActorAnimator SecondAnimator;
        // 当前场景的只读招式表现消费者，由场景序列化注入。
        public DuelStrikeEffects Effects;
        // 双人擂台镜头，只消费已确认快照的坐标和招式事件。
        public DuelCameraRig CameraRig;
        // 最近一次消费命中或招式事件的帧号，避免同一快照重复播放。
        private int _seenFrame = -1;
        // HUD 点按后的招式编号，由下一次输入采样取走。
        private int _clickedAction;
        // 待下一次 Update 消费的一次跳跃点按。
        private bool _clickedJump;
        // 输入提示结束的本地非缩放时间。
        private float _inputFeedbackUntil;
        // 白方上一次用于选跳跃姿态的高度，单位为千分格。高度不变时沿用最近一次升降方向。
        private readonly int[] _lastYayaAltitude = new int[2];
        // 最近一次离地高度变化是否向下，决定使用下落还是上升的两张图。
        private readonly bool[] _yayaDescending = new bool[2];
        // 原生水平千分格映射到擂台设计像素，预测、镜头、边界和技能都读同一比例。
        public const float ArenaScale = .195f;
        // 跳跃高度单独按角色原画画布比例换算，避免放宽场地时人物跳出画面。
        private const float HeightScale = .138f;
        // 两个站位小于一步的距离时保持已有镜像，防止重叠或相互穿过时连续翻面。
        private const float FacingHoldDistance = 140 * ArenaScale;
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

        // 场景激活后先绑定角色资源和 RevUI，GameFlow 确认可画后才提交 Ready。
        internal void AttachHud(DuelHudPanel hud)
        {
            HudPanel = hud;
            hud.Attach(this);
            BindDuelRoster();
            Effects.Prepare();
        }

        // 失焦和切后台由 Revolution 统一去重，按住的触控和预测在同一处归零。
        private void OnEnable()
        {
            RevEvent.AddEventListener(RevAppLifecycle.Paused, ClearHeldControls, owner: this);
        }

        // 只缓存 UGUI 按下事件，Update 仍负责与键盘合并并调用业务入口。
        internal void QueueHudPress(int action)
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
        internal void ReconnectServer()
        {
            var service = RevServices.GetRequired<PvpService>();
            service.StartServer(service.Endpoint, service.Nickname);
        }

        // 场景退出或组件被禁用时，和失焦一样清除未发送按键与预测轨迹。
        private void OnDisable()
        {
            RevEvent.RemoveAllByOwner(this);
            ClearHeldControls();
        }

        // 清除触控状态及本地预测。已发送的输入仍由权威处理，清理表现不会撤销它们。
        internal void ClearHeldControls()
        {
            if (HudPanel != null)
            {
                HudPanel.MoveStick.ResetInput();
            }
            _clickedJump = false;
            _clickedAction = 0;
            RevServices.Get<PvpService>()?.ClearDuelUnsealedInput();
        }

        // 交给统一 GameFlow 退房和换场景。
        internal void ReturnHome()
        {
            RevServices.GetRequired<GameFlow>().ReturnHome();
        }

        // 每个表现帧采样键盘及触摸，交给业务服务缓存到下一逻辑帧。
        private void Update()
        {
            // 编辑器的 Game 视图和主窗口焦点分离，RevUI 仍可能收到有效触点。
            if (HudPanel == null || (!Application.isEditor && RevAppLifecycle.IsBackground))
            {
                return;
            }
            var service = RevServices.GetRequired<PvpService>();
            bool left = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            bool right = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
            int keyboard = (right ? 1 : 0) - (left ? 1 : 0);
            // 键盘和触控都输出同一种离散意图。反向同时按住时归中。
            int direction = Mathf.Clamp(keyboard + HudPanel.MoveStick.Direction, -1, 1);
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
                _inputFeedback = "";
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
            string name = DuelHudPanel.ActionCaption(action, black ? _blackYaya : _whiteYaya);
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
            _inputFeedback = name + "  ·  " + message;
            _inputFeedbackUntil = Time.unscaledTime + .85f;
        }

        // 在独立播放时钟上取样。到包可以批量推进原生世界，画面仍按相邻已确认帧插值。
        private void LateUpdate()
        {
            if (HudPanel == null)
            {
                return;
            }
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
            int firstAltitude = view.FirstY;
            // 白方的贴图和屏幕坐标使用同一插值高度，起跳与触地不会错开一帧。
            int secondAltitude = Mathf.RoundToInt(Mathf.Lerp(view.SecondY, next.SecondY, fraction));
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
            bool newFrame = _seenFrame != view.Frame;
            if (newFrame)
            {
                // 正常跨帧时依序消费特效；重连或长停顿后只保留最近四帧的短时表现。
                for (int frame = Mathf.Max(_seenFrame + 1, view.Frame - 3); frame <= view.Frame; frame++)
                {
                    PaintDuelEvents(service.DuelDisplay.Get(frame), frame > 0 ? service.DuelDisplay.Get(frame - 1) : default);
                }
                _seenFrame = view.Frame;
            }
            if (Mathf.Abs(confirmedFirstX - confirmedSecondX) >= FacingHoldDistance)
            {
                _firstDrawFacing = firstFacing;
                _secondDrawFacing = secondFacing;
            }
            PaintActor(First, firstPosition, _firstDrawFacing);
            PaintActor(Second, secondPosition, _secondDrawFacing);
            PaintMirrorBadges();
            CameraRig.Paint(confirmedFirstX, confirmedSecondX, Time.unscaledDeltaTime);
            FirstAnimator.Paint(view.FirstHealth, firstAltitude, view.FirstAction, view.FirstStun, firstMove != 0, view.Frame, view.FirstAge, fraction,
                _blackYaya ? SampleYayaJumpPose(0, firstAltitude) : -1);
            SecondAnimator.Paint(view.SecondHealth, secondAltitude, view.SecondAction, view.SecondStun, secondMove != 0, view.Frame, view.SecondAge, fraction,
                _whiteYaya ? SampleYayaJumpPose(1, secondAltitude) : -1);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            FirstAnimator.ObserveSprite(First.sprite);
            SecondAnimator.ObserveSprite(Second.sprite);
#endif
            PaintHud(service, view, newFrame, firstMove, secondMove);
        }

        // 只在权威播放帧或可见反馈改变时提交，避免渲染频率驱动 HUD 重建。
        private void PaintHud(PvpService service, DuelSnapshot frame, bool newFrame, int firstMove, int secondMove)
        {
            bool disconnected = service.Mode != PvpConnectionMode.Local && !service.Connected;
            bool reconnect = disconnected && service.Mode == PvpConnectionMode.Server && service.DuelReconnectAvailable;
            string banner = service.DuelError.Length != 0 ? service.DuelError :
                disconnected ? "连接中断 · 决斗等待同步" :
                !service.DuelBothReady ? "等待双方进入决斗场" :
                frame.Result == 0 ? (frame.Frame < 18 ? "开打！" : "") :
                frame.Result == 3 ? "平局" : (frame.Result == (int)service.MySide ? "决斗胜利" : "决斗落败");
            int black = _blackYaya ? YayaExpression(frame.FirstAction, frame.FirstHealth, frame.FirstStun, frame.FirstY, firstMove != 0, FirstAnimator) : 0;
            int white = _whiteYaya ? YayaExpression(frame.SecondAction, frame.SecondHealth, frame.SecondStun, frame.SecondY, secondMove != 0, SecondAnimator) : 0;
            if (!newFrame && _hudData.Banner == banner && _hudData.Feedback == _inputFeedback &&
                _hudData.Seat == service.MySide && _hudData.CanReconnect == reconnect &&
                _hudData.BlackName == service.BlackName && _hudData.WhiteName == service.WhiteName &&
                _hudData.BlackExpression == black && _hudData.WhiteExpression == white)
            {
                return;
            }
            _hudData.Frame = frame;
            _hudData.Seat = service.MySide;
            _hudData.BlackYaya = _blackYaya;
            _hudData.WhiteYaya = _whiteYaya;
            _hudData.BlackName = service.BlackName;
            _hudData.WhiteName = service.WhiteName;
            _hudData.BlackExpression = black;
            _hudData.WhiteExpression = white;
            _hudData.CanReconnect = reconnect;
            _hudData.Banner = banner;
            _hudData.Feedback = _inputFeedback;
            HudPanel.SetData(_hudData);
        }

        // 四张跳跃图依次为起跳、收膝、下落伸腿、落地准备。用同一份实际或预测高度判断阶段。
        private int SampleYayaJumpPose(int seat, int altitude)
        {
            if (altitude != _lastYayaAltitude[seat])
            {
                _yayaDescending[seat] = altitude < _lastYayaAltitude[seat];
                _lastYayaAltitude[seat] = altitude;
            }
            int midpoint = DuelConfig.JumpApexHeight / 2;
            if (_yayaDescending[seat])
            {
                return altitude <= midpoint ? 3 : 2;
            }
            return altitude < midpoint ? 0 : 1;
        }

        // 腿法的第一张和收势沿用原画 02 号，蓄力和接触使用 23 号。跟随实际换图，受击和倒地优先。
        private static int YayaExpression(int action, int health, int stun, int altitude, bool moving, DuelActorAnimator animator)
        {
            bool kicking = action is 1 or 5 or 6;
            bool relaxedKick = kicking && !animator.IsAttackFocusFrame;
            bool playful = action is 2 or 3 or 4 || relaxedKick || (moving && action == 0 && altitude == 0);
            return health == 0 ? 4 : stun > 0 ? 3 : playful ? 2 : action != 0 || altitude > 0 ? 1 : 0;
        }

        // 把预测修正限制在本机的绘制坐标。转向立即换姿态，收到不同结果时位置在很短时间内归位。
        private void PaintLocalMotion(PvpService service, int confirmedAction, ref Vector2 position, ref int move, ref int facing, ref int altitude)
        {
            float delta = Time.unscaledDeltaTime;
            bool predicting = service.TrySampleDuelMotion(delta, out DuelPredictedMotion predicted);
            if (predicting)
            {
                position = new Vector2(predicted.X * ArenaScale, predicted.Y * HeightScale);
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
            CameraRig.ObserveBounds(view.FirstX, view.SecondX);
            Vector2 first = new Vector2(view.FirstX * ArenaScale, view.FirstY * HeightScale);
            Vector2 second = new Vector2(view.SecondX * ArenaScale, view.SecondY * HeightScale);
            Effects.Observe(0, view.FirstAction, view.Frame - view.FirstAge, view.FirstFacing, first, second,
                DuelConfig.ActionRange(_blackYaya, view.FirstAction) * ArenaScale);
            Effects.Observe(1, view.SecondAction, view.Frame - view.SecondAge, view.SecondFacing, second, first,
                DuelConfig.ActionRange(_whiteYaya, view.SecondAction) * ArenaScale);
            if (view.FirstHit != 0)
            {
                // 同帧对打可能已打断出招者。前一帧仍持有命中的动作编号。
                int action = view.FirstAction != 0 ? view.FirstAction : Mathf.Max(1, previous.FirstAction);
                Effects.Hit(0, action, view.FirstFacing, second);
                Flash(Second);
            }
            if (view.SecondHit != 0)
            {
                int action = view.SecondAction != 0 ? view.SecondAction : Mathf.Max(1, previous.SecondAction);
                Effects.Hit(1, action, view.SecondFacing, first);
                Flash(First);
            }
        }

        // 逻辑坐标用千分格保存，只有绘制时才换算到屏幕单位。
        private static Vector2 ActorPosition(int x, int y, int nextX, int nextY, float fraction)
        {
            return new Vector2(Mathf.Lerp(x, nextX, fraction) * ArenaScale, Mathf.Lerp(y, nextY, fraction) * HeightScale);
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
