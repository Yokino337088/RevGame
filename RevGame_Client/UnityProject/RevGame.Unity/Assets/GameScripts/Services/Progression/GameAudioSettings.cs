// 玩家声音偏好由常驻服务持有：只调Revolution的BGM分类音量，不碰战斗音效/系统主音量。
// 拖动时立即落音量。检查点合并磁盘写入，关面板与切后台立即保存最新值。
using System;
using Revolution;
using UnityEngine;

namespace RevGame.Services
{
    /// <summary>设置View的一次数据快照；由服务产生，面板消费，不在面板直接改存档或声音内核。</summary>
    public sealed class AudioSettingsFrame
    {
        public readonly float Volume;
        public readonly bool Muted;
        public AudioSettingsFrame(float volume, bool muted)
        {
            Volume = volume;
            Muted = muted;
        }
    }
    /// <summary>玩家BGM偏好，和Bootstrap同寿命。注入键用于隔离测试，不改玩家生产键。</summary>
    public sealed class GameAudioSettings : IDisposable
    {
        public const string ChangedEvent = "RevGame.Settings.AudioChanged";
        // 纯声音表现的默认值，不影响玩法数值：0.12单曲基准×默认60%分类音量=0.072，为原0.06的120%。
        public const float MusicBaseVolume = 0.12f;
        public const float DefaultVolume = 0.6f;
        private const string ProductionKey = "RevGame.Settings.Bgm.v1";
        private readonly string _key;
        private readonly bool _applySound;
        /// <summary>常驻设置服务持有的保存检查点。负责合并写入和后台事件。</summary>
        private readonly RevSaveCheckpoint _save;
        /// <summary>玩家偏好0～1；静音只暂停声音输出，不丢失此前设置的滑杆值。</summary>
        public float Volume
        {
            get; private set;
        }
        public bool Muted
        {
            get; private set;
        }
        public GameAudioSettings() : this(ProductionKey, true) { }
        public GameAudioSettings(string key, bool applySound)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("声音设置键不能为空");
            }
            _key = key;
            _applySound = applySound;
            float saved = PlayerPrefs.GetFloat(key, DefaultVolume);
            Volume = float.IsNaN(saved) || float.IsInfinity(saved) ? DefaultVolume : Mathf.Clamp01(saved);
            Muted = PlayerPrefs.GetInt(key + ".Muted", 0) != 0;
            Apply();

            _save = new RevSaveCheckpoint(WritePreferences, delaySeconds: 2f, flushOnPause: applySound);
        }

        public AudioSettingsFrame Snapshot()
        {
            return new AudioSettingsFrame(Volume, Muted);
        }
        /// <summary>音量越界被截断，非有限数明确拒绝；业务和自动化都走同一入口。</summary>
        public void SetVolume(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Volume = Mathf.Clamp01(value);
            Changed();
        }
        public void SetMuted(bool value)
        {
            Muted = value;
            Changed();
        }
        public void RestoreDefault()
        {
            Volume = DefaultVolume;
            Muted = false;
            Changed();
        }
        private void Changed()
        {
            _save.MarkDirty();
            Apply();

            if (_applySound)
            {
                RevEvent.DispatchEvent<AudioSettingsFrame>(ChangedEvent, Snapshot());
            }
        }
        private void Apply()
        {
            if (_applySound)
            {
                RevSound.SetVolume(RevSoundKind.Bgm, Muted ? 0f : Volume);
            }
        }
        /// <summary>关闭设置时提交已变动的音量。应用事件交给框架检查点。</summary>
        public void Save()
        {
            _save.Flush();
        }

        /// <summary>将业务的原有键和值提交到 PlayerPrefs。失败交给检查点保留脏状态。</summary>
        private void WritePreferences()
        {
            PlayerPrefs.SetFloat(_key, Volume);
            PlayerPrefs.SetInt(_key + ".Muted", Muted ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>退出常驻宿主时先保存待提交设置，再关闭框架事件与合并计时器。</summary>
        public void Dispose()
        {
            try
            {
                Save();
            }
            finally
            {
                _save.Dispose();
            }
        }
    }
}
