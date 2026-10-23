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
    [InitializeOnLoad]
    public static class RevAutomationDiagnostics
    {
        [Serializable] private sealed class LogRow { public string message, file; public int line, mode; }
        [Serializable] private sealed class ConsoleResult { public int total, offset; public bool truncated; public LogRow[] entries; }
        [Serializable] private sealed class Diagnostic { public string assembly, message, file, type; public int line, column; }
        [Serializable] private sealed class CompilationResult { public bool compiling, failed; public Diagnostic[] messages; }
        private static readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        private static string CompilePath => Path.Combine(RevAutomationBridge.Root, "compilation.json");

        static RevAutomationDiagnostics()
        {
            CompilationPipeline.compilationStarted += _ => { Diagnostics.Clear(); SaveCompilation(true); };
            CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
            {
                Diagnostics.AddRange(messages.Select(m => new Diagnostic { assembly = assembly, message = m.message,
                    file = m.file, line = m.line, column = m.column, type = m.type.ToString() }));
                SaveCompilation(true);
            };
            CompilationPipeline.compilationFinished += _ => SaveCompilation(false);
        }

        private static void SaveCompilation(bool compiling) => RevAutomationBridge.WriteJson(CompilePath,
            new CompilationResult { compiling = compiling, failed = Diagnostics.Any(d => d.type == "Error"), messages = Diagnostics.ToArray() });

        [RevAutomationAction("editor.compilation", Environment = "either", Keys = new string[0], Description = "上次编译诊断及当前编译失败状态")]
        public static object Compilation(RevAutomationRequest request)
        {
            var result = File.Exists(CompilePath) ? JsonUtility.FromJson<CompilationResult>(File.ReadAllText(CompilePath)) : new CompilationResult();
            result.compiling = EditorApplication.isCompiling;
            result.failed = EditorUtility.scriptCompilationFailed;
            return result;
        }

        [RevAutomationAction("editor.console", Environment = "either", Keys = new[] { "offset", "limit", "query", "severity" }, Description = "分页读取 Unity Console（含编译错误）")]
        public static object Console(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            p.CheckPage();
            Type entries = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries", true);
            Type rowType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry", true);
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var start = entries.GetMethod("StartGettingEntries", statics);
            var end = entries.GetMethod("EndGettingEntries", statics);
            var get = entries.GetMethod("GetEntryInternal", statics);
            if (start == null || end == null || get == null) { throw new NotSupportedException("当前 Unity 不提供 Console 读取接口"); }
            var rows = new List<LogRow>();
            start.Invoke(null, null);
            try
            {
                int count = (int)entries.GetMethod("GetCount", statics).Invoke(null, null);
                for (int i = 0; i < count; i++)
                {
                    var row = Activator.CreateInstance(rowType);
                    if (!(bool)get.Invoke(null, new[] { (object)i, row })) { continue; }
                    string message = (string)rowType.GetField("message", fields).GetValue(row);
                    int mode = (int)rowType.GetField("mode", fields).GetValue(row);
                    bool error = (mode & (1 | 2 | 16 | 64 | 256 | 2048 | 4096 | 65536)) != 0;
                    bool warning = (mode & (128 | 512)) != 0;
                    if (!string.IsNullOrEmpty(p.query) && message.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (p.severity == "error" && !error || p.severity == "warning" && !warning || p.severity == "log" && (error || warning)) { continue; }
                    rows.Add(new LogRow { message = message, mode = mode,
                        file = (string)rowType.GetField("file", fields).GetValue(row), line = (int)rowType.GetField("line", fields).GetValue(row) });
                }
            }
            finally { end.Invoke(null, null); }
            return new ConsoleResult { total = rows.Count, offset = p.offset, truncated = rows.Count > p.offset + p.limit,
                entries = rows.Skip(p.offset).Take(p.limit).ToArray() };
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
