// ============================================================
// RevMCPWindow —— 给初次接入的人提供 Unity 内可见的连接控制台。
// 【为什么】先确认工程、Play 与注册命令，再把配置交给 Agent，减少接错实例。
// 【职责】这里只管理/显示 Unity 接线；MCP 协议由外部 Python 服务负责。
// ============================================================
// RevMCP 的 Unity 内入口。这里管理接线和观察状态，MCP 协议服务仍由外部 Python 进程负责。
// 新手可以先在本窗口确认“游戏正在运行、服务已启动、命令确实注册”，再配置 Agent。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public sealed class RevMCPWindow : EditorWindow
    {
        private int _port = 18766;
        private string _message;
        private Vector2 _scroll;
        private double _refresh;
        [MenuItem("Revolution.Tools/AI/RevMCP 控制台", false, 50)]
        private static void Open() => GetWindow<RevMCPWindow>("RevMCP");

        private static string EndpointPath => Path.Combine(RevAutomationBridge.Root, "runtime.json");
        private void OnInspectorUpdate() { if (EditorApplication.timeSinceStartup > _refresh) { _refresh = EditorApplication.timeSinceStartup + 1; Repaint(); } }
        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Revolution · RevMCP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Agent → 外部 Python MCP → 本窗口对应的 Editor / 游戏运行时桥 → 真实 GM、UI、业务能力。\n外部 MCP 不需要嵌入 Unity 的 Python 环境。", MessageType.Info);
            EditorGUILayout.LabelField("Unity 工程", Path.GetFullPath("."));
            EditorGUILayout.LabelField("游戏状态", EditorApplication.isPlaying ? EditorApplication.isPaused ? "Play 已暂停" : "Play 运行中" : "编辑模式");
            EditorGUILayout.LabelField("当前注册 GM", EditorApplication.isPlaying ? RevGM.Count.ToString() : "进入 Play 后查询真实注册表");
            _port = EditorGUILayout.IntField("本机 HTTP 端口", _port);
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || EditorApplication.isPaused))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("启动运行时服务")) { Try(() => _message = "凭据文件：" + RevRuntimeAutomation.Start(_port)); }
                if (GUILayout.Button("停止运行时服务")) { Try(() => { RevRuntimeAutomation.Stop(); _message = "服务已停止，未完成工单已取消"; }); }
                EditorGUILayout.EndHorizontal();
            }
            if (!EditorApplication.isPlaying) { EditorGUILayout.HelpBox("Editor 工具通过文件桥即可使用；游戏 GM / UI / 运行时 HTTP 需要先进入 Play。Player 可通过 -revAutomation 显式启动相同服务。", MessageType.None); }
            EditorGUILayout.LabelField("运行时凭据文件", EndpointPath);
            EditorGUILayout.LabelField("凭据文件状态", File.Exists(EndpointPath) ? "存在；Agent 会校验 session 和实际连接" : "未启动");
            EditorGUILayout.HelpBox("凭据包含随机 token，不显示在窗口、不写入 Git。不同游戏实例使用不同端口。", MessageType.None);
            if (GUILayout.Button("复制 MCP 配置（Editor 模式）")) { CopyConfiguration(false); }
            if (GUILayout.Button("复制 MCP 配置（运行时 HTTP 模式）")) { CopyConfiguration(true); }
            if (GUILayout.Button("显示工单与证据目录")) { Directory.CreateDirectory(RevAutomationBridge.Root); EditorUtility.RevealInFinder(RevAutomationBridge.Root); }
            if (!string.IsNullOrEmpty(_message)) { EditorGUILayout.HelpBox(_message, MessageType.Info); }
            EditorGUILayout.LabelField("命令行自检", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel("python Revolution.RevMCP/rev_mcp.py --project <Unity工程路径> discover\npython Revolution.Automation/rev.py --project <Unity工程路径> --mode live editor coverage", GUILayout.Height(48));
            EditorGUILayout.EndScrollView();
        }
        private void Try(Action action) { try { action(); } catch (Exception e) { _message = e.Message; } }
        private void CopyConfiguration(bool runtime)
        {
            string project = Path.GetFullPath(".");
            string script = Path.Combine(Directory.GetParent(project).FullName, "Revolution.RevMCP", "rev_mcp.py");
            if (!File.Exists(script)) { _message = "未找到相邻 Revolution.RevMCP，请在配置中填入实际 rev_mcp.py 绝对路径。"; return; }
            var args = new List<string> { script, "--project", project };
            if (runtime) { args.Add("--runtime-endpoint"); args.Add(EndpointPath); }
            args.Add("serve");
            GUIUtility.systemCopyBuffer = RevAutomationJson.Write(new Dictionary<string, object> { ["mcpServers"] = new Dictionary<string, object> {
                ["revolution"] = new Dictionary<string, object> { ["command"] = "python", ["args"] = args } } });
            _message = "MCP JSON 配置已复制；command 可替换为本机 Python 的绝对路径。没有修改 Agent 全局设置。";
        }
    }
}
