// ============================================================
// BoardView.BossSkills.cs —— 棋盘对 Boss 技能事件的表现接线（BoardView 的 partial 片段）。
//
// 职责：收到 BossSkillTelegraph / BossSkillCast 事件 → 从对象池取一个 BossSkillFxView 播放 →
// 播完回池；瞬移命中时把 Boss 图像瞬间对齐到新位置；换回合 / 离开战斗时全部收回。
// 这里不做任何规则判断：伤害、瞬移落点都已经在服务里决定好，事件快照只是“通知画面”。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Battle;
using RevGame.Data;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>正在播放的 Boss 技能特效；换回合/退出时逐个回池，避免旧技能残影留在新回合。</summary>
        private readonly HashSet<BossSkillFxView> _bossFx = new HashSet<BossSkillFxView>();
        /// <summary>当前在场的 Boss 特效数量（编辑器验收读取）。</summary>
        public int ActiveBossFxCount => _bossFx.Count;

        /// <summary>订阅 Boss 技能事件；在 OnEnable 里与其他事件一起注册，owner 释放时自动解绑。</summary>
        private void ListenBossSkills()
        {
            RevEvent.AddEventListener<int>(Events.BossStatusChanged, OnBossStatusChanged, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, OnBossTelegraph, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, OnBossCast, owner: this);
        }

        /// <summary>蓄力预警：画危险区域；Boss 身体做一次“蓄力”姿态，让玩家知道是谁在出招。</summary>
        private void OnBossTelegraph(BossSkillInfo info)
        {
            if (_released)
            {
                return;
            }
            if (_pieces.TryGetValue(info.BossId, out PieceView boss))
            {
                boss.PlayAttackPulse();
            }
            if ((int)info.Kind >= (int)BossSkillKind.StarStorm && boss != null) { boss.PlayBossCharge(info.Kind, info.Windup); }
            SpawnBossFx(info, true);
        }

        /// <summary>
        /// 技能命中：先处理 Boss 图像本身（瞬移要立刻对齐，不能平滑滑过去），再播放命中特效。
        /// 服务端瞬移时已经派发过 PieceMoved，BoardView 会开始平滑移动；这里用 SnapTo 覆盖成瞬间到位。
        /// </summary>
        private void OnBossCast(BossSkillInfo info)
        {
            if (_released)
            {
                return;
            }
            if (_pieces.TryGetValue(info.BossId, out PieceView boss))
            {
                if (info.Kind == BossSkillKind.BlinkBlast)
                {
                    Vector3 position = info.A.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                    position += new Vector3(info.OriginSize - 1, info.OriginSize - 1, 0f) * (Consts.CellSize * 0.5f);
                    boss.SnapTo(position);
                }
                boss.PlayAttackPulse();
            }
            if ((int)info.Kind >= (int)BossSkillKind.StarStorm)
            {
                if (boss != null) { boss.PlayBossCharge(info.Kind, .6f, windingUp: false); }
                foreach (BoardCellView cell in _cells)
                {
                    if (BossExclusiveGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, info.Index, cell.Position))
                    { cell.PlayBossTile(BossSkillFxView.ExclusiveTint(info.Kind), Mathf.Max(.8f, BossSkillCatalog.Get(info.Kind).Duration)); }
                }
            }
            SpawnBossFx(info, false);
        }

        /// <summary>
        /// 从对象池取特效并播放。异步加载回来时要核对“批次”：
        /// 期间如果换了回合（_effectGeneration 变化）或离开了战斗（_released），直接回池，不补播旧技能。
        /// </summary>
        private void SpawnBossFx(BossSkillInfo info, bool telegraph)
        {
            int generation = _effectGeneration;
            GamePoolRequests.GetAsync<BossSkillFxView>(fx =>
            {
                if (fx == null)
                {
                    RevLog.Warn("Boss 技能特效加载失败：" + info.Kind, "RevGame");
                    return;
                }
                if (_released || this == null || generation != _effectGeneration)
                {
                    RevPool.Return(fx, 0);
                    return;
                }
                _bossFx.Add(fx);
                fx.Play(info, telegraph, OnBossFxFinished);
            }, _viewRoot);
        }

        /// <summary>动画完整播完才会走到这里；中途回池用 Kill(false)，不会重复回池。</summary>
        private void OnBossFxFinished(BossSkillFxView fx)
        {
            if (_bossFx.Remove(fx))
            {
                RevPool.Return(fx, 0);
            }
        }

        /// <summary>换回合 / 结算 / 退出时立即收回所有 Boss 特效。</summary>
        private void OnBossStatusChanged(int id)
        {
            if (_released || !_pieces.TryGetValue(id, out PieceView view)) { return; }
            var piece = FindPiece(id);
            if (piece != null) { view.SetBossStatus(piece.BossHidden, piece.BossFrozen, piece.BossAttackFactor < 1f || piece.BossSpeedFactor < 1f); }
        }

        private void ClearBossFx()
        {
            foreach (BossSkillFxView fx in _bossFx)
            {
                if (fx != null)
                {
                    RevPool.Return(fx, 0);
                }
            }
            _bossFx.Clear();
            foreach (BoardCellView cell in _cells) { cell.ClearBossTile(); }
        }
    }
}
