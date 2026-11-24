#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Pvp
{
    public static partial class PvpAutomation
    {
        /// <summary>登记安卓开发包与编辑器共用的试验入口，只有编辑器额外允许注入模拟热态。</summary>
        private static void RegisterAndroidTacticalTrial()
        {
            RevRuntimeAutomation.Register("pvp.tactical-android-mode", "隔离九路本局使用 1/2/4 组，实际组数受热状态限制，0 清理",
                "{\"type\":\"object\",\"properties\":{\"groups\":{\"type\":\"integer\",\"enum\":[0,1,2,4]}},\"required\":[\"groups\"],\"additionalProperties\":false}",
                json => SetAndroidTrialMode(JsonUtility.FromJson<AndroidTacticalModeArgs>(json).groups), owner: Owner);
            RevRuntimeAutomation.Register("pvp.tactical-android-state", "读取安卓试验请求、热态上限和最近实战搜索，不采样或推进对局",
                Empty, _ => AndroidTrialState(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.tactical-android-compare", "用同一棋盘交错计时 1/2/4 组并核对完整排名，秒表不计热采样",
                "{\"type\":\"object\",\"properties\":{\"rounds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":12}},\"additionalProperties\":false}",
                json => CompareAndroidTrial(JsonUtility.FromJson<AndroidTacticalCompareArgs>(json).rounds), owner: Owner);
#if UNITY_EDITOR
            RevRuntimeAutomation.Register("pvp.tactical-android-simulate", "仅编辑器隔离局注入热等级、省电、前后台和经过秒数",
                "{\"type\":\"object\",\"properties\":{\"thermalStatus\":{\"type\":\"integer\",\"minimum\":-1,\"maximum\":6},\"powerSave\":{\"type\":\"boolean\"},\"focused\":{\"type\":\"boolean\"},\"advanceSeconds\":{\"type\":\"number\",\"minimum\":0,\"maximum\":60}},\"required\":[\"thermalStatus\",\"powerSave\",\"focused\",\"advanceSeconds\"],\"additionalProperties\":false}",
                json => SimulateAndroidTrial(JsonUtility.FromJson<AndroidTacticalSignalArgs>(json)), owner: Owner);
#endif
        }

        /// <summary>实战组数请求。零用在 finally 和手动退出测试，归还本局的试验资源。</summary>
        [Serializable]
        private sealed class AndroidTacticalModeArgs
        {
            /// <summary>组数只接受 1、2、4，0 关闭试验。</summary>
            public int groups;
        }

        /// <summary>限制每条搜索路线的样本数，避免长时间占用编辑器或手机的主线程。</summary>
        [Serializable]
        private sealed class AndroidTacticalCompareArgs
        {
            /// <summary>每条路线的实测次数，省略时为 6，显式范围 1 至 12。</summary>
            public int rounds;
        }

#if UNITY_EDITOR
        /// <summary>编辑器替代 Android PowerManager 的输入。只影响本局试验状态。</summary>
        [Serializable]
        private sealed class AndroidTacticalSignalArgs
        {
            /// <summary>Android 热等级，-1 代表未知，0 正常，1 轻度，2 至 6 触发单组。</summary>
            public int thermalStatus;
            /// <summary>是否模拟系统省电模式。</summary>
            public bool powerSave;
            /// <summary>是否模拟游戏处于前台。</summary>
            public bool focused;
            /// <summary>虚拟时钟前进量，单位秒，零用于立即切换状态。</summary>
            public double advanceSeconds;
        }
#endif

        /// <summary>仅允许已隔离的本地棋盘。云对手启用时拒绝，避免把服务请求算作本地搜索。</summary>
        private static bool CanUseAndroidTrial(out string reason)
        {
            if (RevServices.GetRequired<StoryProgress>().IsIsolated && Ready(out _) &&
                Service.Mode == PvpConnectionMode.Local && Service.CanAct && Service.Core?.Phase == GoPhase.Playing &&
                (!Service.IsComputerGame || !Service.AgentSettings.Enabled))
            {
                reason = string.Empty;
                return true;
            }
            reason = "需要隔离的本地对局并关闭云对手；等待九路棋盘可落子后再试";
            return false;
        }

        /// <summary>为真实本地对局选择组数。退场后仍可安全调用零，便于失败清理。</summary>
        private static object SetAndroidTrialMode(int groups)
        {
            if (groups != 0 && !CanUseAndroidTrial(out string reason))
            {
                return Result(false, reason);
            }
            Service.SetAndroidTacticalGroups(groups);
            var result = AndroidTrialState();
            result["accepted"] = true;
            return result;
        }

        /// <summary>只读取本局诊断，不用查询本身推进冷却窗口。未开试验时返回清理后的默认值。</summary>
        private static Dictionary<string, object> AndroidTrialState()
        {
            PvpAndroidTacticalTrial trial = Service.AndroidTacticalTrial;
            return new Dictionary<string, object>
            {
                ["active"] = trial != null, ["requestedGroups"] = trial?.RequestedGroups ?? 0,
                ["allowedGroups"] = trial?.AllowedGroups ?? 1, ["thermalStatus"] = trial?.ThermalStatus ?? -1,
                ["powerSave"] = trial?.PowerSave ?? false, ["focused"] = trial?.Focused ?? false,
                ["recoveryRemainingSeconds"] = trial?.RecoveryRemaining ?? 0, ["reason"] = trial?.Reason ?? "inactive",
                ["lastGroups"] = trial != null ? Service.TacticalSearchPartitions : 0,
                ["lastSearchMs"] = Service.AndroidTacticalSearchMs
            };
        }

#if UNITY_EDITOR
        /// <summary>在当前隔离棋盘验证即时降档和连续健康后的恢复，不向 Android 包注册此入口。</summary>
        private static object SimulateAndroidTrial(AndroidTacticalSignalArgs args)
        {
            if (!CanUseAndroidTrial(out string reason) || Service.AndroidTacticalTrial == null)
            {
                return Result(false, reason.Length > 0 ? reason : "请先启用本局安卓试验");
            }
            Service.AndroidTacticalTrial.Simulate(args.thermalStatus, args.powerSave, args.focused, args.advanceSeconds);
            var result = AndroidTrialState();
            result["accepted"] = true;
            return result;
        }
#endif

        /// <summary>每轮三条路线共用同一热样本，交错测量并核对排序。实盘保持原状态。</summary>
        private static object CompareAndroidTrial(int rounds)
        {
            PvpAndroidTacticalTrial trial = Service.AndroidTacticalTrial;
            if (!CanUseAndroidTrial(out string reason) || trial == null)
            {
                throw new InvalidOperationException(reason.Length > 0 ? reason : "请先启用本局安卓试验");
            }
            GoCore board = Service.Core;
            if (Service.IsComputerGame && board.Turn == GoSide.White)
            {
                throw new InvalidOperationException("请在玩家回合执行集中试算，白棋正常思考时用实战状态取样");
            }

            rounds = rounds == 0 ? 6 : rounds;
            const int warmups = 3;
            int[] requests = { 1, 2, 4 };
            var search = new GoTacticalSearch(board, maxPartitions: 4);
            var hints = new[] { new GoHint[10], new GoHint[10], new GoHint[10] };
            var counts = new int[requests.Length];
            var baits = new GoHint[requests.Length];
            var samples = new Dictionary<string, object>[requests.Length];
            var allowed = new int[requests.Length][];
            var actual = new int[requests.Length][];
            var elapsed = new double[requests.Length][];
            var thermalStatus = new int[rounds];
            var policyReasons = new string[rounds];
            for (int route = 0; route < requests.Length; route++)
            {
                allowed[route] = new int[rounds];
                actual[route] = new int[rounds];
                elapsed[route] = new double[rounds];
                samples[route] = new Dictionary<string, object>
                {
                    ["requestedGroups"] = requests[route], ["allowedGroups"] = allowed[route],
                    ["actualGroups"] = actual[route], ["searchMs"] = elapsed[route]
                };
            }

            ulong hash = board.StateHash;
            for (int i = 0; i < warmups + rounds; i++)
            {
                // 每轮只查一次系统电源服务。三条路线共享同一热限制，采样也不进入搜索计时。
                trial.Refresh(beforeSearch: true);
                int ceiling = trial.AllowedGroups;
                int sample = i - warmups;
                if (sample >= 0)
                {
                    thermalStatus[sample] = trial.ThermalStatus;
                    policyReasons[sample] = trial.Reason;
                }
                // 每轮把起跑路线往后挪一个。三条路线复用已预热棋盘工作区，避免平局选择随完成顺序变化。
                for (int jump = 0; jump < requests.Length; jump++)
                {
                    int route = (i + jump) % requests.Length;
                    int cap = Math.Min(requests[route], ceiling);
                    long start = Stopwatch.GetTimestamp();
                    counts[route] = search.Rank(board, hints[route], out baits[route], partitionLimit: cap);
                    double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    if (sample >= 0)
                    {
                        allowed[route][sample] = cap;
                        actual[route][sample] = search.LastPartitionCount;
                        elapsed[route][sample] = ms;
                    }
                }

                if (board.StateHash != hash)
                {
                    throw new InvalidOperationException("试算改变了实盘");
                }
                for (int route = 1; route < requests.Length; route++)
                {
                    if (counts[0] != counts[route] || !baits[0].Equals(baits[route]))
                    {
                        throw new InvalidOperationException("试算候选数或诱敌建议不一致，申请组数 " + requests[route]);
                    }
                    for (int index = 0; index < counts[0]; index++)
                    {
                        if (!hints[0][index].Equals(hints[route][index]))
                        {
                            throw new InvalidOperationException("试算排序或分数不一致，下标 " + index);
                        }
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["passed"] = true, ["positions"] = board.PlacementCount, ["stateHash"] = hash.ToString(),
                ["candidates"] = counts[0], ["samples"] = samples, ["trial"] = AndroidTrialState(),
                ["thermalStatusPerRound"] = thermalStatus, ["policyReasonPerRound"] = policyReasons
            };
        }
    }
}
#endif
