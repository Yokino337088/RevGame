// 固定战术输入与实际Play画面分开验收：PNG/GIF只证明视觉，规则结论来自占格、气与出手次数。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameCombatImpactChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private static int _checks;
        private const string Output="Temp/CombatImpactVerification";

        public static string RunRules()
        {
            if(Application.isPlaying) { throw new InvalidOperationException("规则夹具只在编辑态执行"); }
            GameConfigFileLoader.LoadFromDirectory("Assets/GameRes/Data");
            _checks=0;
            BattleService.VerifyImpactService(Require);
            var board=new BoardModel(9);
            Piece hunter=Put(board,1,PieceKind.Swordman,GameSide.Enemy,4,7);
            Piece prey=Put(board,2,PieceKind.Archer,GameSide.Player,4,0);
            Put(board,3,PieceKind.Shield,GameSide.Player,0,7);
            GoRules.Evaluate(board);
            var moves=new List<BoardPos>(81);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int turn=0;turn<2;turn++)
            {
                string before=board.Dump(); int liberties=hunter.Liberties;
                EnemyTactics.GetAdvanceCandidates(board,hunter,moves);
                BoardPos next=EnemyTactics.ChooseSafeMove(board,hunter,moves,1);
                Require(board.Dump()==before && hunter.Liberties==liberties,"追击候选不污染真实棋盘");
                Require(next!=hunter.Pos && next.X==4 && next.Y<hunter.Pos.Y,"近战优先向后排弓箭推进而非左侧近战诱饵");
                Require(board.Move(hunter,next),"追击落点通过实际占格");
                GoRules.Evaluate(board);
            }
            watch.Stop();
            MoveRules.GetAttackTargets(board,hunter,moves);
            Require(moves.Contains(prey.Pos),"两次合法冲锋后进入玩家远程射界");
            // 直线冲锋不能穿过中途挡路棋；即使终点空闲也不能越过去。
            board=new BoardModel(9);
            hunter=Put(board,1,PieceKind.Swordman,GameSide.Enemy,4,7);
            Put(board,2,PieceKind.Archer,GameSide.Player,4,0);
            Put(board,3,PieceKind.Shield,GameSide.Enemy,4,6);
            GoRules.Evaluate(board);
            EnemyTactics.GetAdvanceCandidates(board,hunter,moves);
            Require(!moves.Contains(new BoardPos(4,5)) && !moves.Contains(new BoardPos(4,4)),"冲锋中途占格阻挡整个方向");
            board=new BoardModel(9);
            hunter=Put(board,1,PieceKind.Swordman,GameSide.Enemy,1,0);
            Put(board,2,PieceKind.Shield,GameSide.Enemy,0,1);
            Put(board,3,PieceKind.Shield,GameSide.Enemy,1,1);
            Put(board,4,PieceKind.Archer,GameSide.Player,4,5);
            GoRules.Evaluate(board);
            Require(hunter.EyeCount>0,"角眼夹具真实成眼");
            EnemyTactics.GetAdvanceCandidates(board,hunter,moves);
            Require(moves.Count==0,"守眼近战不会为追击拆阵");
            board=new BoardModel(9);
            prey=Put(board,1,PieceKind.Archer,GameSide.Player,4,4);
            Put(board,2,PieceKind.Shield,GameSide.Enemy,3,4);
            Put(board,3,PieceKind.Shield,GameSide.Enemy,5,4);
            Put(board,4,PieceKind.Shield,GameSide.Enemy,4,3);
            GoRules.Evaluate(board);
            var reinforcement=new Piece(5,PieceCatalog.Get(PieceKind.Swordman,StarTier.One),GameSide.Enemy);
            string purpose;
            BoardPos deployment;
            Require(EnemyTactics.TryChooseDeployment(board,reinforcement,1,out deployment,out purpose),"补兵找到围堵点");
            Require(deployment==new BoardPos(4,5),"增援封住远程最后一口气");
            board.Place(reinforcement,deployment); GoRules.Evaluate(board);
            Require(prey.IsLocked && prey.Liberties==0,"封堵实际触发零气锁死");

            board=new BoardModel(9);
            Put(board,1,PieceKind.Archer,GameSide.Player,0,0);
            Piece manual=Put(board,2,PieceKind.MageWater,GameSide.Player,4,0);
            Put(board,3,PieceKind.MageFire,GameSide.Player,8,0);
            Put(board,4,PieceKind.Shield,GameSide.Enemy,4,8);
            GoRules.Evaluate(board);
            var plan=new List<FormationMove>();
            string original=board.Dump();
            RangedFormationPlanner.Plan(board,6,2,2,plan);
            Require(plan.Count==2 && board.Dump()==original,"整队规划不改实战，散阵可形成完整队形");
            typeof(Piece).GetProperty("NextMoveAt",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(manual,100f);
            RangedFormationPlanner.Plan(board,6,2,2,plan);
            Require(!plan.Exists(move=>move.PieceId==manual.Id),"尊重手动移动冷却");
            // 二格身体需要完整2×2空间，不能只验证左下角。
            board=new BoardModel(9);
            Put(board,1,PieceKind.Archer,GameSide.Player,0,0);
            var large=new Piece(2,PieceCatalog.Get(PieceKind.MageFire,StarTier.Three),GameSide.Player,2);
            Require(board.Place(large,new BoardPos(6,0)),"大棋夹具完整占格");
            Put(board,3,PieceKind.Shield,GameSide.Enemy,4,8);GoRules.Evaluate(board);
            RangedFormationPlanner.Plan(board,6,2,2,plan);
            Require(plan.Count==1,"大棋也可参与整队");
            Require(board.Move(large,plan[0].Position),"大棋整队提交成功");
            for(int y=0;y<2;y++) { for(int x=0;x<2;x++) { Require(board.GetPiece(new BoardPos(large.Pos.X+x,large.Pos.Y+y))==large,"大棋没有丢失身体占格"); } }
            LastResult="通过 "+_checks+" 条战斗冲击规则断言；两次追击查询合计 "+watch.ElapsedMilliseconds+"ms（编辑器单次，不代表手机性能）";
            return LastResult;
        }

        /// <summary>真实流程冷加载后拍摄三组连续帧；finally返回大厅，不保留夹具棋盘或暂停模型。</summary>
        public static async void Begin()
        {
            if(!Application.isPlaying || LastResult=="运行中") { throw new InvalidOperationException("需要空闲Play"); }
            LastResult="运行中";_checks=0;Directory.CreateDirectory(Output);
            GameFlow flow=RevServices.GetRequired<GameFlow>();
            IDisposable scorePreview=((BattleService)RevServices.GetRequired<IBattleService>()).BeginScorePreview();
            string outcome="失败：验收未完成";
            try
            {
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1),"真实流程进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                var battle=(BattleService)RevServices.GetRequired<IBattleService>();
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                Require(battle.LoadImpactScenario(),"加载固定展示输入");
                battle.Tick(3);Require(battle.StartBattle(),"正式开战入口");
                await RevTask.Delay(600);
                var board=UnityEngine.Object.FindObjectOfType<BoardView>();
                ScreenCapture.CaptureScreenshot(Output+"/initial.png");
                // 先真实推进到整队窗口；同一步事件会启动DOTween，之后逐帧采样而不再推进模型。
                int formations=0;
                object owner=new object();
                RevEvent.AddEventListener<int>(Events.PieceFormationMoved,id=>formations++,owner:owner);
                try { while(battle.BattleElapsed<Balance.RangedFormationSeconds) { battle.Tick(Balance.BattleSimulationStepSeconds); } }
                finally { RevEvent.RemoveAllByOwner(owner); }
                Require(formations>0,"真实Tick触发散落远程自动整队");
                bool animated=false;
                for(int i=0;i<12;i++)
                {
                    foreach(PieceView piece in UnityEngine.Object.FindObjectsOfType<PieceView>()) { animated|=piece.IsFormationMoving; }
                    ScreenCapture.CaptureScreenshot(Output+"/formation-"+i.ToString("D2")+".png");
                    await RevTask.Delay(65);
                }
                Require(animated,"整队确实运行DOTween位移");
                RegionPulseInfo region=default(RegionPulseInfo);
                RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning,info=>region=info,owner:owner);
                try { Require(battle.TriggerRegionPulse(),"通过真实区域波动入口"); }
                finally { RevEvent.RemoveAllByOwner(owner); }
                Require(board.ActiveHazardCellCount==Balance.RegionSize*Balance.RegionSize,"Shader覆盖真实预警区域");
                for(int i=0;i<28;i++)
                {
                    ScreenCapture.CaptureScreenshot(Output+"/region-"+i.ToString("D2")+".png");
                    await RevTask.Delay(120);
                    if (i==5) { Require(RegionPurpleCoverage(region)>.25f,"实际GPU画面中至少25%的预警区域呈现深紫地面，不能只有Tween句柄"); }
                    battle.Tick(.12f);
                }
                Require(board.ActiveHazardCellCount==0,"区域效果自然结束并隐藏");
                // 密集原画展示使用已结算的表现快照；不把注入VFX当作AI命中证明。
                PieceKind[] kinds={PieceKind.Archer,PieceKind.Crossbow,PieceKind.MageWater,PieceKind.MageFire,PieceKind.Priest};
                int meleePeak=0,projectilePeak=0;
                for(int frame=0;frame<24;frame++)
                {
                    for(int i=0;i<5;i++)
                    {
                        RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit,new BattleHitInfo(200+i,new BoardPos(7,i+2),1,GameSide.Enemy,12,true,new BoardPos(1,i+2),100+i,false,kinds[i],1,true));
                    }
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit,new BattleHitInfo(300,new BoardPos(3,4),1,GameSide.Enemy,18,true,new BoardPos(2,4),900,false,PieceKind.Swordman));
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit,new BattleHitInfo(301,new BoardPos(6,4),1,GameSide.Enemy,18,true,new BoardPos(5,4),901,false,PieceKind.Shield));
                    await RevTask.Delay(100);
                    meleePeak=Math.Max(meleePeak,board.ActiveMeleeCount);projectilePeak=Math.Max(projectilePeak,board.ActiveProjectileCount);
                    Require(board.ActiveMeleeCount<=BoardView.MaxMeleeEffects && board.ActiveProjectileCount<=BoardView.MaxProjectiles,"近战与远程分别遵守池预算");
                    ScreenCapture.CaptureScreenshot(Output+"/combat-"+frame.ToString("D2")+".png");
                }
                Require(meleePeak>0 && projectilePeak>0,"实际屏幕同时显示近战和远程特效");
                await RevTask.Delay(600);
                for(int frame=0;frame<16;frame++)
                {
                    // 独立近战镜头留出空格，避免法球/数字把斩弧遮住。仍走同一个真实命中反馈监听器。
                    if(frame%4==0)
                    {
                        PieceKind[] melee={PieceKind.Swordman,PieceKind.Shield,PieceKind.Assassin};
                        for(int i=0;i<3;i++)
                        {
                            RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit,new BattleHitInfo(400+i,new BoardPos(2+i*2,6),1,GameSide.Enemy,18,true,new BoardPos(1+i*2,6),950+i,false,melee[i]));
                        }
                    }
                    await RevTask.Delay(70);
                    ScreenCapture.CaptureScreenshot(Output+"/melee-"+frame.ToString("D2")+".png");
                }
                await RevTask.Delay(1700);
                Require(board.ActiveEffectCount==0,"全部命中反馈按寿命回池");
                // 再触发区域后马上退出，验证中途取消路径。
                battle.Tick(2);Require(battle.TriggerRegionPulse(),"再次释放区域用于离场取消");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(250);
                Require(UnityEngine.Object.FindObjectsOfType<BattleEffectView>().Length==0,"返回大厅没有残留命中对象");
                ScreenCapture.CaptureScreenshot(Output+"/final.png");
                outcome="通过 "+_checks+" 条Play断言；近战峰值 "+meleePeak+"，弹道峰值 "+projectilePeak+"；证据 "+Output;
            }
            catch(Exception error) { outcome="失败："+error; }
            finally
            {
                try { await GameSceneTransitionChecks.ReturnHomeAsync(flow); }
                catch(Exception cleanup) { outcome+="；清理失败："+cleanup.Message; }
                scorePreview.Dispose();
                LastResult=outcome;
                File.WriteAllText(Output+"/result.txt",outcome);
            }
        }

        private static Piece Put(BoardModel board, int id, PieceKind kind, GameSide side, int x, int y)
        {
            var piece=new Piece(id,PieceCatalog.Get(kind,StarTier.One),side);
            if(!board.Place(piece,new BoardPos(x,y))) { throw new InvalidOperationException("夹具占格冲突"); }
            return piece;
        }

        /// <summary>只在编辑器验收读回GPU；按每格固定采样数归一化，不把“组件启用”误当画面可见。</summary>
        private static float RegionPurpleCoverage(RegionPulseInfo region)
        {
            // CaptureScreenshotAsTexture要求帧末；此处异步续点在Update。读取前一帧已落盘的真实截图，
            // 避免拿到null纹理，也不另外渲染一张与玩家视口不同的假画面。
            Texture2D capture=new Texture2D(2,2,TextureFormat.RGBA32,false);
            capture.LoadImage(File.ReadAllBytes(Output+"/region-05.png"));
            try
            {
                Color32[] pixels=capture.GetPixels32();
                Camera camera=Camera.main;
                int dark=0,total=0;
                for(int y=0;y<region.Size;y++)
                {
                    for(int x=0;x<region.Size;x++)
                    {
                        Vector3 world=new BoardPos(region.Origin.X+x,region.Origin.Y+y).ToWorldPosition(Consts.CellSize,Consts.BoardOriginOffset);
                        Vector3 min=camera.WorldToScreenPoint(world-Vector3.one*Consts.CellSize*.42f);
                        Vector3 max=camera.WorldToScreenPoint(world+Vector3.one*Consts.CellSize*.42f);
                        for(int sy=0;sy<20;sy++)
                        {
                            for(int sx=0;sx<20;sx++)
                            {
                                int px=Mathf.Clamp((int)Mathf.Lerp(min.x,max.x,(sx+.5f)/20),0,capture.width-1);
                                int py=Mathf.Clamp((int)Mathf.Lerp(min.y,max.y,(sy+.5f)/20),0,capture.height-1);
                                Color32 c=pixels[py*capture.width+px];
                                // PNG是sRGB编码：线性空间0.07的绿色会编码到约75，不能拿线性70阈值误判。
                                if(c.r<150 && c.g<120 && c.b>30 && c.b>c.r*1.2f && c.b>c.g*1.4f) { dark++; }
                                total++;
                            }
                        }
                    }
                }
                return dark/(float)Math.Max(1,total);
            }
            finally { UnityEngine.Object.DestroyImmediate(capture); }
        }
        private static void Require(bool condition, string message)
        {
            _checks++;if(!condition) { throw new InvalidOperationException("断言失败："+message); }
        }
    }
}
