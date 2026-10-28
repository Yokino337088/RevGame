using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Automation;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>真实Play视觉入口：六章材质、四帧、UI移除、指定目标、盘外、弹幕/激光与退出回收。</summary>
    public static class GameHelperPlayChecks
    {
        [Serializable] private sealed class Evidence { public List<string> assertions = new List<string>(); public string status = "idle", error; public int unityErrors; }
        private static Evidence _evidence = new Evidence();
        private static string _folder;
        private static int _diamondSamples;
        private static readonly HashSet<int> FramesObserved = new HashSet<int>();
        public static string State() { return JsonUtility.ToJson(_evidence); }
        public static string Begin()
        {
            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated || _evidence.status == "running")
            { throw new InvalidOperationException("帮手Play测试需隔离首页且没有其它帮手测试"); }
            _evidence = new Evidence { status = "running" };
            _diamondSamples = 0; FramesObserved.Clear();
            _folder = Path.GetFullPath("../../../.codex/work/helper-2026-10-28/play");
            Directory.CreateDirectory(_folder);
            RunAsync(); return "started";
        }
        private static void Check(bool ok, string message)
        {
            if (!ok) { throw new InvalidOperationException(message); }
            _evidence.assertions.Add(message);
        }
        private static async RevTask Wait(Func<bool> condition, string message)
        {
            float end = Time.realtimeSinceStartup + 35;
            while (!condition())
            {
                if (!Application.isPlaying || Time.realtimeSinceStartup > end) { throw new InvalidOperationException(message); }
                var flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsStoryPlaying && !flow.IsTransitioning) { flow.AdvanceStory(); }
                await RevTask.Yield();
            }
        }
        private static async RevTask Capture(string title)
        {
            for (int i = 0; i < 12; i++)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(_folder, title + "-" + i.ToString("00") + ".png"));
                foreach (HelperAttackView attack in UnityEngine.Object.FindObjectsOfType<HelperAttackView>())
                {
                    if (attack.DiamondVisible) { _diamondSamples++; }
                    File.AppendAllText(Path.Combine(_folder, "visual-geometry.txt"), title + " " + i + " " + attack.VisualStatus + " " + attack.DiamondStatus + "\n");
                }
                HelperView helper = UnityEngine.Object.FindObjectOfType<HelperView>();
                if (helper != null)
                {
                    Check(helper.GoldReflectionReady, "金光仍采样真实图集帧" + title + "-" + i);
                    FramesObserved.Add(helper.VisibleFrameIndex);
                }
                await RevTask.Delay(120);
            }
        }
        private static async void RunAsync()
        {
            var pointer = new GamePointerInput();
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            IDisposable scorePreview = null;
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception) { _evidence.unityErrors++; _evidence.error = message; }
            };
            Application.logMessageReceived += logger;
            object owner = new object();
            int barrageShots = 0, beamShots = 0;
            long spent = 0;
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, info =>
            {
                if (info.Beam) { beamShots++; } else { barrageShots++; }
                spent += info.Cost;
            }, owner: owner);
            try
            {
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsGameplayBlocked, "隔离首页未就绪");
                // 隔离GameFlow已持有积分预览租约，不能再嵌套；测试只在这个既有隔离区内造局。
                Time.timeScale = 1;
                battle.UnlockAllChaptersForDevelopment();
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    flow.OpenLevels(); Check(flow.EnterBattle(chapter), "接受进入章节" + chapter);
                    await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "章节过场失败");
                    Check(battle.LoadHelperPreview(chapter, false), "独立视觉输入" + chapter);
                    await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperVisualReady == true, "帮手四帧/材质未就绪");
                    Check(RevGM.Execute("RevGame/帮手状态").Success, "帮手GM实际注册" + chapter);
                    if (chapter == 1)
                    {
                        int id = battle.PlayerPieces[0].Id;
                        BoardPos pos = battle.PlayerPieces[0].Pos;
                        BoardCellView cell = null;
                        foreach (var candidate in UnityEngine.Object.FindObjectsOfType<BoardCellView>()) { if (candidate.Position == pos) { cell = candidate; break; } }
                        Check(pointer.PressCell(cell), "实际指针选中棋子"); await RevTask.Yield(); Check(pointer.Release(true), "松开选中棋子");
                        await Wait(() => pointer.FindButton("BattleHudPanel/btnRemovePiece") != null, "移除按钮没有出现");
                        await Capture("remove-ui");
                        Check(pointer.PressButton(pointer.FindButton("BattleHudPanel/btnRemovePiece")), "实际指针点击移除UI");
                        await RevTask.Yield(); Check(pointer.Release(true), "松开移除UI");
                        Check(battle.Board.FindPiece(id) == null, "移除UI真实撤占格");
                        pos = battle.EnemyPieces[0].Pos;
                        foreach (var candidate in UnityEngine.Object.FindObjectsOfType<BoardCellView>()) { if (candidate.Position == pos) { cell = candidate; break; } }
                        Check(pointer.PressCell(cell), "实际指针点击指定敌棋");
                        await RevTask.Yield(); Check(pointer.Release(true), "松开指定敌棋");
                    }
                    battle.SetHelperPreviewTier(2);
                    float lowAttackLink = battle.HelperRangedAttackBonus, lowSpeedLink = battle.HelperRangedSpeedBonus;
                    barrageShots = beamShots = 0; spent = 0;
                    Check(battle.StartBattle(), "开始正常FSM视觉演示" + chapter);
                    await RevTask.Delay(1600);
                    await Capture("chapter" + chapter + "-barrage");
                    Check(barrageShots > 0 && spent > 0, "当前章积分弹幕真实发射并扣能源" + chapter);
                    battle.SetHelperPreviewTier(5);
                    Check(battle.HelperRangedAttackBonus > lowAttackLink && battle.HelperRangedSpeedBonus > lowSpeedLink,
                        "帮手高档位实时强化玩家远程攻击/攻速" + chapter);
                    // 等积分先流进面板再到帮手，视觉攻击延迟1.4秒；不能只截刚开始的空画面。
                    await RevTask.Delay(1600);
                    await Capture("chapter" + chapter + "-beam");
                    Check(beamShots > 0, "当前章蓝束真实发射并扣能源" + chapter);
                    Check(RevGM.Execute("RevGame/帮手目标 " + battle.EnemyPieces[0].Id).Success, "指定攻击GM" + chapter);
                    Check(flow.ReturnHome(), "接受退出章节" + chapter);
                    await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "退出章节失败");
                    Check(UnityEngine.Object.FindObjectOfType<HelperView>() == null, "退出回收帮手视图" + chapter);
                }
                flow.OpenLevels(); Check(flow.EnterBattle(6), "进入拥挤视觉夹具");
                await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "拥挤准备未交付");
                Check(battle.LoadHelperPreview(6, true), "三十二枚玩家棋盘输入");
                battle.SetHelperPreviewTier(2); Check(battle.StartBattle(), "启动拥挤帮手FSM");
                await Wait(() => battle.Helper.Outside, "拥挤帮手没有移至盘外");
                await Capture("helper-outside");
                Check(!battle.Helper.Position.IsInside(9), "拥挤状态帮手确实在盘外");
                Check(flow.ReturnHome(), "退出拥挤夹具");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "拥挤夹具收尾失败");
                flow.OpenLevels(); Check(flow.EnterBattle(6), "进入数量边界画面");
                await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "数量夹具准备未交付");
                Check(battle.LoadHelperLimitsPreview(false), "三十远程视觉输入");
                await Wait(() => RevUI.IsOpen<PreparePanel>() && !flow.IsGameplayBlocked, "远程上限UI未就绪");
                int handBefore = battle.Hand.Count;
                Check(pointer.PressButton(pointer.FindButton("PreparePanel/card0")), "指针选择第三十一枚远程牌");
                await RevTask.Yield(); Check(pointer.Release(true), "松开远程牌");
                BoardCellView capCell = null;
                foreach (var cell in UnityEngine.Object.FindObjectsOfType<BoardCellView>()) { if (cell.Position == new BoardPos(4, 6)) { capCell = cell; break; } }
                Check(pointer.PressCell(capCell), "指针尝试超额远程落子");
                await RevTask.Yield(); Check(pointer.Release(true), "松开超额落子");
                Check(battle.Hand.Count == handBefore && battle.LastError.Contains("远程棋子最多"), "真实UI超限拒绝保留手牌");
                await Capture("ranged-limit");
                Check(battle.LoadHelperLimitsPreview(true), "五十四棋真实进入Boss回合");
                await Wait(() => !flow.IsGameplayBlocked && !flow.IsBossIntroPlaying, "吞噬后Boss演出未完成");
                Check(battle.PlayerPieces.Count == 45 && battle.LastBossSwallowedCount == 9, "图形Play吞九颗保留四十五");
                await Capture("boss-swallow");
                Check(flow.ReturnHome(), "数量边界退出");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "数量边界收尾失败");
                Check(_evidence.unityErrors == 0, "本次新增Unity错误为零");
                Check(_diamondSamples > 0, "实际菱形MeshRenderer在相机中可见");
                Check(FramesObserved.Count == 4, "金色反光四帧全部实际播放");
                Check(_evidence.unityErrors == 0, "金光和菱形弹幕新增错误为零");
                _evidence.status = "passed";
            }
            catch (Exception error) { _evidence.status = "failed"; _evidence.error = error.ToString(); }
            finally
            {
                if (pointer.IsPressed) { pointer.Release(false); }
                Time.timeScale = oldScale;
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                scorePreview?.Dispose();
                RevEvent.RemoveAllByOwner(owner);
                Application.logMessageReceived -= logger;
                File.WriteAllText(Path.Combine(_folder, "result.json"), State());
            }
        }
    }
}
