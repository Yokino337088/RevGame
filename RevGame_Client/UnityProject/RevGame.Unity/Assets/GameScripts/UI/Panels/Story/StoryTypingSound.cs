using Revolution;
using UnityEngine;

namespace RevGame.UI
{
    /// <summary>主线与 Gal 共用的字幕节拍。每个视图只持有自己的落子音效句柄。</summary>
    internal sealed class StoryTypingSound
    {
        private RevSoundHandle _voice;
        private float _nextSound;

        public bool Started { get; private set; }

        /// <summary>每句文字增长时尝试播放。使用非缩放时间限制间隔，停止上一声以防叠音。</summary>
        public void OnTextWritten()
        {
            float now = Time.unscaledTime;
            if (now < _nextSound)
            {
                return;
            }
            _voice.Stop();
            _voice = RevSound.Play("Place", volume: 0.12f);
            Started |= !_voice.IsEmpty;
            _nextSound = now + 0.085f;
        }

        /// <summary>暂停表现时停掉当前音效，保留本句已经播放过的状态。</summary>
        public void Stop()
        {
            _voice.Stop();
            _voice = default;
        }

        /// <summary>切句或关闭时归还当前播放，并允许新句立即响起。</summary>
        public void Reset()
        {
            Stop();
            _nextSound = 0;
            Started = false;
        }
    }
}
