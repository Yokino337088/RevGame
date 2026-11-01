using System;
using RevGame.Flow;
using Revolution;
using UnityEngine.UI;
namespace RevGame.Endless
{
    /// <summary>棋阵选择只收集输入，实际加载与存档恢复由唯一GameFlow执行。</summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Popup)]
    public sealed class EndlessEntryPanel : RevUIPanel
    {
        [RevBind("description")] private Text _description;
        private int _formation, _difficulty;
        private EndlessRecordBook _records;
        private string _recordError;
        protected override void OnBindView()
        {
        }
        protected override void OnOpen()
        {
            try
            {
                _records = EndlessService.ReadRecords();
                _recordError = "";
            }
            catch (Exception error)
            {
                _records = new EndlessRecordBook();
                _recordError = "本地纪录读取失败，原档保留：" + error.Message;
            }
        }
        protected override void OnRefreshView()
        {
            string[] formations =
            {
                "弩雨棋阵 · 双弩与护卫\n直线穿透，适合首次挑战", "炎环棋阵 · 火术与双护卫\n隔棋开火，燃烧连锁清场", "守眼棋阵 · 驻眼与眼位石\n稳守成眼，脉冲与有限召唤"
            };
            string[] modes =
            {
                "普通 · 标准属性", "困难 · 更强敌潮", "每日 · 当天固定种子"
            };
            _description.text = formations[_formation] + "\n\n" + modes[_difficulty] + "\n\n六章进度不影响入口，局外攻血不带入。";
            long best = 0;
            if (_records != null)
            {
                if (_records.bestByDifficulty != null && _records.bestByDifficulty.Length == 3)
                {
                    EndlessRecord record = _records.bestByDifficulty[_difficulty];
                    if (record != null && record.version == 1 && (_difficulty != 2 || record.seed == int.Parse(DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd"))))
                    {
                        best = record.score;
                    }
                }
                for (int i = 0; i < _records.records.Count; i++)
                {
                    EndlessRecord record = _records.records[i];
                    if (record.version == 1 && record.difficulty == _difficulty && (_difficulty != 2 || record.seed == int.Parse(DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd"))))
                    {
                        best = Math.Max(best, record.score);
                    }
                }
                _description.text += "\n本模式个人最佳 " + best + " · 进化图鉴 " + CountBits(_records.evolutionMask) + "/6";
            }
            _description.text += "\n" + _recordError;
        }
        [RevButtonClick("formation0")] private void Formation0()
        {
            _formation = 0;
            RefreshView();
        }
        [RevButtonClick("formation1")] private void Formation1()
        {
            _formation = 1;
            RefreshView();
        }
        [RevButtonClick("formation2")] private void Formation2()
        {
            _formation = 2;
            RefreshView();
        }
        [RevButtonClick("difficulty")] private void Difficulty()
        {
            _difficulty = (_difficulty + 1) % 3;
            RefreshView();
        }
        [RevButtonClick("start")] private void StartRun()
        {
            int seed = _difficulty == 2 ? int.Parse(DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd")) : Environment.TickCount;
            RevServices.GetRequired<GameFlow>().EnterEndless(_formation, _difficulty, seed);
        }
        [RevButtonClick("resume")] private void Resume()
        {
            if (EndlessService.HasSavedRun())
            {
                RevServices.GetRequired<GameFlow>().EnterEndless(0, 0, 1, true);
            }
            else
            {
                _description.text = "还没有无尽存档，选择棋阵开始第一局。";
            }
        }
        [RevButtonClick("close")] private void Back()
        {
            CloseSelf();
        }
        private static int CountBits(int value)
        {
            int count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }
            return count;
        }
    }
}
