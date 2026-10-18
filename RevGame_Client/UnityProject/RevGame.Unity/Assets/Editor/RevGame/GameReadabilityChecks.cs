// ============================================================
// GameReadabilityChecks.cs —— 在真实Play中注入密集反馈，检查全场预算、合并、遮挡和阶段清理。
// 人工检查很难稳定触发同帧上百次命中；本夹具只广播表现快照，不伪造或修改模型伤害。
// ============================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameReadabilityChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private static int _checks;

        /// <summary>必须在Play运行；finally返回大厅，让测试冻结的动画与时钟随原战斗域一起回收。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            _checks = 0;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            try
            {
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                Require(flow.OpenLevels() && flow.EnterBattle(1), "进入真实战斗域");
                await RevTask.Delay(650);
                BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
                Require(battle.LoadDevelopmentScenario("events"), "载入真实事件场景");
                // 暂停模型自动小步，仅由本夹具发出确定输入，避免随机攻击混入合计数字的断言。
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                battle.Tick(3f);
                Require(battle.StartBattle(), "正常经过开战入口");
                await RevTask.Delay(100);
                BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                Pieces.Piece player = battle.PlayerPieces[0];
                BoardPos old = player.Pos;
                board.OnCellClicked(old);
                // 敌阵由固定种子生成，不能把某个写死坐标当成永远空闲；按真实完整占格校验寻找目标。
                BoardPos empty = new BoardPos(-1, -1);
                for (int y = 0; y < 9 && !empty.IsInside(9); y++)
                {
                    for (int x = 0; x < 9 && !empty.IsInside(9); x++)
                    {
                        var candidate = new BoardPos(x, y);
                        if (candidate != old && battle.Board.GetPiece(candidate) == null && battle.CanPlaySelectedAt(candidate)) { empty = candidate; }
                    }
                }
                Require(battle.CanPlaySelectedAt(empty), "实际棋盘点击选中并高亮战斗换位");
                board.OnCellClicked(empty);
                Require(player.Pos == empty && battle.Board.GetPiece(old) == null, "实际点击入口搬移模型与旧占格");

                Pieces.Piece target = battle.EnemyPieces[0];
                int hp = target.Hp;
                for (int i = 0; i < 120; i++)
                {
                    RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked, new PieceAttackInfo(player.Id, player.Def.Kind, player.Pos, player.Size, player.Side));
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, 3, true, player.Pos));
                }
                Require(board.PendingDamageTextCount == 1, "同目标120次伤害仅保留一条待播数字");
                IDictionary pending = GetField<IDictionary>(board, "_pendingDamage");
                int sum = 0;
                foreach (object item in pending.Values) { sum += GetField<int>(item, "Damage"); }
                Require(sum == 360 && target.Hp == hp, "合计显示360，表现层不再额外扣血");
                await RevTask.Delay(60);
                Require(board.ActiveProjectileCount <= 6 && board.ActiveFloatingTextCount <= 2, "同帧攻击风暴没有穿透弹道与口号预算");
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    SpriteRenderer sprite = GetField<SpriteRenderer>(effect, "spriteRenderer");
                    if (sprite.enabled && sprite.sprite != null)
                    {
                        // bounds是旋转后的轴对齐包围盒，斜飞时对角线略大于本体；0.6仍不足一格的一半。
                        Require(sprite.drawMode == SpriteDrawMode.Simple && sprite.bounds.size.magnitude < 0.6f, "弹道按源图尺寸归一化为短弹，不再撑成长条：" + sprite.bounds.size);
                    }
                }
                await RevTask.Delay(400);
                bool shown = false;
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    if (effect.IsFloatingText && !effect.IsAttackText && GetField<TextMesh>(effect, "damageText").text.Contains("360")) { shown = true; }
                }
                Require(shown, "屏幕实际文字显示合并后的360");

                for (int i = 0; i < 100; i++)
                {
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(10000 + i, new BoardPos(4, 4), 1, GameSide.Enemy, i + 1, true, player.Pos));
                }
                Require(board.PendingDamageTextCount <= 12, "不同目标风暴仍保持有界待播队列");
                int peak = 0;
                for (int frame = 0; frame < 24; frame++)
                {
                    await RevTask.Delay(45);
                    peak = Math.Max(peak, board.ActiveFloatingTextCount);
                    Require(board.ActiveFloatingTextCount <= 6 && board.ActiveProjectileCount <= 6, "动画播放中持续满足全场数量上限");
                }
                Require(peak >= 3, "实际同时播放多条文字，避让检查不是空场通过");
                List<BattleEffectView> texts = new List<BattleEffectView>();
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>()) { if (effect.IsFloatingText) { texts.Add(effect); } }
                texts.Sort((a, b) => a.TextOrder.CompareTo(b.TextOrder));
                Require(texts.Count >= 2, "存在可检查遮挡的两条活跃文字");
                BattleEffectView lower = texts[0];
                BattleEffectView upper = texts[texts.Count - 1];
                foreach (BattleEffectView effect in texts) { GetField<Tween>(effect, "_animation").Pause(); }
                // 先检查自然避让后的实际包围盒，再人为制造不可避让的相交，验证半透明兜底真正落屏。
                int overlaps = 0;
                for (int i = 0; i < texts.Count; i++)
                {
                    for (int j = i + 1; j < texts.Count; j++) { if (texts[i].TextScreenRect.Overlaps(texts[j].TextScreenRect)) { overlaps++; } }
                }
                Require(overlaps < texts.Count, "同一命中点的数字已分散到不同可见位置");
                System.IO.Directory.CreateDirectory("Temp/ReadabilityVerification");
                ScreenCapture.CaptureScreenshot("Temp/ReadabilityVerification/dense-feedback.png");
                upper.transform.position = lower.transform.position;
                upper.transform.localScale = lower.transform.localScale;
                await RevTask.Delay(80);
                Require(GetField<TextMesh>(lower, "damageText").color.a <= 0.351f, "无法避让时下层文字实际半透明");

                Require(battle.TriggerBattleEvent(Data.BattleEventKind.Reinforcement), "触发真实天降援兵事件");
                await RevTask.Delay(80);
                BattleEventNoticeView notice = UnityEngine.Object.FindObjectOfType<BattleEventNoticeView>();
                Require(notice != null, "真实事件面板已显示");
                Text title = GetField<Text>(notice, "titleText");
                Text detail = GetField<Text>(notice, "detailText");
                Require(title.alignment == TextAnchor.MiddleCenter && detail.alignment == TextAnchor.MiddleCenter, "事件标题与说明真正居中对齐");
                Require(Mathf.Abs(title.rectTransform.anchoredPosition.x) < 0.01f && Mathf.Abs(detail.rectTransform.anchoredPosition.x) < 0.01f, "标题说明的文字框也与底板几何中心对齐");
                Require(battle.StartRound(1, 2), "密集动画尚未结束时切换回合");
                Require(board.ActiveEffectCount == 0 && board.PendingDamageTextCount == 0 && board.ActiveFloatingTextCount == 0 && board.ActiveProjectileCount == 0, "切阶段清空冻结文字、弹道、合并队列和预算");
                await RevTask.Delay(450);
                Require(board.ActiveEffectCount == 0, "旧异步请求没有在新准备期补播");
                LastResult = "通过 " + _checks + " 条Play断言：120次合并、100目标控频、短弹道、避让/透明、居中、战斗换位与阶段清理；峰值飘字=" + peak;
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "战斗可读性验收失败", "RevGame");
            }
            finally
            {
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
            }
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Require(bool success, string reason)
        {
            if (!success) { throw new InvalidOperationException(reason); }
            _checks++;
        }
    }
}
