// ============================================================
// GameArcanePresentationChecks.cs —— 真实Play验收卡片反馈、动态材质和弹道完整寿命。
// 故意切断弹道Tween验证超时兜底；仅测试表现对象，不给模型补伤害，也不把截图当作性能测量。
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameArcanePresentationChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        public static string LastPreview { get; private set; } = "未运行";
        private static int _checks;

        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            _checks = 0;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            GameObject container = new GameObject("弹道寿命验收");
            BattleEffectView effect = null;
            try
            {
                foreach (string name in new[] { "RevGame/UI/CardGleam", "RevGame/UI/ArcanePanel", "RevGame/Background/ArcaneBackdrop" })
                {
                    Shader shader = Shader.Find(name);
                    Require(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "Shader真实编译并受当前GPU支持：" + name);
                }
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                Require(flow.OpenLevels() && flow.EnterBattle(1), "经过真实流程进入准备期");
                await RevTask.Delay(800);
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                CardItem[] cards = UnityEngine.Object.FindObjectsOfType<CardItem>();
                Require(cards.Length == 6, "准备期仍为六张可见手牌");
                Material shared = Get<Image>(cards[0], "background").material;
                Require(shared.shader.name == "RevGame/UI/CardGleam", "卡片实际使用光泽材质");
                foreach (CardItem card in cards)
                {
                    Image image = Get<Image>(card, "background");
                    Require(image.material == shared && image.GetComponent<ArcaneImageCoordinates>() != null, "六牌共用材质并携带独立流光坐标");
                }
                CardItem clicked = cards[0];
                Button button = Get<Button>(clicked, "button");
                for (int i = 0; i < 8; i++) { button.onClick.Invoke(); }
                Require(Get<Tween>(clicked, "_press").IsActive(), "连续点击实际启动DOTween抖动");
                await RevTask.Delay(70);
                Require(Quaternion.Angle(clicked.transform.localRotation, Quaternion.identity) > 0.1f, "卡面真实发生倾斜");
                IBattleService battle = RevServices.GetRequired<IBattleService>();
                Require(battle.RefreshHand(), "点击抖动中刷新剩余手牌");
                Require(clicked.IsFlipping && Get<Tween>(clicked, "_press") == null, "翻牌接管前已取消抖动");
                await RevTask.Delay(1200);
                Require(!clicked.IsFlipping && Quaternion.Angle(clicked.transform.localRotation, Quaternion.identity) < 0.01f, "翻牌完成无倾斜残留");
                Require(Get<Image>(clicked, "background").material == shared, "连续点击刷新没有复制材质");
                using (var vertices = new VertexHelper())
                {
                    UIVertex vertex = UIVertex.simpleVert;
                    vertex.position = Get<Image>(clicked, "background").rectTransform.rect.min;
                    vertex.uv0 = new Vector2(0.23f, 0.67f);
                    vertices.AddVert(vertex);
                    clicked.GetComponent<ArcaneImageCoordinates>().ModifyMesh(vertices);
                    vertices.PopulateUIVertex(ref vertex, 0);
                    Require(vertex.uv0 == new Vector4(0.23f, 0.67f) && vertex.uv1.x == 0f && vertex.uv1.y == 0f, "原图UV保持不变，特效UV单独从零开始");
                }
                SpriteRenderer background = Get<SpriteRenderer>(UnityEngine.Object.FindObjectOfType<GamePresentation>(), "backgroundRenderer");
                Require(background.sharedMaterial.shader.name == "RevGame/Background/ArcaneBackdrop", "场景实际经RevResManager使用动态背景材质");
                System.IO.Directory.CreateDirectory("Temp/ArcaneVerification");
                ScreenCapture.CaptureScreenshot("Temp/ArcaneVerification/prepare.png");

                int callbacks = 0;
                foreach (float distance in new[] { 0f, 0.2f, 1.15f, 8f })
                {
                    effect = await Load(container.transform);
                    BattleEffectView current = effect;
                    int before = callbacks;
                    current.ShowProjectile(Vector3.zero, Vector3.right * distance, item => callbacks++);
                    await RevTask.Delay(650);
                    Require(callbacks == before + 1 && !current.gameObject.activeInHierarchy && !current.HasActiveAnimation, "零距/近距/远距自然结束仅回调一次：" + distance);
                    foreach (SpriteRenderer layer in current.GetComponentsInChildren<SpriteRenderer>(true)) { Require(!layer.enabled, "回池本体/轮廓/尾巴全部关闭"); }
                    effect = null;
                }
                effect = await Load(container.transform);
                BattleEffectView stopped = effect;
                stopped.ShowProjectile(Vector3.zero, Vector3.right * 6f, item => callbacks++);
                Get<Tween>(stopped, "_animation").Kill(false);
                await RevTask.Delay(700);
                Require(!stopped.gameObject.activeInHierarchy && !stopped.HasActiveAnimation, "外部Kill弹道后仍被寿命保险回收");
                effect = null;
                LastResult = "通过 " + _checks + " 条Play断言：Shader/六牌共享材质/UV隔离/连点抖动与翻牌互斥/场景动态材质/零距近距远距弹道自然回收/中断回收";
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error.Message;
                RevLog.Exception(error, "动态美术验收失败", "RevGame");
            }
            finally
            {
                if (effect != null && effect.gameObject.activeInHierarchy) { RevPool.Return(effect, 0); }
                UnityEngine.Object.Destroy(container);
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
            }
        }

        private static RevTask<BattleEffectView> Load(Transform parent)
        {
            var source = new RevTaskCompletionSource<BattleEffectView>();
            RevPool.GetAsync<BattleEffectView>(item => source.SetResult(item), parent);
            return source.Task;
        }

        /// <summary>保存真实1920×1080画面的连续帧，供交付报告展示流光；只暂停模型自动推进，不修改分辨率。</summary>
        public static async void CapturePreview()
        {
            if (!Application.isPlaying || LastPreview == "运行中") { return; }
            LastPreview = "运行中";
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            try
            {
                if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                if (!flow.OpenLevels() || !flow.EnterBattle(1)) { throw new InvalidOperationException("无法进入准备预览"); }
                await RevTask.Delay(650);
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                System.IO.Directory.CreateDirectory("Temp/ArcaneVerification/Frames");
                CardItem card = UnityEngine.Object.FindObjectOfType<CardItem>();
                for (int frame = 0; frame < 30; frame++)
                {
                    if (frame == 1) { Get<Button>(card, "button").onClick.Invoke(); }
                    ScreenCapture.CaptureScreenshot("Temp/ArcaneVerification/Frames/" + frame.ToString("D2") + ".png");
                    await RevTask.Delay(200);
                }
                LastPreview = "已保存30帧真实Play预览";
            }
            catch (Exception error) { LastPreview = "失败：" + error.Message; RevLog.Exception(error, "预览采集失败", "RevGame"); }
            finally { if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); } }
        }

        private static T Get<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
            _checks++;
        }
    }
}
