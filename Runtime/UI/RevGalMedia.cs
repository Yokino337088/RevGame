using System;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>面板独占的舞台资源租约。新帧或关闭发生后，迟到的结果只归还引用。</summary>
    internal sealed class RevGalMedia : IDisposable
    {
        /// <summary>面板拥有的一组背景资源。两条请求完成后整组提交，避免新图片短暂套用上一幕的 Shader。</summary>
        private sealed class BackgroundSlot
        {
            /// <summary>只接收最终 Sprite 与 Material 的界面。</summary>
            private readonly RevGalView _view;
            /// <summary>正在显示的图片租约，切换成功或关闭后释放。</summary>
            private RevGalAsset<Sprite> _sprite;
            /// <summary>正在显示的剧情材质租约，主题材质的引用由 Prefab 自己持有。</summary>
            private RevGalAsset<Material> _material;
            /// <summary>最新请求的图片路径，相邻节点沿用同一对资源时无需重复加载。</summary>
            private string _spritePath;
            /// <summary>最新请求的覆盖材质路径，空路径表示回退到界面的默认材质。</summary>
            private string _materialPath;
            /// <summary>请求代号。切幕、清空、关面板都会让旧异步结果过期。</summary>
            private int _request;

            /// <summary>由面板媒体所有者建立，面板切幕和销毁要通过此对象管理请求。</summary>
            public BackgroundSlot(RevGalView view)
            {
                _view = view;
            }

            /// <summary>刷新期交付下一对逻辑路径。资源仍在读取时保留上一次完整背景。</summary>
            public void Set(string spritePath, string materialPath)
            {
                spritePath = string.IsNullOrEmpty(spritePath) ? null : spritePath;
                materialPath = string.IsNullOrEmpty(materialPath) ? null : materialPath;
                if (_spritePath == spritePath && _materialPath == materialPath)
                {
                    return;
                }
                _spritePath = spritePath;
                _materialPath = materialPath;
                LoadAndPresent(spritePath, materialPath, ++_request).Forget();
            }

            /// <summary>注销待加载请求并解除当前 Image 的引用，随后归还已显示的两份租约。</summary>
            public void Clear()
            {
                ++_request;
                _spritePath = _materialPath = null;
                if (_sprite != null || _material != null)
                {
                    Replace(null, null);
                }
            }

            /// <summary>并行发起读取，始终消费两个结果。迟到的资源由本方法直接归还。</summary>
            private async RevTask LoadAndPresent(string spritePath, string materialPath, int ticket)
            {
                var spriteTask = TryLoad<Sprite>(spritePath);
                var materialTask = TryLoad<Material>(materialPath);
                var (sprite, spriteError) = await spriteTask;
                var (material, materialError) = await materialTask;
                try
                {
                    if (ticket != _request)
                    {
                        return;
                    }
                    if (material != null && !RevGalView.IsSupportedBackgroundMaterial(material.Value))
                    {
                        materialError = "背景材质的 Shader 在当前设备不可用：" + materialPath;
                        material.Dispose();
                        material = null;
                    }
                    // 一个资源失败时仍交付另一个可用项。界面会把空材质换成主题或预制体默认值。
                    Replace(sprite, material);
                    sprite = null;
                    material = null;
                    if (spriteError != null || materialError != null)
                    {
                        _view.ShowNotice(spriteError == null || materialError == null
                            ? spriteError ?? materialError : spriteError + " · " + materialError);
                    }
                }
                finally
                {
                    sprite?.Dispose();
                    material?.Dispose();
                }
            }

            /// <summary>将资源异常转成可展示的结果。另一项加载成功时，其引用仍能被正常消费。</summary>
            private static async RevTask<(RevGalAsset<T> Asset, string Error)> TryLoad<T>(string path) where T : UnityEngine.Object
            {
                if (path == null)
                {
                    return (null, null);
                }
                try
                {
                    return (await RevGalAsset<T>.Load(path), null);
                }
                catch (Exception error)
                {
                    return (null, error.Message);
                }
            }

            /// <summary>先更新 UGUI 引用，再释放旧素材，防止 Image 继续引用已卸载的 AB 内容。</summary>
            private void Replace(RevGalAsset<Sprite> sprite, RevGalAsset<Material> material)
            {
                _view.ShowBackground(sprite?.Value, material?.Value);
                _sprite?.Dispose();
                _material?.Dispose();
                _sprite = sprite;
                _material = material;
            }
        }

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

        /// <summary>同一舞台背景的图片与 Shader 材质成组更新。</summary>
        private readonly BackgroundSlot _background;
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
            _background = new BackgroundSlot(view);
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
            _background.Set(stage.Background, stage.BackgroundMaterial);
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
