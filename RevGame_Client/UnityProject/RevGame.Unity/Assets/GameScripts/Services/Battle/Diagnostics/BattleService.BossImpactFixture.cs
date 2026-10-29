// 本文件只提供开发态视觉夹具；真实技能仍经GM→Boss状态机→统一伤害提交。
// 高生命与冻结无关自动攻击是固定输入，不能据此宣称正常玩家胜率、Boss战时长或手机性能达标。
#if UNITY_EDITOR
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        private BossOwner _bossImpactFixtureOwner;

        /// <summary>只在隔离测试的Boss准备态重建高生命靶场；不修改配置与生产存档，每一招重新造局。</summary>
        public bool PrepareBossImpactFixture(int chapter)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase != GamePhase.Prepare
                || Chapter != chapter || !IsBossRound) { return Reject("Boss视觉夹具只允许隔离Boss准备态"); }
            // 旧技能观测夹具把Boss攻击缩成0.1倍，只适合看控制效果：基础1点再经20%护甲会取整成0。
            // 命中震动验收必须恢复1倍真实图鉴攻击；双方仍为1000倍生命，所以能够存活观察，不能删除正伤害断言。
            if (!LoadBossSkillPreview(chapter, bossAttackScale: 1f)) { return false; }
            // 视觉靶场额外提供贴身目标；规则轮换靶场保留宽通道，以免三格Boss被靶子堵死。
            var adjacent = new RevGame.Pieces.Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player, 1, hpScale: 1000f);
            if (!_board.Place(adjacent,new BoardPos(4,6))) { return Reject("贴身视觉靶子占格失败"); }
            _playerPieces.Add(adjacent);
            RefreshGoState();
            // 保留明确宿主引用，避免测试靠反射修改FSM内核。重建的仍是正式BossAI与所有正式技能状态。
            foreach (var ai in _bossAI.Values) { ai.Dispose(); }
            _bossAI.Clear();
            _bossImpactFixtureOwner = new BossOwner(this, _enemyPieces[0]) { NextSkillAt = float.MaxValue, Enraged = true };
            _bossAI.Add(_enemyPieces[0].Id, new BossAI(_bossImpactFixtureOwner));
            SetPlayerInvincible(false);
            HoldBossImpactFixture();
            return true;
        }

        /// <summary>
        /// 每个渲染帧只稳定靶场输入：不允许普通攻击/移动/援助抢走观测目标，指定Boss技能的模拟时间与伤害不变。
        /// 一招结束后自然调度被延后，下一招必须由GM明确请求；没有此隔离，音效与相机可能被别的技能误记成通过。
        /// </summary>
        public void HoldBossImpactFixture()
        {
            if (_bossImpactFixtureOwner == null) { return; }
            _bossImpactFixtureOwner.NextSkillAt = float.MaxValue;
            // 本组输入固定为“无随机护甲/护盾的高生命靶子”：最低1点技能若再随机减伤可能取整归零。
            // 保留实际伤害公式与眼阵防御；随机准备护甲不属于此次技能命中反馈覆盖范围。
            _prepDamageReduction = 0f;
            foreach (var piece in _playerPieces)
            {
                piece.NextMoveAt = float.MaxValue;
                piece.ActionProgress = 0f;
                piece.ShieldCharges = 0;
            }
            foreach (var piece in _enemyPieces)
            {
                piece.NextMoveAt = float.MaxValue;
                piece.ActionProgress = 0f;
            }
            _eventScheduled = false;
            _nextEnemyDeployAt = _nextEyeSupportAt = _nextRegionAt = _nextBombAt = _nextEmergencyAt = float.MaxValue;
            _nextFormationAt = float.MaxValue;
            _helperNextMove = _helperNextShot = _helperNextBeam = float.MaxValue;
            _stormMissileAt = _stormJellyAt = _stormBeamAt = float.MaxValue;
        }

        /// <summary>只清本夹具宿主；随后调用正式ReturnHome收回战斗域、AI、Tween、音效与资源租约。</summary>
        public void CloseBossImpactFixture()
        {
            _bossImpactFixtureOwner = null;
        }
    }
}
#endif
