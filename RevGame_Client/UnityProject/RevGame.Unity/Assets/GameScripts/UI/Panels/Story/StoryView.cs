using System.Globalization;
using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>框架节点级View（RevUIPart）：负责画像、打字与点击通知，不读存档、不改变场景。</summary>
    public sealed class StoryView : RevUIPart
    {
        [RevBind] private Text _title;
        [RevBind] private Text _speaker;
        [RevBind] private Text _dialogue;
        [RevBind] private Text _counter;
        [RevBind] private Image _guide;
        /// <summary>只保存自己创建的Tween/音效句柄，换句、关闭、销毁均收尾，绝不KillAll。</summary>
        private Tween _typing;
        private Tween _guideAnimation;
        /// <summary>帧序号仅用于验证动画确实切帧；表现时长不属于战斗数值。</summary>
        public int GuideFrame { get; private set; }
        private RevSoundHandle _sound;
        private float _nextSound;
        private int _version = -1;
        private int _index = -1;
        public int VisibleCharacters { get; private set; }
        /// <summary>可观察的播放证据：成功获得声音句柄才算发声；用于区分“调用了Play”和“确实开始播放”。</summary>
        public bool SoundStarted { get; private set; }
        public bool IsTyping { get { return _typing != null && _typing.IsActive(); } }

        protected override void OnBindView() { _dialogue.supportRichText = false; }

        /// <summary>按Unicode文字元素切分，避免半个emoji/代理字符。DOTween用非缩放时间，暂停战斗仍可阅读。</summary>
        public void Show(StoryPanel.Frame frame)
        {
            if (_version == frame.Version && _index == frame.Index) { return; }
            StopTyping();
            _version = frame.Version;
            _index = frame.Index;
            var line = frame.Episode.Lines[frame.Index];
            _title.text = frame.Episode.Title;
            _speaker.text = line.Speaker;
            _counter.text = (frame.Index + 1) + " / " + frame.Episode.Lines.Length + "    点击任意处 · 下一句";
            _guideAnimation?.Kill(false);
            GuideFrame = 0;
            _guide.sprite = frame.Book.GuideFrames[0];
            _guideAnimation = DOTween.To(() => GuideFrame, index =>
            {
                GuideFrame = index;
                _guide.sprite = frame.Book.GuideFrames[index % frame.Book.GuideFrames.Length];
            }, frame.Book.GuideFrames.Length, frame.Book.GuideFrames.Length * 0.18f)
                .SetEase(Ease.Linear).SetLoops(-1, LoopType.Restart).SetUpdate(true);
            _dialogue.text = string.Empty;
            int[] ends = StringInfo.ParseCombiningCharacters(line.Text);
            VisibleCharacters = 0;
            SoundStarted = false;
            _nextSound = 0;
            _typing = DOTween.To(() => VisibleCharacters, count =>
            {
                if (count == VisibleCharacters) { return; }
                VisibleCharacters = count;
                int end = count >= ends.Length ? line.Text.Length : ends[count];
                _dialogue.text = line.Text.Substring(0, end);
                // 轻落子声作为棋旅打字节拍；限制最短间隔，逐字播放不会堆出几十条重叠音源。
                if (Time.unscaledTime >= _nextSound)
                {
                    _sound.Stop();
                    _sound = RevSound.Play("Place", volume: 0.12f);
                    SoundStarted |= !_sound.IsEmpty;
                    _nextSound = Time.unscaledTime + 0.085f;
                }
            }, ends.Length, ends.Length / frame.Book.CharactersPerSecond).SetEase(Ease.Linear).SetUpdate(true)
                .OnComplete(() => _typing = null);
        }

        [RevButtonClick("btnNext")]
        private void OnNext() { NotifyHost(); }
        protected override void OnPartClose() { StopTyping(); _version = -1; _index = -1; }
        protected override void OnDestroy() { StopTyping(); base.OnDestroy(); }
        private void StopTyping() { _guideAnimation?.Kill(false); _guideAnimation = null; _typing?.Kill(false); _typing = null; _sound.Stop(); }
    }
}
