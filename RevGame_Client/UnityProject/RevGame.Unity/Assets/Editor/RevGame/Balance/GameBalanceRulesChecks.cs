using System;
using System.IO;
using RevGame.Data;
using RevGame.Services;
using Revolution;
using UnityEditor;

namespace RevGame.Editor
{
    /// <summary>离线规则回放与支援边界；独立服务不注册到游戏，不会改变已打开场景或生产存档。</summary>
    public static class GameBalanceRulesChecks
    {
        public static string Run()
        {
            if(EditorApplication.isPlaying){throw new InvalidOperationException("规则回放需要编辑态");}
            bool loaded=GameConfig.IsLoaded;
            RevLogLevel oldLevel=RevLog.MinLevel;
            int assertions=0;
            try
            {
                GameConfigTestSupport.Ensure();
                // 关闭纯夹具中的重复Info/状态诊断，错误仍然可见；不修改框架全局计时器/资源。
                RevLog.MinLevel=RevLogLevel.Error;
                BattleService.VerifyEmergencySupport((ok,message)=>{if(!ok){throw new InvalidOperationException(message);}assertions++;});
                string json=BattleService.VerifyBalanceSeeds();
                string folder=Path.GetFullPath("../../../.codex/work/balance-2026-10-28");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"multi-seed.json"),json);
                return "失误支援"+assertions+"条规则断言通过；三种子180回合真实服务回放结果已保存："+folder;
            }
            finally{RevLog.MinLevel=oldLevel;if(!loaded){GameConfig.Reset();}}
        }
    }
}
