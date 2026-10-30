using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Data;
using RevGame.Services;
using UnityEngine;
namespace RevGame.Editor
{
    /// <summary>非Play规则验收，使用真实配置和服务，不触碰用户场景；机器证据保存在忽略目录。</summary>
    public static class GameBossSkillExpansionChecks
    {
        [Serializable]
        private sealed class Result
        {
            public bool passed; public string error, summary; public List<string> assertions = new List<string>();
        }
        public static string Run()
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("规则检查要求编辑态");
            }
            bool loaded = GameConfig.IsLoaded;
            var result = new Result();
            try
            {
                GameConfigTestSupport.Ensure();
                BattleService.VerifyExpandedBossSkills((ok, message) =>
                {
                    if (!ok)
                    {
                        throw new InvalidOperationException(message);
                    }
                    result.assertions.Add(message);
                });
                result.summary = BattleService.VerifyBossSkillExpansion((ok, message) =>
                {
                    if (!ok)
                    {
                        throw new InvalidOperationException(message);
                    }
                    result.assertions.Add(message);
                });
                result.passed = true;
            }
            catch (Exception error) { result.error = error.ToString(); throw; }
            finally
            {
                string folder = Path.GetFullPath("../../../.codex/work/boss-skills-2026-10-29");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "rules.json"), JsonUtility.ToJson(result, true));

                if (!loaded)
                {
                    GameConfig.Reset();
                }
            }
            return JsonUtility.ToJson(result);
        }
    }
}
