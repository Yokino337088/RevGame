#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using RevGame.Core;
using RevGame.Endless;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    internal static partial class MathAbScenarios
    {
        /// <summary>隔离炎环的外围敌人初始数量，通过正式出生规则分配门点。</summary>
        private const int EndlessEnemyLoad = 240;
        /// <summary>开战头 20 个逻辑步留给资源和发射表现预热。</summary>
        private const int EndlessWarmupSteps = 20;
        /// <summary>在固定步触发选择盾战的操作，避免运行速度影响棋盘位置。</summary>
        private const int EndlessInputStep = 24;
        /// <summary>同样推进 100 个固定步，后 80 步采集整帧。</summary>
        private const int EndlessEndStep = 100;

        /// <summary>从正常进入的炎环棋阵执行压力对照，核对盾战原位驻守、真实固定步和绿格渲染，离场由作业拥有者执行。</summary>
        internal static IEnumerator Endless(MathAbRun run)
        {
            var service = RevServices.GetRequired<EndlessService>();
            var arena = UnityEngine.Object.FindObjectOfType<EndlessArena>();
            var match = service.Run;
            if (arena == null || match == null || !service.Isolated || match.formation != 1 || match.seed != 1101 ||
                match.phase != EndlessPhase.Formation || match.seconds != 0 || service.PlayerCount() != 8)
            {
                throw new InvalidOperationException("无尽对照需要已就绪的新开隔离炎环阵（种子 1101，八枚玩家棋）");
            }
            if (arena.PlacementHintCount != 0 || match.selectedCard >= 0 || match.selectedUnit >= 0)
            {
                throw new InvalidOperationException("无尽对照开打前须清掉已有选中提示");
            }

            // 固定阵的盾战开局落在 (1,4)。记录身份，避免采样点把后来占格的棋子误认成原来的盾战。
            var shieldPos = new BoardPos(1, 4);
            int shieldId = service.Board.GetPiece(shieldPos)?.Id ?? 0;
            long startStep = service.TickCount;
            if (!service.DebugStress(EndlessEnemyLoad) || service.HordeCount() != EndlessEnemyLoad)
            {
                throw new InvalidOperationException("没有沿真实出生入口生成 240 只外围敌人：" + service.LastError);
            }

            // 固定步由 EndlessLogic 正常推进。漏过指定步就失败，禁止拿另一个时刻的结果冒充配对。
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (service.TickCount - startStep < EndlessWarmupSteps)
            {
                CheckEndlessAbProgress(service, match, deadline);
                yield return null;
            }
            CheckEndlessAbStep(service, match, startStep, EndlessWarmupSteps, deadline);
            run.BeginFrames();
            int hints = 0;
            try
            {
                while (service.TickCount - startStep < EndlessInputStep)
                {
                    CheckEndlessAbProgress(service, match, deadline);
                    yield return null;
                }
                CheckEndlessAbStep(service, match, startStep, EndlessInputStep, deadline);

                // 在开局格点选同一枚存活盾战，测量玩家点选到可操作提示的时间。
                var shield = service.Board.GetPiece(shieldPos);
                if (shield == null || shield.Id != shieldId || shield.Side != GameSide.Player || !shield.IsAlive ||
                    shield.Def.Kind != PieceKind.Shield || arena.PlacementHintCount != 0)
                {
                    throw new InvalidOperationException("无尽盾战开局格或身份不符，或旧绿格仍亮着");
                }

                long inputAt = Stopwatch.GetTimestamp();
                if (!service.ClickCell(shieldPos.X, shieldPos.Y) || match.selectedUnit != shield.Id)
                {
                    throw new InvalidOperationException("真实棋盘操作未选中盾战：" + service.LastError);
                }
                double feedbackDeadline = Time.realtimeSinceStartupAsDouble + 2;
                while (arena.PlacementHintCount <= 0)
                {
                    CheckEndlessAbProgress(service, match, feedbackDeadline);
                    yield return null;
                }
                run.RecordFeedback("无尽点选盾战到棋盘绿格启用", inputAt);
                hints = arena.PlacementHintCount;

                while (service.TickCount - startStep < EndlessEndStep)
                {
                    CheckEndlessAbProgress(service, match, deadline);
                    yield return null;
                }
                CheckEndlessAbStep(service, match, startStep, EndlessEndStep, deadline);
            }
            finally
            {
                // 编排器负责返回大厅；这里先结束本次采样，异常会让编排器拒绝本轮。
                run.EndFrames();
            }

            // 指纹和汇总放在计时窗外。保留落点、击杀与受击回执等数值以定位规则差异。
            ulong fingerprint = EndlessAbFingerprint(service);
            run.RecordRule(FormattableString.Invariant(
                $"endless seed={match.seed} formation={match.formation} initialHorde={EndlessEnemyLoad} fixedSteps={EndlessEndStep} logicSeconds={match.seconds:F3} shieldAtInput={shieldPos.X},{shieldPos.Y} hints={hints} heart={match.heart:R} kills={match.kills} invaderKills={match.invaderKills} score={match.score} level={match.level} random={match.random} units={service.Units.Count} liveHorde={service.HordeCount()} impacts={service.ImpactSerial} damageReceipts={service.DamageSerial} state={fingerprint:x16}"));
        }

        /// <summary>等待中保持同一实例和战斗阶段，并用真实秒为逻辑步设置超时。</summary>
        private static void CheckEndlessAbProgress(EndlessService service, EndlessRun match, double deadline)
        {
            if (!ReferenceEquals(service.Run, match) || match.phase != EndlessPhase.Fighting ||
                Time.realtimeSinceStartupAsDouble >= deadline)
            {
                throw new InvalidOperationException("无尽对照等待失败，棋局离场、战斗提前结束或计时超时");
            }
        }

        /// <summary>若一帧跨过固定采样点，立即作废本轮，避免拿不同模型步比较战果。</summary>
        private static void CheckEndlessAbStep(EndlessService service, EndlessRun match, long start, int step, double deadline)
        {
            CheckEndlessAbProgress(service, match, deadline);
            if (service.TickCount - start != step)
            {
                throw new InvalidOperationException("无尽对照跨过固定模型步 " + step);
            }
        }

        /// <summary>按容器原顺序和敌槽编号折叠选定规则状态。浮点按原始位写入，忽略每局随机 GUID 与显示时间。</summary>
        private static ulong EndlessAbFingerprint(EndlessService service)
        {
            ulong hash = 14695981039346656037UL;
            // 同样的字段顺序产生可复查的指纹；浮点小数末位改变也会反映到结果中。
            void Add(ulong value)
            {
                hash = unchecked((hash ^ value) * 1099511628211UL);
            }

            void AddFloat(float value)
            {
                Add(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
            }

            var match = service.Run;
            AddFloat(match.seconds);
            AddFloat(match.heart);
            Add(match.random);
            Add(unchecked((ulong)match.score));
            Add((uint)match.nextId);
            Add((uint)match.kills);
            Add((uint)match.invaderKills);
            Add((uint)match.level);
            Add((uint)match.xp);
            Add((uint)service.ImpactSerial);
            Add((uint)service.DamageSerial);
            Add((uint)service.DeathSerial);
            Add((uint)service.Units.Count);
            for (int index = 0; index < service.Units.Count; index++)
            {
                EndlessUnit actor = service.Units[index];
                var piece = actor.Piece;
                Add((uint)piece.Id);
                Add((uint)piece.Side);
                Add((uint)piece.Def.Kind);
                Add((uint)piece.Def.Star);
                Add(unchecked((uint)piece.Pos.X));
                Add(unchecked((uint)piece.Pos.Y));
                Add((uint)piece.Hp);
                Add((uint)piece.MaxHp);
                Add((uint)piece.Liberties);
                Add((uint)piece.EyeCount);
                AddFloat(actor.AttackAt);
                AddFloat(actor.MoveAt);
            }
            Add((uint)service.HordeCount());
            for (int slot = 0; slot < service.Horde.Length; slot++)
            {
                EndlessHorde horde = service.Horde[slot];
                if (!horde.active)
                {
                    continue;
                }
                Add((uint)slot);
                Add((uint)horde.id);
                Add((uint)horde.kind);
                Add((uint)horde.gate);
                AddFloat(horde.x);
                AddFloat(horde.y);
                AddFloat(horde.hp);
                AddFloat(horde.attackAt);
            }
            return hash;
        }
    }
}
#endif
