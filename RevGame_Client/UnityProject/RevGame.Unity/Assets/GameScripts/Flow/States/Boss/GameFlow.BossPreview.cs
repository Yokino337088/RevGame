#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Core;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>当前活主将的出场重播。复用正式入场序列，阶段/生命/占格/积分不变，播放结束恢复准备或战斗。</summary>
        public bool ReplayBossIntroForDevelopment()
        {
            BossDefeatedInfo info = GetBossPreviewInfo();

            if (info == null)
            {
                return false;
            }
            LastError = string.Empty;
            OnBossPrepare(info.Round);
            return IsBossIntroPlaying;
        }
        /// <summary>借当前活主将预览退场。只播特写、死亡、波浪、弹幕；finally还回活棋，不假造胜负/奖励。</summary>
        public bool PreviewBossExitForDevelopment()
        {
            BossDefeatedInfo info = GetBossPreviewInfo();

            if (info == null)
            {
                return false;
            }
            _victoryPreview = true;
            StartVictorySequence(info);
            return IsBossVictoryPlaying;
        }
        /// <summary>GM拒绝路径先验证再触碰视图；不能在过场、剧情或已有演出期间抢同一主将/相机。</summary>
        private BossDefeatedInfo GetBossPreviewInfo()
        {
            if (_disposed || IsGameplayBlocked || Screen != GameScreen.Battle)
            {
                LastError = "请等待过场/剧情/演出结束，并进入Boss回合";
                return null;
            }
            var battle = RevServices.GetRequired<IBattleService>();
            var board = Object.FindObjectOfType<BoardView>();

            if (!battle.IsBossRound || (battle.Phase != GamePhase.Prepare && battle.Phase != GamePhase.Battle)
                || board == null || !board.IsReady || _activePresentation == null)
            {
                LastError = "出退场测试需要已就绪的Boss准备或战斗回合";
                return null;
            }
            foreach (var king in battle.EnemyPieces)
            {
                if (king.IsKing && king.IsAlive)
                {
                    return new BossDefeatedInfo(battle.Chapter, battle.Round, king.Id, king.Pos, king.Size, battle.ChapterScore, false);
                }
            }
            LastError = "当前没有存活主将，请重新进入Boss关卡";
            return null;
        }
    }
}
#endif
