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

        [UnityEditor.MenuItem("RevGame/验收/池化动画（Play）")]
        public static async void BeginPoolCheck()
        {
            if (!Application.isPlaying) { RevLog.Warn("请先从StartScene进入Play再验收池化动画", "RevGame"); return; }
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
                SpriteRenderer body = BodyOf(piece);
                Require(body.transform != piece.transform, "棋子身体必须放在独立Body子节点，攻击动作不能缩放占格根节点");
                Sprite currentFrame = body.sprite;
                Require(currentFrame != null, "棋子当前帧未就绪");
                // 实际出手不能完全依赖有名额上限的口号/弹道。检查身体真有动作，并且不移动占格根节点。
                Vector3 bodyRest = body.transform.localScale;
                Vector3 rootPosition = piece.transform.position;
                Vector3 rootScale = piece.transform.localScale;
                piece.PlayAttackPulse();
                // 不依赖编辑器后台帧率采样中间帧：PlayAttackPulse当帧就应有姿态，随后再核对自然恢复。
                Require((body.transform.localScale - bodyRest).sqrMagnitude > 0.00001f, "真实攻击未立即产生身体动作");
                await RevTask.Delay(70);
                Require(piece.transform.position == rootPosition && piece.transform.localScale == rootScale, "攻击姿态错误地改变了根节点位置或占格大小");
                await RevTask.Delay(250);
                Require(body.transform.localScale == bodyRest, "攻击动作结束后未恢复身体基准缩放");
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
                Require(body.transform.localScale == bodyRest, "死亡开始后仍残留攻击动作缩放");
                RevPool.Return(piece, 0);
                piece = null;
                // 死亡动画完整时长1.05秒；必须超过旧回调原本的触发时间，才能证明取消真正有效。
                await RevTask.Delay(1200);
                Require(cancelledDeath == 0, "棋子中途回池仍触发了旧死亡回调");
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99002, PieceKind.Archer, StarTier.One, GameSide.Player);
                Require(piece.DeathDissolveProgress == 0f && piece.BirthProgress == 0f && piece.StormProgress == 0f,
                    "棋子复用后残留上次的死亡/落地/升星 Shader 进度");
                // 特意在姿态中途归池：下一颗棋不能继承上一颗攻击时被压扁的身体。
                piece.PlayAttackPulse();
                await RevTask.Delay(70);
                RevPool.Return(piece, 0);
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99002, PieceKind.Archer, StarTier.One, GameSide.Player);
                body = BodyOf(piece);
                Require(body.transform.localScale == bodyRest, "攻击中途回池没有恢复身体大小");
                piece.PlayBirthFire();
                RevPool.Return(piece, 0);
                Require(piece.BirthProgress == 0f, "浴火入场中途回池没有清除表现状态");
                piece = await Load<PieceView>(container.transform);
                piece.Bind(99002, PieceKind.Archer, StarTier.One, GameSide.Player);
                int completedDeath = 0;
                Require(piece.BeginDeathDissolve(item => { completedDeath++; RevPool.Return(item, 0); }), "原棋子再次播放溶解失败");
                piece = null; // 动画完成时已由回调归池，finally 不得重复归还。
                // 死亡表现为1.05秒；给调度留余量，不能沿用旧0.6秒特效的验收等待时间。
                await RevTask.Delay(1400);
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
                Require(cancelledDeath == 0, "后续复用时旧死亡回调仍被触发");
                LastResult = "通过：攻击身体回弹不改占格、自然恢复及中途回池正常；原棋浴火入场/雷暴升星/死亡消融及伤害、弹道、提示，复用和旧回调均正常";
                RevLog.Info(LastResult, "GameFeelChecks");
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
                // 留下可读取的结果；这些文件只在忽略目录Temp中，不修改场景资产或正式资源。
                System.IO.File.WriteAllText("Temp/GameFeelChecks.txt", LastResult);
            }
        }

        private static RevTask<T> Load<T>(Transform parent) where T : Component
        {
            var source = new RevTaskCompletionSource<T>();
            RevPool.GetAsync<T>(item => source.SetResult(item), parent);
            return source.Task;
        }

        /// <summary>仅验收夹具读取业务视图的私有身体引用，不向正式玩法公开修改渲染器的接口。</summary>
        private static SpriteRenderer BodyOf(PieceView piece)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            return (SpriteRenderer)typeof(PieceView).GetField("bodyRenderer", flags).GetValue(piece);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new System.InvalidOperationException(message); }
        }
    }
}
