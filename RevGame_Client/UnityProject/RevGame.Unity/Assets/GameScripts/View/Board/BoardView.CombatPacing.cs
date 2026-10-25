// 棋盘节奏表现：只监听事实，区域格子用DOTween起伏，炸弹复用框架池化的技能特效零件。
using RevGame.Core;
using Revolution;
using UnityEngine;

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
                if (index < 0) { continue; }
                int x = index % info.Size;
                int y = index / info.Size;
                // 只画外轮廓亮边，九格连成一整片侵蚀地面；内部仍保留暗菱形纹理。
                Vector4 edges = new Vector4(x == 0 ? 1 : 0,x == info.Size-1 ? 1 : 0,y == 0 ? 1 : 0,y == info.Size-1 ? 1 : 0);
                float delay = info.WarningSeconds + index * info.CellDelaySeconds;
                cell.PlayHazardWave(delay, delay + .85f, edges);
            }
        }
        private void OnEnergyBombWarning(EnergyBombInfo info) { SpawnEnergyBomb(info, true); }
        private void OnEnergyBombImpact(EnergyBombInfo info) { SpawnEnergyBomb(info, false); }
        private void SpawnEnergyBomb(EnergyBombInfo info, bool warning)
        {
            int generation = _effectGeneration;
            GamePoolRequests.GetAsync<BossSkillFxView>(fx =>
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
