using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>致死后主将不在存活字典，独立保留一份演出视图，不参与占格/攻击/HP刷新。</summary>
        private PieceView _victoryBoss;
        /// <summary>擒王模型仍有生命；记录本次主将Id直到新回合，防演出回池后血量刷新又生成旧主将。</summary>
        private int _victoryBossId;
        /// <summary>GM退场预览借活棋，结束归还原视图；正式死亡才从池归还，不能把预览变成真实击杀。</summary>
        private bool _victoryBossPreview;
        public PieceView VictoryBossView { get { return _victoryBoss; } }
        public bool HasVictoryBossView { get { return _victoryBoss != null; } }

        private bool HoldVictoryBoss(PieceView view)
        {
            if (!view.IsKingVisual || view.BossAppearanceChapter == 0) { return false; }
            ReleaseVictoryBoss();
            _victoryBoss = view;
            _victoryBossId = view.PieceId;
            view.HoldBossDefeat();
            return true;
        }
        /// <summary>擒王时主将可能仍存活，将视图摘出但不改模型；击杀时使用先前独立保留的同一视图。</summary>
        public void PrepareVictoryBoss(BossDefeatedInfo info)
        {
            if (_victoryBoss == null && _pieces.TryGetValue(info.BossId, out PieceView view))
            {
                _pieces.Remove(info.BossId); _lastHp.Remove(info.BossId);
                HoldVictoryBoss(view);
            }
            if (_victoryBoss == null) { throw new System.InvalidOperationException("主将死亡演出视图不存在"); }
        }
        public void BorrowVictoryBossForPreview(BossDefeatedInfo info)
        {
            if (_victoryBoss != null || !_pieces.TryGetValue(info.BossId,out PieceView view))
            { throw new System.InvalidOperationException("主将视图未就绪或已有演出持有"); }
            _victoryBoss = view; _victoryBossPreview = true;
            view.HoldBossDefeat();
        }
        public void BeginVictoryDeath(BossDefeatedInfo info) { PrepareVictoryBoss(info); _victoryBoss.BeginBossDeath(); }
        public void SetVictoryDeathProgress(float value) { if (_victoryBoss != null) { _victoryBoss.SetBossDeathProgress(value); } }
        public float VictoryDeathSeconds { get { return _victoryBoss == null ? 3.2f : _victoryBoss.BossDeathSeconds; } }
        public void ReleaseVictoryBoss()
        {
            PieceView view = _victoryBoss; _victoryBoss = null;
            bool preview = _victoryBossPreview; _victoryBossPreview = false;
            if (view != null)
            {
                if (preview) { view.FinishBossBirth(); }
                else { RevPool.Return(view, 0); }
            }
        }

        /// <summary>只移动格子渲染器子层，物理格子与棋盘模型保持原位置；每格相位按到Boss的距离推进。</summary>
        public void SetVictoryBoardWave(float progress, Vector3 center)
        {
            foreach (BoardCellView cell in _cells)
            {
                float distance = Vector2.Distance(cell.transform.position, center) / Consts.CellSize;
                float local = Mathf.Clamp01((progress * 1.8f - distance * .075f) / .8f);
                cell.SetVictoryWave(local);
            }
        }
        public void ClearVictoryBoardWave() { foreach (BoardCellView cell in _cells) { if (cell != null) { cell.SetVictoryWave(1); } } }
        /// <summary>离场先使所有动画续体失效，清视图/波浪，再释放Boss图集。</summary>
        private void CancelVictoryViews()
        { ReleaseVictoryBoss(); _victoryBossId = 0; ClearVictoryBoardWave(); }
    }
}
