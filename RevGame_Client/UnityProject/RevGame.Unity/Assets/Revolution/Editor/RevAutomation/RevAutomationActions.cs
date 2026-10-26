// RevAutomationActions.cs —— 把既有编辑器能力接到 CLI；仍调用原工具的校验、生成器与打包链。
// 不调用会 Exit 的旧 CI 方法，也不靠点击窗口按钮假装无人值守完成。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Revolution.Editor.ExcelTool;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    // ============================================================================
    //  RevAutomationActions —— 所有动作的"分发中心"
    //
    //  外部发来 <c>action="ab.build"</c>，最终就是走到这里的 <see cref="Execute"/>：
    //    1. 先把参数 JSON 解析成 RevAutomationOptions；
    //    2. 按 action 字符串分派到具体实现（AB 打包 / Excel 导表 / UI 生成 / GM / 菜单 …）；
    //    3. 返回一个对象，由上层序列化成 dataJson 写回给外部。
    //
    //  【49 个动作分四类】
    //    · editor.*   编辑器状态/刷新/菜单
    //    · gm.*       执行项目自己注册的 GM 指令（需要 Play 且未暂停）
    //    · ab.*       资源包：标记、扫描、依赖、构建、拷贝首包…
    //    · excel.*    导表：读取、计划、导出、清理孤儿文件
    //    · ui.* / platform.* / demo.*   脚手架类工具
    //
    //  ⚠️ 三条最容易踩的规则（新手必读）：
    //    1. **资产类动作必须退出 Play**（改资源时 Unity 在运行会出各种怪问题）→ RequireEditMode；
    //    2. **路径必须在当前工程 Assets/ 内**（防止 ../ 逃出工程）→ RequireAssetPath；
    //    3. 执行期间 Unity 只要打了 Error/Exception 日志，整个请求就算失败（见 Bridge）。
    // ============================================================================

    public static class RevAutomationActions
    {
        [Serializable]
        public sealed class RevAutomationText
        {
            public string text;
            public RevAutomationText(string value) { text = value; }
        }

        [Serializable]
        public sealed class RevAutomationRows
        {
            public string[] items;
            public RevAutomationRows(IEnumerable<string> values) { items = values.ToArray(); }
        }

        [Serializable]
        private sealed class RevAutomationGMInfo
        {
            public string name;
            public string description;
            public string signature;
            public bool highRisk;
            public bool hidden;
        }

        [Serializable]
        private sealed class RevAutomationGMList
        {
            public bool playing;
            public RevAutomationGMInfo[] commands;
            public string[] registrationErrors;
        }

        [Serializable]
        private sealed class RevAutomationGMResult
        {
            public bool success;
            public string message;
            public double elapsedMs;
        }

        [Serializable]
        private sealed class RevAutomationExcelSettings
        {
            public string projectJson;
            public string userJson;
        }

        [Serializable]
        private sealed class RevAutomationStatus
        {
            public string unityVersion;
            public string project;
            public bool playing;
            public bool paused;
            public bool compiling;
            public bool updating;
            public bool gmEnabled;
            public int gmCount;
        }

        /// <summary>
        /// 动作分发入口：把 <paramref name="request"/> 里的 action 字符串翻译成具体调用。
        /// 返回值会被上层序列化成 JSON 写进 response.json 的 dataJson 字段。
        ///
        /// 阅读顺序建议：先看下面 editor.* 的分支（最简单），再看 ab./excel.（最复杂），
        /// 最后看末尾用特性扫描出来的"扩展动作"（如 hot.*）。
        /// </summary>

        [Serializable]
        private sealed class RevAutomationBatchPayload
        {
            public RevAutomationBatchStep[] steps;
            public bool stopOnError = true;
        }

        [Serializable]
        private sealed class RevAutomationBatchStep
        {
            public string action;
            public string payloadJson;
            public bool confirm;
        }

        [Serializable]
        private sealed class RevAutomationBatchStepResult
        {
            public int index;
            public string action;
            public bool success;
            public string message;
            public string dataJson;
        }

        [Serializable]
        private sealed class RevAutomationBatchResult
        {
            public bool success;
            public int total;
            public int failed;
            public double elapsedMs;
            public RevAutomationBatchStepResult[] results;
        }

        /// <summary>
        /// 一次请求执行多个动作，省掉"每步一次往返"。
        ///
        /// 为什么值得做：实测单次往返约 110ms，而耗时几乎全在编辑器 tick 上，
        /// 压轮询间隔没有意义；把 N 步压进一次请求，才是真正的数量级改善。
        ///
        /// 边界：
        /// - 每步仍走同一个 Execute 分发，参数校验、edit/play 限制和 confirm 规则不变；
        /// - 不允许嵌套 batch，避免超时与错误归属变得不可推理；
        /// - 默认遇错即停（stopOnError=false 可继续），因为后续步骤常常依赖前一步的状态。
        /// </summary>
        /// <summary>发起后需要等待完成、或长耗时阻塞的动作，不适合塞进同帧批量。</summary>
        private static readonly string[] BatchDeferredActions =
        {
            "editor.refresh", "editor.menu", "editor.play", "editor.stop", "editor.pause", "editor.resume", "editor.step",
        };

        private static string BatchRejectReason(string action)
        {
            string environment = RevAutomationCatalog.EnvironmentOf(action);
            if (environment == "live" || environment == "play")
            {
                return "该动作需要 Play 或运行时状态，无法在同一帧内完成；请改用 pipeline 或计划 JSON";
            }
            foreach (string deferred in BatchDeferredActions)
            {
                if (action == deferred)
                {
                    return "该动作发起后需要等待（刷新/菜单/Play 切换），放进批量会读到旧状态";
                }
            }
            if (action.EndsWith(".build") || action.StartsWith("build."))
            {
                return "构建类动作耗时长且不适合批量；请单独调用以便看到进度";
            }
            return null;
        }

        private static object ExecuteBatch(RevAutomationRequest request)
        {
            var payload = JsonUtility.FromJson<RevAutomationBatchPayload>(request.payloadJson ?? "{}");
            if (payload == null || payload.steps == null || payload.steps.Length == 0)
            {
                throw new ArgumentException("batch.call 需要非空 steps 数组");
            }
            if (payload.steps.Length > 64)
            {
                throw new ArgumentException("batch.call 单次最多 64 步；更多步骤请拆成多次，便于定位失败");
            }
            var started = DateTime.UtcNow;
            var results = new List<RevAutomationBatchStepResult>(payload.steps.Length);
            int failed = 0;
            for (int i = 0; i < payload.steps.Length; i++)
            {
                var step = payload.steps[i];
                var item = new RevAutomationBatchStepResult { index = i, action = step?.action };
                if (step == null || string.IsNullOrEmpty(step.action))
                {
                    item.success = false;
                    item.message = "步骤缺少 action";
                    results.Add(item);
                    failed++;
                    if (payload.stopOnError) { break; }
                    continue;
                }
                if (step.action == "batch.call")
                {
                    item.success = false;
                    item.message = "batch.call 不允许嵌套";
                    results.Add(item);
                    failed++;
                    if (payload.stopOnError) { break; }
                    continue;
                }
                // 批量是"同一帧内同步跑完"，所以必须挡掉两类动作，否则后续步骤会读到旧状态：
                // 1) 需要 Play / 运行时状态的（live、play）；
                // 2) 发起后要等一帧或长时间阻塞的（刷新、菜单、各种构建）。
                // 运行期/UI/战斗这类多帧验证请继续用 pipeline 或计划 JSON，那里有等待与断言。
                string reason = BatchRejectReason(step.action);
                if (reason != null)
                {
                    item.success = false;
                    item.message = reason;
                    results.Add(item);
                    failed++;
                    if (payload.stopOnError) { break; }
                    continue;
                }
                var sub = new RevAutomationRequest
                {
                    protocol = request.protocol,
                    id = request.id,
                    action = step.action,
                    payloadJson = string.IsNullOrEmpty(step.payloadJson) ? "{}" : step.payloadJson,
                    sessionId = request.sessionId,
                    timeoutSeconds = request.timeoutSeconds,
                    deadlineUtc = request.deadlineUtc,
                    // 外层 confirm 视为整批授权；单步也可自行声明，避免批处理绕过高危确认。
                    confirm = step.confirm || request.confirm,
                };
                try
                {
                    object value = Execute(sub);
                    item.success = true;
                    item.message = "完成";
                    item.dataJson = value == null ? "" : JsonUtility.ToJson(value, false);
                }
                catch (Exception error)
                {
                    item.success = false;
                    item.message = error.GetType().Name + ": " + error.Message;
                    failed++;
                }
                results.Add(item);
                if (!item.success && payload.stopOnError) { break; }
            }
            return new RevAutomationBatchResult
            {
                success = failed == 0,
                total = results.Count,
                failed = failed,
                elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
                results = results.ToArray(),
            };
        }

        public static object Execute(RevAutomationRequest request)
        {
            RevAutomationCatalog.ValidatePayload(request);
            var p = JsonUtility.FromJson<RevAutomationOptions>(request.payloadJson ?? "{}");
            if (p == null) { throw new ArgumentException("payloadJson 必须是 JSON 对象"); }
            string action = request.action;
            if (action == "batch.call") { return ExecuteBatch(request); }
            if (action == "hot.demo-prepare") { RequireEditMode(); ValidateCopyDestination(Target(null)); }
            MethodInfo extension = RevAutomationCatalog.Extension(action);
            if (extension != null) { return RevAutomationCatalog.Invoke(extension, request); }
            if (action == "editor.status")
            {
                return new RevAutomationStatus { unityVersion = Application.unityVersion, project = Path.GetFullPath("."),
                    playing = EditorApplication.isPlaying, paused = EditorApplication.isPaused, compiling = EditorApplication.isCompiling,
                    updating = EditorApplication.isUpdating, gmEnabled = RevGM.Enabled, gmCount = RevGM.Count };
            }
            if (action == "editor.refresh") { RequireEditMode(); AssetDatabase.Refresh(); return new RevAutomationText("已请求资源刷新；后续编译状态请用 editor.status 检查"); }
            if (action == "editor.menus") { return new RevAutomationRows(MenuMethods().Select(pair => pair.Key).OrderBy(name => name)); }
            if (action == "editor.menu") { RequireEditMode(); return ExecuteMenu(p.path); }
            if (action == "gm.list")
            {
                return new RevAutomationGMList
                {
                    playing = EditorApplication.isPlaying,
                    commands = RevGMEditorCatalog.Commands.Select(c => new RevAutomationGMInfo
                    { name = c.Name, description = c.Description, signature = c.Signature(), highRisk = c.IsHighRisk, hidden = c.IsHidden }).ToArray(),
                    registrationErrors = RevGMEditorCatalog.Entries.Where(e => !string.IsNullOrEmpty(e.Error)).Select(e => e.Display + ": " + e.Error).ToArray()
                };
            }
            if (action == "gm.exec")
            {
                RevGMResult result = ExecuteGM(p.command, request.confirm);
                if (!result.Success) { throw new InvalidOperationException(result.Message); }
                return new RevAutomationGMResult { success = result.Success, message = result.Message, elapsedMs = result.ElapsedMs };
            }
            RequireEditMode();
            if (action.StartsWith("ab.", StringComparison.Ordinal)) { return AssetBundles(action, p, request.confirm); }
            if (action.StartsWith("excel.", StringComparison.Ordinal)) { return Excel(action, p, request.confirm); }
            if (action.StartsWith("ui.", StringComparison.Ordinal)) { return RevUIPanelScriptWindow.Automate(action, p, request.confirm); }
            if (action == "platform.resources") { return new RevAutomationText(RevFirstPackageAudit.BuildReport()); }
            if (action == "platform.linkxml") { return new RevAutomationText(RevLinkXmlGenerator.GenerateForAutomation(request.confirm)); }
            if (action == "demo.ui-prefabs")
            {
                if (!request.confirm) { throw new InvalidOperationException("重新生成演示预制体会覆盖演示资产，需要 --confirm"); }
                if (!EditorApplication.ExecuteMenuItem("Revolution.Tools/Demo/生成 UI 演示预制体（全部）"))
                { throw new InvalidOperationException("UI 演示菜单不存在，请安装 Demo"); }
                return new RevAutomationText("已生成 UI 演示预制体");
            }

            throw new ArgumentException("动作不存在（可选扩展可能未安装）：" + action);

        }

        /// <summary>
        /// 守卫：要求"当前不在 Play 模式"。
        /// 为什么：AB 打包、导表、生成 UI 脚本这些动作会改工程资源，Play 模式下改资源
        /// 轻则改不进去、重则触发域重载把正在跑的逻辑打断。
        /// 失败会抛异常，由上层转成 error 码写回外部。
        /// </summary>

        public static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("编辑器资产操作需要先退出 Play"); }
        }

        /// <summary>
        /// 把外部传进来的平台名（如 "Android"/"StandaloneWindows64"）转成 <see cref="BuildTarget"/>。
        /// 传空字符串 = 用当前编辑器激活的平台；传了但不认识 → 直接抛错（不猜）。
        /// </summary>

        public static BuildTarget Target(string name)
        {
            if (string.IsNullOrEmpty(name)) { return EditorUserBuildSettings.activeBuildTarget; }
            if (!Enum.TryParse(name, out BuildTarget target) || !Enum.IsDefined(typeof(BuildTarget), target))
            {
                throw new ArgumentException("未知 Unity BuildTarget：" + name);
            }
            return target;
        }

        /// <summary>
        /// 执行一条 GM 指令（项目自己用 RevGM.Register 注册的业务命令）。
        ///
        /// 三个要点：
        /// 1. 必须"已进入 Play 且未暂停"—— GM 是给运行中的游戏用的；
        /// 2. 会先解析出**命令名**做高危判定：高危命令必须带 confirm=true，防止脚本误删数据；
        /// 3. 命令不存在时这里不报错，交给 RevGM.Execute 返回业务错误（保持错误信息单一来源）。
        /// </summary>

        public static RevGMResult ExecuteGM(string line, bool confirm)
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) { throw new InvalidOperationException("GM 执行需要已进入且未暂停的 Play"); }
            // 这里只读取命令名检查 HighRisk；参数仍完整交给 RevGM.Execute 解析。
            // 命令名也可能加引号，必须按运行期相同的三种分隔符与引号规则处理，不能只 Split(' ')。
            var token = new StringBuilder();
            bool quoted = false, started = false;
            foreach (char c in line ?? "")
            {
                if (c == '"') { quoted = !quoted; started = true; continue; }
                if (!quoted && (c == ' ' || c == '\t' || c == '\u3000'))
                { if (started) { break; } continue; }
                started = true;
                token.Append(c);
            }
            string name = token.ToString();
            if (!RevGM.TryGet(name, out RevGMCommand command)) { return RevGM.Execute(line); }
            if (command.IsHighRisk && !confirm) { throw new InvalidOperationException("高危 GM 需要 CLI --confirm：" + command.Name); }
            return RevGM.Execute(line);
        }

        private static IEnumerable<KeyValuePair<string, MethodInfo>> MenuMethods()
        {
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                foreach (MenuItem menu in method.GetCustomAttributes<MenuItem>())
                {
                    if (menu.validate || !menu.menuItem.StartsWith("Revolution.Tools/", StringComparison.Ordinal)) { continue; }
                    yield return new KeyValuePair<string, MethodInfo>(menu.menuItem.Split(' ')[0] == menu.menuItem ? menu.menuItem : StripShortcut(menu.menuItem), method);
                }
            }
        }

        private static string StripShortcut(string value)
        {
            int space = value.LastIndexOf(' ');
            if (space >= 0 && value.Substring(space + 1).Any(c => "%#&_".Contains(c))) { return value.Substring(0, space); }
            return value;
        }

        /// <summary>
        /// 打开一个编辑器工具窗口（注意：**只是打开窗口**，不是"点菜单里的任意按钮"）。
        /// 为什么限制这么死：菜单项可能弹出确认对话框，脚本调用会卡在弹窗上永远不返回，
        /// 所以只允许白名单里的 6 个工具窗口，其余路径一律报错。
        /// </summary>

        private static object ExecuteMenu(string path)
        {
            if (Application.isBatchMode) { throw new InvalidOperationException("菜单窗口需要在线编辑器；无人值守请使用对应的有结果动作"); }
            var allowed = new[] { "Revolution.Tools/资源/RevAB 打包工具", "Revolution.Tools/资源/RevAB 分包浏览", "Revolution.Tools/GM 指令面板",
                "Revolution.Tools/UI/面板代码生成器", "Revolution.Tools/配置表/导表工具", "Revolution.Tools/热更新/热更清单窗口", "Revolution.Tools/平台/包体大小预测", "Revolution.Tools/AI/RevMCP 控制台" };
            if (!allowed.Contains(path)) { throw new ArgumentException("editor.menu 仅负责打开工具窗口；执行生成/删除请使用明确的 CLI 动作，避免阻塞确认对话框"); }
            if (!EditorApplication.ExecuteMenuItem(path)) { throw new InvalidOperationException("菜单未执行：" + path); }
            return new RevAutomationText("已打开：" + path);
        }

        /// <summary>
        /// AB（AssetBundle）相关动作总入口，共 23 个：标记 / 改名 / 删除标记 / 扫描 / 依赖 /
        /// 体积估算 / 快照与差异 / 校验 / 构建 / 拷贝首包 …
        ///
        /// 新手最容易误解的两点：
        /// · <c>ab.mark</c> 里 **bundle 传空字符串表示"移除标记"**，不是"不设包名"；
        /// · <c>ab.sizes</c> 给的是**源码字节估算**，真实压缩后大小要看 <c>ab.artifacts</c>（产物文件）。
        /// </summary>

        private static object AssetBundles(string action, RevAutomationOptions p, bool confirm)
        {
            ABBuildConfig config = ABBuildConfig.Instance;
            BuildTarget target = Target(p.target);
            switch (action)
            {
                case "ab.config": return config;
                case "ab.configure":
                    if (string.IsNullOrEmpty(p.configJson)) { throw new ArgumentException("需要 configJson（配置字段 JSON 字符串）"); }
                    JsonUtility.FromJsonOverwrite(p.configJson, config);
                    config.ApplyToRuntime();
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssets();
                    return config;
                case "ab.res-paths":
                    if (!config.HasResRoot || !AssetDatabase.IsValidFolder(config.GetResRoot())) { throw new InvalidOperationException("先配置有效的资源根目录 resRoot"); }
                    if (!ABBuildSetting.EnsureWritableForGeneratedCode("RevResPath", ABBuildSetting.ResPathCodePath)) { throw new InvalidOperationException("资源路径生成位置不可写"); }
                    ABResPathGenerator.Generate(); return new RevAutomationText("已生成 RevResPath");
                case "ab.sound-paths":
                    if (!ABBuildSetting.EnsureWritableForGeneratedCode("RevSoundPath", ABBuildSetting.SoundPathCodePath)) { throw new InvalidOperationException("音效路径生成位置不可写"); }
                    ABSoundPathGenerator.Generate(); return new RevAutomationText("已生成 RevSoundPath");
                case "ab.mode": RevResBootstrap.UseABInEditor = p.enabled; return new RevAutomationText("UseABInEditor=" + p.enabled);
                case "ab.overlay": ABProjectWindowOverlay.Enabled = p.enabled; return new RevAutomationText("Project AB 标记显示=" + p.enabled);
                case "ab.sync": ABMarkerWatcher.RefreshNow("CLI 强制同步"); return new RevAutomationText("已刷新标记与扫描缓存");
                case "ab.mark-source":
                    RequireAssetPath(p.path);
                    return new RevAutomationText(ABBundleEditor.FindMarkSource(p.path) ?? "没有显式或继承标记");
                case "ab.clean-names": return new RevAutomationText("清理闲置包名数：" + ABBundleEditor.RemoveUnusedNames());
                case "ab.rename":
                case "ab.delete":
                    if (!confirm) { throw new InvalidOperationException("批量改名/移除包标记需要 --confirm"); }
                    string oldName = ABBundleEditor.Normalize(p.bundle);
                    if (!ABBundleEditor.IsLegalBundleName(oldName, out string why)) { throw new ArgumentException(why); }
                    int changed;
                    if (action == "ab.rename")
                    {
                        string newName = ABBundleEditor.Normalize(p.newBundle);
                        if (!ABBundleEditor.IsLegalBundleName(newName, out why)) { throw new ArgumentException(why); }
                        if (ABBundleEditor.IsUnder(newName, oldName)) { throw new ArgumentException("不能把包移动到自身内部"); }
                        changed = ABBundleEditor.RenamePrefix(oldName, newName);
                    }
                    else { changed = ABBundleEditor.DeletePrefix(oldName); }
                    ABMarkerWatcher.RefreshNow("CLI 修改包名");
                    return new RevAutomationText("修改标记数：" + changed);
                case "ab.mark":
                    if (p.assets == null || p.assets.Length == 0 || p.bundle == null) { throw new ArgumentException("需要 assets[] 和 bundle；空 bundle 表示移除标记"); }
                    foreach (string asset in p.assets) { RequireAssetPath(asset); if (AssetImporter.GetAtPath(asset) == null) { throw new ArgumentException("资产不存在：" + asset); } }
                    string bundle = ABBundleEditor.Normalize(p.bundle);
                    if (bundle.Length > 0 && !ABBundleEditor.IsLegalBundleName(bundle, out string reason)) { throw new ArgumentException(reason); }
                    if (bundle.Length == 0) { ABBundleEditor.ClearMarks(p.assets); }
                    else { ABBundleEditor.AddAssets(bundle, p.assets, out _, out _); }
                    ABMarkerWatcher.RefreshNow("CLI 标记资源");
                    return new RevAutomationRows(p.assets);
                case "ab.copy":
                    if (!config.copyToStreamingAssets) { throw new InvalidOperationException("请先 ab.configure 开启 copyToStreamingAssets"); }
                    ValidateCopyDestination(target);
                    if (!Directory.Exists(ABBuildSetting.GetOutputDir(target))) { throw new DirectoryNotFoundException("未找到对应平台产物，请先 ab.build"); }
                    ABBuilderCore.CopyToStreamingAssets(ABBuildSetting.GetOutputDir(target));
                    return new RevAutomationText("已按配置拷贝首包");
                case "ab.artifacts":
                    string output = ABBuildSetting.GetOutputDir(target);
                    if (!Directory.Exists(output)) { throw new DirectoryNotFoundException(output); }
                    return new RevAutomationRows(Directory.GetFiles(output, "*", SearchOption.AllDirectories).OrderBy(x => x)
                        .Select(x => new FileInfo(x).Length + " bytes\t" + Path.GetFullPath(x)));
                case "ab.map":
                    int count = ABMapGenerator.Generate("CLI", out ABValidateResult mapValidation);
                    if (count < 0) { throw new InvalidOperationException(string.Join("\n", mapValidation.errors)); }
                    return new RevAutomationText("映射条数：" + count);
                case "ab.check-map":
                    ABCollectCache.Invalidate();
                    ABResMapCheckReport map = ABResMapChecker.Check(target);
                    if (map.Errors.Count > 0) { throw new InvalidOperationException(string.Join("\n", map.Errors)); }
                    return new RevAutomationRows(map.Warnings);
                case "ab.snapshot":
                    ABLayoutSnapshot snapshot = ABLayoutSnapshotStore.Capture("CLI");
                    ABLayoutSnapshotStore.Save(ABLayoutSnapshotStore.BaselinePath, snapshot);
                    return snapshot;
                case "ab.diff":
                    string baseline = string.IsNullOrEmpty(p.baseline) ? ABLayoutSnapshotStore.BaselinePath : p.baseline;
                    if (!File.Exists(baseline)) { throw new FileNotFoundException("没有布局基准，请先 ab.snapshot", baseline); }
                    ABLayoutSnapshot before = ABLayoutSnapshotStore.Load(baseline);
                    if (before == null) { throw new InvalidOperationException("基准快照损坏：" + baseline); }
                    ABLayoutDiff diff = ABLayoutDiffer.Compare(before, ABLayoutSnapshotStore.Capture("CLI diff"));
                    return new RevAutomationRows(new[] { $"包 {diff.bundlesBefore}→{diff.bundlesAfter}; 资源 {diff.assetsBefore}→{diff.assetsAfter}; 源字节 {diff.bytesBefore}→{diff.bytesAfter}" }
                        .Concat(diff.added.Select(x => "+ " + x.name)).Concat(diff.removed.Select(x => "- " + x.name))
                        .Concat(diff.renamed.Select(x => "rename " + x.before + " -> " + x.after))
                        .Concat(diff.moved.Select(x => "move " + x.asset + ": " + x.from + " -> " + x.to))
                        .Concat(diff.changed.Select(x => "change " + x.name + ": +" + string.Join(",", x.added) + " -" + string.Join(",", x.removed))));
            }
            ABCollectResult collect = action == "ab.build" ? ABCollector.Collect() : ABCollector.CollectReadOnly();
            if (action == "ab.scan") { return ABLayoutSnapshotStore.From(collect, "CLI scan"); }
            if (action == "ab.sizes")
            {
                // 源文件与依赖源文件大小用于编辑器估算；真实压缩后 AB 大小请用 ab.artifacts。
                var rows = new List<string>();
                foreach (var pair in collect.bundleToAssets.OrderBy(x => x.Key))
                {
                    string[] assets = pair.Value.Select(logic => collect.logicToAssetPath[logic]).ToArray();
                    string[] dependencies = AssetDatabase.GetDependencies(assets, true).Distinct().ToArray();
                    long own = assets.Where(File.Exists).Sum(x => new FileInfo(x).Length);
                    long withDeps = dependencies.Where(File.Exists).Sum(x => new FileInfo(x).Length);
                    rows.Add(pair.Key + ": sourceBytes=" + own + ", sourceWithDependenciesBytes=" + withDeps);
                }
                return new RevAutomationRows(rows);
            }
            if (action == "ab.dependencies")
            {
                ABDependencyReport dep = ABDependencyAnalyzer.AnalyzeFromDatabase(collect);
                return new RevAutomationRows(dep.directDeps.Select(x => x.Key + " -> " + string.Join(", ", x.Value))
                    .Concat(dep.sharedAssets.Select(x => "shared: " + x.Key + " -> " + string.Join(", ", x.Value)))
                    .Concat(dep.circularBundles.Select(x => "cycle: " + x)));
            }
            if (action != "ab.build" && action != "ab.validate") { throw new ArgumentException("未知 AB 动作：" + action); }
            if (action == "ab.build") { ValidateCopyDestination(target); }
            ABValidateResult validation = ABValidator.Validate(collect);
            ABValidator.ValidateAndroidShaders(collect, validation, target);
            if (!validation.CanBuild) { throw new InvalidOperationException(string.Join("\n", validation.errors)); }
            if (action == "ab.validate") { return new RevAutomationRows(validation.warnings); }
            ABBuildResult build = ABBuilderCore.Build(target);
            if (build == null || !build.success) { throw new InvalidOperationException("AB 打包失败，详见 Unity 日志"); }
            ABDependencyReport dependency = ABDependencyAnalyzer.Analyze(build.manifest.raw);
            if (dependency.circularBundles.Count > 0) { throw new InvalidOperationException("AB 存在循环依赖：" + string.Join(",", dependency.circularBundles)); }
            ABManifestWriter.WriteBuildManifest(build);
            ABMapGenerator.WriteOutputs(collect, "CLI build");
            ABBuilderCore.CopyToStreamingAssets(build.outputDir);
            return new RevAutomationText("已构建 " + build.manifest.allBundles.Length + " 包 -> " + build.outputDir);
        }

        /// <summary>
        /// 守卫：路径必须落在**当前工程的 Assets/** 下。
        /// 目的：外部 JSON 里的路径是不可信输入，必须挡住 "../../" 之类的逃逸写法，
        /// 否则一条脚本就能改到工程外面的文件。
        /// </summary>

        public static void RequireAssetPath(string path)
        {
            string assets = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path ?? "");
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("路径必须在当前工程 Assets 内：" + path); }
        }

        private static void ValidateCopyDestination(BuildTarget target)
        {
            ABBuildConfig config = ABBuildConfig.Instance;
            if (!config.copyToStreamingAssets) { return; }
            string assets = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            string folder = Path.Combine("Assets", config.copyTarget ?? "", ABBuildSetting.GetPlatformName(target));
            string destination = Path.GetFullPath(folder);
            if (string.IsNullOrWhiteSpace(config.copyTarget) || Path.IsPathRooted(config.copyTarget)
                || !destination.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("首包拷贝目标必须在当前工程 Assets 子目录内：" + destination); }
            // 原工具会先递归删除目标再拷贝，不能让 junction 把清理导向工程外目录。
            for (var directory = new DirectoryInfo(destination); directory != null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                { throw new InvalidOperationException("首包拷贝路径包含重解析点：" + directory.FullName); }
            }
        }

        /// <summary>
        /// 导表相关动作总入口，共 8 个：读表 / 出计划 / 查孤儿文件 / 清理 / 导出 …
        /// 注意：<c>excel.mark</c> 复用 <c>bundle</c> 字段来传"数据目录"，语义和 AB 的 bundle 完全不同，
        /// 这是为了少加一个参数，读代码时别被绕进去。
        /// </summary>

        private static object Excel(string action, RevAutomationOptions p, bool confirm)
        {
            if (action == "excel.settings")
            {
                return new RevAutomationExcelSettings { projectJson = JsonUtility.ToJson(RevExcelProjectSettings.instance), userJson = JsonUtility.ToJson(RevExcelUserSettings.instance) };
            }
            if (action == "excel.configure")
            {
                if (string.IsNullOrEmpty(p.configJson) && string.IsNullOrEmpty(p.userConfigJson)) { throw new ArgumentException("需要 configJson 或 userConfigJson"); }
                if (!string.IsNullOrEmpty(p.configJson))
                {
                    JsonUtility.FromJsonOverwrite(p.configJson, RevExcelProjectSettings.instance);
                    RevExcelProjectSettings.instance.SaveNow();
                }
                if (!string.IsNullOrEmpty(p.userConfigJson))
                {
                    JsonUtility.FromJsonOverwrite(p.userConfigJson, RevExcelUserSettings.instance);
                    RevExcelUserSettings.instance.SaveNow();
                }
                return new RevAutomationExcelSettings { projectJson = JsonUtility.ToJson(RevExcelProjectSettings.instance), userJson = JsonUtility.ToJson(RevExcelUserSettings.instance) };
            }
            if (action == "excel.mark")
            {
                if (!RevExcelService.MarkDataDir(p.bundle)) { throw new InvalidOperationException("数据目录标记失败，请确认资源根和数据目录配置"); }
                return new RevAutomationText("已标记配置表数据目录");
            }
            var sources = p.sources ?? RevExcelUserSettings.instance.sources.ToArray();
            RevExcelReadResult read = RevExcelService.Read(sources, p.recursive, false);
            var errors = read.FileErrors.Select(x => x.Key + ": " + x.Value)
                .Concat(read.Tables.Where(t => !t.Ignored && !t.IsValid).SelectMany(t => t.Errors.Select(e => t.Name + ": " + e.Message))).ToArray();
            if (errors.Length > 0 || read.Files.Count == 0) { throw new InvalidOperationException("Excel 读取失败或无输入：\n" + string.Join("\n", errors)); }
            if (action == "excel.read") { return new RevAutomationRows(read.Tables.Select(t => t.Name + (t.Ignored ? " [ignored]" : " [valid]"))); }
            ExcelExportPlan plan = RevExcelService.Plan(read.Tables);
            if (!plan.CanExport) { throw new InvalidOperationException(string.Join("\n", plan.Errors)); }
            if (action == "excel.plan") { return new RevAutomationRows(plan.Files.Select(f => f.Change + " " + f.Path).Concat(plan.RemovedTables.Select(t => "removed table: " + t))); }
            if (action == "excel.orphans") { return new RevAutomationRows(plan.OrphanDataFiles); }
            if (action == "excel.clean")
            {
                if (!confirm) { throw new InvalidOperationException("删除旧数据文件需要 --confirm；先用 excel.orphans 查看清单"); }
                foreach (string file in plan.OrphanDataFiles) { RequireAssetPath(file); }
                return new RevAutomationText("删除文件数：" + RevExcelService.DeleteOrphans(plan.OrphanDataFiles));
            }
            if (action != "excel.export") { throw new ArgumentException("未知 Excel 动作：" + action); }
            RevExcelReport report = RevExcelService.Export(read.Tables, p.force, removed => confirm, p.dataOnly ? ExcelExportMode.DataOnly : ExcelExportMode.Full);
            if (report.cancelled || !report.success || report.mapFailed) { throw new InvalidOperationException("导出失败/取消（移除旧表需 --confirm）：" + string.Join("\n", report.errors) + "\n" + report.mapStatus); }
            return report;
        }
    }
}
