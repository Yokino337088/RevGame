using System;
using Revolution;
using UnityEngine;

namespace RevGame.Services
{
    /// <summary>PC 显示偏好由常驻服务持有。窗口可由系统拉伸，切换时才请求 Unity 修改尺寸。</summary>
    public sealed class GameDisplaySettings : IDisposable
    {
        public const string ChangedEvent = "RevGame.Settings.DisplayChanged";

#if UNITY_STANDALONE && !UNITY_EDITOR
        public static bool Available => true;
        private const string Key = "RevGame.Settings.Display.v1";
        private readonly bool _persist;
        private Vector2Int _windowSize;
        private FullScreenMode? _pendingMode;
        private Vector2Int _pendingSize;
        private float _deadline;
        private bool _dirty;
#else
        public static bool Available => false;
#endif

        /// <summary>由实际窗口模式确认，操作系统切换模式时也会更新。</summary>
        public bool Fullscreen { get; private set; }
        /// <summary>Unity 在帧末切换，待确认期间避免重复发送切换请求。</summary>
        public bool Switching { get; private set; }

        public GameDisplaySettings(Vector2Int defaultWindowSize)
        {
#if UNITY_STANDALONE && !UNITY_EDITOR
            string[] arguments = Environment.GetCommandLineArgs();
            _persist = Array.IndexOf(arguments, "-revTestSession") < 0;
            Fullscreen = Screen.fullScreenMode != FullScreenMode.Windowed;
            Vector2Int fallback = Fullscreen ? defaultWindowSize : new Vector2Int(Screen.width, Screen.height);
            _windowSize = _persist ? new Vector2Int(PlayerPrefs.GetInt(Key + ".Width", fallback.x),
                PlayerPrefs.GetInt(Key + ".Height", fallback.y)) : fallback;
            if (_windowSize.x <= 0 || _windowSize.y <= 0)
            {
                _windowSize = defaultWindowSize;
            }

            // 显式启动参数由 Unity 处理，供玩家启动器和不同窗口尺寸的验收使用。
            if (Array.IndexOf(arguments, "-screen-fullscreen") < 0 &&
                Array.IndexOf(arguments, "-screen-width") < 0 && Array.IndexOf(arguments, "-screen-height") < 0)
            {
                bool saved = _persist ? PlayerPrefs.GetInt(Key + ".Fullscreen", Fullscreen ? 1 : 0) != 0 : Fullscreen;
                RequestMode(saved);
            }
            RevMono.AddUpdate(ObserveScreen, owner: this);
            RevEvent.AddEventListener(RevAppLifecycle.Quitting, Save, owner: this);
#endif
        }

        /// <summary>设置页切换窗口与原生分辨率无边框全屏；进入全屏前记录此次窗口尺寸。</summary>
        public void SetFullscreen(bool fullscreen)
        {
#if UNITY_STANDALONE && !UNITY_EDITOR
            if (Switching || fullscreen == Fullscreen)
            {
                return;
            }
            if (Screen.fullScreenMode == FullScreenMode.Windowed)
            {
                RememberWindow(new Vector2Int(Screen.width, Screen.height));
            }
            RequestMode(fullscreen);
#endif
        }

#if UNITY_STANDALONE && !UNITY_EDITOR
        private void RequestMode(bool fullscreen)
        {
            DisplayInfo display = Screen.mainWindowDisplayInfo;
            Vector2Int resolution = new Vector2Int(display.width, display.height);
            if (resolution.x <= 0 || resolution.y <= 0)
            {
                Resolution fallback = Screen.currentResolution;
                resolution = new Vector2Int(fallback.width, fallback.height);
            }
            Vector2Int workArea = display.workArea.size;
            if (workArea.x <= 0 || workArea.y <= 0)
            {
                workArea = resolution;
            }
            FullScreenMode mode = fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            Vector2Int size = fullscreen ? resolution : FitWindow(_windowSize, workArea);
            if (size.x <= 0 || size.y <= 0 || (mode == Screen.fullScreenMode && size.x == Screen.width && size.y == Screen.height))
            {
                return;
            }
            _pendingMode = mode;
            _pendingSize = size;
            _deadline = Time.unscaledTime + 2f;
            Switching = true;
            Screen.SetResolution(size.x, size.y, mode);
            RevEvent.DispatchEvent(ChangedEvent);
        }

        private void ObserveScreen()
        {
            FullScreenMode mode = Screen.fullScreenMode;
            var size = new Vector2Int(Screen.width, Screen.height);
            bool wasSwitching = Switching;
            if (Switching && ((mode == _pendingMode && size == _pendingSize) || Time.unscaledTime >= _deadline))
            {
                _pendingMode = null;
                Switching = false;
            }
            bool fullscreen = mode != FullScreenMode.Windowed;
            bool modeChanged = fullscreen != Fullscreen;
            Fullscreen = fullscreen;
            _dirty |= modeChanged;
            if (!Fullscreen && !Switching)
            {
                RememberWindow(size);
            }
            if (modeChanged || wasSwitching != Switching)
            {
                RevEvent.DispatchEvent(ChangedEvent);
            }
        }

        private void RememberWindow(Vector2Int size)
        {
            if (size.x > 0 && size.y > 0 && size != _windowSize)
            {
                _windowSize = size;
                _dirty = true;
            }
        }
#endif

        /// <summary>窗口放不下时等比缩小，在当前显示器工作区留出标题栏与边框的空间。</summary>
        internal static Vector2Int FitWindow(Vector2Int requested, Vector2Int workArea)
        {
            const float UsableRatio = .94f;
            float scale = Mathf.Min(1f, Mathf.Min(workArea.x * UsableRatio / requested.x, workArea.y * UsableRatio / requested.y));
            return new Vector2Int(Mathf.Max(1, Mathf.FloorToInt(requested.x * scale)), Mathf.Max(1, Mathf.FloorToInt(requested.y * scale)));
        }

        /// <summary>设置关闭或进程退出时一次写入，拖动窗口只更新内存尺寸。</summary>
        public void Save()
        {
#if UNITY_STANDALONE && !UNITY_EDITOR
            if (!_persist || !_dirty)
            {
                return;
            }
            PlayerPrefs.SetInt(Key + ".Fullscreen", Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt(Key + ".Width", _windowSize.x);
            PlayerPrefs.SetInt(Key + ".Height", _windowSize.y);
            PlayerPrefs.Save();
            _dirty = false;
#endif
        }

        public void Dispose()
        {
#if UNITY_STANDALONE && !UNITY_EDITOR
            RevMono.RemoveAllOf(this);
            RevEvent.RemoveAllByOwner(this);
            Save();
#endif
        }
    }
}
