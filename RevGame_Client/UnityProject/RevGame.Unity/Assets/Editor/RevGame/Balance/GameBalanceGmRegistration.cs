// Editor编目从GameGmCommands唯一账本接入；不修改框架或正式包，GM重用同一数值测试宿主。
using RevGame.Bootstrap;
using Revolution;
using UnityEditor;

namespace RevGame.Editor
{
    [InitializeOnLoad]
    public static class GameBalanceGmRegistration
    {
        static GameBalanceGmRegistration(){EditorApplication.playModeStateChanged+=OnPlayState;}
        private static void OnPlayState(PlayModeStateChange change)
        {
            if(change!=PlayModeStateChange.EnteredPlayMode){return;}
            EditorApplication.delayCall+=Register;
        }
        private static void Register()
        {
            if(!EditorApplication.isPlaying){return;}
            // 唯一账本负责退出注销；Editor测试类不进入正式发布包。
            GameGmCommands.RegisterEditorBalanceCommands();
        }
    }
}
