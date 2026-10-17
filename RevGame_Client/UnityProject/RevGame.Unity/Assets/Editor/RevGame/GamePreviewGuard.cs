// ============================================================
// GamePreviewGuard.cs —— Unity 2022.3 双预览的编辑器保护。
// 本项目已复现：Game(1920×1080) 与 Simulator(3088×1440) 同时保留时，
// 调整预览比例会出现“一幅小画面盖在大画面上”；场景实际上只有一台相机和一个根画布。
// 单独保留当前使用的预览后恢复正常。这里只处理编辑器标签，不修改手机运行时代码或框架快照。
// ============================================================
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    [InitializeOnLoad]
    public static class GamePreviewGuard
    {
        private static double _nextCheck;
        public static string LastRepair { get; private set; } = "尚未需要修复";

        static GamePreviewGuard()
        {
            EditorApplication.update += CheckPreviewWindows;
        }

        private static void CheckPreviewWindows()
        {
            // 每半秒检查一次；编译和进出 Play 时不碰窗口，避免在域重载的交接过程操作旧对象。
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating) { return; }
            if (EditorApplication.timeSinceStartup < _nextCheck) { return; }
            _nextCheck = EditorApplication.timeSinceStartup + 0.5d;
            RepairDuplicatePreview();
        }

        /// <summary>只在本项目的 2022.3 游戏运行时去掉重复预览，Unity 编辑器和当前预览始终保留。</summary>
        public static string RepairDuplicatePreview()
        {
            if (!Application.unityVersion.StartsWith("2022.3.") || !Application.isPlaying) { return "当前版本/状态不需要保护"; }
            if (Object.FindObjectOfType<Bootstrap.GameBootstrap>() == null) { return "当前不是 RevGame 场景"; }
            EditorWindow game = null;
            EditorWindow simulator = null;
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                // 使用类型全名，而不是可能被本地化的标签文字；不访问 Unity 私有渲染字段。
                if (window.GetType().FullName == "UnityEditor.GameView") { game = window; }
                if (window.GetType().FullName == "UnityEditor.DeviceSimulation.SimulatorWindow") { simulator = window; }
            }
            if (game == null || simulator == null) { return "只有一个游戏预览，无需修复"; }
            // 人工正看 Game 时保留 Game；其它情况下优先保留 Simulator，继续使用用户选定的手机型号。
            EditorWindow keep = EditorWindow.focusedWindow == game ? game : simulator;
            EditorWindow duplicate = keep == game ? simulator : game;
            string closedTitle = duplicate.titleContent.text;
            duplicate.Close();
            keep.Repaint();
            LastRepair = "已保留 " + keep.titleContent.text + "，关闭重复的 " + closedTitle + " 预览标签，避免双尺寸画面叠加";
            return LastRepair;
        }
    }
}
