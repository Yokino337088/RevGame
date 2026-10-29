// 非Play的固定入口：使用正式导表与真实服务验证单棋保护/Boss调度，绝不把纯规则结论当作画面或手机验收。
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
    /// <summary>
    /// 一次规则作业拥有自己的临时服务和事件；子检查finally负责停止AI/撤销监听。
    /// 本类负责配置装配、日志级别恢复和证据保存，不切换场景、不进入Play、不改存档。
    /// </summary>
    public static class GameBossSupportChecks
    {
        /// <summary>可由MCP/CLI读取的规则证据；条数包含重复冷却断言，不代表同等数量的独立场景。</summary>
        [Serializable]
        private sealed class Result
        {
            public bool passed;
            public string mode = "Unity Editor 编辑态，正式配置/真实服务规则；未执行画面、AB或设备检查";
            public string configurationSource;
            public string summary;
            public string error;
            public int protectionAssertions;
            public int pacingAssertions;
            public List<string> assertions = new List<string>();
        }

        /// <summary>
        /// 当前正式配置已装配时复用，未装配时由项目测试入口读取同一导表；退出恢复测试前的装配状态。
        /// 任一断言失败先保留第一原因，再抛给自动化桥，不能只返回“命令执行成功”。
        /// </summary>
        public static string Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Boss与单棋保护规则检查只允许稳定编辑态执行");
            }
            bool loaded = GameConfig.IsLoaded;
            RevLogLevel originalLogLevel = RevLog.MinLevel;
            var result = new Result { configurationSource = loaded ? "复用当前已装配正式配置" : "Assets/GameRes/Data正式导表" };
            string folder = Path.GetFullPath("../../../.codex/work/boss-impact-2026-10-29/pacing/editor-rules");
            try
            {
                GameConfigTestSupport.Ensure();
                // 固定输入会产生大量战斗Info，降低本次作业的日志噪声；Error与异常仍可见，结束恢复原级别。
                RevLog.MinLevel = RevLogLevel.Error;
                BattleService.VerifyHelperProtectionRules((ok, message) =>
                {
                    if (!ok) { throw new InvalidOperationException("单棋保护：" + message); }
                    result.protectionAssertions++;
                    result.assertions.Add("单棋保护：" + message);
                });
                result.summary = BattleService.VerifyBossPacingRules((ok, message) =>
                {
                    if (!ok) { throw new InvalidOperationException("Boss调度：" + message); }
                    result.pacingAssertions++;
                    result.assertions.Add("Boss调度：" + message);
                });
                result.passed = true;
            }
            catch (Exception error)
            {
                result.error = error.ToString();
                throw;
            }
            finally
            {
                // 先恢复全局测试环境，证据写入即使失败也不能留下配置/日志的半清理状态。
                RevLog.MinLevel = originalLogLevel;
                if (!loaded) { GameConfig.Reset(); }
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "rules.json"), JsonUtility.ToJson(result, true));
            }
            return JsonUtility.ToJson(result);
        }
    }
}
