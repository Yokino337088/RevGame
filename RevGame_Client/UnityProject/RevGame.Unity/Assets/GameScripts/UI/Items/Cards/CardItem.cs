// ============================================================
// CardItem.cs —— 手牌栏中的一个按钮，只负责显示牌和转发点击。
// 六个固定槽位复用预制体，刷新时换内容，避免每次选牌都创建/销毁按钮。
// ============================================================
using System;
using DG.Tweening;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.View;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    public sealed class CardItem : MonoBehaviour
    {
        [SerializeField]
        private Button button;
        [SerializeField]
        private Image background;
        [SerializeField]
        private Image portrait;
        [SerializeField]
        private Text title;
        [SerializeField]
        private Text stats;
        [SerializeField]
        private Sprite normalSprite;
        [SerializeField]
        private Sprite selectedSprite;
        [SerializeField]
        private PieceArtConfig artConfig;
        private Action<int> _clicked;
        // 每个常驻槽位只持有自己的翻牌序列。禁止 KillAll，否则会把其他卡和战斗飘字一起停掉。
        private Tween _flip;
        // 点击与刷新分别持有句柄；开始翻牌前先停止点击反馈，避免两个动画同时修改同一张牌的姿态。
        private Tween _press;
        // 数据已在服务里更新，但正面需等转到 90 度才换；期间普通刷新只更新这份待显示内容。
        private HandCard _pendingCard;
        private int _pendingSelection;
        private Vector2 _lastLayoutSize;
        public bool IsFlipping { get { return _flip != null && _flip.IsActive(); } }
        public int CardId { get; private set; }

        public void BindClick(Action<int> clicked)
        {
            _clicked = clicked;
            // 面板复用时先移除自己的监听，保证一次点击只转发一次。
            button.onClick.RemoveListener(OnClick);
            button.onClick.AddListener(OnClick);
        }

        /// <summary>空槽位隐藏；选中框只比较唯一编号，不比较同名牌。</summary>
        public void Show(HandCard card, int selectedId)
        {
            if (IsFlipping)
            {
                _pendingCard = card;
                _pendingSelection = selectedId;
                return;
            }
            RenderCard(card, selectedId);
        }

        private void RenderCard(HandCard card, int selectedId)
        {
            gameObject.SetActive(card != null);
            if (card == null)
            {
                CardId = 0;
                return;
            }

            CardId = card.Id;
            PieceDef def = PieceCatalog.Get(card.Kind, card.Star);
            title.text = def.DisplayName;
            // 小卡片把两项属性分成两行，粗体中文就不必缩成很细的一条，也不会顶到相邻卡片。
            stats.text = "生命 " + def.MaxHp + "\n攻击 " + def.Attack;
            background.sprite = selectedId == card.Id ? selectedSprite : normalSprite;
            var art = artConfig.Find(card.Kind, card.Star, GameSide.Player);
            portrait.sprite = art != null && art.idleFrames.Length > 0 ? art.idleFrames[0] : null;
            portrait.enabled = portrait.sprite != null;
        }

        /// <summary>先保留旧正面，转到侧面时才换数据，再回弹到正面；动画只影响显示。</summary>
        public void FlipTo(HandCard card, int selectedId, float delay, Action completed)
        {
            StopFlip();
            if (card == null)
            {
                RenderCard(null, 0); return;
            }
            _pendingCard = card;
            _pendingSelection = selectedId;
            button.interactable = false;
            float angle = 0f;
            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            sequence.AppendInterval(delay);
            sequence.Append(DOTween.To(() => angle, value =>
            {
                angle = value;
                transform.localRotation = Quaternion.Euler(0f, value, 0f);
            }, 90f, 0.18f).SetEase(Ease.InQuad));
            sequence.AppendCallback(() =>
            {
                RenderCard(_pendingCard, _pendingSelection);
                angle = -90f;
                transform.localRotation = Quaternion.Euler(0f, angle, 0f);
            });
            sequence.Append(DOTween.To(() => angle, value =>
            {
                angle = value;
                transform.localRotation = Quaternion.Euler(0f, value, 0f);
            }, 0f, 0.32f).SetEase(Ease.OutBack));
            sequence.Join(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * 1.08f, 0.15f));
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one, 0.12f));
            _flip = sequence.OnComplete(() =>
            {
                _flip = null;
                _pendingCard = null;
                button.interactable = true;
                completed?.Invoke();
            });
        }

        private void StopFlip()
        {
            StopPress();
            // 关闭准备面板也会走这里：不执行旧完成回调，恢复姿态，下一回合不会从半张牌开始。
            Tween previous = _flip;
            _flip = null;
            previous?.Kill(false);
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            button.interactable = true;
            _pendingCard = null;
        }

        private void OnDisable() { StopFlip(); }

        private void OnRectTransformDimensionsChange() { LayoutContents(); }
        private void OnEnable() { LayoutContents(); }

        /// <summary>牌内的肖像和文字一起适配槽位，不能只缩小底板而让固定宽度的文字伸到相邻牌上。</summary>
        private void LayoutContents()
        {
            RectTransform rect = transform as RectTransform;
            if (rect == null || title == null || stats == null || portrait == null)
            {
                return;
            }
            Vector2 size = rect.rect.size;
            if (size == _lastLayoutSize || size.x < 1f || size.y < 1f)
            {
                return;
            }
            _lastLayoutSize = size;
            // 设计分辨率的卡牌内部布局已经保存到 CardItem.prefab，保留美术在预制体中调整的结果。
            if (Mathf.Abs(size.x - 162.93528f) < 0.01f && Mathf.Abs(size.y - 217.24704f) < 0.01f)
            {
                return;
            }
            SetContentRect(portrait.rectTransform, new Vector2(0f, size.y * 0.18f), Vector2.one * Mathf.Min(size.x * 0.70f, size.y * 0.48f));
            SetContentRect(title.rectTransform, new Vector2(0f, -size.y * 0.16f), new Vector2(size.x * 0.94f, size.y * 0.18f));
            // 两行属性独占底部区域；先预留实际高度，再让 BestFit 在小范围内缩放，不能让粗体越出卡底。
            SetContentRect(stats.rectTransform, new Vector2(0f, -size.y * 0.365f), new Vector2(size.x * 0.94f, size.y * 0.23f));
            title.resizeTextForBestFit = stats.resizeTextForBestFit = true;
            title.resizeTextMinSize = stats.resizeTextMinSize = 14;
            title.resizeTextMaxSize = 24;
            stats.resizeTextMaxSize = 20;
        }

        private static void SetContentRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
        }

        /// <summary>回合交接主动复位；即使旧翻牌被暂停，也不允许旧内容覆盖新回合。</summary>
        public void ResetForRound()
        {
            StopFlip();
            CardId = 0;
        }

        private void OnClick()
        {
            if (_clicked != null && CardId != 0)
            {
                PlayPressFeedback();
                _clicked(CardId);
            }
        }

        /// <summary>点击时先轻微压下，再左右衰减摆动；不改落子规则或手牌数据，连点也只保留最后一次反馈。</summary>
        private void PlayPressFeedback()
        {
            if (IsFlipping)
            {
                return;
            }
            StopPress();
            float progress = 0f;
            _press = DOTween.To(() => progress, value =>
            {
                progress = value;
                // 正弦负责左右摆，(1-progress)让幅度自然收敛；首尾角度都为零，不积累旋转误差。
                float swing = Mathf.Sin(value * Mathf.PI * 6f) * (1f - value);
                transform.localRotation = Quaternion.Euler(0f, swing * 5f, swing * 6f);
                transform.localScale = Vector3.one * (1f - Mathf.Sin(value * Mathf.PI) * 0.045f);
            }, 1f, 0.42f).SetUpdate(true).SetEase(Ease.Linear).OnComplete(() =>
            {
                _press = null;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            });
        }

        /// <summary>关闭、切回合和刷新都取消旧抖动；Kill(false)不会执行旧的完成回调。</summary>
        private void StopPress()
        {
            Tween previous = _press;
            _press = null;
            previous?.Kill(false);
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        public void Release()
        {
            StopFlip();
            button.onClick.RemoveListener(OnClick);
            _clicked = null;
        }
    }
}
