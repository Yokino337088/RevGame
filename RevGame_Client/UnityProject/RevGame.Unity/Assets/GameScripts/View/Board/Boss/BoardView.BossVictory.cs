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
        public PieceView VictoryBossView
        {
            get
            {
                return _victoryBoss;
            }
        }
        public bool HasVictoryBossView
        {
            get
            {
                return _victoryBoss != null;
            }
        }
        private bool HoldVictoryBoss(PieceView view)
        {
            if (!view.IsKingVisual || view.BossAppearanceChapter == 0)
            {
                return false;
            }
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
                _pieces.Remove(info.BossId);
                _lastHp.Remove(info.BossId);
                HoldVictoryBoss(view);
            }
            if (_victoryBoss == null)
            {
                throw new System.InvalidOperationException("主将死亡演出视图不存在");
            }
        }
        public void BorrowVictoryBossForPreview(BossDefeatedInfo info)
        {
            if (_victoryBoss != null || !_pieces.TryGetValue(info.BossId, out PieceView view))
            {
                throw new System.InvalidOperationException("主将视图未就绪或已有演出持有");
            }
            _victoryBoss = view;
            _victoryBossPreview = true;
            view.HoldBossDefeat();
        }
        public void BeginVictoryDeath(BossDefeatedInfo info)
        {
            PrepareVictoryBoss(info);
            _victoryBoss.BeginBossDeath();
        }
        public void SetVictoryDeathProgress(float value)
        {
            if (_victoryBoss != null)
            {
                _victoryBoss.SetBossDeathProgress(value);
            }
        }
        public float VictoryDeathSeconds
        {
            get
            {
                return _victoryBoss == null ? 3.2f : _victoryBoss.BossDeathSeconds;
            }
        }
        public void ReleaseVictoryBoss()
        {
            PieceView view = _victoryBoss;
            _victoryBoss = null;
            bool preview = _victoryBossPreview;
            _victoryBossPreview = false;

            if (view != null)
            {
                if (preview)
                {
                    view.FinishBossBirth();
                }
                else
                {
                    RevPool.Return(view, 0);
                }
            }
        }
        /// <summary>胜利波移动可见格层。先撤下静止深度，再按到 Boss 的距离驱动视觉相位。</summary>
        public void SetVictoryBoardWave(float progress, Vector3 center)
        {
            _backgroundDepthSuspended = true;
            SetBackgroundDepthAttached(false);
            foreach (BoardCellView cell in _cells)
            {
                float distance = Vector2.Distance(cell.transform.position, center) / Consts.CellSize;
                float local = Mathf.Clamp01((progress * 1.8f - distance * .075f) / .8f);
                cell.SetVictoryWave(local);
            }
        }
        /// <summary>格层回到静止位置后，恢复背景的深度预通道；已退场的棋盘继续保持关闭。</summary>
        public void ClearVictoryBoardWave()
        {
            foreach (BoardCellView cell in _cells)
            {
                if (cell != null)
                {
                    cell.SetVictoryWave(1);
                }
            }
            _backgroundDepthSuspended = false;
            SetBackgroundDepthAttached(!_released);
        }
        /// <summary>离场先使所有动画续体失效，清视图/波浪，再释放Boss图集。</summary>
        private void CancelVictoryViews()
        {
            ReleaseVictoryBoss();
            _victoryBossId = 0;
            ClearVictoryBoardWave();
        }
    }
}
