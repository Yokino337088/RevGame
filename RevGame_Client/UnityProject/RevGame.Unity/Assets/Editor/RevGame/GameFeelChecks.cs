// ============================================================
// GameFeelChecks.cs —— Play 态验证 DOTween 与 RevPool 的交接契约。
// 特意在动画中途回池、马上复用、等待旧动画原本该结束的时间，检查是否发生重复回调。
// ============================================================
using DG.Tweening;
using RevGame.Core;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameFeelChecks
    {
        public static string LastResult { get; private set; } = "未运行";

        public static async void BeginPoolCheck()
        {
            if (LastResult == "运行中") { return; }
            LastResult = "运行中";
            GameObject container = new GameObject("池化动画验收", typeof(RectTransform));
            container.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
            BattleEffectView effect = null;
            PieceView piece = null;
            BattleEventNoticeView notice = null;
            int callbacks = 0;
            int before = DOTween.TotalActiveTweens();
            try
            {
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99001, PieceKind.Archer, StarTier.One, GameSide.Player);
                SpriteRenderer body = (SpriteRenderer)typeof(PieceView).GetField("bodyRenderer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(piece);
                Sprite currentFrame = body.sprite;
                Require(currentFrame != null, "棋子当前帧未就绪");
                var block = new MaterialPropertyBlock();
                piece.PlayBirthFire();
                await RevTask.Delay(120);
                body.GetPropertyBlock(block);
                Require(piece.BirthProgress > 0f && block.GetFloat("_Birth") > 0f, "原棋子的浴火入场没有推进");
                piece.PlayUpgradeStorm();
                body.GetPropertyBlock(block);
                Require(piece.BirthProgress == 0f && block.GetFloat("_Birth") == 0f, "升级未中断旧浴火动画");
                await RevTask.Delay(120);
                body.GetPropertyBlock(block);
                Require(piece.StormProgress > 0f && block.GetFloat("_Storm") > 0f, "原棋子的雷暴升级没有推进");
                await RevTask.Delay(1200);
                body.GetPropertyBlock(block);
                Require(piece.StormProgress == 0f && block.GetFloat("_Storm") == 0f, "雷暴完成后没有清理 Shader 参数");
                // 恢复待机后身体会继续换帧；死亡应冻结触发当刻的帧，不能拿入场前的旧帧作比较。
                currentFrame = body.sprite;
                int cancelledDeath = 0;
                Require(piece.BeginDeathDissolve(item => cancelledDeath++), "原棋子未能启动身体溶解");
                Require(body.sprite == currentFrame, "死亡不能换成独立特效贴图或另外实例");
                await RevTask.Delay(120);
                body.GetPropertyBlock(block);
                Require(piece.DeathDissolveProgress > 0f && block.GetFloat("_Dissolve") > 0f, "原棋子身体溶解进度未推进");
                RevPool.Return(piece, 0);
                piece = null;
                await RevTask.Delay(800);
                Require(cancelledDeath == 0, "棋子中途回池仍触发了旧死亡回调");
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99002, PieceKind.Archer, StarTier.One, GameSide.Player);
                Require(piece.DeathDissolveProgress == 0f && piece.BirthProgress == 0f && piece.StormProgress == 0f,
                    "棋子复用后残留上次的死亡/落地/升星 Shader 进度");
                piece.PlayBirthFire();
                RevPool.Return(piece, 0);
                Require(piece.BirthProgress == 0f, "浴火入场中途回池没有清除表现状态");
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99002, PieceKind.Archer, StarTier.One, GameSide.Player);
                int completedDeath = 0;
                Require(piece.BeginDeathDissolve(item => { completedDeath++; RevPool.Return(item, 0); }), "原棋子再次播放溶解失败");
                piece = null; // 动画完成时已由回调归池，finally 不得重复归还。
                await RevTask.Delay(950);
                Require(completedDeath == 1, "原棋子溶解自然完成未恰好回调一次");

                effect = await Load<BattleEffectView>(container.transform);
                int effectId = effect.GetInstanceID();
                effect.Show(BattleEffectKind.Damage, Vector3.zero, 123, item => callbacks++);
                Require(effect.HasActiveAnimation, "伤害动画未启动");
                RevPool.Return(effect, 0);
                Require(!effect.HasActiveAnimation, "伤害回池后 Tween 未停止");
                effect = await Load<BattleEffectView>(container.transform);
                Require(effect.GetInstanceID() == effectId, "伤害实例未复用");
                effect.ShowProjectile(Vector3.zero, Vector3.right, item => callbacks++);
                Require(effect.HasActiveAnimation, "弹道动画未启动");
                RevPool.Return(effect, 0);
                Require(!effect.HasActiveAnimation, "弹道回池后 Tween 未停止");
                effect = await Load<BattleEffectView>(container.transform);
                Require(effect.GetInstanceID() == effectId, "攻击文字实例未复用");
                effect.ShowAttack(GameTextConfig.Require("Attack_Archer"), Vector3.zero, item => callbacks++);
                Require(effect.HasActiveAnimation, "攻击文字动画未启动");
                foreach (SpriteRenderer renderer in effect.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    Require(!renderer.enabled, "弹道复用成攻击文字时仍残留弹头、轮廓或拖尾");
                }
                RevPool.Return(effect, 0);
                Require(!effect.HasActiveAnimation, "攻击文字回池后 Tween 未停止");
                effect = null;

                notice = await Load<BattleEventNoticeView>(container.transform);
                int noticeId = notice.GetInstanceID();
                notice.Show("炮火支援", null, item => callbacks++);
                Require(notice.HasActiveAnimation, "增益提示动画未启动");
                RevPool.Return(notice, 0);
                Require(!notice.HasActiveAnimation, "提示回池后 Tween 未停止");
                notice = await Load<BattleEventNoticeView>(container.transform);
                Require(notice.GetInstanceID() == noticeId, "提示实例未复用");
                notice.Show("连击", null, item => callbacks++);
                RevPool.Return(notice, 0);
                notice = await Load<BattleEventNoticeView>(container.transform);
                notice.Show(GameTextConfig.Require("Upgrade_2"), "升星验收", null, item => callbacks++);
                Require(notice.HasActiveAnimation, "升级 UI 动画未启动");
                RevPool.Return(notice, 0);
                notice = null;

                // 超过伤害和提示的完整时长，旧动画也不能在新对象上继续跑，更不能重复归还。
                await RevTask.Delay(4000);
                Require(callbacks == 0, "中途回池后仍触发了旧动画完成回调");
                Require(DOTween.TotalActiveTweens() == before, "动画总数没有恢复到验收前");
                LastResult = "通过：原棋浴火入场/雷暴升星/死亡消融及伤害、弹道、提示，回池清理、复用和旧回调均正常";
            }
            catch (System.Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "池化动画验收失败", "RevGame");
            }
            finally
            {
                if (effect != null) { RevPool.Return(effect, 0); }
                if (piece != null) { RevPool.Return(piece, 0); }
                if (notice != null) { RevPool.Return(notice, 0); }
                Object.Destroy(container);
            }
        }

        private static RevTask<T> Load<T>(Transform parent) where T : Component
        {
            var source = new RevTaskCompletionSource<T>();
            RevPool.GetAsync<T>(item => source.SetResult(item), parent);
            return source.Task;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new System.InvalidOperationException(message); }
        }
    }
}
