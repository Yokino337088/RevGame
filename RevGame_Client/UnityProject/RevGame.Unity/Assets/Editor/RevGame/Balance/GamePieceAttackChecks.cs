using System;
using System.Collections.Generic;
using RevGame.Data;
using RevGame.Services;

namespace RevGame.Editor
{
    /// <summary>编辑态独立服务验收入口；只临时装配配置，finally恢复配置生命周期，不操作用户存档与场景。</summary>
    public static class GamePieceAttackChecks
    {
        public static object Run()
        {
            bool loaded = GameConfig.IsLoaded;
            var assertions = new List<string>();
            try
            {
                GameConfigTestSupport.Ensure();
                string summary = BattleService.VerifyPieceAttackRules((ok, message) =>
                {
                    if (!ok)
                    {
                        throw new InvalidOperationException(message);
                    }
                    assertions.Add(message);
                });
                return new Result { success = true, summary = summary, assertions = assertions.ToArray() };
            }
            finally
            {
                if (!loaded)
                {
                    GameConfig.Reset();
                }
            }
        }

        /// <summary>序列化结果只包含已执行断言；自动化调用成功不替代success和断言内容。</summary>
        [Serializable]
        private sealed class Result
        {
            public bool success;
            public string summary;
            public string[] assertions;
        }
    }
}
