#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using Revolution;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RevGame.Core;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>用独立临时目录验证候选局写入门禁、正式落盘、试盘恢复与战绩读取。</summary>
        public static object CheckSaveRecovery()
        {
            string directory = Path.Combine(Path.GetTempPath(), "RevGame-Endless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var checks = new List<string>();
            try
            {
                using var source = new EndlessService(directory);
                using var restored = new EndlessService(directory);
                source.Begin(0, 0, 1101, false);
                string path = Path.Combine(directory, RunFileName);
                string backup = path + ".bak";
                source.Run.score = 80;
                Check(source.Save(), "真实写出第一份无尽快照", checks);
                source.Run.score = 120;
                Check(source.Save(), "第二次保存生成上一份备份", checks);
                string savedBackup = File.ReadAllText(backup);
                Check(restored.Restore(false) && restored.Run.score == 120, "主档有效时恢复最新棋局", checks);

                // 模拟首屏未就绪时的后台写入，再丢弃候选。主档和备份都应保留原始字节。
                string savedMain = File.ReadAllText(path);
                using (var candidate = new EndlessService(directory))
                {
                    candidate.Begin(1, 0, 1103, deferFirstSave: true);
                    Check(!candidate.Save(), "无尽候选局在首屏交割前拒绝写入", checks);
                }
                Check(File.ReadAllText(path) == savedMain && File.ReadAllText(backup) == savedBackup,
                    "放弃候选局时旧续档和备份均保留", checks);

                File.WriteAllText(path, "{}");
                Check(restored.Restore(false) && restored.Run.score == 80 && restored.Run.seed == 1101
                    && restored.Run.runId == source.Run.runId && restored.PlayerCount() == source.PlayerCount()
                    && restored.Run.random == source.Run.random && restored.Run.hand.Count == source.Run.hand.Count,
                    "主档校验失败时从备份恢复同一局的比分、棋子、随机源和手牌", checks);
                Check(File.ReadAllText(path) == "{}" && File.ReadAllText(backup) == savedBackup, "恢复读取保留原始文件", checks);
                Check(restored.Save() && File.ReadAllText(backup) == savedBackup, "恢复后的首笔保存保留有效备份", checks);
                File.Delete(path);
                Check(RevFileSave.Exists(path) && restored.Restore(false) && restored.Run.score == 80, "只有备份时仍有可继续的棋局", checks);

                // 让主档通过校验和，却因棋盘冲突无法提交。备份必须通过完整的模型预检。
                var invalid = Decode(savedBackup);
                invalid.units[1].x = invalid.units[0].x;
                invalid.units[1].y = invalid.units[0].y;
                string payload = JsonUtility.ToJson(invalid);
                File.WriteAllText(path, JsonUtility.ToJson(new Envelope { payload = payload, checksum = Digest(payload) }));
                Check(restored.Restore(false) && restored.Run.score == 80, "主档模型非法时仍可使用备份", checks);
                File.WriteAllText(backup, "{}");
                EndlessRun current = restored.Run;
                Check(!restored.Restore(false) && ReferenceEquals(restored.Run, current) && File.ReadAllText(backup) == "{}",
                    "双档损坏时拒绝继续，并保留现有模型和备份文件", checks);

                string records = Path.Combine(directory, RecordFileName);
                source.Run.bankedScore = 42;
                source.RecordProgress();
                source.Run.bankedScore = 64;
                source.RecordProgress();
                Check(ReadRecords(records, out _).records[0].score == 64, "战绩优先读取有效主档", checks);
                string recordBackup = File.ReadAllText(records + ".bak");
                File.WriteAllText(records, "{\"records\":[null]}");
                Check(ReadRecords(records, out bool fromBackup).records[0].score == 42 && fromBackup, "战绩结构损坏时读取备份", checks);
                source.RecordProgress();
                Check(ReadRecords(records, out _).records[0].score == 64 && File.ReadAllText(records + ".bak") == recordBackup,
                    "战绩恢复后重新保存仍保留有效备份", checks);
                File.Delete(records);
                Check(ReadRecords(records, out fromBackup).records[0].score == 42 && fromBackup, "主战绩缺失时仍展示备份", checks);

                EndlessRun legacy = Decode(savedBackup);
                legacy.version = 0;
                legacy.bankedScore = 90;
                payload = JsonUtility.ToJson(legacy);
                string legacyBackup = JsonUtility.ToJson(new Envelope { payload = payload, checksum = Digest(payload) });
                File.WriteAllText(backup, legacyBackup);
                Check(restored.Restore(false) && restored.Run.phase == EndlessPhase.Result
                    && restored.Run.bankedScore == 90 && File.ReadAllText(backup) == legacyBackup,
                    "旧版备份安全结算可识别收益并保留原档", checks);

                // 成功交付后的提交要替换同一续玩槽；无需等待下一次自动保存。
                using var replacement = new EndlessService(directory);
                replacement.Begin(2, 0, 1104, deferFirstSave: true);
                replacement.CommitFirstSave();
                Check(restored.Restore(false) && restored.Run.runId == replacement.Run.runId && restored.Run.formation == 2,
                    "首屏成功后的提交可继续到新局", checks);
                Check(!replacement.DescribeSavedRun(false).Settled, "未结束的新档提供续玩入口", checks);

                // 布阵和整备的倒计时也要等玩家主动继续，避免续档一打开就跳过阵容操作。
                float savedFormationLeft = replacement.Run.formationLeft;
                Check(restored.Restore(false, pauseOnEntry: true) && restored.Run.phase == EndlessPhase.Paused
                    && restored.Run.previousPhase == EndlessPhase.Formation, "自动布阵档在暂停页交付", checks);
                restored.Tick(0.5f);
                Check(restored.Run.formationLeft == savedFormationLeft && restored.Pause()
                    && restored.Run.phase == EndlessPhase.Formation, "布阵倒计时只在确认继续后恢复", checks);
                replacement.Run.phase = EndlessPhase.Preparation;
                Check(replacement.Save() && restored.Restore(false, pauseOnEntry: true) && restored.Run.phase == EndlessPhase.Paused
                    && restored.Run.previousPhase == EndlessPhase.Preparation && restored.Pause()
                    && restored.Run.phase == EndlessPhase.Preparation, "整备档暂停交付并回到原阶段", checks);

                // 战斗中的自动档也要等玩家查看棋盘再推进；保持本局身份、时间与棋心。
                Check(replacement.StartWave() && replacement.Save(), "保存未暂停的战斗快照", checks);
                float savedSeconds = replacement.Run.seconds;
                Check(restored.Restore(false, pauseOnEntry: true) && restored.Run.phase == EndlessPhase.Paused
                    && restored.Run.previousPhase == EndlessPhase.Fighting && restored.Run.runId == replacement.Run.runId,
                    "玩家续玩未暂停的战斗档时先等在暂停页", checks);
                restored.Tick(0.5f);
                Check(restored.Run.seconds == savedSeconds && restored.Run.heart == replacement.Run.heart
                    && restored.Pause() && restored.Run.phase == EndlessPhase.Fighting,
                    "恢复后计时和棋心保持，确认继续回到战斗", checks);

                // 保存结算后，查看摘要必须包含归档分数，而且不改动保存文件。
                replacement.Run.score = 36;
                replacement.Finish();
                string settledFile = File.ReadAllText(path);
                var archive = replacement.DescribeSavedRun(false);
                Check(archive.Settled && archive.Description.Contains("战绩 36") && File.ReadAllText(path) == settledFile,
                    "结束的局提供只读战绩摘要，保留原始结算存档", checks);
                CheckResultSaveRetry(Path.Combine(directory, "blocked-result"), checks);
                return new Dictionary<string, object> { ["success"] = true, ["passed"] = checks.Count, ["checks"] = checks };
            }
            finally
            {
                foreach (string file in Directory.GetFiles(directory))
                {
                    File.Delete(file);
                }
                Directory.Delete(directory);
            }
        }

        /// <summary>临时目录制造两个实际写入冲突，逐一解除，验证只补写失败文件且不重复入账。</summary>
        private static void CheckResultSaveRetry(string directory, List<string> checks)
        {
            string runPath = Path.Combine(directory, RunFileName);
            string recordsPath = Path.Combine(directory, RecordFileName);
            try
            {
                // 写目标同名目录令原子移动稳定失败，文件碰撞仅发生在测试目录。
                Directory.CreateDirectory(runPath);
                Directory.CreateDirectory(recordsPath);
                using var retry = new EndlessService(directory);
                retry.Begin(0, 0, 1105, deferFirstSave: true);
                retry.CommitFirstSave();
                Check(retry.RunSavePending && retry.Run.scoreStopped, "首存失败保留待写状态并停止新增计分", checks);
                Check(retry.Pause() && !retry.Save() && retry.Run.phase == EndlessPhase.Paused,
                    "暂停保存失败仍保留暂停棋局", checks);
                retry.Run.score = 20;
                retry.Finish();
                Check(retry.HasPendingResultSave && retry.RunSavePending && retry.RecordSavePending,
                    "结算能识别两份文件各自的写入失败", checks);

                Directory.Delete(runPath);
                Check(!retry.RetryResultSave() && !retry.RunSavePending && retry.RecordSavePending,
                    "续档补写成功后仍提示待保存战绩", checks);
                string settledFile = File.ReadAllText(runPath);
                Directory.Delete(recordsPath);
                Check(retry.RetryResultSave() && retry.RetryResultSave() && File.ReadAllText(runPath) == settledFile,
                    "只重试未写入文件，成功的结算档字节保持一致", checks);
                EndlessRecordBook result = ReadRecords(recordsPath, out _);
                Check(result.records.Count == 1 && result.records[0].runId == retry.Run.runId && result.records[0].score == 20,
                    "重复确认不重复记录已结算收益", checks);
            }
            finally
            {
                // 只清理上述两个确定的占位目录和本夹具创建的文件。
                if (Directory.Exists(runPath))
                {
                    Directory.Delete(runPath);
                }
                if (Directory.Exists(recordsPath))
                {
                    Directory.Delete(recordsPath);
                }
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        File.Delete(file);
                    }
                    Directory.Delete(directory);
                }
            }
        }

        /// <summary>独立规则夹具：实体寿命、空间查询顺序、无限额布阵、固定一格升星、盘外Boss与总承伤预算。</summary>
        public static object CheckEcsRules()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1102, true);
                    Check(s.Board.GetPiece(new BoardPos(4, 4)) == null && !s.CanPlaceAt(4, 4), "棋心禁放，保持中立不占炮架", passed);
                    s.Units.Clear();
                    s.Board.Reset();
                    for (int y = 0; y < 9; y++)
                    {
                        for (int x = 0; x < 9; x++)
                        {
                            if (x == 4 && y == 4)
                            {
                                continue;
                            }
                            s.AddPlayer((PieceKind)((x + y) % 2), x, y, false);
                        }
                    }
                    Check(s.PlayerCount() == 80 && s.Occupied(GameSide.Player) == 81, "80个可用格均可放，突破旧37枚/49格配额", passed);
                    Check(s.DebugSpawnBoss(6) && s.BossUnit().Outside && s.Occupied(GameSide.Enemy) == 0, "满盘时领主仍从盘外出场，不占玩家格", passed);
                    EndlessUnit boss = s.BossUnit();
                    int hp = boss.Piece.Hp;
                    s.HitUnit(boss, hp * 100f, 0);
                    Check(boss.Piece.Hp == hp, "入场交付前不受伤，不在出生首帧秒杀", passed);
                    s.Run.seconds = boss.BornAt + V("BossEntranceSeconds");
                    for (int i = 0; i < 200; i++)
                    {
                        s.HitUnit(boss, hp, i % 6);
                    }
                    Check(boss.Piece.Hp > 0 && hp - boss.Piece.Hp <= hp * V("BossDamagePerSecond") * V("SimulationStep"), "200次多来源伤害共用一个模拟步预算", passed);
                    float xBefore = boss.Center.x;
                    s.MarchOutsideBoss(boss, 1);
                    Check(boss.Center.x != xBefore && (boss.Center.x < 0 || boss.Center.x > 8), "领主真实步进逼近，始终保持盘外", passed);
                    Check(s.Save() && s.Restore(true) && s.BossUnit().Outside && s.PlayerCount() == 80, "ECS与盘外Boss存档恢复，不减玩家配额", passed);
                }
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1202, true);
                    s.Units.Clear();
                    s.Board.Reset();
                    s.AddPlayer(PieceKind.Archer, 0, 0, false);
                    s.AddPlayer(PieceKind.Archer, 1, 0, false);
                    s.AddPlayer(PieceKind.Archer, 2, 0, false);
                    int id = s.Units[0].Piece.Id;
                    s.Run.hand.Clear();
                    s.Run.hand.Add((int)PieceKind.Archer);
                    Check(s.PlayCardAt(0, 0, 0) && s.Units.Count == 3 && s.Find(id).Piece.Size == 1 && s.Find(id).Piece.Def.Star == StarTier.Two, "单卡升星只改变一枚棋的战力，身体仍一格且伙伴保留", passed);
                    s.Run.seconds = V("MinuteSeconds") * 12;
                    Check(s.WaveDensityScale() > 1 && s.WaveSpeedScale() > 1, "后续波次增加出生数量和现存敌人移速", passed);
                }
                return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }

        /// <summary>显式压力造局只允许隔离局：沿正式出生入口填满预算，不访问玩家存档。</summary>
        public bool DebugStress(int enemies)
        {
            if (!Isolated || Run == null || enemies < 1 || enemies > Horde.Length)
            {
                return Reject("压力夹具需要隔离局与有效并发数");
            }
            Horde.Clear();
            for (int i = 0; i < enemies; i++)
            {
                if (!SpawnHorde())
                {
                    return Reject("压力夹具生成未完成");
                }
            }
            Run.phase = EndlessPhase.Fighting;
            return true;
        }

        /// <summary>
        /// 固定输入对照旧全扫描与ECS空间查询：同一组1024/240实体、2000矩形、逐个核对数量与顺序。
        /// 计时预热后各执行5轮，中位数只代表查询CPU，不替代整帧/GPU或手机验收。
        /// </summary>
        public static object BenchmarkEcs()
        {
            var rows = new List<object>();
            foreach (int count in new[] { 240, 1024 })
            {
                var world = new EndlessEcsWorld(count);
                var random = new System.Random(1102);
                var old = new EndlessHorde[count];
                for (int i = 0; i < count; i++)
                {
                    old[i] = new EndlessHorde { active = true, id = i + 1, hp = 100, maxHp = 100, x = (float)random.NextDouble() * 24 - 8, y = (float)random.NextDouble() * 12 - 2 };
                    world.Horde[i] = old[i];
                }
                var queries = new Vector2[2000];
                for (int i = 0; i < queries.Length; i++)
                {
                    queries[i] = new Vector2((float)random.NextDouble() * 12 - 2, (float)random.NextDouble() * 8);
                }
                var baseline = new List<int>(count);
                var result = new List<int>(count);
                foreach (Vector2 q in queries)
                {
                    Scan(old, q, baseline);
                    world.Query(q.x - 1.5f, q.y - 1.5f, q.x + 1.5f, q.y + 1.5f, result);
                    if (baseline.Count != result.Count)
                    {
                        throw new InvalidOperationException("ECS候选数量不一致");
                    }
                    for (int i = 0; i < result.Count; i++)
                    {
                        if (baseline[i] != result[i])
                        {
                            throw new InvalidOperationException("ECS候选顺序不一致");
                        }
                    }
                }
                var before = new double[5];
                var after = new double[5];
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    long started = Stopwatch.GetTimestamp();
                    foreach (Vector2 q in queries)
                    {
                        Scan(old, q, baseline);
                    }
                    before[repeat] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                    started = Stopwatch.GetTimestamp();
                    foreach (Vector2 q in queries)
                    {
                        world.Query(q.x - 1.5f, q.y - 1.5f, q.x + 1.5f, q.y + 1.5f, result);
                    }
                    after[repeat] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                }
                Array.Sort(before);
                Array.Sort(after);
                rows.Add(new Dictionary<string, object> { ["entities"] = count, ["queries"] = queries.Length, ["repeats"] = 5, ["scanMedianMs"] = before[2], ["ecsMedianMs"] = after[2], ["scanRoundsMs"] = before, ["ecsRoundsMs"] = after, ["sameCandidatesAndOrder"] = true });
                int generation = world.Horde.Generation(0);
                world.Horde[0] = default;
                world.Horde[0] = new EndlessHorde { active = true, id = 100000, hp = 10, maxHp = 10 };
                if (world.Horde.Generation(0) == generation)
                {
                    throw new InvalidOperationException("实体槽复用未更新代次");
                }
            }
            return new Dictionary<string, object> { ["success"] = true, ["scope"] = "Unity主线程固定查询CPU，非整帧/设备", ["rows"] = rows };
        }
        private static void Scan(EndlessHorde[] data, Vector2 center, List<int> result)
        {
            result.Clear();
            for (int i = 0; i < data.Length; i++)
            {
                EndlessHorde h = data[i];
                if (h.active && h.hp > 0 && h.x >= center.x - 1.5f && h.x <= center.x + 1.5f && h.y >= center.y - 1.5f && h.y <= center.y + 1.5f)
                {
                    result.Add(i);
                }
            }
        }
    }
}
#endif
