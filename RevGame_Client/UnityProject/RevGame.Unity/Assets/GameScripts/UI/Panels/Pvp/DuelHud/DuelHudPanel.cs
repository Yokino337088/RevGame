using DG.Tweening;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>决斗视图每局复用一份 HUD 数据，血量与命中数来自已经播放到的权威帧。</summary>
    public sealed class DuelHudData
    {
        // 帧号、血量、能量、连击和胜负使用同一份快照，避免画出不同逻辑帧的数据。
        internal DuelSnapshot Frame;
        // 本机座位决定按键名称及胜负提示。
        internal GoSide Seat;
        // 选角在入场时锁定，镜像局可让两席共用同一角色素材。
        internal bool BlackYaya, WhiteYaya;
        // 鸦鸦表情由角色当前姿态计算，值域为立绘差分表的 0 至 4。
        internal int BlackExpression, WhiteExpression;
        // 权威座位名，面板只有名称或选角变化时才拼接文字。
        internal string BlackName, WhiteName;
        // 提示文字由视图根据已确认的连接、胜负和输入回执生成。
        internal string Banner, Feedback;
        // 只有可续局的服务端断线才展示重连按钮。
        internal bool CanReconnect;
    }

    /// <summary>决斗界面由 RevUI 管理，控件通过特性绑定，输入交给当前决斗视图合并。</summary>
    [RevUIPanel("UI/Panel/Pvp/DuelHud", RevUILayer.Normal, InBackStack = false, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class DuelHudPanel : RevUIPanel<DuelHudData>
    {
        // 预制体直接引用专用图集中的立绘，面板开着期间由 GameFlow 持有图集租约。
        [SerializeField] internal Sprite EmberPortrait;
        // 按待机、专注、微笑、受击、倒地排列。
        [SerializeField] internal Sprite[] YayaPortraits;
        // 两侧仪表分别填充；美术层级由预制体定义。
        [RevBind("BlackHealth/Fill")] private Image _firstHealth;
        [RevBind("WhiteHealth/Fill")] private Image _secondHealth;
        [RevBind("BlackEnergy/Fill")] private Image _firstEnergy;
        [RevBind("WhiteEnergy/Fill")] private Image _secondEnergy;
        // 血量整数及 HUD 的显示文本。
        [RevBind("BlackHp")] private Text _firstHp;
        [RevBind("WhiteHp")] private Text _secondHp;
        [RevBind("BlackName")] internal Text FirstName;
        [RevBind("WhiteName")] internal Text SecondName;
        [RevBind("BlackCombo")] private Text _firstCombo;
        [RevBind("WhiteCombo")] private Text _secondCombo;
        [RevBind("Clock")] internal Text Clock;
        [RevBind("Banner")] private Text _banner;
        [RevBind("InputFeedback")] internal Text InputFeedback;
        // 两席头像使用固定朝向，选角只替换 Sprite。
        [RevBind("BlackPortrait")] internal Image FirstPortrait;
        [RevBind("WhitePortrait")] internal Image SecondPortrait;
        // 技能名跟随本席选角，同一个按钮始终发送同一输入槽。
        [RevBind("Skill/Caption")] private Text _skill;
        [RevBind("SkillTwo/Caption")] private Text _skillTwo;
        [RevBind("Ultimate/Caption")] private Text _ultimate;
        [RevBind("KeyboardGuide")] internal GameObject KeyboardGuide;
        [RevBind("KeyboardGuide/Keys")] private Text _keyboardLegend;
        // UI 测试复用实际控件发送指针，玩家回调由特性注册。
        [RevBind("MoveStick")] internal DuelMoveStick MoveStick;
        [RevBind("Jump")] internal Button Jump;
        [RevBind("Jab")] internal Button Jab;
        [RevBind("Skill")] internal Button Skill;
        [RevBind("SkillTwo")] internal Button SkillTwo;
        [RevBind("Ultimate")] internal Button Ultimate;
        [RevBind("Reconnect")] private Button _reconnect;
        // 只接受当前场景的视图。关闭后脱离，防止迟到的输入落到旧局。
        private PvpDuelView _owner;
        // 最近一次落屏的数值帧，头像或连接变化不会重刷仪表。
        private int _paintedFrame = -1;
        // 首次出现胜负时只播一次文字反馈。
        private int _shownResult;
        // 本席控制说明在选角或座位变化时更新。
        private (GoSide seat, bool yaya) _legend;
        // 首次确认名称或名称改变时更新对应标签。
        private (string black, string white, bool blackYaya, bool whiteYaya) _names;

        /// <summary>绑定横屏安全框和本平台的键盘图例，控件布局来自预制体。</summary>
        protected override void OnBindView()
        {
            GameMobileLayout.EnableDuelHud((RectTransform)transform);
            KeyboardGuide.SetActive(!UnityEngine.Device.Application.isMobilePlatform);
        }

        /// <summary>场景和面板都加载完成后建立本局输入的唯一接收者。</summary>
        internal void Attach(PvpDuelView owner)
        {
            _owner = owner;
        }

        /// <summary>只把视图交来的快照、表情及反馈落屏。</summary>
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            DuelSnapshot frame = Data.Frame;
            if (_paintedFrame != frame.Frame)
            {
                _paintedFrame = frame.Frame;
                _firstHealth.fillAmount = Mathf.Clamp01(frame.FirstHealth / (float)DuelConfig.Health);
                _secondHealth.fillAmount = Mathf.Clamp01(frame.SecondHealth / (float)DuelConfig.Health);
                _firstEnergy.fillAmount = Mathf.Clamp01(frame.FirstEnergy / (float)DuelConfig.Energy);
                _secondEnergy.fillAmount = Mathf.Clamp01(frame.SecondEnergy / (float)DuelConfig.Energy);
                PaintNumber(_firstHp, frame.FirstHealth);
                PaintNumber(_secondHp, frame.SecondHealth);
                PaintCombo(_firstCombo, frame.FirstCombo);
                PaintCombo(_secondCombo, frame.SecondCombo);
                Clock.text = DuelConfig.FrameLimit == 0 ? "∞" :
                    Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - frame.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            }
            var names = (Data.BlackName, Data.WhiteName, Data.BlackYaya, Data.WhiteYaya);
            if (_names != names)
            {
                _names = names;
                FirstName.text = (Data.BlackYaya ? "鸦鸦" : "绯焰") + "  /  " + Data.BlackName;
                SecondName.text = Data.WhiteName + "  /  " + (Data.WhiteYaya ? "鸦鸦" : "绯焰");
            }
            PaintPortrait(FirstPortrait, Data.BlackYaya, Data.BlackExpression);
            PaintPortrait(SecondPortrait, Data.WhiteYaya, Data.WhiteExpression);
            PaintLegend();
            _reconnect.gameObject.SetActive(Data.CanReconnect);
            if (_banner.text != Data.Banner)
            {
                _banner.text = Data.Banner;
            }
            if (InputFeedback.text != Data.Feedback)
            {
                InputFeedback.text = Data.Feedback;
            }
            if (_shownResult == 0 && frame.Result != 0)
            {
                _shownResult = frame.Result;
                _banner.rectTransform.DOPunchScale(Vector3.one * .16f, .4f).SetUpdate(true).SetLink(gameObject);
            }
        }

        // 只有差分变化才替换图片，角色落屏帧率不会反复触发 HUD 的图形重建。
        private void PaintPortrait(Image image, bool yaya, int expression)
        {
            Sprite sprite = yaya ? YayaPortraits[expression] : EmberPortrait;
            if (image.sprite != sprite)
            {
                image.sprite = sprite;
            }
        }

        // 血量文本在实际扣血时才更新，避免每个逻辑帧都生成字符串。
        private static void PaintNumber(Text label, int value)
        {
            if (!int.TryParse(label.text, out int painted) || painted != value)
            {
                label.text = value.ToString();
            }
        }

        // 空挥只走动作序列；只有权威命中数大于一才出现 HIT。
        private static void PaintCombo(Text label, int hits)
        {
            if (hits > 1 || label.text.Length != 0)
            {
                label.text = hits > 1 ? hits + " HIT" : "";
            }
        }

        // 本席阵容改变时才更新按钮和桌面图例。
        private void PaintLegend()
        {
            bool yaya = Data.Seat == GoSide.White ? Data.WhiteYaya : Data.BlackYaya;
            var legend = (Data.Seat, yaya);
            if (Data.Seat == GoSide.Empty || _legend == legend)
            {
                return;
            }
            _legend = legend;
            _skill.text = ActionCaption(2, yaya);
            _skillTwo.text = ActionCaption(4, yaya);
            _ultimate.text = ActionCaption(3, yaya);
            _keyboardLegend.text = yaya
                ? "A/D 移动    SPACE 跳跃    J 普攻    K 鸦袭    L 掠羽    I 鸦宴"
                : "A/D 移动    SPACE 跳跃    J 普攻    K 贯枪    L 坠枪    I 赤阵";
        }

        // 按钮说明和输入反馈使用同一组动作名称。
        internal static string ActionCaption(int action, bool yaya)
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

        // 跳跃转交本局视图，与键盘边沿合并。
        [RevButtonClick("Jump")]
        private void OnJump()
        {
            _owner?.QueueHudPress(0);
        }

        // 普攻转交固定输入槽，二、三段由权威取消窗口决定。
        [RevButtonClick("Jab")]
        private void OnJab()
        {
            _owner?.QueueHudPress(1);
        }

        // 技能一转交固定输入槽。
        [RevButtonClick("Skill")]
        private void OnSkill()
        {
            _owner?.QueueHudPress(2);
        }

        // 技能二转交固定输入槽。
        [RevButtonClick("SkillTwo")]
        private void OnSkillTwo()
        {
            _owner?.QueueHudPress(4);
        }

        // 大招转交固定输入槽。
        [RevButtonClick("Ultimate")]
        private void OnUltimate()
        {
            _owner?.QueueHudPress(3);
        }

        // 返回由 GameFlow 处理，并在过场遮住 HUD 后关闭面板。
        [RevButtonClick("Back")]
        private void OnBack()
        {
            _owner?.ReturnHome();
        }

        // 重连仍使用当前房间的服务入口。
        [RevButtonClick("Reconnect")]
        private void OnReconnect()
        {
            _owner?.ReconnectServer();
        }

        /// <summary>离场前清掉未发送的手势和文字动效，框架负责销毁面板和解绑按钮。</summary>
        protected override void OnClose()
        {
            _owner?.ClearHeldControls();
            _owner = null;
            _banner.rectTransform.DOKill();
        }
    }
}
