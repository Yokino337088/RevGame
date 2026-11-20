// RevHotAutomation.cs —— 可选热更包主动注册 CLI，框架本体不反向依赖本扩展包。
using System;
using System.IO;
using Revolution.Editor;
using UnityEngine;

namespace Revolution.HotUpdate.Editor
{
    public static class RevHotAutomation
    {
        [RevAutomationAction("hot.manifest", Keys = new[] { "target", "outputDir", "appVersion", "resVersion", "channel", "env" }, Description = "构建并校验热更清单")]
        public static object Manifest(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<RevAutomationOptions>(request.payloadJson);
            var target = RevAutomationActions.Target(options.target);
            string output = string.IsNullOrEmpty(options.outputDir) ? ABBuildSetting.GetOutputDir(target) : options.outputDir;
            string app = string.IsNullOrEmpty(options.appVersion) ? Application.version : options.appVersion;
            var manifest = RevHotManifestBuilder.Build(target, output, app, options.resVersion, options.channel, options.env, out string error);
            if (manifest == null) { throw new InvalidOperationException(error); }
            RevHotManifestBuilder.WriteOutputs(manifest, output);
            error = RevHotManifestBuilder.SelfCheck(manifest, output);
            if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
            return new RevAutomationActions.RevAutomationText(Path.GetFullPath(output));
        }

        [RevAutomationAction("hot.check", Keys = new[] { "target", "outputDir" }, Description = "检查热更清单和真实产物一致性")]
        public static object Check(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<RevAutomationOptions>(request.payloadJson);
            string output = string.IsNullOrEmpty(options.outputDir) ? ABBuildSetting.GetOutputDir(RevAutomationActions.Target(options.target)) : options.outputDir;
            string path = Path.Combine(output, new RevHotConfig().ManifestFileName);
            var manifest = RevHotManifest.Parse(File.ReadAllText(path), out string error);
            if (manifest == null) { throw new InvalidOperationException(error); }
            error = RevHotManifestBuilder.SelfCheck(manifest, output);
            if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
            return new RevAutomationActions.RevAutomationText("清单与产物一致：" + Path.GetFullPath(path));
        }
    }
}
