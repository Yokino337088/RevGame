using System;
using Revolution.Editor;
using UnityEngine;
namespace RevGame.Editor
{
    /// <summary>固定项目检查白名单，无任意源码/类型/方法输入；注册与真实业务分离。</summary>
    public static class GameBossHealingAutomation
    {
        [Serializable] private sealed class Options{public int chapter=1;}
        [Serializable] private sealed class Result{public string text;}
        [RevAutomationAction("game.boss-healing.begin",Environment="play",Keys=new[]{"chapter"},Description="隔离Boss多人风暴、治疗轮换、图层与回池检查")]
        public static object Begin(RevAutomationRequest request)
        {
            var options=JsonUtility.FromJson<Options>(request.payloadJson)??new Options();
            return Invoke("Begin",new object[]{options.chapter});
        }
        [RevAutomationAction("game.boss-healing.state",Environment="play",Keys=new string[0],Description="读取Boss治疗Play状态与断言")]
        public static object State(RevAutomationRequest request){return Invoke("State",null);}
        [RevAutomationAction("game.boss-healing.cancel",Environment="play",Keys=new string[0],Description="取消并执行检查器回首页清理")]
        public static object Cancel(RevAutomationRequest request){return Invoke("Cancel",null);}
        private static object Invoke(string method,object[] args)
        {
            try{return new Result{text=(string)Type.GetType("RevGame.Editor.GameBossHealingPlayChecks, Assembly-CSharp-Editor",true).GetMethod(method).Invoke(null,args)};}
            catch(System.Reflection.TargetInvocationException e){throw e.InnerException??e;}
        }
    }
}
