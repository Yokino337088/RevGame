using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>GM驱动的真实Play验收：只注入高生命视觉输入，启动、撤棋、伤害和退出均走正式业务。</summary>
    public static class GameStormPlayChecks
    {
        [Serializable]
        private sealed class Evidence
        {
            public string status = "idle", error;
            public List<string> assertions = new List<string>();
            public int errors, missiles, jellyfish, beams, explosions, movingBeamSamples;
            public float maxOriginError;
        }
        private static Evidence _result = new Evidence();
        private static readonly string Folder = Path.GetFullPath("../../../.codex/work/storm-2026-10-28/play");
        public static string State()
        {
            return JsonUtility.ToJson(_result);
        }
        public static string Begin()
        {
            var flow = RevServices.GetRequired<GameFlow>();

            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated || flow.Screen != GameScreen.Home || _result.status == "running")
            {
                throw new InvalidOperationException("风暴测试需要隔离首页且没有运行中的测试");
            }
            _result = new Evidence { status = "running" };
            Directory.CreateDirectory(Folder);
            Run();
            return "started";
        }
        private static void Check(bool ok, string message)
        {
            if (!ok)
            {
                throw new InvalidOperationException(message);
            }
            _result.assertions.Add(message);
        }
        private static async RevTask Wait(Func<bool> predicate, string message)
        {
            float deadline = Time.realtimeSinceStartup + 35;
            while (!predicate())
            {
                if (!Application.isPlaying || Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException(message);
                }
                var flow = RevServices.GetRequired<GameFlow>();

                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                await RevTask.Yield();
            }
        }
        private static async void Run()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    _result.errors++;
                    _result.error = message;
                }
            };
            Application.logMessageReceived += logger;
            var missileFrames = new HashSet<int>();
            var jellyFrames = new HashSet<int>();
            var explosionFrames = new HashSet<int>();
            try
            {
                Time.timeScale = 1;
                flow.OpenLevels();
                Check(flow.EnterBattle(1), "正式GameFlow接受入场");
                await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "准备态未就绪");
                Check(battle.LoadHelperPreview(1, false), "建立隔离高生命视觉输入，不用于Boss时长统计");
                await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperVisualReady == true, "帮手资源未就绪");
                // 先保留三颗，再用GM撤掉第三颗，验证真实玩家操作触发风暴而非直接写状态。
                while (battle.PlayerPieces.Count > 3)
                {
                    Check(battle.RemovePlayerPiece(battle.PlayerPieces[battle.PlayerPieces.Count - 1].Id), "准备阶段正式撤棋");
                }
                Check(battle.StartBattle(), "正式开始战斗");
                await RevTask.Delay(250);
                Check(!battle.Helper.Storm, "三颗玩家棋不触发");
                ScreenCapture.CaptureScreenshot(Path.Combine(Folder, "initial.png"));
                Check(RevGM.Execute("RevGame/移除棋子 " + battle.PlayerPieces[2].Id).Success, "GM调用正式撤棋入口");
                await Wait(() => battle.Helper.Storm, "二颗玩家棋没有触发风暴");
                var helper = UnityEngine.Object.FindObjectOfType<HelperView>();
                Vector3 prior = helper.transform.position;
                float until = Time.realtimeSinceStartup + 14, nextCapture = 0;
                int capture = 0;
                while (Time.realtimeSinceStartup < until)
                {
                    await RevTask.Yield();
                    helper = UnityEngine.Object.FindObjectOfType<HelperView>();
                    bool moving = helper != null && Vector3.Distance(prior, helper.transform.position) > .0001f;

                    if (helper != null)
                    {
                        prior = helper.transform.position;
                    }
                    foreach (var attack in UnityEngine.Object.FindObjectsOfType<HelperAttackView>())
                    {
                        if (attack.IsBeam && attack.IsStorm)
                        {
                            _result.beams++;
                            _result.maxOriginError = Math.Max(_result.maxOriginError, attack.OriginError);

                            if (moving)
                            {
                                _result.movingBeamSamples++;
                            }
                        }

                        var visual = attack.StormVisual;

                        if (visual == null || !visual.Visible)
                        {
                            continue;
                        }
                        if (visual.IsExplosion)
                        {
                            _result.explosions++;
                            explosionFrames.Add(visual.FrameIndex);
                        }
                        else

                            if (visual.Weapon == HelperWeapon.Missile)
                            {
                                _result.missiles++;
                                missileFrames.Add(visual.FrameIndex);
                            }
                            else

                                if (visual.Weapon == HelperWeapon.Jellyfish)
                                {
                                    _result.jellyfish++;
                                    jellyFrames.Add(visual.FrameIndex);
                                }
                    }

                    if (Time.realtimeSinceStartup >= nextCapture && capture < 40)
                    {
                        ScreenCapture.CaptureScreenshot(Path.Combine(Folder, "storm-" + (capture++).ToString("00") + ".png"));
                        nextCapture = Time.realtimeSinceStartup + .12f;
                    }
                }
                Check(missileFrames.Count == 3 && jellyFrames.Count == 5 && explosionFrames.Count == 4, "导弹3帧、水母5帧、爆炸4帧全部在相机中实际播放");
                Check(_result.beams > 0 && _result.movingBeamSamples > 0, "帮手移动期间存在真实风暴激光");
                Check(_result.maxOriginError < .03f, "激光起点跟随实际帮手，小于0.03世界单位误差");
                Check(battle.Helper.Storm && battle.PlayerPieces.Count == 2, "持续战斗保持双棋风暴状态");
                ScreenCapture.CaptureScreenshot(Path.Combine(Folder, "final.png"));
                Check(flow.ReturnHome(), "正式退出战斗");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "返回首页未完成");
                Check(UnityEngine.Object.FindObjectsOfType<HelperAttackView>().Length == 0 && UnityEngine.Object.FindObjectOfType<HelperView>() == null, "退出回收所有帮手及攻击视图");
                Check(!battle.HelperStormActive, "退出清风暴模型状态");
                Check(_result.errors == 0, "本次Play新增Unity错误为零");
                _result.status = "passed";
            }
            catch (Exception error) { _result.status = "failed"; _result.error = error.ToString(); }
            finally
            {
                Time.timeScale = oldScale;

                if (flow.Screen != GameScreen.Home)
                {
                    flow.ReturnHome();
                }
                Application.logMessageReceived -= logger;
                File.WriteAllText(Path.Combine(Folder, "result.json"), JsonUtility.ToJson(_result, true));
            }
        }
    }
}
