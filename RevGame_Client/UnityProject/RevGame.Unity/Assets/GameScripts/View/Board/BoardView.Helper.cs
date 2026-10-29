using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>帮手表现归BoardView战斗生命周期所有，异步回调带代次，退场后不会创建幽灵演员或弹道。</summary>
    public sealed partial class BoardView
    {
        private IBattleService Battle => RevServices.GetRequired<IBattleService>();
        private HelperView _helperView;
        /// <summary>持续保护只拥有一条治疗射线；目标身份由服务决定，视图不推算存活数或治疗量。</summary>
        private HelperHealingView _helperHealing;
        private bool _helperHealingLoading;
        private int _helperHealingGeneration;
        /// <summary>只租用共享棋子图集，四帧在一次加载期间复用；最后收走视图后才归还租约。</summary>
        private GameSpriteAtlasService.ScopedAtlasLease _helperAtlas;
        private GameSpriteAtlasService.ScopedAtlasLease _helperStormAtlas;
        private bool _helperLoading;
        // 常驻刷新与阶段短效独立owner；棋盘ClearRoundEffects取消本组件计时器时不能停掉帮手装配。
        private readonly object _helperRefreshOwner = new object(), _helperAttackOwner = new object();
        private int _helperAttackGeneration;
        private readonly HashSet<HelperAttackView> _helperAttacks = new HashSet<HelperAttackView>();
        private Material _helperProjectile, _helperBeam;
        private int _helperVisualChapter, _helperVisualGeneration, _helperPendingAttacks;
        // 表现硬预算，不影响实际攻击数量与扣血；同屏最多40个帮手弹道（含预约），忙时只合并视觉。
        public int HelperAttackVisualCount => _helperAttacks.Count;
        public bool HelperHealingVisible => _helperHealing != null && _helperHealing.IsConnected;
        public int HelperHealingTargetId => _helperHealing == null ? 0 : _helperHealing.TargetId;
        public bool HelperVisualReady => _helperView != null && _helperView.FramesReady && _helperProjectile != null && _helperBeam != null;
        /// <summary>开发验收定位加载/帧/材质失败，查询只读，不触发重新加载。</summary>
        public string HelperVisualStatus => "view=" + (_helperView != null) + ",frames=" + (_helperView != null && _helperView.FramesReady)
            + ",projectile=" + (_helperProjectile != null) + ",beam=" + (_helperBeam != null) + ",loading=" + _helperLoading
            + ",chapter=" + _helperVisualChapter + ",released=" + _released + ",activeAttacks=" + _helperAttacks.Count + ",pending=" + _helperPendingAttacks;

        private void OpenHelperVisual()
        {
            _helperAtlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas("HelperFrames");
            _helperStormAtlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas("HelperStormFrames");
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelperShot, owner: this);
            RevTimer.Every(.06f, RefreshHelperVisual, owner: _helperRefreshOwner);
        }

        private void RefreshHelperVisual()
        {
            if (_released || _viewRoot == null) { return; }
            var battle = Battle;
            if (_helperVisualChapter != battle.Chapter) { LoadHelperMaterials(battle.Chapter); }
            if (_helperView == null && !_helperLoading)
            {
                _helperLoading = true;
                int generation = _generation;
                GamePoolRequests.GetAsync<HelperView>(view =>
                {
                    if (_released || generation != _generation)
                    {
                        if (view != null) { RevPool.Return(view, 0); }
                        return;
                    }
                    _helperLoading = false;
                    if (view == null) { RevLog.Error("助阵帮手预制体加载失败", "RevGame"); return; }
                    _helperView = view;
                    view.Present(Battle.Helper);
                }, _viewRoot);
            }
            if (_helperView != null) { _helperView.Present(battle.Helper); }
            RefreshHelperHealing(battle.HelperProtectionTargetId);
        }

        /// <summary>
        /// 无保护/回合结束立即回池。异步取池同时捕获目标Id与代次，目标改变后晚到的实例会直接归还，
        /// 不会在已经死亡的棋子或下一回合重新出现一条“幽灵治疗线”。
        /// </summary>
        private void RefreshHelperHealing(int targetId)
        {
            if (targetId == 0 || _helperView == null || !_helperView.gameObject.activeInHierarchy
                || !_pieces.TryGetValue(targetId, out PieceView target) || target == null || target.PieceId != targetId)
            {
                ClearHelperHealing();
                return;
            }
            if (_helperHealing != null)
            {
                _helperHealing.Bind(_helperView.transform, target, targetId);
                return;
            }
            if (_helperHealingLoading) { return; }
            _helperHealingLoading = true;
            int generation = _helperHealingGeneration;
            GamePoolRequests.GetAsync<HelperHealingView>(view =>
            {
                if (_released || generation != _helperHealingGeneration)
                {
                    if (view != null) { RevPool.Return(view, 0); }
                    return;
                }
                _helperHealingLoading = false;
                if (view == null) { RevLog.Warn("帮手治疗射线预制体加载失败", "RevGame"); return; }
                if (Battle.HelperProtectionTargetId != targetId || _helperView == null
                    || !_pieces.TryGetValue(targetId, out PieceView current) || current == null || current.PieceId != targetId)
                {
                    RevPool.Return(view, 0);
                    return;
                }
                _helperHealing = view;
                view.Bind(_helperView.transform, current, targetId);
            }, _viewRoot);
        }

        private void ClearHelperHealing()
        {
            _helperHealingGeneration++;
            _helperHealingLoading = false;
            if (_helperHealing != null) { RevPool.Return(_helperHealing, 0); _helperHealing = null; }
        }

        /// <summary>只加载当前章节材质；切章或关闭时配对Release，六章Shader不会被预制体同时引用。</summary>
        private void LoadHelperMaterials(int chapter)
        {
            ReleaseHelperMaterials();
            _helperVisualChapter = chapter;
            if (chapter < 1 || chapter > 6) { return; }
            int generation = ++_helperVisualGeneration;
            string name = "HelperChapter" + chapter.ToString("00");
            RevResManager.LoadAsync<Material>("Material/Helper", name, mat =>
            {
                if (!_released && generation == _helperVisualGeneration)
                {
                    _helperProjectile = mat;
                    if (mat == null) { LoadError = "当前章助阵弹幕材质加载失败"; RevLog.Error(LoadError, "RevGame"); }
                }
            }, RevResGroup.Battle);
            RevResManager.LoadAsync<Material>("Material/Helper", "HelperBlueBeam", mat =>
            {
                if (!_released && generation == _helperVisualGeneration)
                {
                    _helperBeam = mat;
                    if (mat == null) { LoadError = "助阵蓝色激光材质加载失败"; RevLog.Error(LoadError, "RevGame"); }
                }
            }, RevResGroup.Battle);
        }

        private void OnHelperShot(HelperShotInfo info)
        {
            if (_released || _viewRoot == null) { return; }
            Material material = info.Beam ? _helperBeam : _helperProjectile;
            if (material == null) { RevLog.Warn("帮手攻击材质尚未就绪，伤害已正常结算", "RevGame"); return; }
            Vector3 start = info.Source.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            Vector3 end = info.Target.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            for (int i = 0; i < info.Projectiles && _helperAttacks.Count + _helperPendingAttacks < 40; i++)
            {
                int generation = _helperAttackGeneration, lane = i;
                _helperPendingAttacks++;
                // 入账飞星约0.66秒到HUD，耗能飞星0.72秒后启动再飞0.66秒；攻击视觉随后发射。
                // 这里只延迟画面，模型早已同步扣款与伤害。预约也计预算，退出时owner计时器会取消。
                RevTimer.After(info.Beam || info.Storm ? 0 : 1.4f, () =>
                {
                    if (_released || generation != _helperAttackGeneration) { return; }
                    GamePoolRequests.GetAsync<HelperAttackView>(view =>
                    {
                        if (_released || generation != _helperAttackGeneration)
                        {
                            if (view != null) { RevPool.Return(view, 0); }
                            return;
                        }
                        _helperPendingAttacks--;
                        if (view == null) { RevLog.Warn("帮手攻击预制体加载失败", "RevGame"); return; }
                        _helperAttacks.Add(view);
                        _pieces.TryGetValue(info.TargetId, out PieceView target);
                        view.Play(start, end, material, info.Beam, lane + _helperAttacks.Count, attack => _helperAttacks.Remove(attack),
                            _helperView == null ? null : _helperView.transform, target, info);
                    }, _viewRoot);
                }, domain: RevTimeDomain.Unscaled, owner: _helperAttackOwner);
            }
        }

        private void ReleaseHelperMaterials()
        {
            if (_helperVisualChapter > 0)
            {
                RevResManager.Release("Material/Helper", "HelperChapter" + _helperVisualChapter.ToString("00"));
                RevResManager.Release("Material/Helper", "HelperBlueBeam");
            }
            _helperVisualGeneration++;
            _helperVisualChapter = 0;
            _helperProjectile = _helperBeam = null;
        }

        private void CloseHelperVisual()
        {
            RevTimer.CancelAllOf(_helperRefreshOwner);
            ClearHelperAttacks();
            if (_helperView != null) { RevPool.Return(_helperView, 0); _helperView = null; }
            _helperAtlas?.Dispose(); _helperAtlas = null;
            _helperStormAtlas?.Dispose(); _helperStormAtlas = null;
            _helperLoading = false;
            RevPool.DestroyPool<HelperAttackView>();
            RevPool.DestroyPool<HelperHealingView>();
            RevPool.DestroyPool<HelperView>();
            ReleaseHelperMaterials();
        }

        /// <summary>准备/战斗/结算切换只收短效，演员和刷新租约活到整个BoardView退出。</summary>
        private void ClearHelperAttacks()
        {
            ClearHelperHealing();
            _helperAttackGeneration++;
            RevTimer.CancelAllOf(_helperAttackOwner);
            foreach (HelperAttackView attack in _helperAttacks) { if (attack != null) { RevPool.Return(attack, 0); } }
            _helperAttacks.Clear(); _helperPendingAttacks = 0;
        }
    }
}
