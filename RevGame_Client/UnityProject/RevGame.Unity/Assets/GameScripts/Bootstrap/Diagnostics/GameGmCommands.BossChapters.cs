// 六章Boss手动/AI共用的受控开发GM；只提交真实流程，ready查询完全只读，不通过反复动作轮询造局。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        private static readonly string[] BossChapterLabels = { "第一章", "第二章", "第三章", "第四章", "第五章", "第六章" };

        /// <summary>RegisterAll唯一调用，全部名称归同一注册账本；正式发布不编译这些入口。</summary>
        private static void RegisterBossChapterCommands()
        {
            for (int index = 0; index < BossChapterLabels.Length; index++)
            {
                int chapter = index + 1; // 每次捕获独立局部变量，不能让六条委托都取最后一章。
                RegisterCommand("RevGame/" + BossChapterLabels[index] + "Boss关卡", "直达本章真实Boss准备回合，重开战役、清旧强化；accepted后等待Boss关卡就绪查询", args =>
                {
                    if (args.Count != 0) { throw new RevGMUsageException("本指令不需要参数"); }
                    return EnterBossChapter(chapter);
                });
            }
            RegisterCommand("RevGame/进入Boss关卡", "直达章号1～6的真实Boss准备回合，不自动落牌/开战、不写通关存档", args =>
            {
                if (args.Count != 1) { throw new RevGMUsageException("用法：RevGame/进入Boss关卡 章节号（1～6）"); }
                return EnterBossChapter(args.Int(0));
            }, RevGMArg.Int("章节", 1));
            RegisterCommand("RevGame/Boss关卡就绪", "只读目标章节：loading/ready/not-target；加载错误明确失败，ready不表示打赢或已开战", args =>
            {
                if (args.Count != 1) { throw new RevGMUsageException("用法：RevGame/Boss关卡就绪 章节号（1～6）"); }
                int chapter = args.Int(0);
                ValidateBossChapter(chapter);
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsBossDevelopmentEntering || flow.IsTransitioning) { return "loading"; }
                if (!string.IsNullOrEmpty(flow.LastError)) { throw new RevGMUsageException(flow.LastError); }
                IBattleService battle = RevServices.GetRequired<IBattleService>();
                if (flow.Screen != GameScreen.Battle || RevScene.CurrentName != GameFlow.BattleSceneName
                    || battle.Chapter != chapter || battle.Round != Balance.BossRoundIndex || !battle.IsBossRound) { return "not-target"; }
                BoardView board = Object.FindObjectOfType<BoardView>();
                if (board == null || !board.IsReady || board.BossArtChapter != chapter) { return "loading"; }
                return board.AreBossViewsReady(chapter) ? "ready" : "loading";
            }, RevGMArg.Int("章节", 1));
        }

        private static string EnterBossChapter(int chapter)
        {
            ValidateBossChapter(chapter);
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            if (!flow.EnterBossChapterForDevelopment(chapter)) { throw new RevGMUsageException(flow.LastError); }
            return "accepted";
        }
        private static void ValidateBossChapter(int chapter)
        {
            if (chapter < 1 || chapter > Consts.ChapterCount) { throw new RevGMUsageException("Boss章节只允许1～" + Consts.ChapterCount); }
        }
    }
}
#endif
