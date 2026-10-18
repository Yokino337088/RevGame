// ============================================================
// GameGrowthPresentationChecks.cs —— 在真正Play中验收计时、池化横幅、缓动和Boss逐帧变化。
// 不用“字段写了true”代替屏幕结果；检查实际Text、RectTransform、Image和Sprite。
// ============================================================
using System;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameGrowthPresentationChecks
    {
        private static int _checks;
        public static string LastResult { get; private set; } = "未运行";

        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            _checks = 0;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            try
            {
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                Require(flow.OpenLevels() && flow.EnterBattle(1), "进入战斗");
                await RevTask.Delay(650);
                IBattleService battle = RevServices.GetRequired<IBattleService>();
                Require(!battle.PreparationGranted && battle.PreparationBuffs.Count == 0, "真实准备时钟未到3秒不开奖");
                await RevTask.Delay(2850);
                Require(battle.PreparationGranted && battle.PreparationBuffs.Count >= 3 && battle.PreparationBuffs.Count <= 5, "真实域时钟自动给3～5项增益");
                BattleHudPanel hud = UnityEngine.Object.FindObjectOfType<BattleHudPanel>();
                BattleEventNoticeView banner = GetField<BattleEventNoticeView>(hud, "_preparationNotice");
                Require(banner != null && banner.HasActiveAnimation, "池化横幅确实在播放");
                Tween animation = GetField<Tween>(banner, "_animation");
                animation.Pause();
                Require(Mathf.Abs(animation.Duration(false) - 1.67f) < 0.01f, "入场/0.5秒冲击/0.25秒停顿/出场总时长");
                Text title = GetField<Text>(banner, "titleText");
                Require(title.fontStyle == FontStyle.Bold && title.alignment == TextAnchor.MiddleCenter && title.GetComponent<Outline>() != null, "大字加粗居中且有高对比描边");
                animation.Goto(0.01f, false);
                Require(GetField<float>(banner, "_slide") > 0.9f, "从屏幕右侧进入");
                animation.Goto(0.6f, false);
                Require(Mathf.Abs(GetField<float>(banner, "_slide")) < 0.001f && banner.transform.localScale.x > 1.05f, "到中央后立即放大冲击");
                animation.Goto(1.05f, false);
                Require(Mathf.Abs(banner.transform.localScale.x - 1f) < 0.001f, "0.5秒回弹结束后短停0.25秒");
                animation.Goto(1.6f, false);
                Require(GetField<float>(banner, "_slide") < -0.5f, "最终向屏幕左侧离开");

                // 故意冻结横幅再切阶段，验证不依赖自然播完，也不把剩余队列带到下一轮。
                Require(((BattleService)battle).LoadDevelopmentScenario("boss"), "载入Boss验收场景");
                Require(hud.ActiveNoticeCount == 0 && hud.PendingPreparationNoticeCount == 0, "换轮立即回收冻结横幅及队列");
                await RevTask.Delay(850);
                Pieces.Piece king = null;
                foreach (Pieces.Piece piece in battle.EnemyPieces) { if (piece.IsKing) { king = piece; break; } }
                PieceView kingView = null;
                foreach (PieceView view in UnityEngine.Object.FindObjectsOfType<PieceView>()) { if (view.PieceId == king.Id) { kingView = view; break; } }
                Require(kingView != null && kingView.AnimationFrameCount > 1, "Boss绑定真实多帧序列");
                SpriteRenderer body = GetField<SpriteRenderer>(kingView, "bodyRenderer");
                Sprite frame = body.sprite;
                await RevTask.Delay(260);
                Require(body.sprite != frame, "Boss可见Sprite确实改变，持续刷新不再盖回静态图");

                battle.Tick(3f);
                Require(battle.StartBattle(), "增益后正常开战");
                Require(hud.ActiveNoticeCount == 0 && hud.PendingPreparationNoticeCount == 0, "开战清空准备弹幕，不遮挡战斗");
                Image bossBar = GetField<Image>(hud, "_bossBar");
                float before = bossBar.fillAmount;
                king.TakeDamage(king.MaxHp / 3);
                RevEvent.DispatchEvent<int>(Events.PieceHpChanged, king.Id);
                Require(Mathf.Abs(bossBar.fillAmount - before) < 0.01f && GetField<Tween>(hud, "_bossTween").IsActive(), "Boss受伤创建缓动，没有瞬间跳条");
                await RevTask.Delay(380);
                Require(Mathf.Abs(bossBar.fillAmount - king.HpRatio) < 0.01f, "Boss血条缓动到真实生命比例");
                Require(battle.StartRound(1, 1), "切回普通回合");
                // 夹具已有六牌，正常落一张即可，不能凭空改阶段跳过业务校验。
                bool placed = false;
                for (int y = 0; y < 9 && !placed; y++)
                {
                    for (int x = 0; x < 9 && !placed; x++) { placed = battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(x, y)); }
                }
                battle.Tick(3f);
                Require(placed && battle.StartBattle(), "普通回合正常开战");
                battle.Tick(Consts.BattleSimulationStepSeconds);
                RevEvent.DispatchEvent<int>(Events.PieceHpChanged, battle.PlayerPieces[0].Id);
                Require(GetField<Tween>(hud, "_progressTween") != null, "顶部时间条实际创建DOTween缓动");
                Require(battle.StartRound(1, 2), "再进下一轮");
                await RevTask.Delay(450);
                Require(GetField<Image>(hud, "_progressFill").fillAmount == 0f && hud.ActiveNoticeCount == 0, "下一轮进度归零，旧提示不迟到");
                LastResult = "通过 " + _checks + " 条Play断言：真实3秒增益、横幅时序/方向/冲击/清理、Boss换帧、双进度条缓动";
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "成长表现验收失败", "RevGame");
            }
            finally
            {
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
            }
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            _checks++;
        }
    }
}
