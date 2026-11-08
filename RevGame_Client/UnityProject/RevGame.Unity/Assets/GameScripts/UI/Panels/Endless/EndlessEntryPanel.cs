using System;
using DG.Tweening;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽入口：选一个棋阵，点开始。只收集输入，实际加载与存档恢复由唯一的 GameFlow 执行。
    /// 新手不需要看规则说明，也不用选难度；“继续”只在有存档时才出现。
    /// 动画沿用六章弹窗手感：打开回弹弹入、关闭弹出收起，选中的棋阵卡弹一下；底板流光来自预制体上的 ArcanePanel 共享材质。
    /// </summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Popup)]
    public sealed class EndlessEntryPanel : RevUIPanel
    {
        /// <summary>入口固定普通难度：难度选择对第一次玩的人没有意义。</summary>
        private const int NormalDifficulty = 0;
        private static readonly Color Selected = new Color(1f, .84f, .38f);
        [RevBind("best")] private Text _best;
        [RevBind("resume")] private GameObject _resume;
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
        protected override void OnOpen()
        {
            try
            {
                _records = EndlessService.ReadRecords();
            }
            catch (Exception error)
            {
                // 纪录读不出来不影响开局，原档也不动，只记日志。
                _records = null;
                RevLog.Warn("无尽本地纪录读取失败，原档保留：" + error.Message, "RevGame");
            }
        }
        protected override void OnRefreshView()
        {
            for (int i = 0; i < _formations.Length; i++)
            {
                _formations[i].color = i == _formation ? Selected : Color.white;
            }
            _resume.SetActive(EndlessService.HasSavedRun());
            long best = BestScore();
            _best.text = best > 0 ? "最高战绩 " + best : "第一次挑战，选一个棋阵吧";
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
        [RevButtonClick("start")] private void StartRun()
        {
            RevServices.GetRequired<GameFlow>().EnterEndless(_formation, NormalDifficulty, Environment.TickCount);
        }
        [RevButtonClick("resume")] private void Resume()
        {
            RevServices.GetRequired<GameFlow>().EnterEndless(0, NormalDifficulty, 1, true);
        }
        [RevButtonClick("close")] private void Back()
        {
            CloseSelf();
        }
    }
}
