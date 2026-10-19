// ============================================================
// GameRoundPresentationChecks.cs —— 回合交接的真实服务与 UI 回归检查。
// 普通规则断言看不到隐藏的手牌槽和暂停的 Tween，因此必须在 Play 态另外验证。
// 所有暂停动作都包在 try/finally 中，最终返回主界面，不把验收用的半截动画留给人工游玩。
// ============================================================
using System;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameRoundPresentationChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private static int _checks;

        /// <summary>在主界面开始；内部按真实 API 开战、结算、继续，不能只反复调用发牌函数。</summary>
        public static async void Begin()
        {
            if (LastResult == "运行中") { return; }
            LastResult = "运行中";
            _checks = 0;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            try
            {
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "无法进入正常战役");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                await RevTask.Delay(700);
                IBattleService battle = RevServices.GetRequired<IBattleService>();
                VerifySixCards(battle);
                // 分别覆盖“只剩一张”“全部打完”“还有多张”三种上轮状态。
                foreach (int used in new[] { 5, 6, 2 })
                {
                    int placed = 0;
                    for (int y = 0; y < Consts.BoardSize && placed < used; y++)
                    {
                        for (int x = 0; x < Consts.BoardSize && placed < used; x++)
                        {
                            if (battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(x, y))) { placed++; }
                        }
                    }
                    Require(placed == used && battle.Hand.Count == 6 - used, "消费后的剩余牌数不正确");
                    battle.Tick(3f);
                    Require(battle.StartBattle(), "有棋子时未能开战");
                    battle.Tick(Balance.VictoryAssistFinishSeconds);
                    Require(battle.Phase == GamePhase.Settle && battle.LastWinner == GameSide.Player, "没有经过真实结算");
                    Require(battle.ContinueRound(), "结算后无法继续");
                    await RevTask.Delay(600);
                    VerifySixCards(battle);
                    VerifyNoOldEffects();
                }

                // 故意让翻牌停在半途，再进入下一轮；回调不能覆盖刚发的六张牌。
                Require(battle.RefreshHand(), "刷新未成功");
                PreparePanel prepare = UnityEngine.Object.FindObjectOfType<PreparePanel>();
                foreach (CardItem card in prepare.GetComponentsInChildren<CardItem>(true)) { PauseOwnedTween(card, "_flip"); }
                Require(battle.StartRound(1, 5), "准备中重新装配回合失败");
                await RevTask.Delay(900);
                VerifySixCards(battle);

                // 暂停真实升级与支援提示，模拟动画永远无法自然完成的最坏情况。
                Require(((BattleService)battle).LoadDevelopmentScenario("merge"), "合成场景加载失败");
                Require(battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(2, 0)), "合成未触发");
                await RevTask.Delay(150);
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>()) { PauseOwnedTween(effect, "_animation"); }
                foreach (BattleEventNoticeView notice in UnityEngine.Object.FindObjectsOfType<BattleEventNoticeView>()) { PauseOwnedTween(notice, "_animation"); }
                battle.Tick(3f);
                Require(battle.StartBattle(), "合成后开战失败");
                VerifyNoOldEffects();
                battle.Tick(Balance.BattleEventDelay);
                await RevTask.Delay(120);
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>()) { PauseOwnedTween(effect, "_animation"); }
                foreach (BattleEventNoticeView notice in UnityEngine.Object.FindObjectsOfType<BattleEventNoticeView>()) { PauseOwnedTween(notice, "_animation"); }
                // 该通知会安排 0.3 秒后的伤害数字，随即换回合，专门检查迟到回调。
                RevEvent.DispatchEvent(Events.BattleHit, new BattleHitInfo(1, new BoardPos(2, 2), 1, GameSide.Enemy, 999, true, new BoardPos(0, 0)));
                Require(battle.StartRound(1, 2), "切回准备失败");
                VerifyNoOldEffects();
                await RevTask.Delay(2000);
                VerifyNoOldEffects();
                VerifySixCards(battle);
                BattleHudPanel hud = UnityEngine.Object.FindObjectOfType<BattleHudPanel>();
                Require(hud.transform.Find("logText").GetComponent<Text>().text == string.Empty, "新回合仍有旧战斗日志");
                Require(hud.transform.Find("notice").gameObject.activeSelf == false, "静态旧提示仍在显示");
                Require(battle.BattleElapsed == 0f && battle.BattleEventCount == 0, "战斗时钟或事件数未清零");
                LastResult = "通过 " + _checks + " 条 Play 回归：连续回合六牌、六槽复用、暂停翻牌/升级/弹幕清理、延迟伤害拦截、日志清空";
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "回合表现回归失败", "RevGame");
            }
            finally
            {
                // 关域会收走所有暂停过的验收实例；不能要求使用者事后手动 KillAll 或重启编辑器。
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
            }
        }

        private static void VerifySixCards(IBattleService battle)
        {
            Require(battle.Hand.Count == 6 && battle.CardsRemaining == 6, "新回合没有六张牌/六次额度");
            PreparePanel panel = UnityEngine.Object.FindObjectOfType<PreparePanel>();
            Require(panel != null, "准备面板未打开");
            CardItem[] cards = panel.GetComponentsInChildren<CardItem>(true);
            Require(cards.Length == 6, "预制体不是六个固定槽位");
            for (int i = 0; i < 6; i++)
            {
                Require(cards[i].gameObject.activeInHierarchy && cards[i].CardId == battle.Hand[i].Id, "隐藏或过期手牌槽：" + i);
                Require(!cards[i].IsFlipping && cards[i].transform.localRotation == Quaternion.identity, "旧翻牌动画未复原：" + i);
            }
        }

        private static void VerifyNoOldEffects()
        {
            BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
            BattleHudPanel hud = UnityEngine.Object.FindObjectOfType<BattleHudPanel>();
            Require(board != null && board.ActiveEffectCount == 0, "棋盘仍登记旧特效");
            Require(hud != null && hud.ActiveNoticeCount == 0, "HUD 仍登记旧提示");
            Require(UnityEngine.Object.FindObjectsOfType<BattleEffectView>().Length == 0, "场景中有未归还的弹幕");
            Require(UnityEngine.Object.FindObjectsOfType<BattleEventNoticeView>().Length == 0, "场景中有未归还的升级/增益提示");
        }

        private static void PauseOwnedTween(object view, string field)
        {
            Tween tween = view.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view) as Tween;
            if (tween != null) { tween.Pause(); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            _checks++;
        }
    }
}
