using System;
using System.IO;
using System.Collections.Generic;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.Build.Player;
namespace RevGame.Editor
{
    /// <summary>只编译兼容当前Windows Mono开发包的Player脚本，不触发BuildPlayer，不覆盖编辑器DLL。</summary>
    public static class GameAgentPatchBuild
    {
        [RevAutomationAction("game.pvp.compile-mono-patch", Environment = "edit", Keys = new string[0], Description = "仅编译Mono Player脚本到忽略目录，供增量更新")]
        public static object Compile(RevAutomationRequest request)
        {
            string output = Path.GetFullPath("../../../.codex/work/agent-timeout/player-dll");
            Directory.CreateDirectory(output);
            var before = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            try
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var settings = new ScriptCompilationSettings
                {
                    target = BuildTarget.StandaloneWindows64, group = BuildTargetGroup.Standalone,
                    options = ScriptCompilationOptions.DevelopmentBuild,
                    extraScriptingDefines = new[] { "REV_AUTOMATION" }
                };
                var result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
                if (result.assemblies == null || result.assemblies.Count == 0)
                {
                    throw new InvalidOperationException("Player脚本编译没有产出程序集");
                }
                return new Dictionary<string, object> { ["output"] = output, ["count"] = result.assemblies.Count };
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, before);
            }
        }
    }
}
