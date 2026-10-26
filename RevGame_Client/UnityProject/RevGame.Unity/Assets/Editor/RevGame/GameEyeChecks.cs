// ============================================================
// GameEyeChecks.cs —— 眼位玩法与真实AB画面的独立验收。
// 规则由服务断言；图像只验证图标/Shader实际绘制。测试使用积分预览租约，不写玩家正式积分。
// ============================================================
using System;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameEyeChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private const string Output = "Temp/EyeVerification";
        private static int _checks;

        public static string RunRules()
        {
            if (Application.isPlaying) { throw new InvalidOperationException("规则检查要求编辑态"); }
            GameConfigFileLoader.LoadFromDirectory("Assets/GameRes/Data");
            _checks = 0;
            BattleService.VerifyEyeRules(Require);
            string result = "通过 " + _checks + " 条眼位、属性、真实攻速、补兵、重量级AI规则断言";
            Directory.CreateDirectory(Output); File.WriteAllText(Output + "/rules.txt", result);
            return result;
        }

        /// <summary>由现有RevMCP操作逐帧等待结果；finally回大厅并归还预览租约，失败仍写完整原因。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { throw new InvalidOperationException("需要未暂停的空闲Play"); }
            if (!RevResBootstrap.UseABInEditor) { throw new InvalidOperationException("眼位图像验收必须冷启动真实AB模式"); }
            LastResult = "运行中"; _checks = 0; Directory.CreateDirectory(Output);
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            IDisposable preview = battle.BeginScorePreview();
            string outcome = "失败：眼位验收未完成";
            try
            {
                ScreenCapture.CaptureScreenshot(Output + "/initial.png");
                await RevTask.Delay(200);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "从正式大厅进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                Require(battle.LoadEyeScenario(), "装配两方真眼与空眼的隔离夹具");
                // 正式入口要求准备增益已经发放；推进真实准备时钟，不能绕过门槛直接修改阶段。
                battle.Tick(Balance.PrepDelaySeconds + Balance.BattleSimulationStepSeconds);
                Require(battle.StartBattle(), "通过正式开战入口：" + battle.LastError);
                await RevTask.Delay(1700);
                // 不推进模拟时钟，只拍动态图形，避免攻击/死亡把固定展示输入变掉。
                int eyeIcons = 0;
                foreach (BoardCellView cell in UnityEngine.Object.FindObjectsOfType<BoardCellView>())
                {
                    SpriteRenderer eye = cell.transform.Find("眼").GetComponent<SpriteRenderer>();
                    if (!eye.enabled) { continue; }
                    Require(eye.sprite != null && eye.sprite.name.StartsWith("EyeBlink_", StringComparison.Ordinal) && cell.EyeFrameCount == 4, "眼位不再引用Checkmark");
                    Require(eye.sprite.packed && eye.sprite.texture.width > 72, "眼图标来自真实AB的Atlas V2页面");
                    eyeIcons++;
                }
                Require(eyeIcons >= 2, "真实棋盘显示双方空眼标记");
                PieceView player = null, enemy = null;
                foreach (PieceView view in UnityEngine.Object.FindObjectsOfType<PieceView>())
                {
                    var model = battle.Board.FindPiece(view.PieceId);
                    if (model == null) { continue; }
                    Require(view.IsEyeGarrisonVisual == model.IsEyeGarrison, "视图驻眼特效与模型一致");
                    if (!model.IsEyeGarrison) { continue; }
                    if (model.Side == GameSide.Player) { player = view; } else { enemy = view; }
                    // 冻住序列帧，只剩Shader时间变化；不能把换帧误当作驻眼Shader动态证据。
                    RevTimer.CancelAllOf(view);
                    SpriteRenderer body = Body(view);
                    var block = new MaterialPropertyBlock(); body.GetPropertyBlock(block);
                    Require(block.GetFloat("_EyeGarrison") == (model.Side == GameSide.Player ? 1f : 2f), "真实渲染参数正确区分两方");
                    Require(body.sharedMaterial.shader.name == "RevGame/Pieces/ArcanePiece" && body.sprite.packed, "共享棋子Shader使用真实打包帧");
                }
                Require(player != null && enemy != null, "两方都有实际驻眼棋");
                for (int frame = 0; frame < 16; frame++)
                {
                    ScreenCapture.CaptureScreenshot(Output + "/eye-" + frame.ToString("D2") + ".png");
                    await RevTask.Delay(130);
                }
                Require(ChangedPixels(player, Output + "/eye-00.png", Output + "/eye-08.png") > 0.03f, "冻结序列帧后玩家驻眼Shader仍有真实动态像素");
                Require(ChangedPixels(enemy, Output + "/eye-00.png", Output + "/eye-08.png") > 0.03f, "冻结序列帧后敌方驻眼Shader仍有真实动态像素");
                // 推进正式固定步模拟，确认支援不是测试直接塞棋；随后只让表现时间继续，完整录下中央弹幕。
                battle.Tick(Balance.EyeSupportSeconds + Balance.BattleSimulationStepSeconds);
                Require(battle.EyeSupportCount == 1, "真实Tick到3秒窗口补入一颗远程；实际=" + battle.EyeSupportCount + "，时间=" + battle.BattleElapsed + "，阶段=" + battle.Phase);
                var supported = battle.Board.GetPiece(new BoardPos(3, 3));
                Require(supported != null && supported.Def.IsRanged && supported.IsEyeGarrison, "援军落在先前空眼且成为有效驻军");
                await CaptureNotice("support", "眼位成型");
                Require(battle.PreviewEyeRevival(), "真实致死伤害触发一次驻眼原地复活");
                await CaptureNotice("revive", "眼位复苏");
                Require(player != null && player.IsEyeGarrisonVisual, "原地复活保留视图和醒目Shader");
                // 中心棋留在原地，只移动外围守子：复现“原有中心棋在后来成型眼中不亮Shader”的反馈。
                Require(battle.HandleBoardClick(new BoardPos(1, 0)) && battle.HandleBoardClick(new BoardPos(2, 0)), "真实点击移走最后一颗守子");
                Require(!battle.Board.FindPiece(player.PieceId).IsEyeGarrison && !player.IsEyeGarrisonVisual, "破眼时既有中心棋撤销Shader");
                ScreenCapture.CaptureScreenshot(Output + "/late-before.png");
                await RevTask.Delay(180);
                Require(battle.HandleBoardClick(new BoardPos(2, 0)) && battle.HandleBoardClick(new BoardPos(1, 0)), "真实点击补齐四邻，中心棋始终不移动");
                Require(battle.Board.FindPiece(player.PieceId).IsEyeGarrison && player.IsEyeGarrisonVisual, "后成型全盘刷新同步激活既有中心棋Shader");
                var lateBlock = new MaterialPropertyBlock(); Body(player).GetPropertyBlock(lateBlock);
                Require(lateBlock.GetFloat("_EyeGarrison") == 1f, "既有中心棋的实际GPU属性同步为我方驻眼");
                await CaptureNotice("late", "眼位成型");
                int playerId = player.PieceId;
                Require(battle.HandleBoardClick(new BoardPos(0, 0)) && battle.HandleBoardClick(new BoardPos(0, 4)), "通过真实玩家点击入口移出眼位");
                await RevTask.Delay(350);
                Require(!battle.Board.FindPiece(playerId).IsEyeGarrison && !player.IsEyeGarrisonVisual, "离眼即撤属性和Shader");
                ScreenCapture.CaptureScreenshot(Output + "/after-move.png");
                await RevTask.Delay(180);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(250);
                Require(battle.PlayerAICount == 0 && battle.EnemyAICount == 0, "退出战斗释放双方状态机");
                Require(UnityEngine.Object.FindObjectsOfType<PieceView>().Length == 0, "所有棋子视图归池，无驻眼特效残留");
                ScreenCapture.CaptureScreenshot(Output + "/final.png");
                await RevTask.Delay(180);
                outcome = "通过 " + _checks + " 条真实AB眼图标、双方Shader动态、离眼及回收断言";
            }
            catch (Exception error) { outcome = "失败：" + error; }
            finally
            {
                try { await GameSceneTransitionChecks.ReturnHomeAsync(flow); }
                catch (Exception error) { outcome += "；清理失败：" + error.Message; }
                preview.Dispose();
                LastResult = outcome; File.WriteAllText(Output + "/play.txt", outcome);
            }
        }

        private static SpriteRenderer Body(PieceView view)
        {
            return (SpriteRenderer)new SerializedObject(view).FindProperty("bodyRenderer").objectReferenceValue;
        }

        /// <summary>拍摄实际池化弹幕；检查文字确实激活且位于屏幕中央，而非仅收到事件就宣称显示成功。</summary>
        private static async RevTask CaptureNotice(string name, string expectedTitle)
        {
            bool visible = false;
            for (int frame = 0; frame < 16; frame++)
            {
                ScreenCapture.CaptureScreenshot(Output + "/" + name + "-" + frame.ToString("D2") + ".png");
                await RevTask.Delay(110);
                foreach (UnityEngine.UI.Text text in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Text>())
                {
                    if (text.text != expectedTitle || !text.isActiveAndEnabled) { continue; }
                    Vector2 point = RectTransformUtility.WorldToScreenPoint(null, text.rectTransform.position);
                    visible |= point.x > Screen.width * 0.35f && point.x < Screen.width * 0.65f;
                }
            }
            Require(visible, expectedTitle + "中央弹幕实际可见");
        }

        /// <summary>读取已经落盘的真实GameView，比较指定棋子身体范围；不采UI、弹道或其它棋子的动画。</summary>
        private static float ChangedPixels(PieceView view, string beforePath, string afterPath)
        {
            var before = new Texture2D(2, 2); var after = new Texture2D(2, 2);
            try
            {
                before.LoadImage(File.ReadAllBytes(beforePath)); after.LoadImage(File.ReadAllBytes(afterPath));
                Bounds bounds = Body(view).bounds;
                Camera camera = Camera.main;
                Vector3 min = camera.WorldToViewportPoint(bounds.min), max = camera.WorldToViewportPoint(bounds.max);
                int left = Mathf.Clamp((int)(min.x * before.width), 0, before.width - 1);
                int bottom = Mathf.Clamp((int)(min.y * before.height), 0, before.height - 1);
                int right = Mathf.Clamp((int)(max.x * before.width), left + 1, before.width);
                int top = Mathf.Clamp((int)(max.y * before.height), bottom + 1, before.height);
                Color[] a = before.GetPixels(left, bottom, right - left, top - bottom);
                Color[] b = after.GetPixels(left, bottom, right - left, top - bottom);
                int changed = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 0.12f) { changed++; }
                }
                return (float)changed / a.Length;
            }
            finally { UnityEngine.Object.DestroyImmediate(before); UnityEngine.Object.DestroyImmediate(after); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            _checks++;
        }
    }
}
