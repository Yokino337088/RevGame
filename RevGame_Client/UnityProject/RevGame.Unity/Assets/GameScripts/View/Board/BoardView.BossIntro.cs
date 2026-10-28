using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>入场主将仍在存活字典，绝不摘掉占格；与死亡后独立保留的视图是不同生命周期。</summary>
        private PieceView _introBoss;
        public float BossIntroSeconds { get { return _introBoss == null ? 3.2f : _introBoss.BossDeathSeconds; } }
        public void BeginBossIntro(BossDefeatedInfo info)
        {
            if (!_pieces.TryGetValue(info.BossId,out PieceView view)) { throw new System.InvalidOperationException("入场Boss视图未就绪"); }
            _introBoss = view; view.BeginBossBirth();
        }
        public void SetBossIntroProgress(float progress) { if (_introBoss != null) { _introBoss.SetBossDeathProgress(1-Mathf.Clamp01(progress)); } }
        public void EndBossIntroVisual()
        {
            PieceView view = _introBoss; _introBoss = null;
            if (view != null) { view.FinishBossBirth(); }
            // 取消可能发生在序列取到主将之前，Prime已隐藏的视图也要还原；当前死亡主将已在独立列表。
            foreach (PieceView pending in _pieces.Values)
            { if (pending != null && pending.HasBossDeathVisual) { pending.FinishBossBirth(); } }
        }
        /// <summary>81格按棋盘坐标错开节拍，两轮柔和跳跃；根Collider始终不动，结尾强制归位。</summary>
        public void SetBossIntroDance(float progress)
        {
            if (progress <= 0 || progress >= 1) { ClearVictoryBoardWave(); return; }
            float envelope = Mathf.Sin(progress*Mathf.PI);
            foreach (BoardCellView cell in _cells)
            {
                float phase = progress * Mathf.PI * 4 + cell.Position.X*.48f + cell.Position.Y*.38f;
                float height = (.5f + .5f*Mathf.Sin(phase))*envelope;
                cell.SetVictoryWave(Mathf.Asin(Mathf.Clamp(height,0,.999f))/Mathf.PI);
            }
        }
        /// <summary>Boss开局首先隐藏主将，正式序列取到视图后再逆播。等待资源期间不闪出完整Boss。</summary>
        private void PrimeBossIntro(PieceView view)
        {
            if (!view.IsKingVisual || view.BossAppearanceChapter == 0) { return; }
            var flow = Revolution.RevServices.Get<RevGame.Flow.GameFlow>();
            if (flow != null && flow.IsBossIntroPlaying && flow.BossIntroStage == "waiting" && view.IsBossFrameTextureReady) { view.BeginBossBirth(); }
        }
    }
}
