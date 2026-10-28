using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Data;
using RevGame.Services;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>独立规则断言不进入Play，不把纯服务回放冒充画面验收。</summary>
    public static class GameHelperChecks
    {
        [Serializable] private sealed class Evidence { public List<string> assertions = new List<string>(); public string error; }
        public static string Run()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("规则检查需编辑态"); }
            bool loaded = GameConfig.IsLoaded;
            RevLogLevel old = RevLog.MinLevel;
            var evidence = new Evidence();
            string folder = Path.GetFullPath("../../../.codex/work/helper-2026-10-28");
            Directory.CreateDirectory(folder);
            try
            {
                GameConfigTestSupport.Ensure();
                RevLog.MinLevel = RevLogLevel.Error;
                BattleService.VerifyHelperRules((ok, message) =>
                {
                    if (!ok) { throw new InvalidOperationException(message); }
                    evidence.assertions.Add(message);
                });
                return evidence.assertions.Count + "条帮手与棋盘管理真实规则断言通过";
            }
            catch (Exception error) { evidence.error = error.Message; throw; }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "rules.json"), JsonUtility.ToJson(evidence, true));
                RevLog.MinLevel = old;
                if (!loaded) { GameConfig.Reset(); }
            }
        }
    }
}
