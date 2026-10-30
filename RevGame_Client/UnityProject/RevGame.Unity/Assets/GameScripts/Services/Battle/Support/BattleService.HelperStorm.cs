using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>
    /// Boss回合自动风暴与普通少子兜底属于正式玩法：状态由回合和存活棋数推导，三种武器各自用模拟秒冷却。
    /// 帮手仍不占格、不计存活、不改胜负。所有伤害交回统一管线，表现不能重复结算。
    /// </summary>
    public sealed partial class BattleService
    {
        /// <summary>Boss回合专属倍率取Excel；普通少子救场仍保持原数值，不重复叠加共鸣。</summary>
        private float HelperStormAttackFactor => IsBossRound ? 1f + Balance.HelperBossStormAttackBonus : 1f;
        private float HelperStormSpeedFactor => IsBossRound ? 1f + Balance.HelperBossStormSpeedBonus : 1f;
        public bool HelperStormActive
        {
            get; private set;
        }
        private float _stormMissileAt, _stormJellyAt, _stormBeamAt, _stormBeamUntil, _stormBeamRemainder;
        // 服务拥有自己的缓冲：伤害可能从敌军名单移除对象，不能边遍历原名单边扣血。
        private readonly List<Piece> _stormTargets = new List<Piece>(Consts.BoardSize * Consts.BoardSize);
        private int _stormJellyCursor;

        private void ResetHelperStorm()
        {
            HelperStormActive = false;
            _stormMissileAt = _stormJellyAt = _stormBeamAt = _stormBeamUntil = _stormBeamRemainder = 0;
            _stormJellyCursor = 0;
            _stormTargets.Clear();
        }
        /// <summary>只认仍在棋盘上的活玩家棋；准备态、全灭、结算都不能启动救场火力。</summary>
        private void UpdateHelperStorm()
        {
            int alive = 0;
            foreach (Piece player in _playerPieces)
            {
                if (player.IsAlive && _board.FindPiece(player.Id) == player)
                {
                    alive++;
                }
            }
            bool active = Phase == GamePhase.Battle && alive > 0 && (IsBossRound || alive < Balance.HelperStormThreshold);

            if (active == HelperStormActive)
            {
                return;
            }
            HelperStormActive = active;
            // 清掉旧激光，不把普通模式的剩余时长带进风暴，也不让补兵后留下免费激光。
            _helperBeamUntil = _stormBeamUntil = _stormBeamRemainder = 0;
            _helperSpeech = active ? "风暴模式！导弹锁定，水母弹幕掩护！" : "援军到位，恢复能量支援！";
            // 不重置武器冷却：反复撤兵/补兵切换模式不能刷无间隔爆发。
        }
        /// <summary>风暴导弹无视普通模式的Boss/手动优先级，先压敌远程，再选其他敌棋。</summary>
        private Piece StormTarget()
        {
            Piece best = null;
            foreach (Piece enemy in _enemyPieces)
            {
                if (!enemy.IsAlive || _board.FindPiece(enemy.Id) != enemy)
                {
                    continue;
                }
                if (best == null || (enemy.Def.IsRanged && !best.Def.IsRanged)
                    || (enemy.Def.IsRanged == best.Def.IsRanged && (enemy.Hp < best.Hp || (enemy.Hp == best.Hp && enemy.Id < best.Id))))
                {
                    best = enemy;
                }
            }
            return best;
        }
        private void CollectStormTargets()
        {
            _stormTargets.Clear();
            foreach (Piece enemy in _enemyPieces)
            {
                if (enemy.IsAlive && _board.FindPiece(enemy.Id) == enemy)
                {
                    _stormTargets.Add(enemy);
                }
            }
        }
        /// <summary>
        /// FSM只调用一次此动作；每种武器每步最多触发一次，不用while补发卡顿期间积攒的无限弹幕。
        /// 免费兜底火力使用不返能量的HelperBeam伤害种类，避免它自供能刷满普通帮手钱包。
        /// </summary>
        private void StepHelperStorm(float seconds, HelperOwner host)
        {
            Piece target = StormTarget();

            if (target == null)
            {
                return;
            }
            _helperTargetId = target.Id;

            if (host.MoveDue)
            {
                host.Crawl();
            }
            _helperSpeech = "风暴模式！锁定远程，火力全开！";

            if (_battleElapsed >= _stormMissileAt)
            {
                _stormMissileAt = _battleElapsed + Balance.HelperStormMissileSeconds / HelperStormSpeedFactor;
                BoardPos impact = target.Pos;
                CollectStormTargets();
                for (int index = 0; index < _stormTargets.Count; index++)
                {
                    // 最后一击可能同步结算并清空缓冲；索引循环能安全停止，不让枚举器因清理而失效。
                    Piece enemy = _stormTargets[index];
                    // 大棋按身体最近点检查爆炸，命中同一棋子只结算一次。
                    float dx = Math.Max(0, Math.Max(enemy.Pos.X - impact.X, impact.X - (enemy.Pos.X + enemy.Size - 1)));
                    float dy = Math.Max(0, Math.Max(enemy.Pos.Y - impact.Y, impact.Y - (enemy.Pos.Y + enemy.Size - 1)));

                    if (dx * dx + dy * dy <= Balance.HelperStormBlastRadius * Balance.HelperStormBlastRadius)
                    {
                        DamagePiece(enemy, ClampDamage(Balance.HelperStormMissileDamage * HelperStormAttackFactor * (enemy == target ? 1f : .5f), 1), damageKind: BattleDamageKind.HelperBeam);
                    }
                }
                EmitStorm(target, impact, HelperWeapon.Missile, 1);
            }
            if (Phase != GamePhase.Battle)
            {
                return;
            }
            if (_battleElapsed >= _stormJellyAt)
            {
                _stormJellyAt = _battleElapsed + Balance.HelperStormJellySeconds / HelperStormSpeedFactor;
                CollectStormTargets();
                int count = _stormTargets.Count;
                for (int i = 0; i < Balance.HelperStormJellyCount && count > 0 && Phase == GamePhase.Battle; i++)
                {
                    Piece enemy = _stormTargets[_stormJellyCursor++ % count];

                    if (!enemy.IsAlive)
                    {
                        continue;
                    }
                    BoardPos position = enemy.Pos;
                    DamagePiece(enemy, ClampDamage(Balance.HelperStormJellyDamage * HelperStormAttackFactor, 1), damageKind: BattleDamageKind.HelperBeam);
                    EmitStorm(enemy, position, HelperWeapon.Jellyfish, 1);
                }
                _stormJellyCursor %= Math.Max(1, count);
            }
            if (Phase != GamePhase.Battle)
            {
                return;
            }
            target = StormTarget();

            if (target == null)
            {
                return;
            }
            if (_battleElapsed >= _stormBeamAt)
            {
                _stormBeamAt = _battleElapsed + Balance.HelperStormBeamCooldown / HelperStormSpeedFactor;
                _stormBeamUntil = _battleElapsed + Balance.HelperStormBeamSeconds;
                _stormBeamRemainder = 0;
            }
            if (_battleElapsed >= _stormBeamUntil)
            {
                return;
            }
            // 小数伤害累积后取整，避免每个固定步向上取1使低DPS被帧率放大。
            _stormBeamRemainder += Balance.HelperStormBeamDps * HelperStormAttackFactor * seconds;
            int damage = (int)_stormBeamRemainder;
            _stormBeamRemainder -= damage;
            BoardPos end = target.Pos;
            CollectStormTargets();
            for (int index = 0; index < _stormTargets.Count; index++)
            {
                Piece enemy = _stormTargets[index];

                if (damage > 0 && StormBeamTouches(_helperPosition, end, enemy, Balance.HelperStormBeamWidth))
                {
                    DamagePiece(enemy, damage, damageKind: BattleDamageKind.HelperBeam);
                }
            }
            EmitStorm(target, end, HelperWeapon.Beam, 1);
        }
        /// <summary>格中心到线段的距离检测；逐身体格而非重复伤害，让宽束穿透多个敌人且兼容大Boss。</summary>
        private static bool StormBeamTouches(BoardPos start, BoardPos end, Piece enemy, float width)
        {
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float length2 = dx * dx + dy * dy;
            for (int y = enemy.Pos.Y; y < enemy.Pos.Y + enemy.Size; y++)
            {
                for (int x = enemy.Pos.X; x < enemy.Pos.X + enemy.Size; x++)
                {
                    float t = length2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - start.X) * dx + (y - start.Y) * dy) / length2));
                    float offsetX = x - start.X - t * dx, offsetY = y - start.Y - t * dy;

                    if (offsetX * offsetX + offsetY * offsetY <= width * width * .25f)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        private void EmitStorm(Piece target, BoardPos position, HelperWeapon weapon, int count)
        {
            RevEvent.DispatchEvent<HelperShotInfo>(Events.HelperShot,
                new HelperShotInfo(_helperPosition, position, target.Id, Chapter, count, 0, weapon == HelperWeapon.Beam, weapon, true, Balance.HelperStormBeamWidth));
        }
    }
}
