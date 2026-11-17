using System;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>面板独占的舞台资源租约。新帧或关闭发生后，迟到的结果只归还引用。</summary>
    internal sealed class RevGalMedia : IDisposable
    {
        private sealed class Slot<T> where T : UnityEngine.Object
        {
            private readonly Action<T> _apply;
            private readonly Action<string> _report;
            private RevGalAsset<T> _asset;
            private string _path;
            private int _request;
            public bool Loading { get; private set; }

            public Slot(Action<T> apply, Action<string> report)
            {
                _apply = apply;
                _report = report;
            }

            public void Set(string path, bool replay = false)
            {
                path = string.IsNullOrEmpty(path) ? null : path;
                if (!replay && _path == path)
                {
                    return;
                }
                Clear();
                _path = path;
                if (path != null)
                {
                    Loading = true;
                    Load(path, _request).Forget();
                }
            }

            public void Clear()
            {
                ++_request;
                Loading = false;
                _path = null;
                if (_asset != null)
                {
                    _apply(null);
                    _asset.Dispose();
                    _asset = null;
                }
            }

            private async RevTask Load(string path, int ticket)
            {
                RevGalAsset<T> asset = null;
                try
                {
                    asset = await RevGalAsset<T>.Load(path);
                    if (ticket == _request)
                    {
                        _apply(asset.Value);
                        _asset = asset;
                        asset = null;
                    }
                }
                catch (Exception error)
                {
                    if (ticket == _request)
                    {
                        _path = null;
                        _report(error.Message);
                    }
                }
                finally
                {
                    asset?.Dispose();
                    if (ticket == _request)
                    {
                        Loading = false;
                    }
                }
            }
        }

        private readonly Slot<Sprite> _background;
        private readonly Slot<Sprite> _left;
        private readonly Slot<Sprite> _center;
        private readonly Slot<Sprite> _right;
        private readonly Slot<AudioClip> _music;
        private readonly Slot<AudioClip> _voice;
        private readonly Slot<AudioClip> _sound;
        private readonly AudioSource _musicSource;
        private readonly AudioSource _voiceSource;
        private readonly AudioSource _soundSource;
        private bool _disposed;

        public bool VoiceBusy => _voice.Loading || (_voiceSource != null && _voiceSource.isPlaying);

        public RevGalMedia(RevGalView view, AudioSource music, AudioSource voice, AudioSource sound)
        {
            _musicSource = music;
            _voiceSource = voice;
            _soundSource = sound;
            _background = new Slot<Sprite>(view.ShowBackground, view.ShowNotice);
            _left = new Slot<Sprite>(view.ShowLeft, view.ShowNotice);
            _center = new Slot<Sprite>(view.ShowCenter, view.ShowNotice);
            _right = new Slot<Sprite>(view.ShowRight, view.ShowNotice);
            _music = new Slot<AudioClip>(clip => Play(music, clip), view.ShowNotice);
            _voice = new Slot<AudioClip>(clip => Play(voice, clip), view.ShowNotice);
            _sound = new Slot<AudioClip>(clip => Play(sound, clip), view.ShowNotice);
        }

        public void Present(RevGalFrame frame, RevGalOptions options, bool quiet)
        {
            if (_disposed)
            {
                return;
            }
            RevGalStage stage = frame.Stage;
            _background.Set(stage.Background);
            _left.Set(stage.Left);
            _center.Set(stage.Center);
            _right.Set(stage.Right);
            _musicSource.volume = Mathf.Clamp01(options.MusicVolume) * RevSound.GetVolume(RevSoundKind.Bgm);
            _voiceSource.volume = Mathf.Clamp01(options.VoiceVolume) * RevSound.GetVolume(RevSoundKind.Voice);
            _soundSource.volume = Mathf.Clamp01(options.SoundVolume) * RevSound.GetVolume(RevSoundKind.Sfx);
            _music.Set(stage.Music);
            _voice.Set(quiet ? null : frame.Voice, true);
            _sound.Set(quiet ? null : frame.Sound, true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _background.Clear();
            _left.Clear();
            _center.Clear();
            _right.Clear();
            _music.Clear();
            _voice.Clear();
            _sound.Clear();
        }

        private static void Play(AudioSource source, AudioClip clip)
        {
            if (source == null)
            {
                return;
            }
            source.Stop();
            source.clip = clip;
            if (clip != null)
            {
                source.Play();
            }
        }
    }
}
