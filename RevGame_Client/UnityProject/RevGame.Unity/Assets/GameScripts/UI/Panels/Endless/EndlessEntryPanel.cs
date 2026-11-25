using System;
using DG.Tweening;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽入口：选一个棋阵，点开始。只收集输入，实际加载与存档恢复由唯一的 GameFlow 执行。
    /// 新手不需要看规则说明，也不用选难度；“继续”只在有存档时才出现。
    /// 打开与关闭沿用框架弹窗动画，阵型卡按选择状态显示色彩和回弹。
    /// </summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Popup)]
    public sealed class EndlessEntryPanel : RevUIPanel
    {
        /// <summary>入口固定普通难度：难度选择对第一次玩的人没有意义。</summary>
        private const int NormalDifficulty = 0;
        /// <summary>已选阵型使用浅金高亮，仍保留图案和深字反差。</summary>
        private static readonly Color Selected = new Color(1f, .91f, .63f);
        [RevBind("best")] private Text _best;
        [RevBind("resume")] private GameObject _resume;
        /// <summary>现有开始按钮也提供确认框的字体和按钮样式。</summary>
        [RevBind("start")] private Button _start;
        /// <summary>本次新开确认，取消或关闭入口时销毁，不接管续玩动作。</summary>
        private GameConfirmation _replaceConfirmation;
        /// <summary>隔离 Play 的新开和续玩都走内存槽，界面不会写玩家存档。</summary>
        private static bool Isolated => RevServices.GetRequired<StoryProgress>().IsIsolated;
        /// <summary>三张棋阵卡的图片，被选中的染金。</summary>
        private readonly Image[] _formations = new Image[3];
        /// <summary>当前选中的棋阵序号。</summary>
        private int _formation;
        private EndlessRecordBook _records;
        /// <summary>弹窗打开：回弹弹入（六章弹窗默认手感）；onDone 交给动画，播完框架才算打开完成。</summary>
        protected override void PlayOpenTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.PopIn, .3f, onDone, owner: this);
        }
        /// <summary>弹窗关闭：弹出收起（六章设置面板同款），播完才真正隐藏。</summary>
        protected override void PlayCloseTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.PopOut, .12f, onDone, owner: this);
        }
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "templeArt");
            for (int i = 0; i < _formations.Length; i++)
            {
                _formations[i] = transform.Find("formation" + i).GetComponent<Image>();
            }
        }
        /// <summary>打开时读出本地战绩；隔离会话使用其独立的内存续玩槽。</summary>
        protected override void OnOpen()
        {
            try
            {
                _records = Isolated ? null : EndlessService.ReadRecords();
            }
            catch (Exception error)
            {
                // 纪录读不出来不影响开局，原档也不动，只记日志。
                _records = null;
                RevLog.Warn("无尽本地纪录读取失败，原档保留：" + error.Message, "RevGame");
            }
        }
        /// <summary>选择变化或首次打开时更新卡图和文案，静止界面不重复写 UI。</summary>
        protected override void OnRefreshView()
        {
            for (int i = 0; i < _formations.Length; i++)
            {
                Color color = i == _formation ? Selected : Color.white;
                if (_formations[i].color != color)
                {
                    _formations[i].color = color;
                }
            }
            bool canResume = EndlessService.HasSavedRun(Isolated);
            if (_resume.activeSelf != canResume)
            {
                _resume.SetActive(canResume);
            }
            long best = BestScore();
            string text = best > 0 ? "最高战绩 " + best : "第一次挑战，选一个棋阵吧";
            if (_best.text != text)
            {
                _best.text = text;
            }
        }
        /// <summary>从本地纪录里找普通难度的最高分。</summary>
        private long BestScore()
        {
            long best = 0;
            if (_records == null)
            {
                return best;
            }
            if (_records.bestByDifficulty != null && _records.bestByDifficulty.Length > NormalDifficulty)
            {
                EndlessRecord record = _records.bestByDifficulty[NormalDifficulty];
                if (record != null && record.version == 1)
                {
                    best = record.score;
                }
            }
            for (int i = 0; i < _records.records.Count; i++)
            {
                EndlessRecord record = _records.records[i];
                if (record.version == 1 && record.difficulty == NormalDifficulty)
                {
                    best = Math.Max(best, record.score);
                }
            }
            return best;
        }
        private void Pick(int index)
        {
            _formation = index;
            // 选中的棋阵卡弹一下，和六章选牌的反馈一致；先收尾上一次弹跳，连点不会越弹越大。
            Transform card = _formations[index].transform;
            card.DOKill(true);
            card.localScale = Vector3.one;
            card.DOPunchScale(Vector3.one * .08f, .28f, 6, .5f).SetUpdate(true);
            RefreshView();
        }
        [RevButtonClick("formation0")] private void Formation0()
        {
            Pick(0);
        }
        [RevButtonClick("formation1")] private void Formation1()
        {
            Pick(1);
        }
        [RevButtonClick("formation2")] private void Formation2()
        {
            Pick(2);
        }
        /// <summary>准备新局。有续档时先显示旧局摘要，玩家确认后才提交新局请求。</summary>
        [RevButtonClick("start")]
        private void StartRun()
        {
            if (_replaceConfirmation?.IsOpen == true)
            {
                return;
            }
            bool isolated = Isolated;
            int formation = _formation;
            if (!EndlessService.HasSavedRun(isolated))
            {
                BeginNewRun(formation, isolated);
                return;
            }

            string previous = RevServices.GetRequired<EndlessService>().DescribeSavedRun(isolated);
            _replaceConfirmation = new GameConfirmation(_start, "开始新的无尽挑战？",
                previous + "\n新局成功进入战场后会替换这份续档。",
                "开始新局", "取消", () => BeginNewRun(formation, isolated));
        }

        /// <summary>把确认时选定的棋阵交给流程，旧槽由流程在新首屏就绪后替换。</summary>
        private static void BeginNewRun(int formation, bool isolated)
        {
            RevServices.GetRequired<GameFlow>().EnterEndless(formation, NormalDifficulty, Environment.TickCount, isolated: isolated);
        }

        /// <summary>从当前存档槽提交续玩，确认框开着时底层继续入口保持门禁。</summary>
        [RevButtonClick("resume")]
        private void Resume()
        {
            if (_replaceConfirmation?.IsOpen != true)
            {
                RevServices.GetRequired<GameFlow>().EnterEndless(0, NormalDifficulty, 1, resume: true, isolated: Isolated);
            }
        }

        /// <summary>退出入口时释放确认框及其点击遮挡。</summary>
        protected override void OnClose()
        {
            _replaceConfirmation?.Dispose();
            _replaceConfirmation = null;
        }
        [RevButtonClick("close")] private void Back()
        {
            CloseSelf();
        }
    }
}
