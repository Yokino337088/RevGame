// 设置面板展示 BGM 与桌面显示偏好，两个服务分别持有实际状态与存档。
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel("UI/Panel/Settings", RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose, Mask = RevUIMaskMode.ClickBlock)]
    public sealed class AudioSettingsPanel : RevUIPanel<AudioSettingsFrame>
    {
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
            // 无通知赋值防止刷新滑杆反过来触发业务，形成循环或把持久化值覆盖掉。
            _sldBgm.SetValueWithoutNotify(Data.Volume);
            _tglMute.SetIsOnWithoutNotify(Data.Muted);
            _volumeText.text = Mathf.RoundToInt(Data.Volume * 100f) + "%";
        }
        [RevSliderChanged("sldBgm")]
        private void OnBgm(float value)
        {
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
