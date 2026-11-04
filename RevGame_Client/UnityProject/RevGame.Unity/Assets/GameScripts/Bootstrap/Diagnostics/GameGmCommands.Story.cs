#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 剧情窄GM入口：复用GameFlow、StoryPanel与真实玩家动作，集中注册/注销仍由GameGmCommands账本负责。
using System.Collections.Generic;
using RevGame.Flow;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        /// <summary>登记对白查询/动作和失败检查；配置ID来自StoryBook，GM不直接写行号或存档完成标记。</summary>
        private static void RegisterStoryCommands()
        {
            RegisterCommand("剧情/篇章清单", "只读当前StoryBook所有篇章ID、标题和背景章节", args =>
            {
                var list = new System.Text.StringBuilder();
                foreach (RevGame.Data.StoryBook.Episode episode in RevServices.GetRequired<GameFlow>().StoryContent.Episodes)
                {
                    list.Append(episode.Id).Append(" | ").Append(episode.Title).Append(" | 背景=").Append(episode.BackgroundChapter).Append(" | 句数=").Append(episode.Lines.Length).AppendLine();
                }
                return list.ToString();
            });
            RegisterCommand("剧情/重播开场", "正式开场重播，不改首次存档或启动实操教学；accepted表示接受", args =>
            {
                return RequireStoryResult(RevServices.GetRequired<GameFlow>().ReplayOpeningStory());
            });
            RegisterCommand("剧情/播放篇章", "隔离测试会话播放指定StoryBook篇章，复用正式流程；背景必须与当前场景对应", args =>
            {
                return RequireStoryResult(RevServices.GetRequired<GameFlow>().PreviewStoryForTest(args.Str(0, "opening")));
            }, RevGMArg.Str("篇章ID", "opening"));
            RegisterCommand("剧情/下一句", "与剧情点击共用入口，末句正常结束；未播放时明确拒绝", args =>
            {
                return RequireStoryResult(RevServices.GetRequired<GameFlow>().AdvanceStory());
            });
            RegisterCommand("剧情/跳过", "与右上角跳过按钮共用正式收尾，解除输入锁；不伪造棋盘实操完成", args =>
            {
                return RequireStoryResult(RevServices.GetRequired<GameFlow>().SkipStory());
            });
            RegisterCommand("剧情/状态", "只读当前篇章、行号、门禁、立绘/背景就绪与布局", args =>
            {
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                string state = "playing=" + flow.IsStoryPlaying + ",id=" + flow.StoryId + ",line=" + flow.StoryLine + ",blocked=" + flow.IsGameplayBlocked;
                var details = new System.Text.StringBuilder(state);
                // 读取真实面板落屏结果，不能只回报流程请求已接受就声称立绘和背景就绪。
                foreach (var pair in (Dictionary<string, object>)StoryPanel.YayaSnapshot())
                {
                    details.AppendLine().Append(pair.Key).Append('=').Append(pair.Value);
                }
                details.AppendLine().Append("error=").Append(flow.LastError);
                return details.ToString();
            });
            RegisterCommand("大厅/场景状态", "只读StartScene小屋、鸦鸦Renderer/Animator与UI旧背景残留", args =>
            {
                LobbySceneView lobby = UnityEngine.Object.FindObjectOfType<LobbySceneView>();
                HomePanel home = RevUI.Get<HomePanel>();
                if (lobby == null)
                {
                    throw new RevGMUsageException("当前场景没有大厅画面");
                }
                return "scene=" + lobby.gameObject.scene.name + ",ready=" + lobby.Ready + ",oldUIBackground=" + (home != null && home.transform.Find("screenBackground") != null);
            });
        }
        /// <summary>统一拒绝出口，不能把false当工具成功；具体资源/流程失败仍可查GameFlow.LastError。</summary>
        private static string RequireStoryResult(bool accepted)
        {
            if (!accepted)
            {
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                string reason = string.IsNullOrEmpty(flow.LastError) ? "剧情未就绪、未播放或测试环境/场景不匹配" : flow.LastError;
                throw new RevGMUsageException(reason);
            }
            return "accepted；请查询剧情/状态或story.state等待可见与收尾";
        }
    }
}
#endif
