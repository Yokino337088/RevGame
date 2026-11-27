#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using Revolution;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>用独立临时目录验证写入门禁、满池落盘、主备档恢复与战绩读取，退出前清理测试文件。</summary>
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
                CheckHordeSaveSlots(directory, checks);
                CheckFullPoolSaveRecovery(directory, checks);
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

        /// <summary>通过临时磁盘槽验证满池保存、异常读回、主档损坏后的续玩及备份保留。</summary>
        private static void CheckFullPoolSaveRecovery(string directory, List<string> checks)
        {
            using var source = new EndlessService(directory);
            using var restored = new EndlessService(directory);
            string path = Path.Combine(directory, RunFileName);
            string backupPath = path + ".bak";
            source.Begin(1, 0, 1101, deferFirstSave: true);
            source.CommitFirstSave();
            Check(source.StartWave(), "满池续玩夹具沿正式路径开战", checks);
            FillSaveCheckHorde(source);

            source.Run.score = 80;
            Check(source.Save(), "满池首次写入真实磁盘", checks);
            string backup = File.ReadAllText(path);
            source.Run.score = 120;
            Check(source.Save() && File.ReadAllText(backupPath) == backup, "满池新档提交时留下上一份有效备份", checks);
            string main = File.ReadAllText(path);
            Check(restored.Restore(false, pauseOnEntry: true) && restored.Run.score == 120 &&
                restored.Run.phase == EndlessPhase.Paused && restored.Run.runId == source.Run.runId &&
                SameFullHorde(source.Horde, restored.Horde), "满池主档恢复同一局以及每个敌人的槽号和状态", checks);

            // 两份文本都可读且各有正确校验和。故意提交不同的一份，验证读回比较能拒绝这次替换。
            bool rejectedReceipt = false;
            try
            {
                RevFileSave.Write(path, backup, value => VerifySaveReadback(main, value));
            }
            catch (InvalidDataException)
            {
                rejectedReceipt = true;
            }
            Check(rejectedReceipt && File.ReadAllText(path) == main && File.ReadAllText(backupPath) == backup &&
                Directory.GetFiles(directory, "*.tmp").Length == 0, "读回不同快照时拒绝替换、保留两份旧档并清除临时档", checks);

            source.Run.score = -1;
            Check(!source.Save() && source.RunSavePending && source.Run.scoreStopped &&
                File.ReadAllText(path) == main && File.ReadAllText(backupPath) == backup,
                "存档基本信息非法时暂停计分且保留主档和备份", checks);

            File.WriteAllText(path, "{}");
            Check(restored.Restore(false, pauseOnEntry: true) && restored.Run.score == 80 &&
                restored.Run.phase == EndlessPhase.Paused && restored.Run.random == source.Run.random &&
                SameFullHorde(source.Horde, restored.Horde), "满池主档损坏后回退备份并保留随机状态和全部敌人", checks);
            Check(restored.Save() && File.ReadAllText(backupPath) == backup, "满池恢复后的首笔保存保留原有效备份", checks);
            Check(restored.Pause() && restored.Run.phase == EndlessPhase.Fighting, "从满池备份恢复后可确认继续战斗", checks);
#if UNITY_EDITOR
            CheckBackgroundCheckpoints(restored, path, backupPath, backup, checks);
#endif
        }

        /// <summary>测试与计时都沿正式出生入口填满外围槽。任何出生失败立即结束当前检查。</summary>
        private static void FillSaveCheckHorde(EndlessService source)
        {
            for (int slot = 0; slot < source.Horde.Length; slot++)
            {
                if (!source.SpawnHorde())
                {
                    throw new InvalidOperationException("满池存档夹具未完成敌潮出生");
                }
            }
        }

