// 死棋回收回归：使用真实扣血、事件、BoardView 与 RevPool，避免只测一个孤立 Tween。
using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GamePieceDeathChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<string> Evidence = new List<string>();
        public static string LastResult { get; private set; } = "未运行";
        private static int _assertions;

        /// <summary>只在开发编辑器运行；在首页发起并最终返回首页，不改资源、存档或正式数值。</summary>
        [UnityEditor.MenuItem("RevGame/验收/死棋回收（Play）")]
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            _assertions = 0;
            Evidence.Clear();
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            PieceView reused = null;
            try
            {
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "正式流程进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                Require(battle.LoadDevelopmentScenario("boss"), "加载固定Boss与护卫夹具");
                await RevTask.Delay(1700);
                var hud = UnityEngine.Object.FindObjectOfType<BattleHudPanel>();
                // 固定触发旧血条自然完成→复用池→死亡动画→下一次血条更新的顺序。
                // 旧实现没有OnKill清字段，这一条必失败；不能只检查IsActive，因为句柄可能已改属别的动画。
                CompleteBar(hud, false);
                CompleteBar(hud, true);
                PieceView normal = KillGuard(battle);
                CompleteBar(hud, false);
                CompleteBar(hud, true);
                Require(VisualTween(normal).IsActive() && VisualTween(normal).IsPlaying(), "刷新HUD没有误停死亡动画");
                ScreenCapture.CaptureScreenshot("Temp/DeathFix-dissolving.png");
                await RevTask.Delay(1550);
                Require(!normal.gameObject.activeSelf && normal.PieceId == 0, "正常消融后视图已归池且绑定清空");
                ScreenCapture.CaptureScreenshot("Temp/DeathFix-after.png");

                // 故意破坏动画推进：独立回收期限必须仍然生效，且只从死亡集合归还一次。
                foreach (bool pause in new[] { true, false })
                {
                    PieceView interrupted = KillGuard(battle);
                    Tween tween = VisualTween(interrupted);
                    if (pause) { tween.Pause(); } else { tween.Kill(false); }
                    if (!pause) { Require(VisualTween(interrupted) == null, "外部Kill后解除死亡Tween引用，不误伤复用者"); }
                    await RevTask.Delay(1550);
                    Require(!interrupted.gameObject.activeSelf && interrupted.PieceId == 0,
                        pause ? "死亡Tween暂停后仍按期限归池" : "死亡Tween被Kill后仍按期限归池");
                }

                // 中途归池马上复用，超过旧期限后新棋子仍必须存活；旧回调不能收走下一位借用者。
                reused = await LoadPiece();
                reused.Bind(99021, PieceKind.Archer, StarTier.One, GameSide.Enemy);
                int oldCallbacks = 0;
                Require(reused.BeginDeathDissolve(item => oldCallbacks++), "启动可取消的死亡动画");
                RevPool.Return(reused, 0);
                reused = await LoadPiece();
                reused.Bind(99022, PieceKind.Archer, StarTier.One, GameSide.Enemy);
                await RevTask.Delay(1550);
                Require(oldCallbacks == 0 && reused.gameObject.activeSelf && reused.PieceId == 99022,
                    "回池取消旧期限，复用棋子未被旧回调归还");
                RevPool.Return(reused, 0);
                reused = null;

                // 最后一击同帧结算与换轮：死亡视图独立于存活字典，随后仍须自然收尾。
                PieceView settling = KillGuard(battle);
                Require(battle.StartRound(1, 2), "死亡同帧切入下一轮");
                await RevTask.Delay(1550);
                Require(settling.PieceId != _lastKilledId, "换轮后旧死亡绑定已清除或安全复用");
                Require(DissolvingCount() == 0, "死亡集合没有遗留实例");
                PieceView exiting = KillGuard(battle);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(1550);
                Require(UnityEngine.Object.FindObjectsOfType<PieceView>().Length == 0, "死亡途中退出后没有战斗棋子残留");
                Require(flow.Screen == GameScreen.Home && !flow.IsTransitioning, "最终返回完整首页");
                LastResult = "通过 " + _assertions + " 条：真实扣血/占格释放、HUD句柄解绑、自然消融、暂停/Kill回收、复用、换轮与离场";
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error;
                RevLog.Exception(error, "死棋回收验收失败", "RevGame");
            }
            finally
            {
                if (reused != null) { RevPool.Return(reused, 0); }
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                System.IO.File.WriteAllText("Temp/GamePieceDeathChecks.txt", LastResult + "\n" + string.Join("\n", Evidence));
            }
        }

        private static int _lastKilledId;
        private static PieceView KillGuard(BattleService battle)
        {
            Piece target = null;
            foreach (Piece piece in battle.EnemyPieces)
            {
                if (piece.Side == GameSide.Enemy && piece.IsAlive && !piece.IsKing) { target = piece; break; }
            }
            Require(target != null, "固定夹具存在存活敌方护卫");
            PieceView view = null;
            foreach (PieceView item in UnityEngine.Object.FindObjectsOfType<PieceView>())
            {
                if (item.PieceId == target.Id) { view = item; break; }
            }
            Require(view != null, "死亡前已有真实棋子视图");
            _lastKilledId = target.Id;
            BoardPos position = target.Pos;
            // 反射只用于Editor夹具接入既有私有扣血出口；真实伤害、死亡事件和移除占格均由服务完成。
            typeof(BattleService).GetMethod("DamagePiece", PrivateInstance).Invoke(battle, new object[] { target, target.Hp, null, false });
            Require(!target.IsAlive && battle.Board.FindPiece(target.Id) == null, "致死当帧从模型移除");
            Require(battle.Board.GetCell(position).Occupant == null, "致死当帧释放棋盘格");
            Require(view.DeathDissolveProgress > 0f && DissolvingCount() > 0, "原视图只进入短期死亡表现集合");
            return view;
        }

        private static void CompleteBar(BattleHudPanel hud, bool boss)
        {
            string prefix = boss ? "_boss" : "_progress";
            var field = typeof(BattleHudPanel).GetField(prefix + "Tween", PrivateInstance);
            var image = (Image)typeof(BattleHudPanel).GetField(boss ? "_bossBar" : "_progressFill", PrivateInstance).GetValue(hud);
            var callback = (TweenCallback)Delegate.CreateDelegate(typeof(TweenCallback), hud, boss ? "ClearBossTween" : "ClearProgressTween");
            object[] arguments = { image, 0.5f, field.GetValue(hud), 1f, false, callback };
            typeof(BattleHudPanel).GetMethod("UpdateBar", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
            Tween tween = (Tween)arguments[2];
            field.SetValue(hud, tween);
            tween.Complete(true);
            Require(field.GetValue(hud) == null, (boss ? "Boss血条" : "回合进度条") + "自然结束后清空Tween引用");
        }

        private static Tween VisualTween(PieceView view)
        {
            return (Tween)typeof(PieceView).GetField("_visualTween", PrivateInstance).GetValue(view);
        }

        private static int DissolvingCount()
        {
            var board = UnityEngine.Object.FindObjectOfType<BoardView>();
            return ((HashSet<PieceView>)typeof(BoardView).GetField("_dissolvingPieces", PrivateInstance).GetValue(board)).Count;
        }

        private static RevTask<PieceView> LoadPiece()
        {
            var completion = new RevTaskCompletionSource<PieceView>();
            RevPool.GetAsync<PieceView>(item => completion.SetResult(item));
            return completion.Task;
        }

        private static void Require(bool condition, string message)
        {
            _assertions++;
            if (!condition) { throw new InvalidOperationException(message); }
            Evidence.Add("PASS " + message);
        }

        // 整章观察器只读取死亡后的状态，不扣血、不跳关；按业务死亡事件延时检查原Id是否还在画面。
        private static readonly object MonitorOwner = new object();
        private static int _observedDeaths, _checkedDeaths;
        private static readonly List<string> MonitorFailures = new List<string>();
        public static void StartMonitor()
        {
            RevEvent.RemoveAllByOwner(MonitorOwner);
            RevTimer.CancelAllOf(MonitorOwner);
            _observedDeaths = _checkedDeaths = 0;
            MonitorFailures.Clear();
            RevEvent.AddEventListener<int>(Events.PieceDied, MonitorDeath, owner: MonitorOwner);
        }

        private static void MonitorDeath(int id)
        {
            _observedDeaths++;
            RevTimer.After(1.6f, () =>
            {
                _checkedDeaths++;
                foreach (PieceView view in UnityEngine.Object.FindObjectsOfType<PieceView>())
                {
                    if (view.PieceId == id) { MonitorFailures.Add("死亡1.6游戏秒后仍有活动视图，Id=" + id); }
                }
            }, owner: MonitorOwner);
        }

        public static string MonitorReport()
        {
            return "observed=" + _observedDeaths + ";checked=" + _checkedDeaths + ";failures=" + MonitorFailures.Count
                + "\n" + string.Join("\n", MonitorFailures);
        }

        public static void StopMonitor()
        {
            RevEvent.RemoveAllByOwner(MonitorOwner);
            RevTimer.CancelAllOf(MonitorOwner);
            System.IO.File.WriteAllText("Temp/GamePieceDeathMonitor.txt", MonitorReport());
        }
    }
}
