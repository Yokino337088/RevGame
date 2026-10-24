using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.Profiling;

namespace RevGame.Editor
{
    public static partial class GameScoreChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private const string Output = "Temp/ScoreVerification";
        /// <summary>真实AB流程内验收：伤害/积分走服务，密集渲染用明确标注的快照夹具，所有造分都在存档隔离区。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { throw new InvalidOperationException("需要空闲Play"); }
            LastResult = "运行中"; _checks = 0; Directory.CreateDirectory(Output);
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            long saved = RevServices.GetRequired<IScoreProgress>().Total;
            IDisposable preview = battle.BeginScorePreview();
            string outcome = "失败：验收未完成";
            try
            {
                VerifyLocalPersistence();
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "正式流程进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                Require(battle.LoadImpactScenario(), "加载固定高生命棋盘");
                battle.Tick(3); Require(battle.StartBattle(), "真实开战");
                await RevTask.Delay(500);
                BattleHudPanel hud = UnityEngine.Object.FindObjectOfType<BattleHudPanel>();
                BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                Require(hud != null && board != null, "真实HUD和棋盘存在");
                foreach (ScoreReelView reel in hud.GetComponentsInChildren<ScoreReelView>())
                {
                    int visibleDigits = 0;
                    foreach (UnityEngine.UI.Text digit in reel.GetComponentsInChildren<UnityEngine.UI.Text>())
                    {
                        if (digit.name == "Current" && digit.cachedTextGenerator.vertexCount > 0 && !digit.canvasRenderer.cull) { visibleDigits++; }
                    }
                    Require(visibleDigits > 0, "滚筒必须生成可见数字网格，不能只有Tween句柄：" + reel.name);
                }
                battle.PreviewScoreHit(BattleDamageKind.Critical, false);
                await RevTask.Delay(120);
                Require(battle.RoundScore > 0 && hud.ActiveScoreSparks > 0 && hud.ScoreIsRolling, "真实暴击到账、飞星与数字滚筒启动");
                bool criticalText = false;
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    if (effect.DamageKind != BattleDamageKind.Critical || !effect.IsFloatingText) { continue; }
                    Color color = effect.TextColor;
                    criticalText |= effect.TextContent.Contains("暴击") && color.r > .9f && color.g > .4f && color.g < .7f && color.b < .25f;
                }
                Require(criticalText, "实际文字包含暴击且为Excel橙色");
                for (int i = 0; i < 24; i++)
                {
                    if (i % 5 == 0) { battle.PreviewScoreHit(BattleDamageKind.Critical, false); }
                    if (i == 8) { battle.PreviewScoreHit(BattleDamageKind.Normal, true); }
                    // 技能外观展示只注入已结算快照；真实来源/归属另由服务规则断言覆盖。
                    if (i == 3 || i == 12)
                    {
                        BattleDamageKind[] styles = { BattleDamageKind.RegionPulse, BattleDamageKind.EnergyBomb, BattleDamageKind.BossPoisonMarsh, BattleDamageKind.BossFrostNova, BattleDamageKind.BossFlameWall };
                        for (int n = 0; n < styles.Length; n++)
                        {
                            RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(7000 + n, new BoardPos(n * 2, 6), 1,
                                GameSide.Enemy, 27 + n * 5, false, default(BoardPos), fromSkill: true, damageKind: styles[n]));
                        }
                    }
                    await RevTask.Delay(70);
                    Require(hud.ActiveScoreSparks <= BattleHudPanel.MaxScoreSparks, "积分飞星严格有界");
                    ScreenCapture.CaptureScreenshot(Output + "/score-" + i.ToString("D2") + ".png");
                }
                await RevTask.Delay(1300);
                Require(hud.ActiveScoreSparks == 0 && !hud.ScoreIsRolling, "动画自然结束并归池");
                long points = battle.RoundScore;
                await MeasureRendering(battle, board, hud);
                Require(battle.RoundScore >= points, "渲染繁忙不丢失真实积分");
                Require(battle.StartRound(1, 2) && battle.RoundScore == 0 && battle.LifetimeScore > 0, "换回合清本轮、保留累计");
                await RevTask.Delay(200);
                Require(hud.ActiveScoreSparks == 0 && board.ActiveEffectCount == 0, "换回合清掉飞星和伤害动画");
                Require(RevServices.GetRequired<IScoreProgress>().Total == saved, "视觉验收没有改写玩家永久积分");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(200);
                Require(UnityEngine.Object.FindObjectsOfType<ScoreSparkView>().Length == 0, "退战斗后无活动飞星");
                outcome = "通过 " + _checks + " 条Play积分/伤害/滚筒/回收断言；证据 " + Output;
            }
            catch (Exception error) { outcome = "失败：" + error; }
            finally
            {
                try { await GameSceneTransitionChecks.ReturnHomeAsync(flow); }
                catch (Exception cleanup) { outcome += "；清理失败：" + cleanup.Message; }
                preview.Dispose();
                LastResult = outcome; File.WriteAllText(Output + "/play.txt", outcome);
            }
        }

        /// <summary>使用本轮专属测试键检验原生存储往返，finally仅清本次创建的键，不读写玩家生产键。</summary>
        private static void VerifyLocalPersistence()
        {
            string key = "RevGame.Score.Verification." + Guid.NewGuid().ToString("N");
            try
            {
                using (var first = new ScoreProgressStore(key)) { first.Record(1234567890123L); first.Flush(); }
                using (var second = new ScoreProgressStore(key))
                {
                    Require(second.Total == 1234567890123L, "本地存储重建恢复64位永久总积分");
                    second.Record(1); Require(second.Total == 1234567890123L, "迟到旧总量不能覆盖新存档");
                }
            }
            finally { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); }
        }

        /// <summary>同一高生命棋盘，不截图、不推进AI；采样的是编辑器渲染帧，不冒充低端机帧率或GPU耗时。</summary>
        private static async RevTask MeasureRendering(BattleService battle, BoardView board, BattleHudPanel hud)
        {
            var frames = new List<double>(180); var draws = new List<int>(180); var batches = new List<int>(180);
            var gpu = new List<double>(); var timing = new FrameTiming[1];
            long before = Profiler.GetTotalAllocatedMemoryLong();
            int peakProjectiles = 0, peakText = 0, peakStars = 0;
            for (int frame = 0; frame < 210; frame++)
            {
                if (frame % 4 == 0)
                {
                    PieceKind[] kinds = { PieceKind.Archer, PieceKind.Crossbow, PieceKind.MageWater, PieceKind.MageFire, PieceKind.Priest };
                    for (int n = 0; n < 5; n++)
                    {
                        RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(8000 + n, new BoardPos(8, n + 2), 1,
                            GameSide.Enemy, 12, true, new BoardPos(0, n + 2), 9000 + n, sourceKind: kinds[n], sourceIsRanged: true));
                    }
                    battle.PreviewScoreHit(BattleDamageKind.Critical, false);
                }
                await RevTask.Yield();
                FrameTimingManager.CaptureFrameTimings();
                if (frame < 30) { continue; }
                frames.Add(Time.unscaledDeltaTime * 1000d); draws.Add(UnityEditor.UnityStats.drawCalls); batches.Add(UnityEditor.UnityStats.batches);
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0) { gpu.Add(timing[0].gpuFrameTime); }
                peakProjectiles = Math.Max(peakProjectiles, board.ActiveProjectileCount); peakText = Math.Max(peakText, board.ActiveFloatingTextCount);
                peakStars = Math.Max(peakStars, hud.ActiveScoreSparks);
                Require(board.ActiveProjectileCount <= 35 && board.ActiveFloatingTextCount <= 20 && hud.ActiveScoreSparks <= 12, "密集表现预算");
            }
            long peakMemory = Profiler.GetTotalAllocatedMemoryLong();
            await RevTask.Delay(1700);
            Require(board.ActiveEffectCount == 0 && hud.ActiveScoreSparks == 0, "压力停止后所有活动反馈归零");
            frames.Sort(); draws.Sort(); batches.Sort(); gpu.Sort();
            string report = "180帧采样（30帧预热，不含截图；编辑器，非手机）\n"
                + "帧间隔中位数ms=" + frames[90].ToString("F3") + "；P95=" + frames[171].ToString("F3")
                + "\nDrawCalls中位数=" + draws[90] + "；Batches=" + batches[90]
                + "\nGPU时间=" + (gpu.Count > 0 ? gpu[gpu.Count / 2].ToString("F3") + "ms" : "当前图形后端未提供有效计时，未判定")
                + "\nUnity总已分配内存前/采样末/回收后MiB=" + (before / 1048576d).ToString("F2") + "/" + (peakMemory / 1048576d).ToString("F2")
                + "/" + (Profiler.GetTotalAllocatedMemoryLong() / 1048576d).ToString("F2")
                + "\n峰值弹道/文字/积分飞星=" + peakProjectiles + "/" + peakText + "/" + peakStars
                + "\n总分=" + battle.RoundScore + "；内存含编辑器与保留池，不等于GC分配，不代表泄漏判定。";
            File.WriteAllText(Output + "/render-performance.txt", report);
        }
    }
}
