using System;
using System.Collections.Generic;
using RevGame.Data;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>对局台词只从导表读取。Cue由业务事实选择，供应商原文不会进入游戏UI。</summary>
    internal sealed class PvpYayaLines
    {
        private readonly Dictionary<string, List<PvpYayaLine>> _lines = new Dictionary<string, List<PvpYayaLine>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _next = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>由对局面板入场装配，加载并校验台词、表情与落点占位符。</summary>
        public PvpYayaLines(YayaExpressionCatalog catalog)
        {
            foreach (var row in RevDataTableManager.Get<PvpYayaLineTable>().All)
            {
                if (string.IsNullOrWhiteSpace(row.Cue) || string.IsNullOrWhiteSpace(row.Line) ||
                    float.IsNaN(row.Seconds) || float.IsInfinity(row.Seconds) || row.Seconds <= 0)
                {
                    throw new InvalidOperationException("九路围棋鸦鸦台词的事件、文本或秒数非法，编号 " + row.Id);
                }
                if (!_lines.TryGetValue(row.Cue, out var entries))
                {
                    entries = new List<PvpYayaLine>();
                    _lines.Add(row.Cue, entries);
                }
                // 入场校验Excel表情，缺图在转场时就能发现。
                catalog.Find(row.Expression);
                bool needsPoint = row.Cue.StartsWith("Analysis", StringComparison.Ordinal) ||
                    row.Cue == "HintCloud" || row.Cue == "HintLocal" || row.Cue == "HintBait";
                if (needsPoint && !row.Line.Contains("{point}"))
                {
                    throw new InvalidOperationException("九路围棋落点台词缺少 {point}，编号 " + row.Id);
                }
                entries.Add(row);
            }

            foreach (string cue in new[] { "Opening", "Taunt", "HintThinking", "HintCloud", "HintLocal", "HintPass",
                "OpponentThinking", "OpponentPlay", "OpponentPass", "OpponentFallback", "HintBait",
                "AnalysisAttack", "AnalysisDefend", "AnalysisConnect", "AnalysisTerritory", "AnalysisReduce", "AnalysisEndgame", "AnalysisBalance" })
            {
                if (!_lines.ContainsKey(cue))
                {
                    throw new InvalidOperationException("九路围棋鸦鸦台词缺少事件 " + cue);
                }
            }
        }

        /// <summary>相同事件轮换整行配置，台词与表情总是成对。</summary>
        public PvpYayaLine Take(string cue)
        {
            var entries = _lines[cue];
            _next.TryGetValue(cue, out int index);
            _next[cue] = (index + 1) % entries.Count;
            return entries[index];
        }
    }
}
