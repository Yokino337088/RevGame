// 设置面板展示 BGM 与桌面显示偏好，两个服务分别持有实际状态与存档。
using DG.Tweening;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel("UI/Panel/Settings", RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose, Mask = RevUIMaskMode.ClickBlock)]
    public sealed class AudioSettingsPanel : RevUIPanel<AudioSettingsFrame>
    {
        private const float VolumeTransitionSeconds = .16f;
        /// <summary>实际UGUI滑杆范围0～1，刷新用无通知赋值；输入通过RevSliderChanged转交服务。</summary>
        [RevBind] private Slider _sldBgm;
        /// <summary>静音仅影响BGM分类，保持滑杆的原偏好值。</summary>
        [RevBind] private Toggle _tglMute;
        /// <summary>玩家可读的0～100%数值，不直接参与声音计算。</summary>
        [RevBind] private Text _volumeText;
        // 显示控制仅在桌面 Player 可见，两个按钮从实际窗口状态更新选中态。
        [RevBind("displayModes")] private GameObject _displayModes;
        [RevBind("displayModes/btnWindowed")] private UnityEngine.UI.Button _btnWindowed;
        [RevBind("displayModes/btnFullscreen")] private UnityEngine.UI.Button _btnFullscreen;
        private Tween _volumeTween;

        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "veil");
            _displayModes.SetActive(GameDisplaySettings.Available);
        }
        protected override void OnOpen()
        {
            // 关闭会清理owner监听；每次打开重新订阅，不能只在首次装配订阅。
            RevEvent.AddEventListener<AudioSettingsFrame>(GameAudioSettings.ChangedEvent, OnSettingsChanged, owner: this);
            RevEvent.AddEventListener(GameDisplaySettings.ChangedEvent, RefreshDisplay, owner: this);
            RefreshDisplay();
        }
        private void OnSettingsChanged(AudioSettingsFrame frame)
        {
            SetData(frame);
        }
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            // 滑杆输入已由 UGUI 同步到手柄和填充。外部改值才缓动，且每帧只更新显示。
            _volumeTween?.Kill();
            _volumeTween = null;
            if (!Mathf.Approximately(_sldBgm.value, Data.Volume))
            {
                if (State == RevUIPanelState.Opened)
                {
                    _volumeTween = DOTween.To(() => _sldBgm.value, _sldBgm.SetValueWithoutNotify, Data.Volume, VolumeTransitionSeconds)
                        .SetEase(Ease.OutCubic).SetUpdate(true).SetRecyclable(false);
                }
                else
                {
                    _sldBgm.SetValueWithoutNotify(Data.Volume);
                }
            }
            _tglMute.SetIsOnWithoutNotify(Data.Muted);
            _volumeText.text = Mathf.RoundToInt(Data.Volume * 100f) + "%";
        }
        [RevSliderChanged("sldBgm")]
        private void OnBgm(float value)
        {
            // 玩家开始移动滑杆时接管显示，音量和百分比继续即时更新。
            _volumeTween?.Kill();
            _volumeTween = null;
            RevServices.GetRequired<GameAudioSettings>().SetVolume(value);
        }
        [RevToggleChanged("tglMute")]
        private void OnMute(bool value)
        {
            RevServices.GetRequired<GameAudioSettings>().SetMuted(value);
        }
        [RevButtonClick("btnDefault")]
        private void OnDefault()
        {
            RevServices.GetRequired<GameAudioSettings>().RestoreDefault();
        }
        [RevButtonClick("btnClose")]
        private void OnCloseClick()
        {
            CloseSelf();
        }
        private void RefreshDisplay()
        {
            if (!GameDisplaySettings.Available)
            {
                return;
            }
            GameDisplaySettings display = RevServices.GetRequired<GameDisplaySettings>();
            _btnWindowed.interactable = !display.Switching && display.Fullscreen;
            _btnFullscreen.interactable = !display.Switching && !display.Fullscreen;
        }
        [RevButtonClick("btnWindowed")]
        private void OnWindowed()
        {
            RevServices.GetRequired<GameDisplaySettings>().SetFullscreen(false);
        }
        [RevButtonClick("btnFullscreen")]
        private void OnFullscreen()
        {
            RevServices.GetRequired<GameDisplaySettings>().SetFullscreen(true);
        }
        protected override void OnClose()
        {
            _volumeTween?.Kill();
            _volumeTween = null;
            RevEvent.RemoveAllByOwner(this);
            GameAudioSettings settings = RevServices.Get<GameAudioSettings>();

            if (settings != null)
            {
                settings.Save();
            }
            RevServices.Get<GameDisplaySettings>()?.Save();
        }
        protected override void PlayOpenTransition(System.Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeIn, .12f, onDone, owner: this);
        }
        protected override void PlayCloseTransition(System.Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.PopOut, .1f, onDone, owner: this);
        }
    }
}
