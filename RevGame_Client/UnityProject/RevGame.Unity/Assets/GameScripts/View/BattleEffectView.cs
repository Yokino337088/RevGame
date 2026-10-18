// ============================================================
// BattleEffectView.cs —— RevPool 复用物体，DOTween 负责弹道、冲击和伤害飘字。
// 伤害已经在服务中结算，本类只播放快照；动画完成不能再次扣血。
// ============================================================
using DG.Tweening;
using Revolution;
using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public enum BattleEffectKind { Death, Merge, Damage, Projectile }

    [RevPoolPrefab("Effects/", "BattleEffect", Group = RevResGroup.Battle)]
    public sealed class BattleEffectView : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TextMesh damageText;
        [SerializeField] private Sprite[] deathFrames;
        [SerializeField] private Sprite[] mergeFrames;
        [SerializeField] private Sprite projectileSprite;
        [SerializeField] private TextMesh[] damageOutlines = new TextMesh[0];
        // 每个池对象只拥有一条动画。回池 Kill(false)：停止即可，不能强制完成再触发回池回调。
        private Tween _animation;
        private System.Action<BattleEffectView> _onFinished;
        private float _progress;
        // 透明度分成动画淡出与遮挡淡化两部分，相乘后落屏；避让刷新不能把快消失的旧字重新点亮。
        private float _fadeAlpha = 1f, _occlusionAlpha = 1f;
        private float _baseTextScale = 1f, _textPeak = 1f, _textRise;
        private Renderer _textRenderer;
        public bool IsFloatingText { get; private set; }
        public bool IsAttackText { get; private set; }
        public int TextOrder { get; private set; }

        /// <summary>屏幕上真实文字的矩形，供整个棋盘统一避让；不使用渲染位置以外的固定“第几条通道”。</summary>
        public Rect TextScreenRect
        {
            get
            {
                Camera camera = Camera.main;
                if (camera == null) { return new Rect(); }
                if (_textRenderer == null) { _textRenderer = damageText.GetComponent<Renderer>(); }
                Bounds bounds = _textRenderer.bounds;
                Vector3 min = camera.WorldToScreenPoint(bounds.min);
                Vector3 max = camera.WorldToScreenPoint(bounds.max);
                return Rect.MinMaxRect(Mathf.Min(min.x, max.x) - 3f, Mathf.Min(min.y, max.y) - 3f,
                    Mathf.Max(min.x, max.x) + 3f, Mathf.Max(min.y, max.y) + 3f);
            }
        }

        /// <summary>提前把后续放大和上浮也算入占用范围，避免初始不重叠、动画一放大又挤在一起。</summary>
        public Rect TextReservationRect
        {
            get
            {
                Rect rect = TextScreenRect;
                float scale = _baseTextScale * _textPeak / Mathf.Max(0.01f, transform.localScale.x);
                Vector2 center = rect.center;
                rect.size *= Mathf.Max(1f, scale);
                rect.center = center;
                Camera camera = Camera.main;
                if (camera != null)
                {
                    float rise = camera.WorldToScreenPoint(transform.position + Vector3.up * _textRise).y - camera.WorldToScreenPoint(transform.position).y;
                    rect.height += Mathf.Max(0f, rise);
                }
                return rect;
            }
        }

        /// <summary>数字越大越靠前，文字与自己的描边一起排序，不能让旧描边挡住新字。</summary>
        public void SetTextOrder(int order)
        {
            TextOrder = order;
            damageText.GetComponent<Renderer>().sortingOrder = 100 + order * 2;
            foreach (TextMesh outline in damageOutlines) { outline.GetComponent<Renderer>().sortingOrder = 99 + order * 2; }
        }

        /// <summary>空间不足时，下面那条提示降至35%透明度；动画本身的淡出比例仍然保留。</summary>
        public void SetTextOccluded(bool covered)
        {
            _occlusionAlpha = covered ? 0.35f : 1f;
            ApplyNumberAlpha();
        }
        /// <summary>供开发验收读取；不参与战斗规则。</summary>
        public bool HasActiveAnimation { get { return _animation != null && _animation.IsActive(); } }

        /// <summary>伤害只显示已结算数值；arrange在创建Tween前安排位置，避免动画下一帧把避让位置覆盖掉。</summary>
        public void Show(BattleEffectKind kind, Vector3 position, int damage, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy, System.Action<BattleEffectView> arrange = null)
        {
            ResetVisual(position, onFinished);
            bool number = kind == BattleEffectKind.Damage;
            damageText.gameObject.SetActive(number);
            if (number)
            {
                BattleText config = GameTextConfig.Require("Damage");
                Color color = targetSide == GameSide.Player ? new Color(1f, 0.28f, 0.22f) : GameTextConfig.ColorOf(config);
                ConfigureText(config, config.Text.Replace("{damage}", damage.ToString()), color);
                StartFloatingText(config, position + new Vector3(0f, 0.35f, -0.5f), false, arrange);
                return;
            }
            Sprite[] frames = kind == BattleEffectKind.Death ? deathFrames : mergeFrames;
            spriteRenderer.enabled = frames != null && frames.Length > 0;
            spriteRenderer.color = kind == BattleEffectKind.Merge ? new Color(1f, 0.75f, 0.18f) : Color.white;
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            // 非文字特效仍复用现有帧图，由Tween进度选帧，不给每个特效挂循环Timer。
            _animation = DOTween.To(() => _progress, value =>
            {
                _progress = value;
                if (frames != null && frames.Length > 0) { spriteRenderer.sprite = frames[Mathf.Min(frames.Length - 1, (int)(value * frames.Length))]; }
                transform.localScale = Vector3.one * (1f + Mathf.Sin(value * Mathf.PI) * 0.25f);
            }, 1f, 0.75f).SetEase(Ease.Linear).OnComplete(Finish);
        }

        /// <summary>低频攻击口号；文案与基础字号仍来自Excel，整场数量和频率由BoardView统一控制。</summary>
        public void ShowAttack(BattleText config, Vector3 position, System.Action<BattleEffectView> onFinished, System.Action<BattleEffectView> arrange = null)
        {
            ResetVisual(position, onFinished);
            damageText.gameObject.SetActive(true);
            ConfigureText(config, config.Text, GameTextConfig.ColorOf(config));
            StartFloatingText(config, position + new Vector3(0f, 0.55f, -0.5f), true, arrange);
        }

        private void ConfigureText(BattleText config, string content, Color color)
        {
            spriteRenderer.enabled = false;
            damageText.text = content;
            damageText.fontSize = config.FontSize;
            damageText.color = color;
            damageText.fontStyle = FontStyle.Bold;
            damageText.anchor = TextAnchor.MiddleCenter;
            damageText.alignment = TextAlignment.Center;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.text = content;
                outline.fontSize = config.FontSize;
                outline.fontStyle = FontStyle.Bold;
                outline.anchor = TextAnchor.MiddleCenter;
                outline.alignment = TextAlignment.Center;
            }
            SetNumberAlpha(1f);
        }

        /// <summary>先限定可见字宽，再避让，最后记录Tween起终点；文字最多保留约一秒，不积压上一轮攻击。</summary>
        private void StartFloatingText(BattleText config, Vector3 position, bool attack, System.Action<BattleEffectView> arrange)
        {
            IsFloatingText = true;
            IsAttackText = attack;
            transform.position = KeepTextOnScreen(position);
            _textPeak = Mathf.Clamp(config.PeakScale, 1f, 1.12f);
            _textRise = Consts.CellSize * (attack ? 0.30f : 0.45f);
            Camera camera = Camera.main;
            float maxWidth = camera == null ? 180f : camera.pixelWidth * (attack ? 0.09f : 0.055f);
            _baseTextScale = Mathf.Min(1f, maxWidth / Mathf.Max(1f, TextScreenRect.width * _textPeak));
            transform.localScale = Vector3.one * _baseTextScale;
            arrange?.Invoke(this);
            Vector3 start = transform.position;
            Vector3 end = start + Vector3.up * _textRise;
            if (arrange == null) { end = KeepTextOnScreen(end); }
            float duration = Mathf.Min(config.Duration, attack ? 0.9f : 1.1f);
            transform.localScale = Vector3.one * (_baseTextScale * 0.72f);
            Sequence sequence = DOTween.Sequence();
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * (_baseTextScale * _textPeak), 0.14f).SetEase(Ease.OutQuad));
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * _baseTextScale, 0.14f).SetEase(Ease.OutBack));
            sequence.Insert(0f, DOTween.To(() => transform.position, value => transform.position = value, end, duration).SetEase(Ease.OutQuad));
            sequence.Insert(duration - 0.28f, DOTween.To(() => _fadeAlpha, SetNumberAlpha, 0f, 0.28f));
            _animation = sequence.OnComplete(Finish);
        }

        /// <summary>只播放已发生攻击的轨迹；抵达后 BoardView 根据同一快照显示数字。</summary>
        public void ShowProjectile(Vector3 source, Vector3 target, System.Action<BattleEffectView> onFinished)
        {
            ResetVisual(source, onFinished);
            damageText.gameObject.SetActive(false);
            spriteRenderer.enabled = true;
            spriteRenderer.sprite = projectileSprite;
            spriteRenderer.color = new Color(1f, 0.8f, 0.12f);
            // 源Sprite本身宽约6.17个世界单位，不能直接把原图尺寸当成子弹尺寸。
            // 使用Simple并按源Sprite实际大小归一化，固定为0.38×0.10个世界单位，避免出现横贯棋盘的长条。
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            if (projectileSprite != null)
            {
                Vector3 size = projectileSprite.bounds.size;
                transform.localScale = new Vector3(0.38f / Mathf.Max(0.01f, size.x), 0.10f / Mathf.Max(0.01f, size.y), 1f);
            }
            Vector3 direction = target - source;
            transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            _animation = DOTween.To(() => transform.position, value => transform.position = value, target, Consts.ProjectileSeconds).SetEase(Ease.InQuad).OnComplete(Finish);
        }

        /// <summary>文字离边缘留出可读空间；顶排命中时缩短上浮距离，防止数字被手机屏幕裁掉。</summary>
        private static Vector3 KeepTextOnScreen(Vector3 position, float top = 0.88f)
        {
            Camera camera = Camera.main;
            if (camera == null) { return position; }
            // 视口坐标 0~1 表示整块屏幕，与手机实际像素无关。上方还留有回合 HUD，不能飘进其区域。
            Vector3 viewport = camera.WorldToViewportPoint(position);
            viewport.x = Mathf.Clamp(viewport.x, 0.08f, 0.92f);
            viewport.y = Mathf.Clamp(viewport.y, 0.08f, top);
            return camera.ViewportToWorldPoint(viewport);
        }

        private void ResetVisual(Vector3 position, System.Action<BattleEffectView> onFinished)
        {
            StopAnimation();
            _onFinished = onFinished;
            _progress = 0f;
            _fadeAlpha = _occlusionAlpha = 1f;
            IsFloatingText = IsAttackText = false;
            TextOrder = 0;
            transform.position = position;
            transform.localScale = Vector3.one;
            transform.rotation = Quaternion.identity;
        }

        private void SetNumberAlpha(float alpha)
        {
            _fadeAlpha = alpha;
            ApplyNumberAlpha();
        }

        private void ApplyNumberAlpha()
        {
            float alpha = _fadeAlpha * _occlusionAlpha;
            Color color = damageText.color;
            color.a = alpha;
            damageText.color = color;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.color = new Color(0f, 0f, 0f, alpha);
            }
        }

        private void Finish()
        {
            // 先清句柄再回池，避免 OnPoolReturn 去 Kill 正在完成回调的序列。
            _animation = null;
            System.Action<BattleEffectView> callback = _onFinished;
            _onFinished = null;
            callback?.Invoke(this);
            RevPool.Return(this, 0);
        }

        private void StopAnimation()
        {
            Tween previous = _animation;
            _animation = null;
            previous?.Kill(false);
        }

        public void OnPoolGet() { StopAnimation(); }
        public void OnPoolReturn()
        {
            StopAnimation();
            _onFinished = null;
            IsFloatingText = IsAttackText = false;
            _fadeAlpha = _occlusionAlpha = 1f;
            damageText.text = string.Empty;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.text = string.Empty;
            }
        }
        private void OnDisable() { StopAnimation(); }
        private void OnDestroy() { StopAnimation(); }
    }
}
