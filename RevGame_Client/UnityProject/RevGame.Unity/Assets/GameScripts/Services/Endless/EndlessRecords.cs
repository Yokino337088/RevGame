using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
namespace RevGame.Endless
{
    /// <summary>
    /// 只保存本地个人记录和横向图鉴，不给六章增加永久攻血。记录按规则版本/难度/每日种子分开。
    /// 相同runId只更新最大已记账收益，反复结算、继续后失败不会重发旧巡回奖励。
    /// </summary>
    [Serializable]
    public sealed class EndlessRecordBook
    {
        public int version = 1, evolutionMask, skinMask = 1;
        public List<EndlessRecord> records = new List<EndlessRecord>();
        public EndlessRecord[] bestByDifficulty = new EndlessRecord[3];
    }
    [Serializable]
    public sealed class EndlessRecord
    {
        public string runId, title;
        public int version, difficulty, seed, circuits, kills;
        public float seconds;
        public long score;
    }
    public sealed partial class EndlessService
    {
        private const string RecordFileName = "endless-records-v1.json";

        public static EndlessRecordBook ReadRecords()
        {
            return ReadRecords(Path.Combine(Application.persistentDataPath, RecordFileName), out _);
        }

        private static EndlessRecordBook ReadRecords(string path, out bool fromBackup)
        {
            if (!SaveFileExists(path))
            {
                fromBackup = false;
                return new EndlessRecordBook();
            }
            return ReadSaveFile(path, DecodeRecords, out fromBackup);
        }

        private static EndlessRecordBook DecodeRecords(string json)
        {
            var book = JsonUtility.FromJson<EndlessRecordBook>(json);
            if (book == null || book.records == null || book.records.Exists(record => record == null || string.IsNullOrEmpty(record.runId)))
            {
                throw new InvalidDataException("无尽战绩结构不兼容");
            }
            return book;
        }

        private void RecordProgress()
        {
            if (Isolated)
            {
                return;
            }
            try
            {
                string path = Path.Combine(_saveDirectory, RecordFileName);
                EndlessRecordBook book = ReadRecords(path, out bool fromBackup);
                EndlessRecord record = book.records.Find(item => item.runId == Run.runId);
                if (record == null)
                {
                    record = new EndlessRecord
                    {
                        runId = Run.runId, version = Run.version, difficulty = Run.difficulty, seed = Run.seed
                    };
                    book.records.Add(record);
                }
                record.score = Math.Max(record.score, Run.bankedScore);
                record.seconds = Math.Max(record.seconds, Run.seconds);
                record.circuits = Math.Max(record.circuits, Run.completedCircuits);
                record.kills = Math.Max(record.kills, Run.kills + Run.invaderKills);
                int leading = Run.formation;
                for (int i = 0; i < 6; i++)
                {
                    if (Run.damage[i] > Run.damage[leading])
                    {
                        leading = i;
                    }
                    if (Run.evolved[i])
                    {
                        book.evolutionMask |= 1 << i;
                    }
                }
                record.title = Run.evolved[leading] ? EndlessConfig.Route(leading).Evolution : EndlessConfig.Route(leading).Name + "棋手";
                if (book.bestByDifficulty == null || book.bestByDifficulty.Length != 3)
                {
                    book.bestByDifficulty = new EndlessRecord[3];
                }
                EndlessRecord best = book.bestByDifficulty[Run.difficulty];
                if (best == null || record.score > best.score || (Run.difficulty == 2 && best.seed != Run.seed))
                {
                    book.bestByDifficulty[Run.difficulty] = record;
                }
                book.skinMask |= 1 << Math.Min(3, Run.completedCircuits);
                // 本地只保留有限场次，不让“无限局数”成为无限历史内存；每种难度最高分优先保留。
                book.records.Sort((a, b) => b.score.CompareTo(a.score));
                while (book.records.Count > N("RecordLimit"))
                {
                    book.records.RemoveAt(book.records.Count - 1);
                }
                string json = JsonUtility.ToJson(book);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, json, Encoding.UTF8);
                if (DecodeRecords(File.ReadAllText(temporary, Encoding.UTF8)).records.Count != book.records.Count)
                {
                    throw new InvalidDataException("本地纪录写入校验失败");
                }
                ReplaceSaveFile(temporary, path, fromBackup);
            }
            catch (Exception error)
            {
                Run.scoreStopped = true;
                Run.error = "纪录保存失败，停止新增计分：" + error.Message;
            }
        }
    }
}
