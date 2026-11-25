// ============================================================
// RevAutomationDiagnostics —— Console、编译诊断和编辑器暂停/恢复/单帧。
// 【为什么】Agent 必须知道脚本是否真正编译、Unity 是否报错，不能只看 CLI 退出。
// 【边界】Console 是分页快照；编译结果可能滞后，生成代码后先等待新稳定心跳。
// ============================================================
// AI 的观察接口：Console 与编译诊断独立于 GM，不要求进入 Play。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>在 Unity 主线程读取 Console 与编译证据，并提供播放控制动作。</summary>
    [InitializeOnLoad]
    public static class RevAutomationDiagnostics
    {
        /// <summary>一条原始 Console 行，供 CLI 过滤、定位和显示完整消息。</summary>
        [Serializable]
        private sealed class LogRow
        {
            /// <summary>Unity 原文，可能含多行堆栈。</summary>
            public string message;
            /// <summary>消息关联的源文件，无来源时为空。</summary>
            public string file;
            /// <summary>源文件行号，从 1 开始，未知时为 0。</summary>
            public int line;
            /// <summary>源文件列号，从 1 开始，未知时为 0。</summary>
            public int column;
            /// <summary>Unity 原始消息位掩码，保留给现有调用方。</summary>
            public int mode;
            /// <summary>按 Unity 消息类型归类的 error、warning 或 log。</summary>
            public string severity;
        }

        /// <summary>对本次匹配结果的分页视图。日志仍由 Unity Console 持有。</summary>
        [Serializable]
        private sealed class ConsoleResult
        {
            /// <summary>过滤后匹配的总数，包含其它页。</summary>
            public int total;
            /// <summary>该页从匹配结果的第几个位置开始，从 0 计数。</summary>
            public int offset;
            /// <summary>本页之后仍有匹配项时为真。</summary>
            public bool truncated;
            /// <summary>当前页的完整消息，按 Console 原始顺序排列。</summary>
            public LogRow[] entries;
        }

        [Serializable] private sealed class Diagnostic { public string assembly, message, file, type; public int line, column; }
        /// <summary>一次脚本编译收集的诊断，由编译事件更新并跨脚本域保存。</summary>
        [Serializable]
        private sealed class CompilationResult
        {
            /// <summary>产出此记录的编辑器会话，CLI 用它排除上一次启动的快照。</summary>
            public string sessionId;
            // 工单 ID 只关联本次主动编译。自动编译留空，调用方可据此排除旧结果。
            public string requestId;
            // finished 由 Unity 编译结束事件写入，不能用编辑器暂时空闲代替。
            public bool finished, compiling, failed;
            // 每个程序集的诊断含源码行列，可直接定位错误。
            public Diagnostic[] messages;
        }
        // 只在本次 Unity 进程内保留尚未开始的请求，域重载不会丢失它。
        private const string PendingCompilationKey = "RevAutomation.PendingCompilation";
        // 当前编译从开始到结束使用同一 ID；自然触发的编译没有请求 ID。
        private static string _activeCompilation;
        private static readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        private static string CompilePath => Path.Combine(RevAutomationBridge.Root, "compilation.json");
        /// <summary>Unity Console 折叠开关。查询时临时展开，读取完成后还原。</summary>
        private const int CollapseFlag = 1 << 0;
        /// <summary>Unity Console 的日志、警告和错误可见位，只在本次读取期间全部开启。</summary>
        private const int VisibilityFlags = (1 << 7) | (1 << 8) | (1 << 9);
        /// <summary>Unity 消息位中的错误、断言、导入错误、编译错误和脚本异常等类型。</summary>
        private const int ErrorModes = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8)
            | (1 << 11) | (1 << 17) | (1 << 20) | (1 << 21);
        /// <summary>Unity 消息位中的导入、脚本与编译警告。</summary>
        private const int WarningModes = (1 << 7) | (1 << 9) | (1 << 12);

        static RevAutomationDiagnostics()
        {
            CompilationPipeline.compilationStarted += _ =>
            {
                _activeCompilation = SessionState.GetString(PendingCompilationKey, "");
                SessionState.EraseString(PendingCompilationKey);
                Diagnostics.Clear();
                SaveCompilation(true);
            };
            CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
            {
                Diagnostics.AddRange(messages.Select(m => new Diagnostic { assembly = assembly, message = m.message,
                    file = m.file, line = m.line, column = m.column, type = m.type.ToString() }));
                SaveCompilation(true);
            };
            CompilationPipeline.compilationFinished += _ => SaveCompilation(false);
        }

        /// <summary>在编辑态主动重编所有脚本，并让诊断记录发起请求的工单 ID。</summary>
        internal static void RequestCompilation(string requestId)
        {
            RevAutomationActions.RequireEditMode();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("请等 Unity 当前导入或编译结束后再请求重编译。");
            }
            SessionState.SetString(PendingCompilationKey, requestId);
            try
            {
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            }
            catch
            {
                SessionState.EraseString(PendingCompilationKey);
                throw;
            }
        }

        /// <summary>把真实的编译事件结果写到 Library，供域重载后的桥接读取。</summary>
        private static void SaveCompilation(bool compiling)
        {
            RevAutomationBridge.WriteJson(CompilePath, new CompilationResult
            {
                sessionId = RevAutomationBridge.SessionId,
                requestId = _activeCompilation,
                finished = !compiling,
                compiling = compiling,
                failed = Diagnostics.Any(d => d.type == "Error"),
                messages = Diagnostics.ToArray()
            });
        }

        [RevAutomationAction("editor.compilation", Environment = "either", Keys = new string[0], Description = "上次编译诊断及当前编译失败状态")]
        public static object Compilation(RevAutomationRequest request)
        {
            var result = File.Exists(CompilePath) ? JsonUtility.FromJson<CompilationResult>(File.ReadAllText(CompilePath)) : new CompilationResult();
            result.compiling = result.compiling || EditorApplication.isCompiling;
            result.failed = result.failed || EditorUtility.scriptCompilationFailed;
            return result;
        }

        /// <summary>读取 Console 保留的真实日志，查询和分页独立于编辑器窗口的筛选设置。</summary>
        [RevAutomationAction("editor.console", Environment = "either", Keys = new[] { "offset", "limit", "query", "severity" }, Description = "按级别、消息和源文件分页读取 Unity Console（含编译诊断）")]
        public static object Console(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            p.CheckPage();
            Type entries = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries", true);
            Type rowType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry", true);
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo start = entries.GetMethod("StartGettingEntries", statics);
            MethodInfo end = entries.GetMethod("EndGettingEntries", statics);
            MethodInfo get = entries.GetMethod("GetEntryInternal", statics);
            MethodInfo getFilter = entries.GetMethod("GetFilteringText", statics);
            MethodInfo setFilter = entries.GetMethod("SetFilteringText", statics);
            PropertyInfo flags = entries.GetProperty("consoleFlags", statics);
            FieldInfo messageField = rowType.GetField("message", fields);
            FieldInfo fileField = rowType.GetField("file", fields);
            FieldInfo lineField = rowType.GetField("line", fields);
            FieldInfo columnField = rowType.GetField("column", fields);
            FieldInfo modeField = rowType.GetField("mode", fields);
            if (start == null || end == null || get == null || getFilter == null || setFilter == null || flags?.CanWrite != true
                || messageField == null || fileField == null || lineField == null || columnField == null || modeField == null)
            {
                throw new NotSupportedException("当前 Unity 不提供所需的 Console 快照接口");
            }

            // 原生读取的是 Console 的可见列表。先隔离窗口筛选，等本次读取结束再原样恢复。
            int originalFlags = (int)flags.GetValue(null);
            int allFlags = (originalFlags | VisibilityFlags) & ~CollapseFlag;
            string originalFilter = (string)getFilter.Invoke(null, null);
            var rows = new List<LogRow>();
            int total = 0;
            object[] arguments = { 0, Activator.CreateInstance(rowType) };
            try
            {
                if (allFlags != originalFlags)
                {
                    flags.SetValue(null, allFlags);
                }
                if (!string.IsNullOrEmpty(originalFilter))
                {
                    setFilter.Invoke(null, new object[] { string.Empty });
                }

                int count = (int)start.Invoke(null, null);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        arguments[0] = i;
                        if (!(bool)get.Invoke(null, arguments))
                        {
                            continue;
                        }
                        object row = arguments[1];
                        int mode = (int)modeField.GetValue(row);
                        string severity = ConsoleSeverity(mode);
                        if (p.severity != null && p.severity != severity)
                        {
                            continue;
                        }
                        string message = (string)messageField.GetValue(row) ?? string.Empty;
                        string file = (string)fileField.GetValue(row) ?? string.Empty;
                        if (!string.IsNullOrEmpty(p.query) && message.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) < 0
                            && file.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        // 继续计数供分页，但只把本页条目装进响应。
                        if (total++ < p.offset || rows.Count >= p.limit)
                        {
                            continue;
                        }
                        rows.Add(new LogRow
                        {
                            message = message,
                            file = file,
                            line = (int)lineField.GetValue(row),
                            column = (int)columnField.GetValue(row),
                            mode = mode,
                            severity = severity
                        });
                    }
                }
                finally
                {
                    end.Invoke(null, null);
                }
            }
            finally
            {
                if (allFlags != originalFlags)
                {
                    flags.SetValue(null, originalFlags);
                }
                if (!string.IsNullOrEmpty(originalFilter))
                {
                    setFilter.Invoke(null, new object[] { originalFilter });
                }
            }
            return new ConsoleResult { total = total, offset = p.offset, truncated = total - p.offset > rows.Count, entries = rows.ToArray() };
        }

        /// <summary>错误位优先，编译警告只归 warning；其余条目沿用普通日志分类。</summary>
        private static string ConsoleSeverity(int mode)
        {
            if ((mode & ErrorModes) != 0)
            {
                return "error";
            }
            return (mode & WarningModes) != 0 ? "warning" : "log";
        }

        [RevAutomationAction("editor.console-clear", Environment = "either", Keys = new string[0], Confirm = true, Description = "清空 Console；清理前先保存错误证据")]
        public static object Clear(RevAutomationRequest request)
        {
            typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries", true)
                .GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
            return new RevAutomationActions.RevAutomationText("已清空 Console");
        }

        [RevAutomationAction("editor.pause", Environment = "live", Keys = new string[0], Description = "暂停 Play")]
        public static object Pause(RevAutomationRequest request)
        {
            if (!EditorApplication.isPlaying) { throw new InvalidOperationException("当前没有运行 Play"); }
            EditorApplication.isPaused = true;
            return new RevAutomationActions.RevAutomationText("Play 已暂停");
        }

        [RevAutomationAction("editor.resume", Environment = "live", Keys = new string[0], Description = "恢复 Play")]
        public static object Resume(RevAutomationRequest request)
        {
            if (!EditorApplication.isPlaying) { throw new InvalidOperationException("当前没有运行 Play"); }
            EditorApplication.isPaused = false;
            return new RevAutomationActions.RevAutomationText("Play 已恢复");
        }

        [RevAutomationAction("editor.step", Environment = "live", Keys = new string[0], Description = "暂停 Play 下前进一步并等待该帧完成")]
        public static object Step(RevAutomationRequest request)
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused) { throw new InvalidOperationException("需要暂停中的 Play"); }
            var operation = new StepOperation();
            EditorApplication.Step();
            return operation;
        }

        private sealed class StepOperation : RevAutomationOperation
        {
            private readonly int _frame = Time.frameCount;
            public override bool Tick() => Time.frameCount != _frame && EditorApplication.isPaused;
        }
    }
}
