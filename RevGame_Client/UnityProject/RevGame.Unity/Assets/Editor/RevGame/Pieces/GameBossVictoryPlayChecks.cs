// 图形Play真实流程验收：入场/击败/剧情/退出全走正式流程，只有最后一击是受控固定伤害夹具。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// <summary>仅隔离Play可执行，不改正式进度。状态供MCP只读轮询，详细证据存Temp由报告工具整理。</summary>
    public static class GameBossVictoryPlayChecks
    {
        private static readonly List<string> Assertions = new List<string>();
        public static string Status { get; private set; } = "idle";
        public static string Error { get; private set; } = "";
        private const string Folder = "Temp/BossVictoryPlayChecks";
        public static string Begin()
        {
            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated) { throw new InvalidOperationException("需要隔离进度的图形Play"); }
            if (Status == "running") { throw new InvalidOperationException("Boss流程检查已运行"); }
            Status = "running"; Error = ""; Assertions.Clear(); Directory.CreateDirectory(Folder);
            RunAsync(); return "started";
        }
        public static string State() { return Status; }
        private static void Require(bool condition,string message)
        { if (!condition) { throw new InvalidOperationException(message); } if (!Assertions.Contains(message)) { Assertions.Add(message); } }
        private static async RevTask Until(Func<bool> condition,float seconds,string failure)
        {
            float deadline=Time.realtimeSinceStartup+seconds;
            while (!condition())
            { if (!Application.isPlaying||Time.realtimeSinceStartup>deadline){throw new InvalidOperationException(failure);} await RevTask.Yield(); }
        }
        private static async void RunAsync()
        {
            int errors=0; float originalTime=Time.timeScale;
            Application.LogCallback log=(message,stack,type)=>{if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors++;File.AppendAllText(Folder+"/errors.txt",message+"\n"+stack+"\n");}};
            Application.logMessageReceived+=log;
            GameFlow flow=RevServices.GetRequired<GameFlow>();
            IBattleService battle=RevServices.GetRequired<IBattleService>();
            try
            {
                await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsGameplayBlocked,35,"首页未就绪");
                for(int chapter=1;chapter<=6;chapter++)
                {
                    Require(flow.EnterBossChapterForDevelopment(chapter),"进入真实Boss章节"+chapter+":"+flow.LastError);
                    await Until(()=>flow.IsBossIntroPlaying,30,"没有触发入场序列");
                    bool entranceHeard=false;
                    bool reveal=false,announce=false; int frame=0;float nextCapture=0;
                    float deadline=Time.realtimeSinceStartup+45;
                    while(flow.IsBossIntroPlaying||flow.IsTransitioning||flow.IsBossDevelopmentEntering)
                    {
                        if(Time.realtimeSinceStartup>deadline){throw new InvalidOperationException("入场未结束："+flow.BossIntroStage+"/"+flow.LastError);}
                        entranceHeard |= VoicePlaying("BossEntrance");
                        Require(battle.EnemyPieces.Count(p=>p.IsBoss&&p.IsAlive)==1,"第"+chapter+"章全程唯一Boss");
                        if (RevUI.Get<BossIntroPanel>() != null)
                        {
                            Require(UnityEngine.Object.FindObjectOfType<GamePresentation>().IsBossCameraRestored,"登场弹幕显示时镜头已完全拉回");
                            VerifyBanner(RevUI.Get<BossIntroPanel>().transform);
                        }
                        if(flow.BossIntroStage=="focus"||flow.BossIntroStage=="reveal"||flow.BossIntroStage=="return-camera")
                        { Require(!RevUI.IsOpen<BossIntroPanel>(),"特写和拉回阶段没有提前显示弹幕"); }
                        if(flow.BossIntroStage=="reveal"||flow.BossIntroStage=="announce")
                        {
                            Require(flow.IsGameplayBlocked&&battle.Phase==GamePhase.Prepare,"入场锁玩法且仍为准备阶段");
                            Require(battle.PreparationElapsed==0,"入场不消费准备计时");
                            var prepare=RevUI.Get<PreparePanel>();
                            Require(prepare!=null&&prepare.GetComponent<CanvasGroup>().alpha<.01f,"准备面板隐藏但资源仍就绪");
                            if(flow.BossIntroStage=="reveal"){reveal=true;}
                            if(flow.BossIntroStage=="announce"){announce=true;Require(RevUI.IsOpen<BossIntroPanel>(),"登场弹幕实际可见");}
                            if(Time.realtimeSinceStartup>=nextCapture)
                            {ScreenCapture.CaptureScreenshot(Folder+"/Chapter"+chapter.ToString("D2")+"-Intro-"+(frame++).ToString("D2")+".png");nextCapture=Time.realtimeSinceStartup+.15f;}
                        }
                        await RevTask.Yield();
                    }
                    Require(entranceHeard,"第"+chapter+"章登场声音由真实AudioSource播放");
                    Require(reveal&&announce&&flow.BossIntroStage=="completed","真实入场显现/弹幕两阶段完整结束");
                    Require(!flow.IsGameplayBlocked&&RevUI.Get<PreparePanel>().GetComponent<CanvasGroup>().alpha==1,"入场结束恢复准备UI与操作");
                    BoardView board=UnityEngine.Object.FindObjectOfType<BoardView>();
                    Require(board.AreBossViewsReady(chapter),"实际章Boss视图8帧绑定");
                    // GM实际注册/执行，不直接调用私有序列。重复动作应拒绝，播完生命/占格/积分/阶段原样。
                    int topology=battle.Board.TopologyVersion, hp=0;
                    foreach(var enemy in battle.EnemyPieces){hp+=enemy.Hp;}
                    long score=battle.ChapterScore;
                    Require(RevGM.Execute("RevGame/Boss出场测试").Success,"GM重播Boss出场接受");
                    Require(!RevGM.Execute("RevGame/Boss出场测试").Success,"GM重复出场拒绝重入");
                    await Until(()=>!flow.IsBossIntroPlaying,14,"GM出场重播超时");
                    Require(flow.BossIntroStage=="completed"&&battle.Phase==GamePhase.Prepare,"GM出场完成仍在准备");
                    Require(RevGM.Execute("RevGame/Boss退场测试").Success,"GM预览Boss退场接受");
                    Require(!RevGM.Execute("RevGame/Boss出场测试").Success,"退场期间出场请求拒绝");
                    await Until(()=>!flow.IsBossVictoryPlaying,14,"GM退场预览超时");
                    int afterHp=0;foreach(var enemy in battle.EnemyPieces){afterHp+=enemy.Hp;}
                    Require(flow.BossVictoryStage=="completed"&&battle.Phase==GamePhase.Prepare
                        &&hp==afterHp&&battle.Board.TopologyVersion==topology&&battle.ChapterScore==score,"GM退场还原活棋，生命/占格/积分/阶段不变");
                    Require(!RevUI.IsOpen<SettlePanel>()&&!flow.IsStoryPlaying&&Time.timeScale==originalTime,"GM预览不发结算/剧情并恢复时间");
                    Require(RevGM.Execute("RevGame/Boss演出状态").Success,"GM演出只读查询有效");
                    bool placed=false;
                    foreach(var card in battle.Hand)
                    {
                        for(int y=0;y<4&&!placed;y++)
                        {for(int x=0;x<9&&!placed;x++){var pos=new BoardPos(x,y);if(battle.Board.CanFit(pos,1,GameSide.Player)){placed=battle.TryPlayCard(card.Id,pos);}}}
                        if(placed){break;}
                    }
                    Require(placed,"从真实手牌落下玩家棋子");
                    await Until(()=>battle.PreparationGranted,6,"准备增益没有恢复计时");
                    Require(RevLogic.Get<BattleLogic>().BeginBattle(),"正式开战");
                    Require(((BattleService)battle).DefeatBossForVictoryCheck(),"真实伤害夹具击杀Boss");
                    await Until(()=>flow.IsBossVictoryPlaying,3,"没有触发击败演出");
                    bool exitHeard=false;
                    bool death=false,wave=false,banner=false;frame=0;nextCapture=0;deadline=Time.realtimeSinceStartup+20;
                    while(flow.IsBossVictoryPlaying&&flow.BossVictoryStage!="story")
                    {
                        if(Time.realtimeSinceStartup>deadline){throw new InvalidOperationException("击败演出未推进："+flow.BossVictoryStage);}
                        exitHeard |= VoicePlaying("BossExit");
                        if (RevUI.Get<BossVictoryPanel>() != null)
                        {
                            Require(UnityEngine.Object.FindObjectOfType<GamePresentation>().IsBossCameraRestored,"退场弹幕显示时镜头已完全拉回");
                            VerifyBanner(RevUI.Get<BossVictoryPanel>().transform);
                        }
                        Require(flow.IsGameplayBlocked&&battle.Phase==GamePhase.Settle,"演出时真实结算且输入受锁");
                        Require(!RevUI.IsOpen<SettlePanel>(),"演出时结算面板没有提前弹出");
                        if(flow.BossVictoryStage=="death"){death=true;Require(Mathf.Abs(Time.timeScale-originalTime*.7f)<.0001f,"死亡特写只慢30%，真实速度70%");}
                        if(flow.BossVictoryStage=="wave"){wave=true;}
                        if(flow.BossVictoryStage=="banner")
                        {banner=true;var panel=RevUI.Get<BossVictoryPanel>();Require(panel!=null&&panel.Data.ChapterScore==battle.ChapterScore,"积分弹幕显示最终章积分");}
                        if(Time.realtimeSinceStartup>=nextCapture)
                        {ScreenCapture.CaptureScreenshot(Folder+"/Chapter"+chapter.ToString("D2")+"-Victory-"+(frame++).ToString("D2")+".png");nextCapture=Time.realtimeSinceStartup+.15f;}
                        await RevTask.Yield();
                    }
                    Require(exitHeard,"第"+chapter+"章退场声音由真实AudioSource播放");
                    Require(death&&wave&&banner,"击败慢动作/死亡/波浪/弹幕实际完整推进");
                    await Until(()=>flow.IsStoryPlaying&&flow.StoryId=="boss_after_"+chapter,5,"没有接入本章收尾剧情");
                    Require(!board.HasVictoryBossView&&Time.timeScale==originalTime,"剧情前Boss回池且恢复时间");
                    ScreenCapture.CaptureScreenshot(Folder+"/Chapter"+chapter.ToString("D2")+"-Story.png");
                    int clicks=flow.StoryContent.Find("boss_after_"+chapter).Lines.Length;
                    for(int line=0;line<clicks;line++)
                    {
                        await Until(()=>flow.AdvanceStory(),3,"剧情下一句未接受");await RevTask.Yield();
                    }
                    await Until(()=>!flow.IsBossVictoryPlaying&&RevUI.IsOpen<SettlePanel>(),4,"剧情结束后结算未开放");
                    Require(flow.BossVictoryStage=="completed"&&!flow.IsStoryPlaying&&Time.timeScale==originalTime,"序列收尾与UI交割完整");
                    Require(board.VictoryBossView==null&&battle.LastWinner==GameSide.Player,"真实胜负保留且死亡主将无残留");
                    Require(flow.ReturnHome(),"正常回大厅");
                    await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsTransitioning,20,"回大厅未完成");
                }
                // 取消边界：入场半途返大厅，必须恢复镜头/时间/面板并释放场景，不能继续闪出旧弹幕。
                Require(flow.EnterBossChapterForDevelopment(2),"取消场景进入Boss章");
                await Until(()=>flow.BossIntroStage=="reveal",35,"取消夹具未到显现阶段");
                Require(flow.ReturnHome(),"出生中途返回大厅接受");
                await Until(()=>flow.Screen==GameScreen.Home&&!flow.IsTransitioning,20,"取消后大厅未恢复");
                Require(!flow.IsBossIntroPlaying&&!flow.IsBossVictoryPlaying&&!RevUI.IsOpen<BossIntroPanel>()&&Time.timeScale==originalTime,"取消清入场锁/弹幕/时间");
                Require(errors==0,"本轮新增Unity Error/Exception为0");
                Status="passed";
            }
            catch(Exception error){Error=error.Message;Status="failed";File.WriteAllText(Folder+"/failure.txt",error.ToString());}
            finally
            {
                Application.logMessageReceived-=log;
                File.WriteAllText(Folder+"/assertions.txt",string.Join("\n",Assertions));
                File.WriteAllText(Folder+"/result.json",JsonUtility.ToJson(new Result{status=Status,error=Error,assertions=Assertions.Count,unityErrors=errors},true));
                if(Application.isPlaying&&flow.Screen==GameScreen.Battle&&!flow.IsTransitioning){flow.ReturnHome();}
            }
        }
        /// <summary>验证运行时实际实例，不用编辑器源Prefab替代真实AB的UI证据。</summary>
        private static void VerifyBanner(Transform panel)
        {
            Image background=panel.Find("banner").GetComponent<Image>();
            Color c=background.color;
            Require(background.sprite==null&&c.r==0&&c.g==0&&c.b==0&&c.a>.5f&&c.a<.85f,"真实AB弹幕使用半透明黑底");
        }
        /// <summary>声音句柄被接受不代表已经出声；检测实际播放中的AudioSource，按阶段逐帧累计证据。</summary>
        private static bool VoicePlaying(string clip)
        {
            foreach(AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            { if(source.isPlaying&&source.clip!=null&&source.clip.name==clip){return true;} }
            return false;
        }
        [Serializable]private sealed class Result{public string status,error;public int assertions,unityErrors;}
    }
}
