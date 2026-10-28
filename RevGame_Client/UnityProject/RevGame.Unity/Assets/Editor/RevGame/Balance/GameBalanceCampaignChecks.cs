// 隔离Play数值测试：真实UI指针落子、正式战斗/奖励入口、固定种子；不强制伤害/胜利或修改玩家属性。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Automation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>测试宿主只观察和输入，模型仍由BattleLogic驱动；一次仅一个作业，finally解除事件/手势并恢复时间。</summary>
    public static class GameBalanceCampaignChecks
    {
        public static string Status { get; private set; } = "idle";
        private static Report _report;
        private static Round _round;
        private static readonly GamePointerInput Pointer = new GamePointerInput();
        private static readonly object Owner = new object();
        private static string _folder;
        private static bool _cancel;
        public static string Begin(string label,int seed,bool careless,int first,int last,bool continuous=false,float speed=4,int maxCards=6,int lastRound=10)
        {
            if(!Application.isPlaying||!RevServices.GetRequired<StoryProgress>().IsIsolated||Status=="running")
            {throw new InvalidOperationException("需要隔离Play且没有运行中的数值测试");}
            if(first<1||last>6||first>last||label!=Path.GetFileName(label)){throw new ArgumentException("测试章号/标签无效");}
            if (speed < 1 || speed > 4 || maxCards < 1 || maxCards > 6 || lastRound < 1 || lastRound > 10)
            { throw new ArgumentException("测试速度/落牌数/回合边界无效"); }
            Status="running";_cancel=false;
            _report=new Report{label=label,seed=seed,careless=careless,continuous=continuous,timeScale=speed,maxCards=maxCards};
            _folder=Path.GetFullPath("../../../.codex/work/balance-2026-10-28/"+label+"-"+seed);
            Directory.CreateDirectory(_folder);
            RunAsync(seed,careless,first,last,continuous,speed,maxCards,lastRound);return "started";
        }
        public static string State(){return JsonUtility.ToJson(new StateData{status=Status,chapter=_round?.chapter??0,round=_round?.round??0,count=_report?.rounds.Count??0,error=_report?.error??"",folder=_folder});}
        public static void Cancel(){_cancel=true;}
        private static void Check(){if(_cancel||!Application.isPlaying){throw new OperationCanceledException("数值测试取消");}}
        private static async RevTask Until(Func<bool> test,float timeout,string message)
        {
            float deadline=Time.realtimeSinceStartup+timeout;
            while(!test())
            {
                Check();
                var flow=RevServices.GetRequired<GameFlow>();
                if(flow.IsStoryPlaying&&!flow.IsTransitioning){flow.AdvanceStory();}
                if(Time.realtimeSinceStartup>deadline){throw new InvalidOperationException(message+"；"+flow.LastError);}
                await RevTask.Yield();
            }
        }
        private static async RevTask PressButton(string path)
        {
            await Until(()=>Pointer.PressButton(Pointer.FindButton(path)),8,"按钮不可点击："+path);
            await RevTask.Yield();
            if(!Pointer.Release(true)){throw new InvalidOperationException(Pointer.LastError);}
            await RevTask.Yield();
        }
        private static async RevTask PlaceOne(IBattleService battle,bool careless)
        {
            if(!GameBalanceTactics.ChoosePlacement(battle,careless,out int id,out BoardPos pos,out string reason)){return;}
            int index=0;while(index<battle.Hand.Count&&battle.Hand[index].Id!=id){index++;}
            int hand=battle.Hand.Count;
            await PressButton("PreparePanel/card"+index);
            await Until(()=>battle.SelectedCardId==id,3,"选牌没有生效");
            BoardCellView cell=null;
            foreach(BoardCellView candidate in UnityEngine.Object.FindObjectsOfType<BoardCellView>()){if(candidate.Position==pos){cell=candidate;break;}}
            await Until(()=>Pointer.PressCell(cell),5,"落点被遮挡："+pos);
            await RevTask.Yield();
            if(!Pointer.Release(true)){throw new InvalidOperationException(Pointer.LastError);}
            if(battle.Hand.Count!=hand-1||battle.Board.GetPiece(pos)?.Side!=GameSide.Player){throw new InvalidOperationException("手牌/占格断言失败");}
            _round.decisions.Add("牌#"+id+"落"+pos+"："+reason);
            await RevTask.Yield();
        }
        private static async void RunAsync(int seed,bool careless,int first,int last,bool continuous,float speed,int maxCards,int lastRound)
        {
            var flow=RevServices.GetRequired<GameFlow>();var battle=(BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale=Time.timeScale;
            Application.LogCallback logger=(message,stack,type)=>{if(type==LogType.Error||type==LogType.Exception){_report.unityErrors++;_report.error=message;}};
            Application.logMessageReceived+=logger;
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit,OnHit,owner:Owner);
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked,OnAttack,owner:Owner);
            RevEvent.AddEventListener<int>(Events.PieceDied,OnDeath,owner:Owner);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast,info=>{if(_round!=null){_round.bossCasts++;}},owner:Owner);
            try
            {
                await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsGameplayBlocked,30,"首页未就绪");
                // 只提高整局时钟加快长测；双方AI/冷却/增益都同速，绝不使用攻击/防御/生命GM加成。
                Time.timeScale=speed;
                if (battle.PlayerInvincible || battle.IsAutomationBoostActive) { throw new InvalidOperationException("测试不能继承无敌或开发属性加成"); }
                battle.UnlockAllChaptersForDevelopment();
                for(int chapter=first;chapter<=last;chapter++)
                {
                    Check();
                    if(!continuous||chapter==first)
                    {
                        if(flow.Screen!=GameScreen.Home){flow.ReturnHome();await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsTransitioning,25,"返回首页失败");}
                        battle.SetBalanceTestSeed(seed+chapter*101);
                        if(!flow.OpenLevels()||!flow.EnterBattle(chapter)){throw new InvalidOperationException(flow.LastError);}
                    }
                    for(int number=1;number<=Math.Min(Balance.RoundsPerChapter,lastRound);number++)
                    {
                        await Until(()=>!flow.IsGameplayBlocked&&battle.Phase==GamePhase.Prepare&&RevUI.IsOpen<PreparePanel>(),35,"准备未交付");
                        if(battle.Round!=number||battle.Chapter!=chapter){throw new InvalidOperationException("章回交割不一致");}
                        _round=new Round{chapter=chapter,round=number,boss=battle.IsBossRound};
                        if(!careless)
                        {
                            int front=0,ranged=0;
                            foreach(Piece p in battle.PlayerPieces){if(p.IsAlive){if(p.Def.IsRanged){ranged++;}else{front++;}}}
                            foreach(var card in battle.Hand){if(PieceCatalog.Get(card.Kind,card.Star).IsRanged){ranged++;}else{front++;}}
                            // 免费换牌是正式玩家功能；只在前排/后排严重失衡时刷新一次，不无限抽到最优起手。
                            if(front<2||ranged<2){await PressButton("PreparePanel/btnRefresh");_round.decisions.Add("阵容失衡，免费刷新剩余手牌一次");}
                        }
                        for(int card=0;card<maxCards&&battle.Hand.Count>0&&battle.CardsRemaining>0;card++)
                        {
                            // 正常玩家不盲目把全盘塞满；已有十余颗队伍时保留手牌/空位，只补损失的角色。
                            int alive=0;foreach(Piece p in battle.PlayerPieces){if(p.IsAlive){alive++;}}
                            if(!careless&&alive>=14){break;}
                            int before=battle.Hand.Count;await PlaceOne(battle,careless);if(battle.Hand.Count==before){break;}
                            _round.cardsPlayed++;
                        }
                        await Until(()=>battle.PreparationGranted,10,"准备增益未到达");
                        _round.preparation=new string[battle.PreparationBuffs.Count];for(int i=0;i<_round.preparation.Length;i++){_round.preparation[i]=battle.PreparationBuffs[i].Kind.ToString();}
                        Snapshot(battle,true);Capture("start");
                        // 与玩家开始战斗按钮一致；GM明确记录，落牌使用EventSystem实际指针。
                        var start=RevGM.Execute("RevGame/开始战斗");if(!start.Success){throw new InvalidOperationException(start.Message);}
                        float deadline=Time.realtimeSinceStartup+90, nextDecision=3;int sample=0;
                        while(battle.Phase==GamePhase.Battle)
                        {
                            Check();
                            if(Time.realtimeSinceStartup>deadline){_round.timeout=true;break;}
                            if(battle.BattleElapsed>=nextDecision&&!flow.IsGameplayBlocked)
                            {
                                if(!careless&&GameBalanceTactics.ChooseRetreat(battle,out BoardPos from,out BoardPos to))
                                {
                                    bool selected=RevLogic.Get<BattleLogic>().ClickBoard(from);
                                    bool moved=selected&&RevLogic.Get<BattleLogic>().ClickBoard(to);
                                    if(moved){_round.decisions.Add("受伤远程"+from+"撤至"+to);_round.moves++;}
                                }
                                nextDecision=battle.BattleElapsed+3;
                            }
                            if(battle.BattleElapsed>=5+sample*10){Capture("combat"+(sample++));}
                            await RevTask.Yield();
                        }
                        _round.seconds=battle.BattleElapsed;_round.winner=battle.LastWinner?.ToString()??"timeout";
                        _round.events=battle.BattleEventCount;_round.bombs=battle.EnergyBombCount;_round.regions=battle.RegionPulseCount;_round.emergency=battle.EmergencySupportCount;
                        Snapshot(battle,false);Capture("end");
                        _report.rounds.Add(_round);Save();
                        bool won=battle.LastWinner==GameSide.Player&&!_round.timeout;
                        if(!won)
                        {
                            _report.failures++;
                            if(continuous){_report.error="连续通关真实失败，不跳过失败章节";throw new InvalidOperationException(_report.error);}
                            // 失败事实保留；下一轮显式使用独立六牌夹具，不能把它当连续通关或带入虚假阵容。
                            _round=null;
                            if(number<Math.Min(Balance.RoundsPerChapter,lastRound))
                            {if(!battle.BeginBalanceRound(chapter,number+1)){throw new InvalidOperationException(battle.LastError);}continue;}
                            break;
                        }
                        await Until(()=>!flow.IsBossVictoryPlaying&&!flow.IsStoryPlaying&&RevUI.IsOpen<SettlePanel>(),35,"结算演出未交付");
                        _round=null;
                        if (number == lastRound && number < Balance.RoundsPerChapter) { break; }
                        var next=RevGM.Execute("RevGame/下一回合");if(!next.Success){throw new InvalidOperationException(next.Message);}
                        if(number==Balance.RoundsPerChapter&&chapter<6)
                        {
                            await Until(()=>battle.Phase==GamePhase.Reward&&RevUI.IsOpen<RewardPanel>(),10,"奖励未交付");
                            int choice=GameBalanceTactics.ChooseReward(battle);
                            var reward=RevGM.Execute("RevGame/选奖励 "+choice);if(!reward.Success){throw new InvalidOperationException(reward.Message);}
                        }
                        if(number==Balance.RoundsPerChapter){break;}
                    }
                    if(!continuous||chapter==last)
                    {
                        if(flow.Screen==GameScreen.Battle&&!flow.IsTransitioning){flow.ReturnHome();}
                        await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsTransitioning,25,"章后收尾失败");
                    }
                }
                Status=_report.failures==0?"completed":"completed-with-defeats";
            }
            catch(Exception error){Status="failed";_report.error=error.ToString();}
            finally
            {
                Time.timeScale=oldScale;Pointer.Release(false);RevEvent.RemoveAllByOwner(Owner);Application.logMessageReceived-=logger;
                _round=null;_report.status=Status;Save();
                if(Application.isPlaying&&flow.Screen==GameScreen.Battle&&!flow.IsTransitioning){flow.ReturnHome();}
            }
        }
        private static void OnAttack(PieceAttackInfo attack){if(_round==null){return;}if(attack.Side==GameSide.Player){_round.playerAttacks++;}else{_round.enemyAttacks++;}}
        private static void OnHit(BattleHitInfo hit)
        {
            if(_round==null){return;}
            if(hit.TargetSide==GameSide.Player){_round.playerDamage+=hit.Damage;if(hit.HasAttacker){_round.playerCombatDamage+=hit.Damage;}}
            else{_round.enemyDamage+=hit.Damage;if(hit.HasAttacker){_round.enemyCombatDamage+=hit.Damage;}else{_round.supportDamage+=hit.Damage;}}
            if(_round.firstHit<0){_round.firstHit=RevServices.GetRequired<IBattleService>().BattleElapsed;}
        }
        private static void OnDeath(int id)
        {
            if(_round==null){return;}var battle=RevServices.GetRequired<IBattleService>();
            bool player=false;foreach(Piece p in battle.PlayerPieces){if(p.Id==id){_round.playerDeaths++;player=true;break;}}
            if(!player){_round.enemyDeaths++;}
            if(_round.firstDeath<0){_round.firstDeath=battle.BattleElapsed;}
        }
        private static void Snapshot(IBattleService battle,bool start)
        {
            int players=0,enemies=0,php=0,ehp=0;
            foreach(Piece p in battle.PlayerPieces){if(p.IsAlive){players++;php+=p.Hp;}}
            foreach(Piece p in battle.EnemyPieces){if(p.IsAlive){enemies++;ehp+=p.Hp;}}
            if(start){_round.playersStart=players;_round.enemiesStart=enemies;_round.playerHpStart=php;_round.enemyHpStart=ehp;}
            else{_round.playersEnd=players;_round.enemiesEnd=enemies;_round.playerHpEnd=php;_round.enemyHpEnd=ehp;}
        }
        private static void Capture(string label){if(_round!=null){ScreenCapture.CaptureScreenshot(_folder+"/c"+_round.chapter+"-r"+_round.round+"-"+label+".png");}}
        private static void Save(){File.WriteAllText(_folder+"/result.json",JsonUtility.ToJson(_report,true));}
        [Serializable]private sealed class StateData{public string status,error,folder;public int chapter,round,count;}
        [Serializable]private sealed class Report{public string label,status,error;public int seed,failures,unityErrors,maxCards;public float timeScale;public bool careless,continuous;public List<Round> rounds=new List<Round>();}
        [Serializable]private sealed class Round
        {
            public int chapter,round,cardsPlayed,playersStart,enemiesStart,playersEnd,enemiesEnd,playerHpStart,enemyHpStart,playerHpEnd,enemyHpEnd;
            public int playerAttacks,enemyAttacks,playerDamage,enemyDamage,playerCombatDamage,enemyCombatDamage,supportDamage,playerDeaths,enemyDeaths,events,bombs,regions,bossCasts,moves,emergency;
            public float seconds,firstHit=-1,firstDeath=-1;public string winner;public bool boss,timeout;public string[] preparation;
            public List<string> decisions=new List<string>();
        }
    }
}
