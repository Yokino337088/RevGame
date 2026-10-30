using System.Collections.Generic;
using DG.Tweening;
using RevGame.Core;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>HUD的积分表现分部：所有资源和动画归面板所有，生命周期与提示面板一致。</summary>
    public sealed partial class BattleHudPanel
    {
        [SerializeField] private ScoreReelView roundReel, lifetimeReel, multiplierReel;
        [SerializeField] private RectTransform scoreFrame;
        [SerializeField] private Text scoreGain;
        // GPU/内存表现预算，非玩法上限。超额只合并飞星，积分已在服务入账。
        public const int MaxScoreSparks = 12;
        private readonly HashSet<ScoreSparkView> _scoreSparks = new HashSet<ScoreSparkView>();
        private int _scoreGeneration, _scoreLoading, _scoreLane, _scoreRound = -1, _scoreChapter = -1;
        private Tween _scorePulse;
        private long _pendingVisualGain;
        private float _nextScorePulseAt;
        public int ActiveScoreSparks => _scoreSparks.Count;
        public bool ScoreIsRolling => roundReel != null && roundReel.IsRolling;

        private void OpenScore()
        {
            _scoreRound = _scoreChapter = -1;
            _scoreGeneration++;
            RevEvent.AddEventListener<ScoreGrantedInfo>(Events.ScoreGranted, OnScoreGranted, owner: this);
        }

        private void RefreshScore(IBattleService battle)
        {
            if (roundReel == null || battle == null) { return; }
            bool visible = battle.Phase != GamePhase.Prepare;
            if (scoreFrame.gameObject.activeSelf != visible) { scoreFrame.gameObject.SetActive(visible); }
            bool snap = _scoreRound != battle.Round || _scoreChapter != battle.Chapter;
            _scoreRound = battle.Round; _scoreChapter = battle.Chapter;
            roundReel.Display(battle.RoundScore, snap);
            lifetimeReel.Display(battle.LifetimeScore, snap);
            multiplierReel.Display(Mathf.RoundToInt(battle.ScoreMultiplier * 100f), snap);
            if (_pendingVisualGain > 0 && Time.unscaledTime >= _nextScorePulseAt)
            {
                scoreGain.text = "+" + _pendingVisualGain.ToString("N0"); _pendingVisualGain = 0;
                _nextScorePulseAt = Time.unscaledTime + .24f;
                _scorePulse?.Kill(false); scoreFrame.localScale = Vector3.one;
                _scorePulse = scoreFrame.DOPunchScale(Vector3.one * .045f, .25f, 4, .4f).SetUpdate(true).OnComplete(() => _scorePulse = null);
            }
        }

        private void OnScoreGranted(ScoreGrantedInfo info)
        {
            _pendingVisualGain = _pendingVisualGain > long.MaxValue - info.Added ? long.MaxValue : _pendingVisualGain + info.Added;
            if (_noticeClosed || _noticeRoot == null || scoreFrame == null) { return; }
            Camera camera = Camera.main;
            if (camera == null) { return; }
            Vector3 world = info.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset)
                + new Vector3(1, 1, 0) * ((info.Size - 1) * Consts.CellSize * .5f);
            Vector2 start, end;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_noticeRoot, camera.WorldToScreenPoint(world), null, out start);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_noticeRoot, RectTransformUtility.WorldToScreenPoint(null, scoreFrame.position), null, out end);
            // 击杀三颗飞星、暴击一颗；同时发生时只播三颗。异步预约也算预算，防止一次AOE突破上限。
            int count = info.Kill ? 3 : 1;
            for (int i = 0; i < count && _scoreSparks.Count + _scoreLoading < MaxScoreSparks; i++)
            {
                int generation = _scoreGeneration, lane = _scoreLane++;
                _scoreLoading++;
                GamePoolRequests.GetAsync<ScoreSparkView>(spark =>
                {
                    if (generation != _scoreGeneration || _noticeClosed)
                    {
                        if (spark != null) { RevPool.Return(spark, 0); }
                        return;
                    }
                    _scoreLoading--;
                    if (spark == null) { RevLog.Warn("积分飞星加载失败，分数已入账", "RevGame"); return; }
                    _scoreSparks.Add(spark); spark.Fly(start, end, info.Critical, lane, OnScoreSparkFinished);
                }, _noticeRoot);
            }
        }

        private void OnScoreSparkFinished(ScoreSparkView spark) { _scoreSparks.Remove(spark); }

        private void ClearScoreFlights()
        {
            _scoreGeneration++; _scoreLoading = 0; _pendingVisualGain = 0;
            foreach (ScoreSparkView spark in _scoreSparks) { if (spark != null) { RevPool.Return(spark, 0); } }
            _scoreSparks.Clear();
            _scorePulse?.Kill(false); _scorePulse = null;
            if (scoreFrame != null) { scoreFrame.localScale = Vector3.one; }
            if (scoreGain != null) { scoreGain.text = ""; }
        }

        private void CloseScore()
        {
            ClearScoreFlights();
            roundReel?.StopRoll(); lifetimeReel?.StopRoll(); multiplierReel?.StopRoll();
            RevPool.DestroyPool<ScoreSparkView>();
        }
    }
}
