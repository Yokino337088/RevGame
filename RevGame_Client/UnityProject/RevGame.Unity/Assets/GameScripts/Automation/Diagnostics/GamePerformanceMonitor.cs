// 开发/内部性能包诊断服务。与Bootstrap同寿命，公开Rev门面接线；关闭面板不关闭AI持有的测量。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Text;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;

namespace RevGame.Automation
{
    public sealed class GamePerformanceMonitor : IDisposable
    {
        private const string ToggleAction = "RevGame.Performance.Toggle";
        private bool _opening, _disposed, _autoOpen;
        private readonly StringBuilder _text = new StringBuilder(2048);

        public GamePerformanceMonitor()
        {
            RevInput.Bind(ToggleAction, RevKey.F8);
            RevMono.AddUpdate(UpdateInput, owner: this);
            RevTimer.Every(.5f, Publish, domain: RevTimeDomain.Unscaled, owner: this);
            _autoOpen = Array.IndexOf(Environment.GetCommandLineArgs(), "-revPerformance") >= 0;
            const string schema = "{\"type\":\"object\",\"properties\":{\"visible\":{\"type\":\"boolean\"}},\"required\":[\"visible\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.performance.panel", "打开/隐藏真机性能面板；accepted后查panel-state", schema, json =>
            {
                SetVisible(JsonUtility.FromJson<PanelOptions>(json).visible);
                return new PanelResult { accepted = true };
            }, owner: this);
            RevRuntimeAutomation.Register("game.performance.panel-state", "只读面板可见/开屏状态", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}", _ =>
                new PanelResult { visible = RevUI.Get<PerformanceMonitorPanel>()?.IsOpened == true, opening = _opening }, readOnly: true, owner: this);
        }

        [Serializable] private sealed class PanelOptions
        {
            public bool visible;
        }
        [Serializable] private sealed class PanelResult
        {
            public bool accepted, visible, opening;
        }

        private void UpdateInput()
        {
            GameFlow flow = RevServices.Get<GameFlow>();
            bool ready = flow != null && flow.Screen != GameScreen.Exit && !flow.IsTransitioning;
            if (!ready)
            {
                return;
            }
            if (_autoOpen || RevInput.Pressed(ToggleAction))
            {
                _autoOpen = false;
                SetVisible(RevUI.Get<PerformanceMonitorPanel>()?.IsOpened != true);
            }
        }

        public void SetVisible(bool visible)
        {
            if (!visible)
            {
                RevUI.Close<PerformanceMonitorPanel>();
                return;
            }
            if (_opening || _disposed)
            {
                return;
            }
            RevRuntimeAutomation.EnablePerformance();
            _opening = true;
            RevUI.Open<PerformanceMonitorPanel>(panel =>
            {
                _opening = false;
                if (_disposed)
                {
                    RevUI.Close<PerformanceMonitorPanel>();
                    return;
                }
                if (panel == null)
                {
                    RevLog.Error("性能面板加载失败，请检查真实AB映射", "RevGame");
                    return;
                }
                Publish();
            });
        }

        private void Publish()
        {
            PerformanceMonitorPanel panel = RevUI.Get<PerformanceMonitorPanel>();
            if (panel == null || !panel.IsOpened)
            {
                return;
            }
            RevPerformanceSnapshot s = RevRuntimeAutomation.PerformanceSnapshot();
            _text.Clear();
            _text.Append(s.backend).Append(s.development ? " · 开发构建" : " · 发布构建").Append(" · ").Append(s.editor ? "编辑器" : "独立游戏").AppendLine();
            _text.Append(s.resolution).Append("  垂直同步 ").Append(s.vSyncCount).Append("  帧率上限 ").Append(s.targetFrameRate).Append(s.focused ? "  前台" : "  后台").AppendLine();
            _text.Append("帧时中位／95%／99%：").Append(s.p50Ms.ToString("F2")).Append(" / ").Append(s.p95Ms.ToString("F2")).Append(" / ").Append(s.p99Ms.ToString("F2")).AppendLine(" 毫秒");
            _text.Append("显卡耗时：").Append(s.gpuMs < 0 ? "不可用" : s.gpuMs.ToString("F2") + " 毫秒").Append("  最慢：").Append(s.maxMs.ToString("F2")).AppendLine(" 毫秒");
            foreach (RevPerformanceMetric metric in s.metrics)
            {
                _text.Append(MetricLabel(metric.name));
                _text.Append(": ");
                if (!metric.available)
                {
                    _text.AppendLine("不可用");
                }
                else if (metric.unit == "bytes")
                {
                    _text.Append((metric.value / 1024).ToString("F1")).AppendLine(" 千字节");
                }
                else
                {
                    _text.Append(metric.value.ToString(metric.unit == "ms" ? "F2" : "F0")).Append(' ').AppendLine(metric.unit == "ms" ? "毫秒" : "次");
                }
            }
            var battle = RevServices.Get<IBattleService>();
            if (battle != null)
            {
                _text.Append("棋子（我方／敌方）：").Append(battle.PlayerPieces.Count).Append(" / ").Append(battle.EnemyPieces.Count).AppendLine();
            }
            _text.AppendLine(s.gpu).Append("不可用＝当前构建不提供；正式采样时请隐藏面板");
            panel.SetData(new PerformanceMonitorFrame { Summary = "性能监测 · F8 开关\n" + s.fps.ToString("F1") + " 帧／秒  ·  " + s.frames + " 帧样本", Details = _text.ToString() });
        }

        private static string MetricLabel(string name)
        {
            switch (name)
            {
                case "Draw Calls Count": return "绘制调用";
                case "Total Batches Count": return "总绘制批次";
                case "SetPass Calls Count": return "着色通道切换";
                case "Triangles Count": return "三角形";
                case "Vertices Count": return "顶点";
                case "Dynamic Batches Count": return "动态合批";
                case "Static Batches Count": return "静态合批";
                case "Instanced Batches Count": return "实例化批次";
                case "GC Allocated In Frame": return "本帧托管分配";
                case "Total Used Memory": return "已用内存";
                case "Total Reserved Memory": return "保留内存";
                case "GC Used Memory": return "托管堆已用";
                case "Main Thread": return "主线程";
                case "Render Thread": return "渲染线程";
                default: return "其他计数器";
            }
        }

        public void Dispose()
        {
            _disposed = true;
            RevMono.RemoveAllOf(this);
            RevTimer.CancelAllOf(this);
            RevInput.RemoveAction(ToggleAction);
            RevRuntimeAutomation.UnregisterAllOf(this);
            RevUI.Close<PerformanceMonitorPanel>();
        }
    }
}
#endif
