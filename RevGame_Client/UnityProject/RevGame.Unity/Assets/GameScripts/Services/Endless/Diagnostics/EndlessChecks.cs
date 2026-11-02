#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
namespace RevGame.Endless
{
    /// <summary>测试使用独立服务和内存存档；生产局不会被直接改状态来制造通过。</summary>
    public static class EndlessChecks
    {
        public static object Run()
        {
            var checks = new List<string>();
            using (var service = new EndlessService())
            {
                service.Begin(0, 0, 1101, true);
                int roster = 0;
                foreach (Revolution.EndlessFormation row in Revolution.RevDataTableManager.Get<Revolution.EndlessFormationTable>().All)
                {
                    roster += row.Formation == 0 && row.Stone == 0 ? 1 : 0;
                }
                Require(service.PlayerCount() == roster, "起手主力与开局棋阵表一致", checks);
                Require(service.SelectCard(0), "选择真实手牌", checks);
                int hand = service.Run.hand.Count;
                int version = service.Board.TopologyVersion;
                Require(!service.ClickCell(4, 4) && hand == service.Run.hand.Count && version == service.Board.TopologyVersion, "棋心占地拒绝且不扣牌", checks);
                Require(service.ClickCell(1, 3) && service.Run.hand.Count == hand - 1, "合法部署扣牌且占格", checks);
                Require(service.StartWave(), "正式开战", checks);
                service.Tick(0.1f);
                float seconds = service.Run.seconds;
                Require(service.Pause(), "正式暂停", checks);
                service.Tick(1);
                Require(service.Run.seconds == seconds, "暂停冻结战斗时钟", checks);
                service.Pause();
                Require(service.Rearrange(), "首次改阵接受", checks);
                Require(!service.Rearrange(), "同一分钟改阵重复拒绝", checks);
                uint random = service.Run.random;
                // 部署第三枚同名一星会触发自动三合一，场上数量不再是固定的 4；只核对“存前多少、恢复后还是多少”。
                int players = service.PlayerCount();
                Require(service.Save(), "完整快照保存", checks);
                Require(service.Restore(true) && service.Run.random == random && service.PlayerCount() == players, "恢复随机源和单位占格", checks);
                service.Finish();
                long score = service.Run.score;
                service.Finish();
                Require(service.Run.score == score && service.Run.settled, "重复结算不重复收益", checks);
                Require(service.Occupied(GameSide.Player) <= 81, "玩家占地有界", checks);
            }
            return new Dictionary<string, object>
            {
                ["passed"] = checks.Count, ["checks"] = checks, ["success"] = true
            };
        }
        private static void Require(bool value, string description, List<string> checks)
        {
            if (!value)
            {
                throw new InvalidOperationException("无尽规则失败：" + description);
            }
            checks.Add(description);
        }
    }
}
#endif
