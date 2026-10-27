using RevGame.Services;
using RevGame.UI;
using Revolution;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>首次设置加载也暂停玩法；不改Time.timeScale，菜单与淡入仍能运行。</summary>
        private bool _audioSettingsOpening;
        /// <summary>设置正在加载或可见时屏蔽战斗推进与棋盘输入，关闭后自动恢复。</summary>
        public bool IsAudioSettingsVisible { get { return _audioSettingsOpening || RevUI.IsOpen<AudioSettingsPanel>(); } }

        /// <summary>首页/选关/战斗按钮统一入口；阻止重入，异步首次加载结束后再交付设置面板。</summary>
        public bool OpenAudioSettings()
        {
            if (IsGameplayBlocked || Screen == Core.GameScreen.Exit || _disposed) { LastError = "当前流程暂不能打开设置"; return false; }
            _audioSettingsOpening = true;
            OpenAudioSettingsAsync();
            return true;
        }
        private async void OpenAudioSettingsAsync()
        {
            try
            {
                AudioSettingsFrame frame = RevServices.GetRequired<GameAudioSettings>().Snapshot();
                AudioSettingsPanel panel = await RevUI.OpenAsync<AudioSettingsPanel, AudioSettingsFrame>(frame);
                if (panel == null) { throw new System.InvalidOperationException("声音设置面板加载失败"); }
                if (_disposed) { RevUI.Close<AudioSettingsPanel>(); }
            }
            catch (System.Exception error)
            {
                LastError = "声音设置打开失败：" + error.Message;
                RevLog.Exception(error, "声音设置打开失败", "RevGame");
            }
            finally { _audioSettingsOpening = false; }
        }
    }
}
