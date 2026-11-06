using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;
using RevGame.Pvp.Networking;

namespace Fantasy
{
    /// <summary>SourceGenerator 自动注册的推送入口；只转交快照，不在 Handler 中修改棋盘或 UI。</summary>
    // 当前安装的 Fantasy 2026.1.2003 两端统一使用 Message<T>；旧技能中的双泛型签名不适用。
    public sealed class G2C_GoStateHandler : Message<G2C_GoState>
    {
        protected override async FTask Run(Session session, G2C_GoState message)
        {
            FantasyGoTransport.Active?.Receive(session, message.State);
            await FTask.CompletedTask;
        }
    }
}
