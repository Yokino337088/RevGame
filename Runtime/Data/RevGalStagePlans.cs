using System;
using System.Collections.Generic;

namespace Revolution.Gal
{
    /// <summary>把章节和小节的继承关系整理成有序方案链，供运行时、校验和编辑器共用。</summary>
    public static class RevGalStagePlans
    {
        /// <summary>校验 ID、父引用和循环。保留原始覆盖步骤，使短横线能真正清除上一幕素材。</summary>
        public static Dictionary<string, RevGalStage[]> Compile(RevGalDefinition story)
        {
            var presets = new Dictionary<string, RevGalStagePreset>(StringComparer.Ordinal);
            foreach (var preset in story.StagePresets ?? new List<RevGalStagePreset>())
            {
                if (preset == null || !RevGalValidation.IsId(preset.Id) || presets.ContainsKey(preset.Id))
                {
                    throw new ArgumentException("章节 / 小节舞台方案 ID 无效或重复：" + preset?.Id);
                }
                presets.Add(preset.Id, preset);
            }
            var result = new Dictionary<string, RevGalStage[]>(StringComparer.Ordinal);
            foreach (var preset in presets.Values)
            {
                var chain = new List<RevGalStage>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = preset;
                while (current != null)
                {
                    if (!visited.Add(current.Id))
                    {
                        throw new ArgumentException("章节 / 小节方案存在继承循环：" + string.Join(" → ", visited) + " → " + current.Id);
                    }
                    chain.Add(current.Stage);
                    if (string.IsNullOrEmpty(current.Parent))
                    {
                        break;
                    }
                    if (!presets.TryGetValue(current.Parent, out current))
                    {
                        throw new ArgumentException("方案「" + preset.Id + "」的父方案不存在");
                    }
                }
                chain.Reverse();
                result.Add(preset.Id, chain.ToArray());
            }
            return result;
        }
    }
}
