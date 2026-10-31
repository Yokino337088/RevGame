using System;
using System.Collections.Generic;
using RevGame.Data;
using RevGame.Services;

namespace RevGame.Editor
{
    /// <summary>编辑态独立服务验收，不切场景；运行时配置恢复后才返回断言清单。</summary>
    public static class GameEnemyRangedChecks
    {
        [Serializable]
        private sealed class Result
        {
            public bool success;
            public int count;
            public string[] assertions;
        }

        public static object Run()
        {
            bool loaded = GameConfig.IsLoaded;
            var messages = new List<string>();
            try
            {
                GameConfigTestSupport.Ensure();
                BattleService.VerifyEnemyRangedRules((ok, message) =>
                {
                    if (!ok)
                    {
                        throw new InvalidOperationException(message);
                    }
                    messages.Add(message);
                });
                return new Result { success = true, count = messages.Count, assertions = messages.ToArray() };
            }
            finally
            {
                if (!loaded)
                {
                    GameConfig.Reset();
                }
            }
        }
    }
}
