#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Revolution;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>开发态用同一个滚动视口、模板和数据比较全量行与虚拟列表。</summary>
    public sealed partial class PvpLanPanel
    {
        /// <summary>两组均执行 60 个固定滚动步，并按相同步数重读数据。</summary>
        private const int BenchmarkFrames = 60;
        /// <summary>采样中暂停真实列表的自动刷新，两个用例都只走固定工作负载。</summary>
        private bool _benchmarkRunning;

        /// <summary>全量行基线同样缓存组件引用，计时阶段按旧策略遍历所有行。</summary>
        private sealed class BaselineRow
        {
            /// <summary>一个全量克隆。</summary>
            internal RectTransform Root;
            /// <summary>现有邀请按钮。</summary>
            internal Button Button;
            /// <summary>玩家名文字。</summary>
            internal Text Name;
            /// <summary>设备详情文字。</summary>
            internal Text Detail;
        }

        /// <summary>隔离比较建行和滚动帧间隔，报告 GC 计数是否可用。</summary>
        internal async RevTask<object> CompareListForCheck(RevCancellationToken token)
        {
            if (_benchmarkRunning || _devices == null || !IsOpened || Service.Lan != null)
            {
                throw new InvalidOperationException("请在未搜索的局域网菜单运行列表对比。");
            }
            _benchmarkRunning = true;
            RevUIVirtualList<PeerRow> list = _devices;
            var baseline = new List<BaselineRow>(1000);
            try
            {
                LoadListFixture();
                PeerRow[] rows = _fixtureRows;
                list.SetItems(Array.Empty<PeerRow>());
                await RevTaskScheduler.NextFrame();
                await RevTaskScheduler.NextFrame();
                CheckBenchmarkLifetime(list, token);

                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < rows.Length; i++)
                {
                    RectTransform item = Instantiate(_template, _scroll.content, false);
                    var row = new BaselineRow
                    {
                        Root = item,
                        Button = item.GetComponent<Button>(),
                        Name = item.Find("name").GetComponent<Text>(),
                        Detail = item.Find("detail").GetComponent<Text>()
                    };
                    string id = rows[i].Id;
                    row.Button.onClick.AddListener(() => _fixtureClicked = id);
                    item.gameObject.SetActive(true);
                    baseline.Add(row);
                }
                _scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rows.Length * RowHeight);
                DrawBaseline(rows, baseline);
                double baselineBuildMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                _scroll.verticalNormalizedPosition = 1;
                await RevTaskScheduler.NextFrame();
                await RevTaskScheduler.NextFrame();
                object full = await MeasureListFrames(list, rows, baseline, baselineBuildMs, token);

                ClearBaseline(baseline);
                _scroll.content.anchoredPosition = Vector2.zero;
                await RevTaskScheduler.NextFrame();
                await RevTaskScheduler.NextFrame();
                CheckBenchmarkLifetime(list, token);
                start = Stopwatch.GetTimestamp();
                list.SetItems(rows);
                double virtualBuildMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                await RevTaskScheduler.NextFrame();
                await RevTaskScheduler.NextFrame();
                object bounded = await MeasureListFrames(list, rows, null, virtualBuildMs, token);
                return new Dictionary<string, object>
                {
                    ["rows"] = rows.Length,
                    ["baseline"] = full,
                    ["virtual"] = bounded
                };
            }
            finally
            {
                ClearBaseline(baseline);
                _benchmarkRunning = false;
                if (ReferenceEquals(list, _devices))
                {
                    _fixtureRows = null;
                    _scroll.StopMovement();
                    RefreshView();
                }
            }
        }

        /// <summary>全量基线使用同一份数据和模板，每次刷新遍历全部行。</summary>
        private static void DrawBaseline(PeerRow[] data, List<BaselineRow> rows)
        {
            for (int i = 0; i < data.Length; i++)
            {
                PeerRow peer = data[i];
                BaselineRow row = rows[i];
                row.Name.text = peer.Name + "  ·  点击邀请";
                row.Detail.text = peer.Device + "  /  " + peer.Model + "\n" + peer.Address;
                row.Button.interactable = !peer.Busy;
                row.Root.anchoredPosition = new Vector2(0, -i * RowHeight);
            }
        }

        /// <summary>只清理由本次基线建立的节点，同帧先失活防止切组影响下一轮。</summary>
        private static void ClearBaseline(List<BaselineRow> rows)
        {
            foreach (BaselineRow row in rows)
            {
                if (row.Root != null)
                {
                    row.Root.gameObject.SetActive(false);
                    Destroy(row.Root.gameObject);
                }
            }
            rows.Clear();
        }

        /// <summary>工单取消、关闭或重开时，旧测量停止操作滚动控件。</summary>
        private void CheckBenchmarkLifetime(RevUIVirtualList<PeerRow> list, RevCancellationToken token)
        {
            token.ThrowIfCancelled();
            if (!ReferenceEquals(list, _devices) || !IsOpened)
            {
                throw new InvalidOperationException("列表对比中途关闭。");
            }
        }

        /// <summary>按固定帧进行滚动，每十步刷新同一份输入。窗口结束后才排序与生成回执。</summary>
        private async RevTask<object> MeasureListFrames(RevUIVirtualList<PeerRow> list, PeerRow[] data,
            List<BaselineRow> baseline, double buildMs, RevCancellationToken token)
        {
            float[] frames = new float[BenchmarkFrames];
            int gcSamples = 0;
            long gcBytes = 0;
            int peakInstances = 0;
            using (var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1))
            {
                for (int frame = 0; frame < frames.Length; frame++)
                {
                    CheckBenchmarkLifetime(list, token);
                    _scroll.verticalNormalizedPosition = 1f - (float)frame / (frames.Length - 1);
                    if (baseline != null)
                    {
                        if (frame % 10 == 0)
                        {
                            DrawBaseline(data, baseline);
                        }
                        peakInstances = baseline.Count;
                    }
                    else
                    {
                        if (frame % 10 == 0)
                        {
                            list.SetItems(data);
                        }
                        else
                        {
                            list.RefreshViewport();
                        }
                        peakInstances = Math.Max(peakInstances, list.InstanceCount);
                    }
                    await RevTaskScheduler.NextFrame();
                    frames[frame] = Time.unscaledDeltaTime * 1000f;
                    if (gc.Valid && gc.Count > 0)
                    {
                        gcSamples++;
                        gcBytes += gc.LastValue;
                    }
                }
            }
            Array.Sort(frames);
            return new Dictionary<string, object>
            {
                ["buildMs"] = buildMs,
                ["frames"] = frames.Length,
                ["p95Ms"] = frames[(int)Math.Ceiling(frames.Length * .95) - 1],
                ["gcSamples"] = gcSamples,
                ["gcBytes"] = gcSamples != 0 ? (long?)gcBytes : null,
                ["peakInstances"] = peakInstances
            };
        }
    }
}
#endif
