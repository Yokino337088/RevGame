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
            _text.Append(s.backend).Append(s.development ? " · Development" : " · Release").Append(" · ").Append(s.editor ? "Editor" : "Player").AppendLine();
            _text.Append(s.resolution).Append("  VSync ").Append(s.vSyncCount).Append("  Limit ").Append(s.targetFrameRate).Append(s.focused ? "  Focus" : "  Background").AppendLine();
            _text.Append("P50 / P95 / P99: ").Append(s.p50Ms.ToString("F2")).Append(" / ").Append(s.p95Ms.ToString("F2")).Append(" / ").Append(s.p99Ms.ToString("F2")).AppendLine(" ms");
            _text.Append("GPU: ").Append(s.gpuMs < 0 ? "N/A" : s.gpuMs.ToString("F2") + " ms").Append("  Max: ").Append(s.maxMs.ToString("F2")).AppendLine(" ms");
            foreach (RevPerformanceMetric metric in s.metrics)
            {
                _text.Append(metric.name.Replace(" Count", ""));
                _text.Append(": ");
                if (!metric.available)
                {
                    _text.AppendLine("N/A");
                }
                else if (metric.unit == "bytes")
                {
                    _text.Append((metric.value / 1024).ToString("F1")).AppendLine(" KiB");
                }
                else
                {
                    _text.Append(metric.value.ToString(metric.unit == "ms" ? "F2" : "F0")).Append(' ').AppendLine(metric.unit);
                }
            }
            var battle = RevServices.Get<IBattleService>();
            if (battle != null)
            {
                _text.Append("Pieces: ").Append(battle.PlayerPieces.Count).Append(" / ").Append(battle.EnemyPieces.Count).AppendLine();
            }
            _text.AppendLine(s.gpu).Append("N/A = unavailable; hide panel during benchmark");
            panel.SetData(new PerformanceMonitorFrame { Summary = "PERFORMANCE · F8\n" + s.fps.ToString("F1") + " FPS  /  " + s.frames + " frames", Details = _text.ToString() });
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