#if UNITY_EDITOR || UNITY_ANDROID
        /// <summary>对临时磁盘槽检查后台快照隔离、备份保护、异常回执与显式保存顺序。</summary>
        private static void CheckBackgroundCheckpoints(EndlessService service, string path, string backupPath, string backup, List<string> checks)
        {
            File.WriteAllText(path, "{}");
            Check(service.Restore(false) && service.Run.score == 80, "后台回归先从同一份满池备份恢复", checks);
            service.Run.score = 180;
            string expected = EncodeSave(service.CaptureSaveSnapshot());
            EndlessRun detached = service.CaptureSaveSnapshot();
            // 遍历 Run 的公开字段。以后新增引用容器时，快照仍须持有独立副本。
            foreach (var field in typeof(EndlessRun).GetFields())
            {
                object live = field.GetValue(service.Run);
                if (live != null && !field.FieldType.IsValueType && field.FieldType != typeof(string) &&
                    ReferenceEquals(live, field.GetValue(detached)))
                {
                    throw new InvalidOperationException("后台存档仍共享实时容器：" + field.Name);
                }
            }
            service.RequestRunCheckpoint();
            Check(service._checkpointWrite != null, "原生自动存档已交给后台工作线程", checks);
            service.Run.score = 190;
            service.Run.hand.Clear();
            service.Horde[0] = default;
            service.FinishRunCheckpoint();
            Check(File.ReadAllText(path) == expected && File.ReadAllText(backupPath) == backup,
                "后台仍保存请求时的完整敌潮，并保留恢复所用的备份", checks);

            // 在旧任务仍可能写入时提交手动保存。完成后主档必须是手动保存的更新状态。
            service.RequestRunCheckpoint();
            service.RequestRunCheckpoint();
            service.Run.score = 200;
            Check(service.Save() && Decode(File.ReadAllText(path)).score == 200 && service._checkpointWrite == null &&
                !service._checkpointRequested, "显式保存等待旧任务并覆盖合并请求", checks);

            string recentMain = File.ReadAllText(path);
            string recentBackup = File.ReadAllText(backupPath);
            service.Run.score = -1;
            service.RequestRunCheckpoint();
            service.FinishRunCheckpoint();
            Check(service.RunSavePending && service.Run.scoreStopped &&
                File.ReadAllText(path) == recentMain && File.ReadAllText(backupPath) == recentBackup,
                "后台失败回执在主线程冻结计分并保留主备档", checks);

            service.Run.score = 210;
            service.RequestRunCheckpoint();
            service.Begin(1, 0, 1102, deferFirstSave: true);
            Check(service.Run.seed == 1102 && service._checkpointWrite == null && Decode(File.ReadAllText(path)).score == 210,
                "换局收齐后台任务，候选局首屏前保留旧档", checks);
        }

        /// <summary>真实文件上的满池保存对照。记录主线程调用窗口与从提交到观察到写完的时间，所有文件归临时目录。</summary>
        public static object BenchmarkFullPoolCheckpoints()
        {
            string directory = Path.Combine(Path.GetTempPath(), "RevGame-Endless-Bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var source = new EndlessService(directory);
                source.Begin(1, 0, 1101, deferFirstSave: true);
                source.CommitFirstSave();
                if (!source.StartWave())
                {
                    throw new InvalidOperationException(source.LastError);
                }
                source.Run.nextId = 100;
                source.Run.laneCursor = 0;
                FillSaveCheckHorde(source);
                const int samples = 96;
                const int warmups = 3;
                double millisecondsPerTick = 1000d / Stopwatch.Frequency;
                var synchronous = new double[samples];
                var foreground = new double[samples];
                var observedCommit = new double[samples];
                for (int round = -warmups; round < samples; round++)
                {
                    // 每对交换先后，快照内容和磁盘目录相同。轮询只用于计时台，不计入生产的主线程工作量。
                    for (int order = 0; order < 2; order++)
                    {
                        bool background = ((round + order) & 1) != 0;
                        long began = Stopwatch.GetTimestamp();
                        if (background)
                        {
                            source.RequestRunCheckpoint();
                            long returned = Stopwatch.GetTimestamp();
                            var work = source._checkpointWrite;
                            if (work == null || !System.Threading.SpinWait.SpinUntil(() => work.TryGetResult(wait: false, out _), 5000))
                            {
                                throw new InvalidOperationException("后台满池保存未完成");
                            }
                            long committed = Stopwatch.GetTimestamp();
                            source.AdvanceRunCheckpoint();
                            if (source.RunSavePending)
                            {
                                throw new InvalidOperationException(source.LastError);
                            }
                            if (round >= 0)
                            {
                                foreground[round] = (returned - began) * millisecondsPerTick;
                                observedCommit[round] = (committed - began) * millisecondsPerTick;
                            }
                        }
                        else
                        {
                            if (!source.Save())
                            {
                                throw new InvalidOperationException(source.LastError);
                            }
                            if (round >= 0)
                            {
                                synchronous[round] = (Stopwatch.GetTimestamp() - began) * millisecondsPerTick;
                            }
                        }
                    }
                }
                string path = Path.Combine(directory, RunFileName);
                string main = File.ReadAllText(path);
                if (main != File.ReadAllText(path + ".bak") || Decode(main).horde.Length != source.Horde.Length ||
                    source.Horde.ActiveCount != source.Horde.Length)
                {
                    throw new InvalidDataException("同步和后台未保存同一份满池快照");
                }
                return new Dictionary<string, object>
                {
                    ["enemyCount"] = source.Horde.Length, ["fileBytes"] = new FileInfo(path).Length,
                    ["synchronousMs"] = synchronous, ["foregroundMs"] = foreground, ["observedCommitMs"] = observedCommit
                };
            }
            finally
            {
                // 测量实例先释放后台任务，随后只删除本次工作目录中的文件。
                foreach (string file in Directory.GetFiles(directory))
                {
                    File.Delete(file);
                }
                Directory.Delete(directory);
            }
        }
