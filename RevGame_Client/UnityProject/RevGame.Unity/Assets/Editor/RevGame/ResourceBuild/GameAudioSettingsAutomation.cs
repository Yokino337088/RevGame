using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>固定设置制作与验收接口；资源构建程序集不能直接引用默认游戏程序集，通过固定类型转发。</summary>
    public static class GameAudioSettingsAutomation
    {
        [RevAutomationAction("game.audio-settings.configure", Environment = "edit", Keys = new string[0], Description = "制作声音设置预制体和三个场景UI入口")]
        public static object Configure(RevAutomationRequest request) { return Call("Configure"); }
        [RevAutomationAction("game.audio-settings.check", Environment = "edit", Keys = new string[0], Description = "隔离保存键验证玩家音量设置与默认120%")]
        public static object Check(RevAutomationRequest request) { return Call("CheckPersistence"); }
        [RevAutomationAction("game.audio-settings.preview", Environment = "edit", Keys = new string[0], Description = "隔离真实AB设置面板渲染与滑杆/静音回调检查，不切场景")]
        public static object Preview(RevAutomationRequest request) { return Call("Preview"); }
        [RevAutomationAction("game.audio-settings.slider-layout", Environment = "edit", Keys = new string[0], Description = "只调整已有声音滑杆尺寸，保留用户图片和其它控件布局")]
        public static object SliderLayout(RevAutomationRequest request) { return Call("AdjustSlider"); }
        private static object Call(string method)
        {
            Type type = Type.GetType("RevGame.Editor.GameAudioSettingsBuild, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod(method).Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}
