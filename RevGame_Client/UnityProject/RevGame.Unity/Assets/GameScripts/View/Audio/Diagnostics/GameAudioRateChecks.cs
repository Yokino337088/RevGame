#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;

namespace RevGame.View
{
    /// <summary>固定现实时间序列检查生产门禁；不播放音频/改业务，不把允许请求数冒充实播声音。</summary>
    public static class GameAudioRateChecks
    {
        /// <summary>测试同帧、边界、拒绝不延长、Reset和玩家/帮手分离；失败抛出明确原因。</summary>
        public static object Run()
        {
            var player = new GameAudioRateLimit();
            var helper = new GameAudioRateLimit();
            if (!player.Try(0, .1f) || player.Try(0, .1f) || player.Try(.099f, .1f) || !player.Try(.1f, .1f))
            {
                throw new InvalidOperationException("玩家攻击门禁同帧/边界或拒绝不延长契约失败");
            }
            int playerCount = 0, helperCount = 0;
            player.Reset();
            helper.Reset();
            for (int i = 0; i < 1000; i++)
            {
                float now = i*.01f;
                playerCount += player.Try(now, GameAudioRateLimit.PlayerSeconds) ? 1 : 0;
                helperCount += helper.Try(now, GameAudioRateLimit.HelperSeconds) ? 1 : 0;
            }
            if (playerCount <= helperCount || playerCount > 100 || helperCount > 23)
            {
                throw new InvalidOperationException("玩家比帮手更密集且有上限的声音门禁失败");
            }
            return new Dictionary<string, object> { ["success"] = true, ["requests"] = 1000, ["seconds"] = 10, ["playerAccepted"] = playerCount, ["helperAccepted"] = helperCount, ["playerSuppressed"] = player.Suppressed, ["helperSuppressed"] = helper.Suppressed };
        }
    }
}
#endif