#endif

        /// <summary>逐槽对照满池快照的值。容量、存活数或任一敌人的字段不同，均视为恢复失败。</summary>
        private static bool SameFullHorde(EndlessHordeComponents expected, EndlessHordeComponents actual)
        {
            if (actual.Length != expected.Length || actual.ActiveCount != expected.Length)
            {
                return false;
            }
            for (int slot = 0; slot < expected.Length; slot++)
            {
                if (!actual[slot].Equals(expected[slot]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>用临时槽验证零敌快照、完整长度旧档和含空洞的新档。续玩后还要按原槽号复用。</summary>
        private static void CheckHordeSaveSlots(string directory, List<string> checks)
        {
            using var source = new EndlessService(directory);
            using var restored = new EndlessService(directory);
            string path = Path.Combine(directory, RunFileName);
            source.Begin(1, 0, 1108, deferFirstSave: true);
            source.CommitFirstSave();
            EndlessRun legacy = Decode(File.ReadAllText(path));
            Check(legacy.horde.Length == 0 && restored.Restore(false) && restored.Horde.ActiveCount == 0
                && restored.Horde.FirstFree() == 0, "空外围写出空快照并恢复可用容量", checks);
            Check(source.SpawnHorde() && source.SpawnHorde() && source.SpawnHorde(), "用正式出生入口占用前三个槽", checks);
            EndlessHorde first = source.Horde[0];
            EndlessHorde vacant = source.Horde[1];
            EndlessHorde last = source.Horde[2];
            last.attackAt = 7.25f;
            last.burnDamage = 2.5f;
            source.Horde[2] = last;
            // 旧文件会保留死敌字段，夹具在中间和末尾都放一份，检验任何空槽都不会变成活敌。
            vacant.active = false;
            source.Horde[1] = vacant;
            source.Horde[source.Horde.Length - 1] = vacant;
            legacy.horde = new EndlessHorde[source.Horde.Length];
            for (int slot = 0; slot < legacy.horde.Length; slot++)
            {
                legacy.horde[slot] = source.Horde[slot];
            }
            legacy.nextId = source.Run.nextId;
            legacy.laneCursor = source.Run.laneCursor;
            string payload = JsonUtility.ToJson(legacy);
            File.WriteAllText(path, JsonUtility.ToJson(new Envelope { payload = payload, checksum = Digest(payload) }));
            Check(restored.Restore(false) && restored.Horde.ActiveCount == 2 && restored.Horde[2].Equals(last)
                && restored.Horde.FirstFree() == 1, "旧版满长度槽数组仍可恢复身份、状态与最低空槽", checks);

            Check(source.Save(), "带空洞的外围实际落盘", checks);
            EndlessHorde[] snapshot = Decode(File.ReadAllText(path)).horde;
            Check(snapshot.Length == 3 && snapshot[0].Equals(first) && snapshot[1].Equals(default(EndlessHorde))
                && snapshot[2].Equals(last), "新快照省略尾部空槽并保持活跃敌人的精确槽号和状态", checks);
            Check(restored.Restore(false) && restored.Horde.ActiveCount == 2 && restored.Horde[2].Equals(last)
                && restored.Horde.FirstFree() == 1, "新档恢复保留槽洞和剩余容量", checks);
            int nextId = restored.Run.nextId;
            Check(restored.SpawnHorde() && restored.Horde[1].id == nextId && restored.Horde.FirstFree() == 3,
                "续玩时下一个敌人先填最低空槽并使用续档身份", checks);

            // 用两槽世界覆盖满池后腾空的回退边界，避免等待实际棋局刷满才验证。
            var pool = new EndlessHordeComponents(2);
            pool[0] = first;
            pool[1] = last;
            Check(pool.FirstFree() == -1, "满槽返回不可出生", checks);
            pool[0] = default;
            Check(pool.FirstFree() == 0, "腾出低位后空槽查询及时回退", checks);
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

        /// <summary>对照真实棋盘上逐格查询和行缓存。覆盖两侧、棋心断点、移位、死亡和移除，并返回稳定占格的 CPU 局部用时。</summary>
        public static object CheckMarchBlockers()
        {
            using var service = new EndlessService();
            service.Begin(0, 0, 1101, isolated: true, deferFirstSave: true);
            var queries = new List<(int row, int gate, float x)>();
            for (int row = 0; row < MarchBoardSide; row++)
            {
                for (int gate = 0; gate < 2; gate++)
                {
                    // 四分之一格步长覆盖半格临界点和敌人从盘外入场的坐标。
                    for (int offset = -16; offset <= 48; offset++)
                    {
                        queries.Add((row, gate, offset * .25f));
                    }
                }
            }

            // 前一只敌人可能在同一步打死棋子。比较变更前后，确认缓存不滞后一只怪。
            int checkedQueries = 0;
            void Compare()
            {
                foreach (var q in queries)
                {
                    Piece old = service.ScanMarchBlocker(q.row, q.gate, q.x, out int oldCol);
                    Piece cached = service.FindMarchBlocker(q.row, q.gate, q.x, out int cachedCol);
                    if (!ReferenceEquals(old, cached) || oldCol != cachedCol)
                    {
                        throw new InvalidOperationException("无尽敌潮挡路棋子或列号与逐格查询不同");
                    }
                }
                checkedQueries += queries.Count;
            }

            Compare();
            Piece moved = service.Units[0].Piece;
            bool didMove = false;
            for (int row = 0; row < MarchBoardSide && !didMove; row++)
            {
                for (int col = 0; col < MarchBoardSide && !didMove; col++)
                {
                    var destination = new BoardPos(col, row);
                    if (row != moved.Pos.Y)
                    {
                        didMove = service.Board.Move(moved, destination, respectEnemyEyes: false);
                    }
                }
            }
            if (!didMove)
            {
                throw new InvalidOperationException("挡路缓存对照未找到可移动到另一行的位置");
            }
            Compare();
            moved.TakeDamage(moved.Hp);
            Compare();
            if (!service.Board.Remove(moved))
            {
                throw new InvalidOperationException("挡路缓存对照无法移除棋子");
            }
            Compare();

            // 在相同占格上交替先后。校验和保持两条计时路径实际读取相同的棋子和列。
            const int rounds = 5;
            const int repeats = 80;
            var scanMs = new double[rounds];
            var cacheMs = new double[rounds];
            for (int round = -1; round < rounds; round++)
            {
                int checksum = 0;
                for (int order = 0; order < 2; order++)
                {
                    bool cached = ((round + order) & 1) != 0;
                    int sum = 0;
                    long began = Stopwatch.GetTimestamp();
                    for (int iter = 0; iter < repeats; iter++)
                    {
                        foreach (var q in queries)
                        {
                            int column;
                            Piece hit = cached ? service.FindMarchBlocker(q.row, q.gate, q.x, out column) :
                                service.ScanMarchBlocker(q.row, q.gate, q.x, out column);
                            sum += column + (hit?.Id ?? 0);
                        }
                    }
                    double ms = (Stopwatch.GetTimestamp() - began) * 1000d / Stopwatch.Frequency;
                    if (order == 0)
                    {
                        checksum = sum;
                    }
                    else if (checksum != sum)
                    {
                        throw new InvalidOperationException("计时遍历结果不同");
                    }
                    if (round >= 0)
                    {
                        (cached ? cacheMs : scanMs)[round] = ms;
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["success"] = true, ["checkedQueries"] = checkedQueries, ["queriesPerRound"] = queries.Count * repeats,
                ["scanMs"] = scanMs, ["cacheMs"] = cacheMs
            };
        }

        /// <summary>保留原来逐格取棋的验证口径。取棋心之前先判断坐标是否已越过，顺序与原生产循环一致。</summary>
        private Piece ScanMarchBlocker(int row, int gate, float x, out int column)
        {
            float dir = gate == 0 ? 1f : -1f;
            for (int step = 0; step < MarchBoardSide; step++)
            {
                int col = gate == 0 ? step : MarchBoardSide - 1 - step;
                if ((col - x) * dir < -.5f)
                {
                    continue;
                }
                if (row == 4 && col == 4)
                {
                    break;
                }
                Piece piece = Board.GetPiece(new BoardPos(col, row));
                if (piece != null && piece.Side == GameSide.Player && piece.IsAlive)
                {
                    column = col;
                    return piece;
                }
            }
            column = 4;
            return null;
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
