using DG.Tweening;
using RevGame.Pvp.Agent;
using RevGame.View;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    public sealed partial class PvpPanel
    {
        private const float AnalysisTweenSeconds = .65f;
        private RectTransform _analysisHud, _analysisBlackMask, _analysisBlack;
        private CanvasGroup _analysisHudGroup;
        private Image _analysisLight, _analysisDark;
        private Text _analysisBlackLabel, _analysisWhiteLabel;
        private Tween _analysisTween;
        private GoPositionAnalysis _paintedAnalysis, _spokenAnalysis;
        private float _shownBlackRatio;
        /// <summary>当前可清除的建议气泡类型。自动建议过期或手动建议被取消时清屏。</summary>
        private bool _analysisSpeech, _hintSpeech;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        internal bool AnalysisHudVisible => _analysisHud.gameObject.activeInHierarchy;
        internal float AnalysisBlackRatio => _shownBlackRatio;
        internal string AnalysisBlackLabel => _analysisBlackLabel.text;
        internal string AnalysisWhiteLabel => _analysisWhiteLabel.text;
        internal bool AnalysisSkinPacked => _analysisLight.sprite != null && _analysisLight.sprite.packed;
#endif

        /// <summary>占用棋盘和按钮之间的留白，两色共用同一个九宫格进度条素材。</summary>
        private void BindAnalysisHud()
        {
            _analysisHud = AnalysisRect("winRate", transform, .21f, .101f, .79f, .146f);
            _analysisHud.SetSiblingIndex(transform.Find("invite").GetSiblingIndex());
            _analysisHudGroup = _analysisHud.gameObject.AddComponent<CanvasGroup>();
            _analysisHudGroup.blocksRaycasts = false;
            RectTransform track = AnalysisRect("bar", _analysisHud, .20f, .22f, .80f, .78f);
            _analysisLight = AnalysisSegment("whiteFill", track, new Color(1f, .98f, .88f));
            _analysisBlackMask = AnalysisRect("blackMask", track, 0, 0, .5f, 1);
            _analysisBlackMask.gameObject.AddComponent<RectMask2D>();
            _analysisDark = AnalysisSegment("blackFill", _analysisBlackMask, new Color(.25f, .27f, .34f));
            _analysisBlack = _analysisDark.rectTransform;
            _analysisBlackLabel = AnalysisLabel("blackPercent", 0f, .19f);
            _analysisWhiteLabel = AnalysisLabel("whitePercent", .81f, 1f);
            ResetAnalysisHud();
        }

        private static RectTransform AnalysisRect(string name, Transform parent, float xMin, float yMin, float xMax, float yMax)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(xMin, yMin);
            rect.anchorMax = new Vector2(xMax, yMax);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static Image AnalysisSegment(string name, Transform parent, Color color)
        {
            RectTransform rect = AnalysisRect(name, parent, 0, 0, 1, 1);
            Image segment = rect.gameObject.AddComponent<Image>();
            segment.type = Image.Type.Sliced;
            segment.color = color;
            segment.raycastTarget = false;
            return segment;
        }

        /// <summary>入场只取一次打包 Sprite，双色条共享副本。离场先解绑再归还图集。</summary>
        private void BindAnalysisSkin(GameSpriteAtlasService.ScopedAtlasLease atlas)
        {
            ReleaseAnalysisSkin();
            Sprite skin = atlas.GetPackedSprite("PvpWinRateTrack");
            _analysisLight.sprite = skin;
            _analysisDark.sprite = skin;
        }

        private void ReleaseAnalysisSkin()
        {
            Sprite skin = _analysisLight.sprite;
            _analysisLight.sprite = _analysisDark.sprite = null;
            if (skin != null)
            {
                Destroy(skin);
            }
        }

        private Text AnalysisLabel(string name, float xMin, float xMax)
        {
            RectTransform rect = AnalysisRect(name, _analysisHud, xMin, 0, xMax, 1);
            Text label = rect.gameObject.AddComponent<Text>();
            label.font = _black.font;
            label.fontSize = 26;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = _black.color;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            var outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(.13f, .09f, .07f, .96f);
            outline.effectDistance = new Vector2(2, -2);
            return label;
        }

        private void RefreshAnalysisHud(PvpService service, bool playing)
        {
            bool visible = playing && service.AnalysisMode && _analysisLight.sprite != null;
            Show(_analysisHud.gameObject, visible);
            if (!visible)
            {
                _analysisTween?.Kill(false);
                return;
            }

            GoPositionAnalysis result = service.PositionAnalysis;
            // 轮换期间淡显上一次估计。没有有效回复时仅显示破折号。
            _analysisHudGroup.alpha = service.AnalysisFresh ? 1f : .55f;
            if (ReferenceEquals(_paintedAnalysis, result))
            {
                return;
            }
            _paintedAnalysis = result;
            _analysisTween?.Kill(false);
            Set(_analysisBlackLabel, result == null ? "黑 --" : "黑 " + result.BlackPercent + "%");
            Set(_analysisWhiteLabel, result == null ? "白 --" : "白 " + (100 - result.BlackPercent) + "%");
            if (result == null)
            {
                RenderAnalysisRatio(.5f);
                return;
            }

            _analysisTween = DOTween.To(() => _shownBlackRatio, RenderAnalysisRatio, result.BlackPercent / 100f, AnalysisTweenSeconds)
                .SetEase(Ease.OutCubic).SetUpdate(true);
        }

        private void RenderAnalysisRatio(float ratio)
        {
            _shownBlackRatio = ratio;
            _analysisBlackMask.anchorMax = new Vector2(ratio, 1);
            // 裁切边跟随比例移动，素材仍和底图一样宽，所以分界处不会出现两个圆角。
            _analysisBlack.anchorMax = new Vector2(ratio > 0 ? 1 / ratio : 1, 1);
            Show(_analysisBlackMask.gameObject, ratio > 0);
        }

        private void ResetAnalysisHud()
        {
            _analysisTween?.Kill(false);
            _analysisTween = null;
            _paintedAnalysis = _spokenAnalysis = null;
            _analysisSpeech = _hintSpeech = false;
            Set(_analysisBlackLabel, "黑 --");
            Set(_analysisWhiteLabel, "白 --");
            RenderAnalysisRatio(.5f);
            Show(_analysisHud.gameObject, false);
        }

        /// <summary>新分析复用鸦鸦对话框。换手后旧建议立即隐藏，手动建议仍可覆盖自动对话。</summary>
        private bool RefreshAnalysisSpeech(PvpService service)
        {
            if ((!service.ShowAnalysisAdvice && _analysisSpeech) || (_hintSpeech && service.HintSource.Length == 0))
            {
                _analysisSpeech = _hintSpeech = false;
                _speechTween?.Kill(false);
                Show(_bubble.gameObject, false);
                _portrait.sprite = _idlePortrait;
            }
            GoPositionAnalysis report = service.PositionAnalysis;
            if (!service.ShowAnalysisAdvice || ReferenceEquals(_spokenAnalysis, report))
            {
                return false;
            }
            _spokenAnalysis = report;
            GoAgentAction move = report.Suggestion;
            string point = move.Kind == GoAction.Play ? PvpService.FormatPoint(move.X, move.Y) : "";
            ShowYayaLine(report.Cue, point);
            _analysisSpeech = true;
            _hintSpeech = false;
            return true;
        }
    }
}
