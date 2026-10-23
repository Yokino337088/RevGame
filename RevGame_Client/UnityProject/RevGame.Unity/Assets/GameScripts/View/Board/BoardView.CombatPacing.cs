// 棋盘节奏表现：只监听事实，区域格子用DOTween起伏，炸弹复用框架池化的技能特效零件。
using RevGame.Core;
using Revolution;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        public int ActiveHazardCellCount
        {
            get
            {
                int count = 0;
                foreach (BoardCellView cell in _cells) { if (cell != null && cell.HasHazardAnimation) { count++; } }
                return count;
            }
        }
        private void ListenCombatPacing()
        {
            RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning, OnRegionWarning, owner: this);
            RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombWarning, OnEnergyBombWarning, owner: this);
            RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombImpact, OnEnergyBombImpact, owner: this);
        }
        private void OnRegionWarning(RegionPulseInfo info)
        {
            if (_released) { return; }
            foreach (BoardCellView cell in _cells)
            {
                int index = info.CellIndex(cell.Position);
                if (index >= 0) { cell.PlayHazardWave(info.WarningSeconds + index * info.CellDelaySeconds); }
            }
        }
        private void OnEnergyBombWarning(EnergyBombInfo info) { SpawnEnergyBomb(info, true); }
        private void OnEnergyBombImpact(EnergyBombInfo info) { SpawnEnergyBomb(info, false); }
        private void SpawnEnergyBomb(EnergyBombInfo info, bool warning)
        {
            int generation = _effectGeneration;
            RevPool.GetAsync<BossSkillFxView>(fx =>
            {
                if (fx == null) { RevLog.Warn("能量炸弹特效加载失败", "RevGame"); return; }
                if (_released || this == null || generation != _effectGeneration) { RevPool.Return(fx, 0); return; }
                _bossFx.Add(fx);
                fx.PlayEnergyBomb(info, warning, OnBossFxFinished);
            }, _viewRoot);
        }
        private void ClearCombatPacing()
        {
            foreach (BoardCellView cell in _cells) { if (cell != null) { cell.CancelHazardWave(); } }
        }
    }
}
