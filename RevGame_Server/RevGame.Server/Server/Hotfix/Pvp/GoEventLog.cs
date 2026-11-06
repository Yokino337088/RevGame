using System.Text.Json;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>业务事件日志与终局审计；只观察已接受棋谱，不改变中转服务器的权限与裁判边界。</summary>
public static class GoEventLog
{
    public static string PlayerLabel(GoPlayerEntity? player)
    {
        // 玩家输入可能含ANSI控制字符；日志不允许昵称伪造颜色、换行或追加事件。
        return player == null ? "无" : new string(player.Nickname.Where(c => !char.IsControl(c)).Take(24).ToArray());
    }

    public static void Command(GoRoomEntity room, GoPlayerEntity player, GoRelayCommand command)
    {
        string action = ((GoAction)command.Kind).ToString();
        Log.Info($"[PVP/落子] 房间={room.RoomId} 玩家={PlayerLabel(player)} 方={(GoSide)command.Side} 序号={command.Sequence} 动作={action} 坐标=({command.X},{command.Y}) 用时毫秒={command.ElapsedMs} 指纹={command.ResultHash}");
        if (command.Kind == (int)GoAction.Pass && room.Commands.Count >= 2 && room.Commands[^2].Kind == (int)GoAction.Pass)
        {
            Log.Info($"[PVP/房间] 房间={room.RoomId} 事件=双方停一手/进入数子 棋盘未满也允许正常结束");
        }
        if (room.ResultLogged || (command.Kind != (int)GoAction.ConfirmScore && command.Kind != (int)GoAction.Resign && command.Kind != (int)GoAction.Timeout && command.Kind != (int)GoAction.Disconnect))
        {
            return;
        }
        try
        {
            using var rules = JsonDocument.Parse(room.Black!.RulesJson);
            var core = new GoCore(rules.RootElement.GetProperty("komiHalfPoints").GetInt32());
            foreach (GoRelayCommand move in room.Commands)
            {
                if (core.StateHash.ToString() != move.PreviousHash || !core.TryApply((GoAction)move.Kind, (GoSide)move.Side, move.X, move.Y, out _) || core.StateHash.ToString() != move.ResultHash)
                {
                    Log.Warning($"[PVP/拒绝] 房间={room.RoomId} 事件=终局日志复盘不一致，不输出未经验证的胜负 序号={move.Sequence}");
                    return;
                }
            }
            if (core.Phase != GoPhase.Finished)
            {
                return;
            }
            room.ResultLogged = true;
            string winner = core.Winner == GoSide.Empty ? "和棋" : core.Winner == GoSide.Black ? PlayerLabel(room.Black) : PlayerLabel(room.White);
            string loser = core.Winner == GoSide.Empty ? "无" : core.Winner == GoSide.Black ? PlayerLabel(room.White) : PlayerLabel(room.Black);
            Log.Info($"[PVP/结果] 房间={room.RoomId} 事件=对局结束 同源规则日志复盘 胜方={winner} 负方={loser} 胜方执子={core.Winner} 原因={core.EndReason} 黑白差半目={core.Score.DifferenceHalfPoints} 总序号={core.Sequence}");
        }
        catch (Exception)
        {
            Log.Warning($"[PVP/拒绝] 房间={room.RoomId} 事件=终局审计配置无效，不输出未经验证的胜负");
        }
    }
}
