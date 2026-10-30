// BGM设置面板采用框架Panel与属性事件接线，装配/刷新/交互分别负责不同工作。
// 服务生成数据快照，面板只展示；静音、音量和存档通过GameAudioSettings服务执行。
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
        protected override void OnBindView()
        {
        }
        protected override void OnOpen()
        {
            // 关闭会清理owner监听；每次打开重新订阅，不能只在首次装配订阅。
            RevEvent.AddEventListener<AudioSettingsFrame>(GameAudioSettings.ChangedEvent, OnSettingsChanged, owner: this);
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
        protected override void OnClose()
        {
            RevEvent.RemoveAllByOwner(this);
            GameAudioSettings settings = RevServices.Get<GameAudioSettings>();

            if (settings != null)
            {
                settings.Save();
            }
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
