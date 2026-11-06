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
        /// <summary>绑定对话卡标题、说话人、正文与阅读进度，均仅显示当前帧。</summary>
        [RevBind] private Text _title;
        [RevBind] private Text _speaker;
        [RevBind] private Text _dialogue;
        [RevBind] private Text _counter;
        /// <summary>剧情表情立绘，面板关闭必须清Sprite引用再归还表情图集。</summary>
        [RevBind] private Image _guide;
        /// <summary>只保存自己创建的Tween/音效句柄，换句、关闭、销毁均收尾，绝不KillAll。</summary>
        private Tween _typing;
        /// <summary>当前表情在Book集合里的索引，仅用于检查切换，与大厅待机帧分开。</summary>
        public int GuideFrame
        {
            get; private set;
        }
        /// <summary>单条打字音效句柄与下一次可播放时刻（非缩放秒），防止每字叠音。</summary>
        private RevSoundHandle _sound;
        private float _nextSound;
        /// <summary>缓存当前播放代次与行号，重复落屏不重启动画，新篇章同下标仍能刷新。</summary>
        private int _version = -1;
        private int _index = -1;
        /// <summary>已显示Unicode文字元素数量，用于自动化观察打字进度。</summary>
        public int VisibleCharacters
        {
            get; private set;
        }
        /// <summary>可观察的播放证据：成功获得声音句柄才算发声；用于区分“调用了Play”和“确实开始播放”。</summary>
        public bool SoundStarted
        {
            get; private set;
        }
        /// <summary>打字Tween仍存活时为true；只影响表现，不影响翻页许可。</summary>
        public bool IsTyping
        {
            get
            {
                return _typing != null && _typing.IsActive();
            }
        }
        /// <summary>只装配文字显示规则，禁用富文本避免对白被当标记解释。</summary>
        protected override void OnBindView()
        {
            _dialogue.supportRichText = false;
        }
        /// <summary>按Unicode文字元素切分，避免半个emoji/代理字符。DOTween用非缩放时间，暂停战斗仍可阅读。</summary>
        public void Show(StoryPanel.Frame frame)
        {
            if (_version == frame.Version && _index == frame.Index)
            {
                return;
            }
            StopTyping();
            _version = frame.Version;
            _index = frame.Index;
            var line = frame.Episode.Lines[frame.Index];
            _title.text = frame.Episode.Title;
            _speaker.text = line.Speaker;
            _counter.text = (frame.Index + 1) + " / " + frame.Episode.Lines.Length + "    点击任意处 · 下一句";
            // 每句配置明确指定表情，不把六张不同情绪当序列帧循环；立绘缩放保留原图比例。
            _guide.sprite = frame.Book.ResolvePortrait(frame.Episode, line, out int expressionIndex);
            GuideFrame = expressionIndex;
            _guide.preserveAspect = true;
            _guide.transform.SetAsLastSibling();
            _dialogue.text = string.Empty;
            int[] ends = StringInfo.ParseCombiningCharacters(line.Text);
            VisibleCharacters = 0;
            SoundStarted = false;
            _nextSound = 0;
            _typing = DOTween.To(() => VisibleCharacters, count =>
            {
                if (count == VisibleCharacters)
                {
                    return;
                }
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
        /// <summary>整屏点击通知宿主，宿主把下一句请求交给GameFlow。</summary>
        [RevButtonClick("btnNext")]
        private void OnNext()
        {
            NotifyHost();
        }
        /// <summary>右上角跳过由宿主交给唯一GameFlow收尾，不能只隐藏UI留下剧情锁。</summary>
        [RevButtonClick("btnSkip")]
        private void OnSkip()
        {
            RevServices.GetRequired<RevGame.Flow.GameFlow>().SkipStory();
        }
        /// <summary>面板关闭先取消自己的Tween/声音和立绘引用，防止缓存面板保留旧资源。</summary>
        protected override void OnPartClose()
        {
            StopTyping();
            _guide.sprite = null;
            _version = -1;
            _index = -1;
        }
        /// <summary>真正销毁时仍执行同一收尾，绝不停止其它UI的Tween或声音。</summary>
        protected override void OnDestroy()
        {
            StopTyping();
            base.OnDestroy();
        }
        /// <summary>只停止当前视图拥有的表现句柄，换句、关闭和销毁可以重复调用。</summary>
        private void StopTyping()
        {
            _typing?.Kill(false);
            _typing = null;
            _sound.Stop();
        }
    }
}
