// 固定输入验收链接真实服务/FSM/伤害管线；视觉夹具明确高生命，不能拿此夹具宣称正常胜率或三分钟平衡。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REVGAME_OFFLINE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>已在目标章节准备态时，建立固定高生命测试输入；不修改配置、解锁或存档。</summary>
        public bool LoadBossSkillPreview(int chapter, float bossAttackScale = .1f)
        {
            if (Phase != GamePhase.Prepare || Chapter != chapter || !IsBossRound)
            {
                return Reject("需要先进入目标章节Boss准备回合");
            }
            StopEnemyAI();
            _board.Reset();
            _playerPieces.Clear();
            _enemyPieces.Clear();
            var lineup = LevelCatalog.BossOf(chapter);
            var boss = new Piece(_nextPieceId++, PieceCatalog.Get(lineup.Kind, lineup.Star), GameSide.Enemy,
                lineup.BodySizes[0], true, true, 1000f, bossAttackScale);

            if (!_board.Place(boss, new BoardPos(5, 6)))
            {
                return Reject("Boss固定位置无法放置");
            }
            // 汲取只有主将缺血才合法；固定输入让主将保留七成生命，仍高于狂暴血线。
            boss.TakeDamage(boss.MaxHp * 3 / 10);
            _enemyPieces.Add(boss);
            EnsureAIFor(boss);
            // 第一组围住(2,2)形成真眼；额外分散目标保证十字、横排、近身和后排技能都有可观察对象。
            var cells = new[] { new BoardPos(1,2), new BoardPos(2,1), new BoardPos(3,2), new BoardPos(2,3),
                new BoardPos(0,0), new BoardPos(4,0), new BoardPos(6,2), new BoardPos(8,1), new BoardPos(0,5), new BoardPos(1,7) };
            foreach (BoardPos cell in cells)
            {
                var p = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player, 1, hpScale: 1000f);

                if (!_board.Place(p, cell))
                {
                    throw new InvalidOperationException("固定玩家夹具占格冲突");
                }
                _playerPieces.Add(p);
            }
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }
        /// <summary>针对服务事实的自然轮换与边界断言，接受外部记录器，不依赖屏幕猜测。</summary>
        public static string VerifyBossSkillExpansion(Action<bool, string> check)
        {
            var summaries = new List<string>();
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                var battle = new BattleService(false, 1029);
                try
                {
                    check(battle.StartRound(chapter, Balance.BossRoundIndex), "建立章节" + chapter);
                    check(battle.LoadBossSkillPreview(chapter), "固定输入注入" + chapter);
                    Piece boss = battle._enemyPieces[0];
                    battle.Phase = GamePhase.Battle;
                    var owner = new BossOwner(battle, boss);
                    using (var ai = new BossAI(owner))
                    {
                        var positions = new HashSet<BoardPos>();
                        // 只驱动真实Boss FSM，玩家站位固定以隔离选招因素；不强制施法、不修改冷却。
                        for (int step = 0; step < 180f / Balance.BattleSimulationStepSeconds; step++)
                        {
                            battle._battleElapsed += Balance.BattleSimulationStepSeconds;
                            battle.TickBossEffects();
                            ai.Tick(Balance.BattleSimulationStepSeconds);
                            positions.Add(boss.Pos);
                        }
                        int distinct = owner.SkillUseCounts.Count(n => n > 0);
                        check(distinct == 10, "第" + chapter + "章180秒自然轮换六通用四专属，实际=" + distinct);
                        check(positions.Count >= 5, "第" + chapter + "章独立走位至少5个位置，实际=" + positions.Count);
                        for (int skill = 0; skill < owner.SkillUseCounts.Length; skill++)
                        {
                            check((owner.SkillUseCounts[skill] > 0) == (BossSkillCatalog.IsUnlocked((BossSkillKind)skill, chapter) && (BossSkillKind)skill != BossSkillKind.Enrage), "章节专属隔离及实际轮到：" + chapter + "/" + (BossSkillKind)skill);
                        }
                        summaries.Add("ch=" + chapter + ",skills=" + distinct + ",positions=" + positions.Count
                            + ",casts=" + string.Join(",", owner.SkillUseCounts));
                    }
                }
                finally { battle.StopEnemyAI(); }
            }
            VerifyExclusiveBoundaries(check);
            VerifyExclusiveHits(check);
            return string.Join("\n", summaries);
        }
        /// <summary>逐招独立造局验证持续伤害、位移和范围，避免前一招留下的隐藏状态污染下一招。</summary>
        private static void VerifyExclusiveHits(Action<bool, string> check)
        {
            var covered = new HashSet<BoardPos>();
            for (int ring = 0; ring < 5; ring++)
            {
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var cell = new BoardPos(x, y);

                        if (!BossExclusiveGeometry.Contains(BossSkillKind.StarStorm, new BoardPos(4, 4), default, default, 0, ring, cell))
                        {
                            continue;
                        }
                        check(covered.Add(cell), "星辰风暴每格仅属于一个扩散环：" + cell);
                    }
                }
            }
            check(covered.Count == Consts.BoardSize * Consts.BoardSize, "星辰风暴五环覆盖全盘81格");
            foreach (BossSkillKind kind in new[] { BossSkillKind.SolarFlare, BossSkillKind.SunBrand, BossSkillKind.TidalSurge,
                BossSkillKind.Maelstrom, BossSkillKind.SwampMaw, BossSkillKind.MireChains, BossSkillKind.Sanctuary })
            {
                var battle = new BattleService(false, 1029);
                try
                {
                    int chapter = ((int)kind - 18) / 2 + 1;
                    check(battle.StartRound(chapter, Balance.BossRoundIndex) && battle.LoadBossSkillPreview(chapter), "独立命中夹具：" + kind);
                    battle.Phase = GamePhase.Battle;
                    Piece boss = battle._enemyPieces[0], target = battle._playerPieces[0];
                    BossSkillDef def = BossSkillCatalog.Get(kind);
                    BoardPos origin = target.Pos;
                    var focus = kind == BossSkillKind.Maelstrom ? new BoardPos(2, 2) : target.Pos;
                    var info = new BossSkillInfo(boss.Id, kind, boss.Pos, boss.Size, focus, focus, focus, def.Radius, def.Windup, 0, def.Count, false);
                    battle.ApplyExclusiveHit(boss, info);

                    if (kind == BossSkillKind.SolarFlare || kind == BossSkillKind.SunBrand)
                    {
                        int afterHit = target.Hp;
                        battle._battleElapsed = def.Interval - .001f;
                        battle.TickBossEffects();
                        check(target.Hp == afterHit, kind + "灼烧间隔前不提前扣血");
                        battle._battleElapsed = def.Interval;
                        battle.TickBossEffects();
                        check(target.Hp < afterHit, kind + "灼烧到时实际追加伤害");
                        battle._battleElapsed = def.Duration;
                        battle.TickBossEffects();
                        int expiredHp = target.Hp;
                        battle._battleElapsed += def.Interval;
                        battle.TickBossEffects();
                        check(target.Hp == expiredHp && battle._bossEffects.Count == 0, kind + "到期停止灼烧并释放租约");
                    }
                    else
                        if (kind == BossSkillKind.TidalSurge || kind == BossSkillKind.Maelstrom)
                        {
                            check(target.Pos != origin && battle._board.FindPiece(target.Id) == target, kind + "合法位移保持棋子身份和占格");
                            check(Math.Abs(target.Pos.X - origin.X) <= 1 && Math.Abs(target.Pos.Y - origin.Y) <= 1, kind + "每段至多一步");
                        }
                        else
                            if (kind == BossSkillKind.SwampMaw)
                            {
                                check(target.BossHidden, "沼泽吞噬实际隐藏目标");
                                battle._battleElapsed = def.Duration;
                                battle.TickBossEffects();
                                check(!target.BossHidden && target.Pos == origin, "沼泽到期原位返场");
                            }
                            else
                            {
                                check(target.BossSpeedFactor == def.SpeedFactor && target.BossAttackFactor == def.AttackFactor, kind + "减益倍率与配置完全相同");
                                var melee = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Swordman, StarTier.One), GameSide.Player, 1, hpScale: 1000f);
                                check(battle._board.Place(melee, new BoardPos(1, 1)), "加入区域近战对照");
                                battle._playerPieces.Add(melee);
                                battle.RefreshBossZones();
                                check(melee.BossSpeedFactor == (kind == BossSkillKind.MireChains ? 1f : def.SpeedFactor), kind + "远程限定与全类型领域区分");
                                battle._battleElapsed = def.Duration;
                                battle.TickBossEffects();
                                check(target.BossSpeedFactor == 1f && target.BossAttackFactor == 1f && melee.BossSpeedFactor == 1f, kind + "领域结束所有属性归一");
                            }
                }
                finally { battle.StopEnemyAI(); }
            }
            int crossCells = 0;
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    if (BossExclusiveGeometry.Contains(BossSkillKind.DivineCross, new BoardPos(4, 4), default, default, 0, 0, new BoardPos(x, y)))
                    {
                        crossCells++;
                    }
                }
            }
            check(crossCells == Consts.BoardSize * 2 - 1, "十字神罚只命中交叉行列17格");
        }
        private static void VerifyExclusiveBoundaries(Action<bool, string> check)
        {
            var battle = new BattleService(false, 1029);
            try
            {
                check(battle.StartRound(1, Balance.BossRoundIndex) && battle.LoadBossSkillPreview(1), "控制边界夹具");
                battle.Phase = GamePhase.Battle;
                Piece boss = battle._enemyPieces[0], target = battle._playerPieces[0];
                var info = new BossSkillInfo(boss.Id, BossSkillKind.BlackHole, boss.Pos, boss.Size, target.Pos, target.Pos, target.Pos, 1, 1, 0, 1, false);
                int id = target.Id;
                BoardPos origin = target.Pos;
                check(target.EyeCount > 0, "黑洞前置是真实已成眼棋群");
                battle.ApplyExclusiveHit(boss, info);
                check(target.BossHidden && !MoveRules.CanAttack(target), "黑洞禁止行动");
                int hp = target.Hp;
                check(battle.DamagePiece(target, 100) == 0 && target.Hp == hp, "隐藏期间所有伤害出口拒绝命中");
                battle._battleElapsed = BossSkillCatalog.Get(BossSkillKind.BlackHole).Duration - .001f;
                battle.TickBossEffects();
                check(target.BossHidden, "4秒之前不提前返场");
                battle._battleElapsed += .001f;
                battle.TickBossEffects();
                check(!target.BossHidden && target.Id == id && target.Pos == origin && target.Hp == hp && battle._board.FindPiece(id) == target, "4秒边界原身份原位原血量返场");
                info = new BossSkillInfo(boss.Id, BossSkillKind.FrozenPrison, boss.Pos, boss.Size, target.Pos, target.Pos, target.Pos, 1, 1, 0, 1, false);
                battle.ApplyExclusiveHit(boss, info);
                check(target.BossFrozen && !MoveRules.CanAttack(target) && target.ActionProgress == 0f, "冻结禁止攻击且清积攒进度");
                battle._battleElapsed += 1.999f;
                battle.TickBossEffects();
                check(target.BossFrozen, "两秒前仍冻结");
                battle._battleElapsed += .002f;
                battle.TickBossEffects();
                check(!target.BossFrozen && MoveRules.CanAttack(target), "两秒后可重新攻击");
                info = new BossSkillInfo(boss.Id, BossSkillKind.Sanctuary, boss.Pos, boss.Size, target.Pos, target.Pos, target.Pos, 1, 1, 0, 1, false);
                battle.ApplyExclusiveHit(boss, info);
                check(target.BossAttackFactor < 1f && target.BossSpeedFactor < 1f, "圣域同时削弱攻击和攻速");
                check(battle._board.Move(target, new BoardPos(0, 8), respectEnemyEyes: false), "移出圣域");
                battle.RefreshBossZones();
                check(target.BossAttackFactor == 1f && target.BossSpeedFactor == 1f, "离开圣域立即撤销减益");
                boss.BossInvulnerable = true;
                hp = boss.Hp;
                foreach (DamageBasis basis in Enum.GetValues(typeof(DamageBasis)))
                {
                    check(battle.ResolveDamage(new DamageRequest(boss, basis, BattleDamageKind.Normal, target, .5f, 100)) == 0 && boss.Hp == hp, "冰封免疫统一伤害来源" + basis);
                }
                battle.ClearBossEffects();
                check(!boss.BossInvulnerable && battle._bossEffects.Count == 0 && battle._playerPieces.All(p => !p.BossHidden && !p.BossFrozen && p.BossSpeedFactor == 1f), "取消清空全部控制和无敌");
            }
            finally { battle.StopEnemyAI(); }
        }
    }
}
#endif
