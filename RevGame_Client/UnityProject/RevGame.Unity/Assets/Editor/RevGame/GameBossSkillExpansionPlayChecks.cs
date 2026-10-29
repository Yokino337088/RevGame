using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
namespace RevGame.Editor
{
    /// <summary>真实图形Play + GM施法 + PC AB。夹具只保证存活观察，不参与正常数值结论。</summary>
    public static class GameBossSkillExpansionPlayChecks
    {
        [Serializable] private sealed class Evidence
        {
            public string status = "idle", error, current;
            public int errors;
            public List<string> assertions = new List<string>();
        }
        private static Evidence _result = new Evidence();
        private static readonly string Folder = Path.GetFullPath("../../../.codex/work/boss-skills-2026-10-29/play");
        public static string State() { return JsonUtility.ToJson(_result); }
        public static string Begin()
        {
            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated
                || RevServices.GetRequired<GameFlow>().Screen != GameScreen.Home || _result.status == "running")
            { throw new InvalidOperationException("需要隔离首页且没有运行中的技能验收"); }
            _result = new Evidence { status = "running" }; Directory.CreateDirectory(Folder); Run(); return "started";
        }
        private static void Check(bool ok, string message)
        {
            if (!ok) { throw new InvalidOperationException(message); }
            _result.assertions.Add(message);
        }
        private static async RevTask Wait(Func<bool> ready, string reason)
        {
            float until = Time.realtimeSinceStartup + 45;
            while (!ready())
            {
                if (!Application.isPlaying || Time.realtimeSinceStartup > until) { throw new InvalidOperationException(reason); }
                var flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsStoryPlaying && !flow.IsTransitioning) { flow.AdvanceStory(); }
                await RevTask.Yield();
            }
        }
        private static async void Run()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            Application.LogCallback logger = (message, stack, type) => { if (type == LogType.Error || type == LogType.Exception) { _result.errors++; _result.error = message; } };
            Application.logMessageReceived += logger;
            try
            {
                Time.timeScale = 1f;
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    Check(RevGM.Execute("RevGame/进入Boss关卡 " + chapter).Success, "GM正式进入章节" + chapter);
                    await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare && !flow.IsTransitioning, "Boss准备态超时");
                    Check(battle.LoadBossSkillPreview(chapter), "高生命视觉夹具章" + chapter);
                    await Wait(() => !flow.IsGameplayBlocked && UnityEngine.Object.FindObjectOfType<BoardView>()?.AreBossViewsReady(chapter) == true, "章节美术未加载");
                    await Wait(() => battle.PreparationGranted || !battle.VictoryAssistEnabled, "准备增益尚未实际发放");
                    Check(battle.StartBattle(), "正式开战章" + chapter + ":" + battle.LastError);
                    foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                    {
                        if (!BossSkillCatalog.IsSpecial(kind) || !BossSkillCatalog.IsUnlocked(kind,chapter)) { continue; }
                        BossSkillDef def = BossSkillCatalog.Get(kind);
                        _result.current = chapter + "/" + kind;
                        int hits = 0;
                        Action<BossSkillInfo> observer = info => { if (info.Kind == kind) { hits++; } };
                        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, observer, owner: _result);
                        try
                        {
                            var command = RevGM.Execute("RevGame/Boss技能 " + (int)kind);
                            Check(command.Success, "真实GM施法" + kind + ":" + command.Message);
                            // 规则持续时间按模拟时钟验收；截图开销可能让墙钟领先，不能据此误报未返场。
                            float end = battle.BattleElapsed + def.Windup + (def.Count - 1) * def.Interval + Mathf.Max(1f, def.Duration) + .6f;
                            float nextCapture = Time.realtimeSinceStartup;
                            float captureInterval = (end - battle.BattleElapsed) / 19f;
                            float timeout = Time.realtimeSinceStartup + 45f;
                            int frame = 0;
                            bool sawFx = false, sawTiles = false, sawHidden = false, sawFrozen = false, sawInvulnerable = false;
                            while (battle.BattleElapsed < end)
                            {
                                await RevTask.Yield();
                                if (Time.realtimeSinceStartup > timeout) { throw new InvalidOperationException(kind + " 模拟时钟未推进至 " + end); }
                                var board = UnityEngine.Object.FindObjectOfType<BoardView>();
                                sawFx |= board != null && board.ActiveBossFxCount > 0;
                                sawTiles |= UnityEngine.Object.FindObjectsOfType<BoardCellView>().Any(c => c.HasBossSkillAnimation);
                                sawHidden |= battle.PlayerPieces.Any(p => p.BossHidden);
                                sawFrozen |= battle.PlayerPieces.Any(p => p.BossFrozen);
                                sawInvulnerable |= battle.EnemyPieces.Any(p => p.IsKing && p.BossInvulnerable);
                                if (Time.realtimeSinceStartup >= nextCapture && frame < 20)
                                {
                                    ScreenCapture.CaptureScreenshot(Path.Combine(Folder, kind + "-" + (frame++).ToString("00") + ".png"));
                                    nextCapture = Time.realtimeSinceStartup + captureInterval;
                                }
                            }
                            Check(hits == def.Count, kind + "实际命中段=" + hits);
                            Check(sawFx && sawTiles, kind + "技能主体及棋盘Shader/Tween实际播放");
                            if (kind == BossSkillKind.BlackHole || kind == BossSkillKind.SwampMaw)
                            { Check(sawHidden && battle.PlayerPieces.All(p => !p.BossHidden), kind + "真实隐藏及返场（曾隐藏=" + sawHidden + "，仍隐藏=" + battle.PlayerPieces.Count(p => p.BossHidden) + "，模拟秒=" + battle.BattleElapsed + "）"); }
                            if (kind == BossSkillKind.FrozenPrison) { Check(sawFrozen && battle.PlayerPieces.All(p => !p.BossFrozen), "真实冻结及解冻"); }
                            if (kind == BossSkillKind.GlacialAegis) { Check(sawInvulnerable && battle.EnemyPieces.All(p => !p.BossInvulnerable), "冰封无敌在爆炸后结束"); }
                            await Wait(() => battle.BossState == RevGame.Battle.BossAIState.Idle, "Boss未收招");
                        }
                        finally { RevEvent.RemoveAllByOwner(_result); }
                    }
                    Check(flow.ReturnHome(), "正式返回首页章" + chapter);
                    await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "返回首页超时");
                    Check(UnityEngine.Object.FindObjectsOfType<BossSkillFxView>().Length == 0, "离场回收技能视图章" + chapter);
                }
                Check(_result.errors == 0, "真实Play新增Error/Exception为零");
                _result.status = "passed";
            }
            catch (Exception error) { _result.status = "failed"; _result.error = error.ToString(); }
            finally
            {
                Time.timeScale = oldScale;
                RevEvent.RemoveAllByOwner(_result);
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                Application.logMessageReceived -= logger;
                File.WriteAllText(Path.Combine(Folder, "result.json"), JsonUtility.ToJson(_result, true));
            }
        }
    }
}
