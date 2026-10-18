// ============================================================
// GameFeelChecks.cs —— Play 态验证 DOTween 与 RevPool 的交接契约。
// 特意在动画中途回池、马上复用、等待旧动画原本该结束的时间，检查是否发生重复回调。
// ============================================================
using DG.Tweening;
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
            BattleEventNoticeView notice = null;
            int callbacks = 0;
            int before = DOTween.TotalActiveTweens();
            try
            {
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
                LastResult = "通过：伤害/弹道/攻击文字/升级/增益提示均启动，回池 Kill，实例复用，4秒后无旧回调且 Tween 数恢复";
            }
            catch (System.Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "池化动画验收失败", "RevGame");
            }
            finally
            {
                if (effect != null) { RevPool.Return(effect, 0); }
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
