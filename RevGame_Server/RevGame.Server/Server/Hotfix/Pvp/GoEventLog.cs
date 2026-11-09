using RevGame.Pvp;

namespace Fantasy;

/// <summary>记录已接受的业务事件；胜负与差目直接取自 Battle 对局节点。</summary>
public static class GoEventLog
{
    /// <summary>选择已经净化的日志昵称，空座位使用统一占位文案。</summary>
    public static string PlayerLabel(GoPlayerEntity? player)
    {
        // 玩家输入可能含ANSI控制字符；日志不允许昵称伪造颜色、换行或追加事件。
        return player?.LogNickname ?? "无";
    }

    /// <summary>记录一条已提交到大厅棋谱的操作，序号和指纹便于与客户端日志对账。</summary>
    public static void Command(GoRoomEntity room, GoPlayerEntity player, GoRelayCommand command)
    {
        string action = ((GoAction)command.Kind).ToString();
        Log.Info($"[PVP/落子] 房间={room.RoomId} 玩家={PlayerLabel(player)} 方={(GoSide)command.Side} 序号={command.Sequence} 动作={action} 坐标=({command.X},{command.Y}) 用时毫秒={command.ElapsedMs} 指纹={command.ResultHash}");
        // 当前操作已经追加，倒数第二项是前一手。连续两手停手时记录阶段切换。
        if (command.Kind == (int)GoAction.Pass && room.Commands.Count >= 2 && room.Commands[^2].Kind == (int)GoAction.Pass)
        {
            Log.Info($"[PVP/房间] 房间={room.RoomId} 事件=双方停一手/进入数子 棋盘未满也允许正常结束");
        }
    }

    /// <summary>把 Battle 已裁定的胜负、终局原因与半目分差写入日志。</summary>
    public static void Result(GoRoomEntity room, GoBattleOutcome result)
    {
        // 把协议数值还原为执子方，并按房间座位取对应昵称。
        var side = (GoSide)result.Winner;
        string winner = side == GoSide.Empty ? "和棋" : PlayerLabel(side == GoSide.Black ? room.Black : room.White);
        string loser = side == GoSide.Empty ? "无" : PlayerLabel(side == GoSide.Black ? room.White : room.Black);
        Log.Info($"[PVP/结果] 房间={room.RoomId} 事件=对局结束 节点={room.WorkerAddress} 胜方={winner} 负方={loser} 胜方执子={side} 原因={(GoEndReason)result.EndReason} 黑白差半目={result.DifferenceHalfPoints} 总序号={room.Commands.Count}");
    }
}
