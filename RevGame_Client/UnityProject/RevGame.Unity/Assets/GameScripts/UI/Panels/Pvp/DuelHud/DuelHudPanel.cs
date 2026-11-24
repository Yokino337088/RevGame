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
        // 鸦鸦表情由角色当前姿态计算，值域为立绘差分表的 0 至 4。
        internal int BlackExpression, WhiteExpression;
        // 权威座位名，面板只有名称变化时才拼接文字。
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
        // 两席共用专用图集中的待机、专注、微笑、受击、倒地差分，GameFlow 持有图集租约。
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
        // 两席头像使用固定朝向，战斗按当前表情替换 Sprite。
        [RevBind("BlackPortrait")] internal Image FirstPortrait;
        [RevBind("WhitePortrait")] internal Image SecondPortrait;
        // 桌面显示键位说明，移动端隐藏。
        [RevBind("KeyboardGuide")] internal GameObject KeyboardGuide;
        // UI 测试复用实际控件发送指针，玩家回调由特性注册。
        [RevBind("MoveStick")] internal DuelMoveStick MoveStick;
        [RevBind("Jump")] internal Button Jump;
        [RevBind("Jab")] internal Button Jab;
        [RevBind("Skill")] internal Button Skill;
        [RevBind("SkillTwo")] internal Button SkillTwo;
        [RevBind("Ultimate")] internal Button Ultimate;
        [RevBind("Reconnect")] private Button _reconnect;
        // 结算是同一 HUD 中最上层的全屏页，胜负共用回大厅按钮。
        [RevBind("Result")] private CanvasGroup _result;
        [RevBind("Result/Victory")] private GameObject _victory;
        [RevBind("Result/Defeat")] private GameObject _defeat;
        [RevBind("Result/Draw")] private GameObject _draw;
        // 只接受当前场景的视图。关闭后脱离，防止迟到的输入落到旧局。
        private PvpDuelView _owner;
        // 仪表只看上一份已落屏数值，帧号变化不会重建相同的血条、能量和文字网格。
        private DuelSnapshot _painted;
        private bool _hasPainted;
        internal bool ResultVisible => _result != null && _result.gameObject.activeSelf;
        // 首次确认名称或名称改变时更新对应标签。
        private (string black, string white) _names;

        /// <summary>绑定横屏安全框和本平台的键盘图例，控件布局来自预制体。</summary>
        protected override void OnBindView()
        {
            // 现有边框有透明描边。结算淡入时用同组暗幕挡住外侧擂台，保留原有立绘、文案和按钮。
            var veil = new GameObject("BackdropVeil", typeof(RectTransform), typeof(Image));
            veil.transform.SetParent(_result.transform, false);
            veil.transform.SetAsFirstSibling();
            veil.GetComponent<Image>().color = new Color(.055f, .032f, .046f);
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
            if (!_hasPainted || frame.FirstHealth != _painted.FirstHealth)
            {
                _firstHealth.fillAmount = Mathf.Clamp01(frame.FirstHealth / (float)DuelConfig.Health);
                _firstHp.text = frame.FirstHealth.ToString();
            }
            if (!_hasPainted || frame.SecondHealth != _painted.SecondHealth)
            {
                _secondHealth.fillAmount = Mathf.Clamp01(frame.SecondHealth / (float)DuelConfig.Health);
                _secondHp.text = frame.SecondHealth.ToString();
            }
            if (!_hasPainted || frame.FirstEnergy != _painted.FirstEnergy)
            {
                _firstEnergy.fillAmount = Mathf.Clamp01(frame.FirstEnergy / (float)DuelConfig.Energy);
            }
            if (!_hasPainted || frame.SecondEnergy != _painted.SecondEnergy)
            {
                _secondEnergy.fillAmount = Mathf.Clamp01(frame.SecondEnergy / (float)DuelConfig.Energy);
            }
            if (!_hasPainted || frame.FirstCombo != _painted.FirstCombo)
            {
                PaintCombo(_firstCombo, frame.FirstCombo);
            }
            if (!_hasPainted || frame.SecondCombo != _painted.SecondCombo)
            {
                PaintCombo(_secondCombo, frame.SecondCombo);
            }
            if (!_hasPainted || (DuelConfig.FrameLimit != 0 && frame.Frame != _painted.Frame))
            {
                Clock.text = DuelConfig.FrameLimit == 0 ? "∞" :
                    Mathf.CeilToInt(Mathf.Max(0, DuelConfig.FrameLimit - frame.Frame) * DuelConfig.FrameMs / 1000f).ToString("00");
            }
            _painted = frame;
            _hasPainted = true;
            var names = (Data.BlackName, Data.WhiteName);
            if (_names != names)
            {
                _names = names;
                FirstName.text = "鸦鸦  /  " + Data.BlackName;
                SecondName.text = Data.WhiteName + "  /  鸦鸦";
            }
            PaintPortrait(FirstPortrait, Data.BlackExpression);
            PaintPortrait(SecondPortrait, Data.WhiteExpression);
            _reconnect.gameObject.SetActive(Data.CanReconnect);
            if (_banner.text != Data.Banner)
            {
                _banner.text = Data.Banner;
            }
            if (InputFeedback.text != Data.Feedback)
            {
                InputFeedback.text = Data.Feedback;
            }
        }

        /// <summary>权威终局播完击倒后交付结算。遮挡战斗控件，返回操作继续走 GameFlow。</summary>
        internal void ShowResult(int result, GoSide seat)
        {
            if (ResultVisible)
            {
                return;
            }
            bool draw = result == 3;
            _draw.SetActive(draw);
            _victory.SetActive(!draw && result == (int)seat);
            _defeat.SetActive(!draw && result != (int)seat);
            for (int index = 0; index < transform.childCount; index++)
            {
                Transform child = transform.GetChild(index);
                if (child != _result.transform)
                {
                    child.gameObject.SetActive(false);
                }
            }
            _result.alpha = 0;
            _result.gameObject.SetActive(true);
            _result.DOFade(1, .22f).SetUpdate(true).SetLink(gameObject);
        }

        // 只有差分变化才替换图片，角色落屏帧率不会反复触发 HUD 的图形重建。
        private void PaintPortrait(Image image, int expression)
        {
            Sprite sprite = YayaPortraits[expression];
            if (image.sprite != sprite)
            {
                image.sprite = sprite;
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

        // 将原生输入槽翻译为鸦鸦的按钮名称。动作 0 是独立的跳跃输入。
        internal static string ActionCaption(int action)
        {
            return action switch
            {
                1 => "普攻",
                2 => "鸦袭",
                3 => "鸦宴",
                4 => "掠羽",
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
        // 结算按钮同样走正常退房和场景切换。两处节点共用 Back，一个事件只调用一次。
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
            _result.DOKill();
        }
    }
}
