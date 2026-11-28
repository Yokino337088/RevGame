#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    /// <summary>隔离 Play 会话中的配对测量器。用真实墙钟采样已运行的游戏帧，只在读回时排序和序列化。</summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class MathAbRun : MonoBehaviour
    {
        /// <summary>自动化注册所有者。本脚本只编译到编辑器，不进入任何独立包。</summary>
        private static readonly object Owner = new object();
        /// <summary>最近一份工单，完成后留给只读动作查询，开新工单时替换。</summary>
        private static MathAbRun _current;
        /// <summary>帧间隔和基准时钟使用同一频率，将平台计数换成毫秒。</summary>
        private static readonly double MillisecondsPerTick = 1000.0 / Stopwatch.Frequency;
        /// <summary>每局帧样本的硬上限。达到上限会将本轮标为失败，避免悄悄截掉慢段。</summary>
        private const int FrameCapacity = 65536;
        /// <summary>仅显式控制时钟时，每个渲染帧推进 1/60 模型秒。秒表仍用实际墙钟。</summary>
        private const float BattleCaptureStep = 1f / 60;
        /// <summary>帧上限的参考线，以目标 60 帧每秒换算。</summary>
        private const double FrameBudgetMs = 1000.0 / 60;
        /// <summary>每帧写入一个实际间隔，启动后不需要为正常帧采样扩容。</summary>
        private readonly double[] _intervals = new double[FrameCapacity];
        /// <summary>按实际动作顺序保存模型输出，用来跨版本逐项核对。</summary>
        private readonly List<string> _rules = new List<string>(24);
        /// <summary>仅在收到可验证反馈时追加，间隔已经换成毫秒。</summary>
        private readonly List<Feedback> _feedback = new List<Feedback>(32);
        /// <summary>本轮已收集的帧间隔数。</summary>
        private int _count;
        /// <summary>其中在 Unity 没有焦点时收到的帧间隔数，用于判断两轮环境是否一致。</summary>
        private int _unfocused;
        /// <summary>上一个采样位置的墙钟计数。零表示窗口外或还在等第一个完整间隔。</summary>
        private long _lastFrameAt;
        /// <summary>保存工单开始时的模型时间控制值，退出后恢复。</summary>
        private float _previousCaptureStep;
        /// <summary>窗口开始时的设备和画面信息，避免把退场后的设置记成战斗设置。</summary>
        private Dictionary<string, object> _environment;
        /// <summary>场景迭代器和其中委托的子步骤。结束时从内到外释放。</summary>
        private readonly Stack<IEnumerator> _steps = new Stack<IEnumerator>(4);
        /// <summary>本次场景名供结果核对，由动作参数枚举约束。</summary>
        private string _case;
        /// <summary>宿主确认并传入的源码分支标签，用来匹配回执。</summary>
        private string _variant;
        /// <summary>首个异常文本。空串表示当前未发现失败。</summary>
        private string _error = string.Empty;
        /// <summary>整个工单已经结束，帧计数、规则结果和反馈均停止变化。</summary>
        private bool _finished;
        /// <summary>当前是否采集稳定阶段帧间隔。</summary>
        private bool _sampling;
        /// <summary>仅远征的显式定位轮附带分段计时，常规配对继续用原采样。</summary>
        private bool _profileFrames;
        /// <summary>本次窗口的远征帧归因探针。完成后停止采集但保留回执，开新局替换。</summary>
        private CampaignFrameDiagnostics _campaignProbe;

        /// <summary>从本次操作提交到同进程反馈点的延迟样本。</summary>
        private readonly struct Feedback
        {
            /// <summary>说明哪次提交和哪类反馈对应。</summary>
            internal readonly string Label;
            /// <summary>墙钟毫秒，包含 Unity 正常调度和两端之间的等待。</summary>
            internal readonly double Milliseconds;

            /// <summary>在测试场景捕获反馈可见或已提交画面数据时写入。</summary>
            internal Feedback(string label, double milliseconds)
            {
                Label = label;
                Milliseconds = milliseconds;
            }
        }

        /// <summary>工单的受控入口，只接受指定的玩法和源码版本标签。</summary>
        [Serializable]
        private sealed class Args
        {
            /// <summary>pvp、campaign、campaign-current 或 endless。campaign-current 只观察已有的隔离交火。</summary>
            public string mode;
            /// <summary>before 或 after，与宿主核实的源码版本相匹配。</summary>
            public string variant;
            /// <summary>仅战斗的受控排障轮可固定模型时钟，常规体验对照省略。</summary>
            public bool controlledClock;
            /// <summary>远征定位慢帧时打开分段探针，缺省为常规轻量采样。</summary>
            public bool profileFrames;
        }

        /// <summary>本地 Play 启动后注册三条测试能力。开始动作另外验证隔离状态。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Register()
        {
            RevRuntimeAutomation.UnregisterAllOf(Owner);
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            const string options = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"pvp\",\"campaign\",\"campaign-current\",\"endless\"]},\"variant\":{\"type\":\"string\",\"enum\":[\"before\",\"after\"]},\"controlledClock\":{\"type\":\"boolean\"},\"profileFrames\":{\"type\":\"boolean\"}},\"required\":[\"mode\",\"variant\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("mathab.start", "启动本地隔离的玩法对照，完成后读 mathab.state", options,
                json => StartRun(JsonUtility.FromJson<Args>(json)), owner: Owner);
            RevRuntimeAutomation.Register("mathab.state", "只读当前对照是否完成、帧样本和规则、反馈摘要", empty,
                _ => _current == null ? new Dictionary<string, object> { ["finished"] = false, ["active"] = false } : _current.State(),
                readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("mathab.cancel", "结束本轮采样并恢复模型时间设置", empty, _ =>
            {
                if (_current != null && !_current._finished)
                {
                    _current.Finish("cancelled");
                }
                return new Dictionary<string, object> { ["accepted"] = true };
            }, owner: Owner);
        }

        /// <summary>隔离 Play 一次只跑一份采样。观察真实交火时拒绝调整模型时钟。</summary>
        private static object StartRun(Args args)
        {
            if (RevServices.GetRequired<StoryProgress>().IsIsolated == false || (_current != null && !_current._finished))
            {
                return new Dictionary<string, object> { ["accepted"] = false, ["reason"] = "请先启动隔离 Play，并等待上一轮完成" };
            }
            if (args.mode == "campaign-current" && args.controlledClock)
            {
                return new Dictionary<string, object> { ["accepted"] = false, ["reason"] = "普通交火观察需要保留原模型时钟" };
            }
            if (_current != null)
            {
                Destroy(_current.gameObject);
            }

            var root = new GameObject("RevGame 算法配对测量");
            DontDestroyOnLoad(root);
            MathAbRun run = root.AddComponent<MathAbRun>();
            run._case = args.mode;
            run._variant = args.variant;
            run._profileFrames = args.mode == "campaign-current" || (args.profileFrames && args.mode == "campaign");
            run._previousCaptureStep = Time.captureDeltaTime;
            if (args.controlledClock && args.mode != "pvp")
            {
                Time.captureDeltaTime = BattleCaptureStep;
            }
            run._steps.Push(args.mode switch
            {
                "pvp" => MathAbScenarios.Pvp(run),
                "campaign" => MathAbScenarios.Campaign(run),
                "campaign-current" => MathAbScenarios.CampaignCurrent(run),
                "endless" => MathAbScenarios.Endless(run),
                _ => throw new ArgumentOutOfRangeException(nameof(args.mode))
            });
            _current = run;
            run.StartCoroutine(run.Execute());
            return new Dictionary<string, object> { ["accepted"] = true };
        }

        /// <summary>执行一个受控场景。每次迭代异常都交回宿主，同时恢复时间设置与试验订阅。</summary>
        private IEnumerator Execute()
        {
            while (!_finished && _steps.Count > 0)
            {
                object step;
                try
                {
                    var current = _steps.Peek();
                    if (!current.MoveNext())
                    {
                        (_steps.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    step = current.Current;
                    if (step is IEnumerator nested)
                    {
                        _steps.Push(nested);
                        continue;
                    }
                }
                catch (Exception error)
                {
                    Finish(error.ToString());
                    yield break;
                }
                yield return step;
            }
            if (_campaignProbe != null && !_finished)
            {
                // Unity 原生帧计时会滞后几帧。阶段战果已记完，窗口关闭后仅让回传数据到齐。
                for (int i = 0; i < 8 && !_finished; i++)
                {
                    yield return null;
                }
            }
            Finish(string.Empty);
        }

        /// <summary>由玩法在画面已准备好时打开窗口，排除大厅过场和加载。</summary>
        internal void BeginFrames()
        {
            if (_sampling || _finished || _count != 0)
            {
                throw new InvalidOperationException("每轮只允许一次完整的帧采样");
            }
            _environment = new Dictionary<string, object>
            {
                ["unity"] = Application.unityVersion, ["cpu"] = SystemInfo.processorType, ["gpu"] = SystemInfo.graphicsDeviceName,
                ["api"] = SystemInfo.graphicsDeviceType.ToString(), ["width"] = Screen.width, ["height"] = Screen.height,
                ["quality"] = QualitySettings.GetQualityLevel(), ["vSync"] = QualitySettings.vSyncCount,
                ["targetFrameRate"] = Application.targetFrameRate, ["captureDeltaTime"] = Time.captureDeltaTime,
                ["focused"] = Application.isFocused
            };
            _lastFrameAt = 0;
            if (_profileFrames)
            {
                _campaignProbe = new CampaignFrameDiagnostics(FindObjectOfType<BoardView>());
            }
            _sampling = true;
        }

        /// <summary>停止记录帧间隔，规则摘要和采样结果发布在窗口外。</summary>
        internal void EndFrames()
        {
            _sampling = false;
        }

        /// <summary>按实际业务顺序保存一次棋谱或战斗结果。</summary>
        internal void RecordRule(string record)
        {
            _rules.Add(record);
        }

        /// <summary>从本次操作启动时的 Stopwatch 计数，量到玩法确认反馈的这个运行点。</summary>
        internal void RecordFeedback(string label, long since)
        {
            RecordFeedback(label, since, Stopwatch.GetTimestamp());
        }

        /// <summary>使用已在同进程两个观察点捕获的时间戳，记录精确的反馈区段。</summary>
        internal void RecordFeedback(string label, long since, long until)
        {
            _feedback.Add(new Feedback(label, (until - since) * MillisecondsPerTick));
        }

        /// <summary>只在窗口内测每个完整帧间隔，稳定阶段无日志、资源查询或组织输出。</summary>
        private void LateUpdate()
        {
            if (!_finished)
            {
                _campaignProbe?.SampleNative();
            }
            if (!_sampling)
            {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            _campaignProbe?.ObserveLateUpdate(Time.frameCount, now);
            if (_lastFrameAt != 0)
            {
                if (_count == FrameCapacity)
                {
                    Finish("帧采样已满，无法保留整段");
                    return;
                }
                _intervals[_count++] = (now - _lastFrameAt) * MillisecondsPerTick;
                if (!Application.isFocused)
                {
                    _unfocused++;
                }
            }
            _lastFrameAt = now;
        }

        /// <summary>先停止记录，再关闭场景订阅，恢复调用前的模型时钟。可反复调用。</summary>
        private void Finish(string error)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            _error = error;
            EndFrames();
            try
            {
                while (_steps.Count > 0)
                {
                    (_steps.Pop() as IDisposable)?.Dispose();
                }
            }
            catch (Exception cleanupError)
            {
                _error += " 清理失败：" + cleanupError;
            }
            finally
            {
                _campaignProbe?.Dispose();
                Time.captureDeltaTime = _previousCaptureStep;
            }
        }

        /// <summary>停止 Play 或销毁测试对象时也归还时间设置。</summary>
        private void OnDestroy()
        {
            Finish("工单对象已销毁");
        }

        /// <summary>工单运行时只回进度。完成后再排序或构造数据，避免取证打断采样。</summary>
        private object State()
        {
            var state = new Dictionary<string, object>
            {
                ["active"] = !_finished, ["finished"] = _finished, ["mode"] = _case, ["variant"] = _variant, ["error"] = _error
            };
            if (_finished)
            {
                state["frame"] = FrameSummary();
                state["feedback"] = _feedback.ConvertAll(sample => new Dictionary<string, object>
                {
                    ["label"] = sample.Label, ["ms"] = sample.Milliseconds
                });
                state["rules"] = _rules;
                state["environment"] = _environment;
                if (_campaignProbe != null)
                {
                    state["campaignProfile"] = _campaignProbe.Retell();
                }
            }
            return state;
        }

        /// <summary>分段时长按真实间隔求和，百分位使用向上取整的名次并记录帧预算长尾。</summary>
        private object FrameSummary()
        {
            var ordered = new double[_count];
            Array.Copy(_intervals, ordered, _count);
            Array.Sort(ordered);
            double total = 0;
            int overBudget = 0, overDouble = 0;
            foreach (double ms in ordered)
            {
                total += ms;
                if (ms > FrameBudgetMs)
                {
                    overBudget++;
                }
                if (ms > 2 * FrameBudgetMs)
                {
                    overDouble++;
                }
            }
            return new Dictionary<string, object>
            {
                ["n"] = _count, ["elapsedMs"] = total, ["unfocused"] = _unfocused,
                ["meanMs"] = _count > 0 ? total / _count : 0,
                ["p95Ms"] = _count > 0 ? ordered[(int)Math.Ceiling(_count * .95) - 1] : 0,
                ["p99Ms"] = _count > 0 ? ordered[(int)Math.Ceiling(_count * .99) - 1] : 0,
                ["maxMs"] = _count > 0 ? ordered[_count - 1] : 0,
                ["over16Ms"] = overBudget, ["over33Ms"] = overDouble
            };
        }
    }
}
#endif
