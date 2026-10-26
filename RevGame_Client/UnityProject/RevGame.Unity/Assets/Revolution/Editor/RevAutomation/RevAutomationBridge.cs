// RevAutomationBridge.cs —— 编辑器本机文件队列：一次只执行一个改变工程/游戏的操作。
// 先把请求标记为“已开始”再执行；编译或进 Play 重载时不重放，防止一次按钮被执行两遍。
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Revolution.Editor
{
    [InitializeOnLoad]
    // ============================================================================
    //  RevAutomationBridge —— Unity 侧的"收发室"
    //
    //  职责（只做四件事）：
    //    1. 收：每个编辑帧检查 inbox 目录，取最老的一张"工单"（读完立刻删，避免重复执行）；
    //    2. 执行：按动作类型分派（普通动作直接调 Actions；play/stop/scenario 需要等状态变化）；
    //    3. 回：把结果原子地写到 jobs/<id>/response.json（先写 .tmp 再替换，读者不会读到半个文件）；
    //    4. 兜底：超时、取消、域重载、编辑器退出都要给外部一个明确的失败回执。
    //
    //  【为什么复用文件桥】Editor 资产动作沿用已有工单与域重载恢复，不为 MCP 再开重复执行链；
    //  代价是要轮询：Unity 侧 20ms 一次、Python 侧 5~50ms 自适应，常见往返延迟降到几十毫秒。
    //
    //  【新手最需要理解的 3 个状态】
    //    _active   —— 当前正在处理的请求（同一时刻只允许 1 个，其它拿 Busy）
    //    SessionState —— Unity 的"域重载"（改脚本会重载）会清空静态字段，
    //                    所以活动请求的标记要存进 SessionState，重载后才知道"刚才有个请求没回"
    //    deadlineUtc —— 请求的过期时间，超过就判 Expired，避免外部早已放弃、Unity 还在傻等
    // ============================================================================

    public static class RevAutomationBridge
    {
        public static string Root { get { return Path.GetFullPath("Library/RevolutionAutomation"); } }
        private static string Inbox { get { return Path.Combine(Root, "inbox"); } }
        private const string ActiveKey = "Revolution.RevCLI.ActiveRequest";
        private const string SessionKey = "Revolution.RevCLI.Session";
        public const string TestJobKey = "Revolution.RevCLI.TestJob";
        private static readonly string Session;
        private static RevAutomationRequest _active;
        private static RevAutomationScenario _scenario;
        private static RevAutomationOperation _operation;
        private static double _heartbeat;
        private static double _poll;
        private static string _playAction;
        private static int _playFrames;
        private static int _observedFrame = -1;
        private static bool _batch;
        private static bool _recovered;
        private static bool _quitting;

        static RevAutomationBridge()
        {
            Session = SessionState.GetString(SessionKey, "");
            if (Session.Length == 0)
            {
                Session = Guid.NewGuid().ToString("N");
                SessionState.SetString(SessionKey, Session);
            }
            Directory.CreateDirectory(Inbox);
            EditorApplication.update += Update;
            EditorApplication.quitting += OnQuit;
            EditorApplication.delayCall += Recover;
        }

        /// <summary>
        /// 把请求 id 变成一个安全的目录路径，并强制校验：**必须是 32 位十六进制**。
        /// 为什么这么严：id 来自外部 JSON，如果直接拼路径，传个 "../../secret" 就能把文件写到工程外。
        /// </summary>

        public static string JobDirectory(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 32 || !id.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("请求 id 必须是 32 位十六进制，不能包含路径");
            }
            return Path.Combine(Root, "jobs", id);
        }

        public static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) { File.Replace(temp, path, null); }
                    else { File.Move(temp, path); }
                    break;
                }
                catch (IOException) when (attempt < 7) { System.Threading.Thread.Sleep(10 * (attempt + 1)); }
            }
        }

        /// <summary>
        /// 开始处理一张工单。顺序很重要，任何一步失败都会立刻回执并返回：
        /// ① 幂等检查（response.json 已存在 → 说明处理过，直接返回）
        /// ② 拒绝：忙 / 协议版本不对 / 动作不存在 / 已被取消 / 会话不对 / 已过期
        /// ③ 记录"正在处理"（写 SessionState，域重载后能据此收尾）
        /// ④ batch 模式下拒绝交互类动作（play/stop/menu/gm/scenario）
        /// ⑤ play/stop、scenario 走特殊分支（它们需要跨帧等待）
        /// ⑥ 其它动作：同步执行，执行期间监听 Unity 日志，**有 Error 就算失败**
        /// </summary>

        public static void Begin(RevAutomationRequest request, bool batch = false)
        {
            if (request == null) { throw new ArgumentException("请求不能为空"); }
            string work = JobDirectory(request.id);
            if (batch) { _batch = true; }
            if (File.Exists(Path.Combine(work, "response.json")))
            {
                if (batch)
                {
                    var old = JsonUtility.FromJson<RevAutomationResponse>(File.ReadAllText(Path.Combine(work, "response.json")));
                    EditorApplication.delayCall += () => EditorApplication.Exit(old.success ? 0 : 1);
                }
                return;
            }
            if (_active != null) { Reply(request, false, "Busy", "已有自动化操作在执行，请等待其完成", null); return; }
            if (RevRuntimeAutomation.HasPendingWork && request.action != "editor.status" && request.action != "editor.console" && request.action != "editor.resume"
                && request.action != "runtime.stop" && request.action != "editor.stop")
            { Reply(request, false, "RuntimeBusy", "运行时 HTTP 桥仍有工单，请等待完成或请求取消", null); return; }
            string testJob = SessionState.GetString(TestJobKey, "");
            if (testJob.Length != 0 && testJob != request.id && request.action != "test.result"
                && request.action != "editor.status" && request.action != "editor.console" && request.action != "editor.compilation")
            { Reply(request, false, "TestsRunning", "Test Runner 仍在执行：" + testJob + "；请等待 test.result，避免交叉修改编辑器", null); return; }
            if (request.protocol != 1) { Reply(request, false, "Protocol", "不支持的自动化协议", null); return; }
            if (string.IsNullOrWhiteSpace(request.action)) { Reply(request, false, "Action", "缺少动作 ID", null); return; }
            if (File.Exists(Path.Combine(work, "cancel"))) { Reply(request, false, "Cancelled", "请求在开始前已取消", null); return; }
            if (!batch && request.sessionId != Session) { Reply(request, false, "Session", "编辑器会话已变化，请重新请求", null); return; }
            if (!DateTimeOffset.TryParse(request.deadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset deadline)
                || deadline <= DateTimeOffset.UtcNow)
            {
                Reply(request, false, "Expired", "请求已超时，不执行迟到操作", null);
                return;
            }
            _active = request;
            RevRuntimeAutomation.EditorBusy = true;
            _batch = batch;
            SessionState.SetString(ActiveKey, JsonUtility.ToJson(request));
            try
            {
                RevAutomationCatalog.ValidatePayload(request);
                if (batch && (request.action == "editor.play" || request.action == "editor.stop" || request.action == "editor.menu"
                    || request.action.StartsWith("gm.", StringComparison.Ordinal) || request.action == "scenario.run"))
                {
                    throw new InvalidOperationException("此动作需要在线编辑器；batch 不提供真实的交互 Play 环境");
                }
                if (request.action == "editor.play" || request.action == "editor.stop")
                {
                    var options = JsonUtility.FromJson<RevAutomationOptions>(request.payloadJson);
                    bool play = request.action == "editor.play";
                    if (play && EditorApplication.isPaused) { throw new InvalidOperationException("请先解除编辑器暂停，再进入自动化流程"); }
                    if (play && EditorApplication.isPlaying && !string.IsNullOrEmpty(options.scene)
                        && UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != options.scene)
                    { throw new InvalidOperationException("当前 Play 场景与请求不同，请先 editor.stop 再重试"); }
                    if (play && !EditorApplication.isPlaying && !string.IsNullOrEmpty(options.scene))
                    {
                        // 自动化不猜测如何处理未保存场景；失败返回后由调用方保存，再重试。
                        if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                            .Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
                        {
                            throw new InvalidOperationException("存在未保存场景；请先保存场景再切换自动化场景");
                        }
                        if (!options.scene.StartsWith("Assets/", StringComparison.Ordinal) || !options.scene.EndsWith(".unity", StringComparison.Ordinal)
                            || AssetDatabase.LoadAssetAtPath<SceneAsset>(options.scene) == null)
                        {
                            throw new ArgumentException("scene 必须是工程内存在的 Assets/.../*.unity");
                        }
                        EditorSceneManager.OpenScene(options.scene);
                    }
                    _playAction = request.action;
                    _playFrames = 0;
                    _observedFrame = -1;
                    EditorApplication.isPlaying = play;
                    return;
                }
                if (request.action == "scenario.run")
                {
                    _scenario = new RevAutomationScenario(request);
                    return;
                }
                // 旧生成器有些通过 LogError 返回失败而不是抛异常。把它们转成失败响应，不能谎报完成。
                var errors = new List<string>();
                Application.LogCallback capture = (message, stack, type) =>
                {
                    if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && errors.Count < 20) { errors.Add(message); }
                };
                object result;
                Application.logMessageReceived += capture;
                try { result = RevAutomationActions.Execute(request); }
                finally { Application.logMessageReceived -= capture; }
                if (errors.Count > 0) { Complete(false, "UnityError", string.Join("\n", errors), (result as RevAutomationOperation)?.Result ?? result); return; }
                if (result is RevAutomationOperation operation) { _operation = operation; return; }
                Complete(true, null, "完成", result);
            }
            catch (Exception e) { Complete(false, e.GetType().Name, e.Message, null); }
        }

        /// <summary>
        /// 每帧被调用（编辑器空闲时 0.1 秒轮询一次 inbox）。
        ///
        /// 两件事：
        /// · 有活动请求 → 推进它（检查取消/超时、推进 scenario、等 play/stop 状态到位）；
        /// · 没有活动请求 → 去 inbox 取下一张工单。
        ///
        /// ⚠️ 注意"删文件"的时机：先读内容、再删文件、最后 Begin —— 这样即使 Begin 抛异常，
        /// 这张工单也不会被反复执行。
        /// </summary>

        private static void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            try
            {
                if (now >= _heartbeat)
                {
                    _heartbeat = now + 1;
                    WriteJson(Path.Combine(Root, "endpoint.json"), new RevAutomationEndpoint
                    {
                        pid = Process.GetCurrentProcess().Id, sessionId = Session, project = Path.GetFullPath("."),
                        unityVersion = Application.unityVersion, heartbeatUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                        playing = EditorApplication.isPlaying, compiling = EditorApplication.isCompiling,
                        paused = EditorApplication.isPaused, updating = EditorApplication.isUpdating,
                        compilationFailed = EditorUtility.scriptCompilationFailed,
                        scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                        batchMode = Application.isBatchMode, activeRequest = _active?.id
                    });
                }
                if (_active != null)
                {
                    RevRuntimeAutomation.EditorBusy = true;
                    bool cancelled = File.Exists(Path.Combine(JobDirectory(_active.id), "cancel")) || DeadlinePassed(_active);
                    if (_operation != null)
                    {
                        if (cancelled) { _operation.Cancel(); Complete(false, "Cancelled", "操作取消或超时", _operation.Result); }
                        else if (_operation.Tick())
                        { Complete(_operation.Success, _operation.Success ? null : "OperationFailed", _operation.Message, _operation.Result); }
                    }
                    else if (_scenario != null)
                    {
                        if (cancelled) { _scenario.Cancel("调用方取消或整体超时"); }
                        if (_scenario.Tick())
                        {
                            RevAutomationScenario finished = _scenario;
                            _scenario = null;
                            Complete(finished.Success, finished.Success ? null : "ScenarioFailed", finished.Message, finished.Result);
                        }
                    }
                    else if (_playAction != null)
                    {
                        if (cancelled) { Complete(false, "Timeout", "等待 Play 状态变化超时", null); }
                        else if (_playAction == "editor.stop" && !EditorApplication.isPlayingOrWillChangePlaymode)
                        {
                            Complete(true, null, "已停止 Play", null);
                        }
                        else if (_playAction == "editor.play" && EditorApplication.isPlaying && !EditorApplication.isCompiling)
                        {
                            if (_observedFrame != Time.frameCount) { _playFrames++; _observedFrame = Time.frameCount; }
                            if (_playFrames >= 2) { Complete(true, null, "已进入 Play；业务准备状态请另用 GM 条件等待", null); }
                        }
                    }
                    return;
                }
                // 某些包的 delayCall 抛异常会打断同一轮回调链；Update 再兜一次恢复，
                // 否则心跳看似正常，收发室却会永远停在“尚未恢复”的状态。
                if (!_recovered && !EditorApplication.isCompiling && !EditorApplication.isUpdating) { Recover(); }
                if (!_recovered || now < _poll || EditorApplication.isCompiling || EditorApplication.isUpdating) { return; }
                // 20ms 而不是原来的 100ms：空目录检查只是一次 GetFiles，
                // 但它决定了每个请求要等多久才开始执行，属于纯等待成本。
                // 实际耗时仍在 Complete 里回报，不靠缩短轮询掩盖真实执行时间。
                _poll = now + .02;
                foreach (string path in Directory.GetFiles(Inbox, "*.json").OrderBy(File.GetLastWriteTimeUtc))
                {
                    string text = File.ReadAllText(path);
                    File.Delete(path);
                    Begin(JsonUtility.FromJson<RevAutomationRequest>(text));
                    break;
                }
            }
            catch (Exception e)
            {
                if (_active != null)
                {
                    if (_scenario != null) { _scenario.Abort(e.Message); }
                    Complete(false, e.GetType().Name, e.ToString(), _scenario?.Result);
                }
                else { UnityEngine.Debug.LogException(e); }
            }
        }

        private static bool DeadlinePassed(RevAutomationRequest request)
        {
            return DateTimeOffset.TryParse(request.deadlineUtc, out DateTimeOffset deadline) && DateTimeOffset.UtcNow >= deadline;
        }

        /// <summary>
        /// 域重载（改脚本、重新编译）之后的恢复逻辑。
        ///
        /// 规则很保守：
        /// · play/stop 这类"等状态"的动作 → 继续等；
        /// · 其它动作（正在执行中被重载打断）→ 直接判失败（InterruptedByReload），**绝不重放**。
        /// 为什么不重放：动作可能有副作用（打包、删文件、导表），重放等于执行两次，风险比失败更大。
        /// </summary>

        private static void Recover()
        {
            _recovered = true;
            if (_active != null) { return; }
            string json = SessionState.GetString(ActiveKey, "");
            if (json.Length == 0) { return; }
            var request = JsonUtility.FromJson<RevAutomationRequest>(json);
            if (File.Exists(Path.Combine(JobDirectory(request.id), "response.json"))) { SessionState.EraseString(ActiveKey); return; }
            _active = request;
            RevRuntimeAutomation.EditorBusy = true;
            _batch = Application.isBatchMode;
            if (request.action == "editor.play" || request.action == "editor.stop") { _playAction = request.action; }
            else
            {
                try { _operation = RevAutomationCatalog.Resume(request); }
                catch (Exception e) { Complete(false, "RecoveryFailed", e.Message, null); return; }
                if (_operation == null) { Complete(false, "InterruptedByReload", "脚本重载中断了操作；没有自动重放，请检查产物后再决定重试", null); }
            }
        }

        private static void Reply(RevAutomationRequest request, bool success, string error, string message, object data)
        {
            string dataJson;
            try { dataJson = RevAutomationJson.Write(data); }
            catch (Exception e) { success = false; error = "SerializationFailed"; message = e.Message; dataJson = "null"; }
            WriteJson(Path.Combine(JobDirectory(request.id), "response.json"), new RevAutomationResponse
            {
                id = request.id, success = success, error = error, message = message,
                dataJson = dataJson
            });
            if (_batch && !_quitting && _active == null) { EditorApplication.delayCall += () => EditorApplication.Exit(success ? 0 : 1); }
        }

        /// <summary>
        /// 收尾统一入口：清理 scenario → 清空活动请求 → 抹掉 SessionState 标记 → 写回执。
        /// 所有成功/失败路径最后都会走到这里，保证"每张工单必有回执"。
        /// </summary>

        private static void Complete(bool success, string error, string message, object data)
        {
            RevAutomationRequest request = _active;
            if (request == null) { return; }
            // 项目扩展的 Dispose 也可能抛异常。仍须释放忙标记并给回执，
            // 否则 Agent 只会看到永久 Busy，而真正的清理错误反而丢失。
            try { _scenario?.Dispose(); }
            catch (Exception e) { success = false; error = "DisposeFailed"; message += "; scenario 清理失败：" + e.Message; }
            try { _operation?.Dispose(); }
            catch (Exception e) { success = false; error = "DisposeFailed"; message += "; operation 清理失败：" + e.Message; }
            _operation = null;
            _scenario = null;
            _playAction = null;
            _active = null;
            RevRuntimeAutomation.EditorBusy = false;
            SessionState.EraseString(ActiveKey);
            Reply(request, success, error, message, data);
        }

        private static void OnQuit()
        {
            _quitting = true;
            if (_active != null) { Complete(false, "EditorQuit", "编辑器退出，操作未完成", null); }
            string endpoint = Path.Combine(Root, "endpoint.json");
            if (File.Exists(endpoint)) { File.Delete(endpoint); }
        }
    }
}
