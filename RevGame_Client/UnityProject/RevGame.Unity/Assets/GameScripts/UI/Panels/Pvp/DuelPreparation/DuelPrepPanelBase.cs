using System;
using DG.Tweening;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>业务单向交给两张面板的画面数据，倒计时与百分比来自权威而非 Tween。</summary>
    public sealed class DuelPrepViewData
    {
        // 当前权威阶段和两席身份。
        public DuelPrepState State;
        public DuelRole Black;
        public DuelRole White;
        public string BlackName;
        public string WhiteName;
        // 黑为 1、白为 2，所有客户端使用同样的左右站位。
        public int LocalSeat;
        public int Seconds;
        public bool CanPick;
    }

    /// <summary>一席共用的 uGUI 组件。Prefab 保存布局和 Sprite，面板只更新画面。</summary>
    [Serializable]
    public sealed class DuelPrepSeatView
    {
        // 立绘、席位和状态由预制体装配；进度控件只存在于加载页。
        public Image Portrait;
        public Image Echo;
        public RectTransform Portal;
        public Text RoleName;
        public Text Player;
        public Text Status;
        public Image Meter;
        public Text Percent;
        // 选角立绘的正方形设计尺寸。加载页留为 0，沿用各自的固定构图。
        public float EmberPortraitSize;
        public float YayaPortraitSize;
        // 这些值仅驱动画面，不参与锁定或进度放行。
        private bool _locked;
        private int _target = -1;
        private Tween _fill;

        // 立绘在换人时短促弹入；席位保持固定冷暖色，镜像局同时可看徽记。
        internal void Paint(DuelRole role, Sprite sprite, string player, string seat, bool locked, int progress, bool own)
        {
            Player.text = seat + "  /  " + (own ? "你" : ShortPlayerName(player));
            RoleName.text = DuelPrepPanelBase.RoleName(role);
            if (Portrait.sprite != sprite)
            {
                Portrait.sprite = sprite;
                Echo.sprite = sprite;
                RectTransform portraitRect = Portrait.rectTransform;
                float size = role == DuelRole.Yaya ? YayaPortraitSize : EmberPortraitSize;
                if (size > 0)
                {
                    // 先匹配新角色的透明留白，再从脚下播放缩放。倒计时刷新时不会重复装配。
                    portraitRect.sizeDelta = new Vector2(size, size);
                }
                portraitRect.DOKill();
                portraitRect.localScale = Vector3.one * .94f;
                portraitRect.DOScale(1f, .32f).SetEase(Ease.OutBack).SetUpdate(true);
                Portal.DOKill();
                Portal.localScale = Vector3.one;
                Portal.DOPunchScale(Vector3.one * .20f, .45f, 1, .6f).SetUpdate(true);
            }
            Status.text = Meter != null ? (progress == 100 ? "加载完成" : "正在准备") : (locked ? "已锁定" : "预选中");
            if (!_locked && locked)
            {
                Status.rectTransform.DOKill();
                Status.rectTransform.localScale = Vector3.one;
                Status.rectTransform.DOPunchScale(Vector3.one * .14f, .3f, 1, .6f).SetUpdate(true);
            }
            _locked = locked;
            if (Meter != null && _target != progress)
            {
                _target = progress;
                _fill?.Kill(false);
                _fill = DOTween.To(() => Meter.fillAmount, SetFill, progress / 100f, .24f).SetEase(Ease.OutCubic).SetUpdate(true);
            }
        }

        // 席位徽记只有一行，过长昵称省略；截断处保留完整的 UTF-16 字符。
        private static string ShortPlayerName(string name)
        {
            const int labelUnits = 8;
            if (name.Length <= labelUnits)
            {
                return name;
            }
            int count = char.IsHighSurrogate(name[labelUnits - 1]) ? labelUnits - 1 : labelUnits;
            return name.Substring(0, count) + "…";
        }

        // 填充条与百分比共用同一个显示值，业务进度不会被补间改写。
        private void SetFill(float amount)
        {
            Meter.fillAmount = amount;
            Percent.text = Mathf.RoundToInt(amount * 100f) + "%";
        }

        // 面板退出时释放本席动画，避免回调碰到已关闭的控件。
        internal void Stop()
        {
            _fill?.Kill(false);
            Portrait.rectTransform.DOKill();
            Portal.DOKill();
            Status.rectTransform.DOKill();
        }
    }

    /// <summary>选角和加载页共用席位结构与开关动效，子类只负责各自的中部内容。</summary>
    public abstract class DuelPrepPanelBase : RevUIPanel<DuelPrepViewData>
    {
        // 两席立绘和头像使用同一图集，但不跟随角色换色。
        public Sprite Ember;
        public Sprite Yaya;
        public DuelPrepSeatView BlackSeat;
        public DuelPrepSeatView WhiteSeat;
        // 全屏遮挡与装饰脉冲只活在本次面板期间。
        public RectTransform Accent;
        private CanvasGroup _cover;
        private Tween _fade;
        private Tween _pulse;

        // 运行时与 Prefab Mode 的布局都由序列化组件驱动，这里只装配控件行为。
        protected override void OnBindView()
        {
            BlackSeat.Player.supportRichText = false;
            WhiteSeat.Player.supportRichText = false;
            _cover = GetComponent<CanvasGroup>();
        }

        // 淡入可见后才通知框架本面板真正打开，选角时间不会被入场动效占用。
        protected override void PlayOpenTransition(Action onDone)
        {
            _fade?.Kill(false);
            _cover.alpha = 0;
            _pulse = Accent.DOScale(1.065f, 1.25f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true);
            _fade = _cover.DOFade(1, .28f).SetEase(Ease.OutCubic).SetUpdate(true).OnComplete(() => onDone?.Invoke());
        }

        // 退场仍完整遮挡旧场景，直到流程放行后才淡出。
        protected override void PlayCloseTransition(Action onDone)
        {
            _fade?.Kill(false);
            _fade = _cover.DOFade(0, .22f).SetUpdate(true).OnComplete(() => onDone?.Invoke());
        }

        // 仅本页注册的动画随面板关闭。
        protected override void OnClose()
        {
            _pulse?.Kill(false);
            BlackSeat.Stop();
            WhiteSeat.Stop();
        }

        // 只把两席快照落屏，互斥与同角规则由服务决定。
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            int locks = Data.State?.locked ?? 0;
            BlackSeat.Paint(Data.Black, SpriteFor(Data.Black), Data.BlackName, "1P · 黑方", (locks & 1) != 0, Data.State?.blackLoad ?? 0, Data.LocalSeat == 1);
            WhiteSeat.Paint(Data.White, SpriteFor(Data.White), Data.WhiteName, "2P · 白方", (locks & 2) != 0, Data.State?.whiteLoad ?? 0, Data.LocalSeat == 2);
            PaintCenter();
        }

        // 中部倒计时或加载提示由具体面板显示。
        protected abstract void PaintCenter();

        // 返回面板已有的角色立绘，不额外查找或加载散图。
        private Sprite SpriteFor(DuelRole role)
        {
            return role == DuelRole.Yaya ? Yaya : Ember;
        }

        // 角色名称不因黑白席位或镜像配色改变。
        internal static string RoleName(DuelRole role)
        {
            return role == DuelRole.Yaya ? "鸦鸦" : "绯焰";
        }
    }
}
