using System;
using Revolution.Editor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>只接固定数值测试类；不允许任意反射执行，固定种子与策略不改变生产战斗属性。</summary>
    public static class GameBalanceAutomation
    {
        [Serializable]private sealed class Options{public string label="baseline";public int seed=1028,first=1,last=6,maxCards=6,lastRound=10;public float speed=4;public bool careless,continuous;}
        [RevAutomationAction("game.balance.begin",Environment="play",Keys=new[]{"label","seed","careless","first","last","continuous","speed","maxCards","lastRound"},Description="隔离Play正常策略章回数值测试；不加属性或强制胜利")]
        public static object Begin(RevAutomationRequest request)
        {
            Options o=JsonUtility.FromJson<Options>(request.payloadJson)??new Options();
            Type t=Type.GetType("RevGame.Editor.GameBalanceCampaignChecks, Assembly-CSharp-Editor",true);
            return new Result{text=(string)t.GetMethod("Begin").Invoke(null,new object[]{o.label,o.seed,o.careless,o.first,o.last,o.continuous,o.speed,o.maxCards,o.lastRound})};
        }
        [RevAutomationAction("game.balance.state",Environment="play",Keys=new string[0],Description="只读章回数值测试进度和输出目录")]
        public static object State(RevAutomationRequest request)
        {return new Result{text=(string)Type.GetType("RevGame.Editor.GameBalanceCampaignChecks, Assembly-CSharp-Editor",true).GetMethod("State").Invoke(null,null)};}
        [RevAutomationAction("game.balance.rules",Environment="edit",Keys=new string[0],Description="失误支援边界与三种子180回合独立服务回放；不切场景")]
        public static object Rules(RevAutomationRequest request)
        {
            try{return new Result{text=(string)Type.GetType("RevGame.Editor.GameBalanceRulesChecks, Assembly-CSharp-Editor",true).GetMethod("Run").Invoke(null,null)};}
            catch(System.Reflection.TargetInvocationException error){throw error.InnerException??error;}
        }
        [Serializable]private sealed class Result{public string text;}
    }
}
