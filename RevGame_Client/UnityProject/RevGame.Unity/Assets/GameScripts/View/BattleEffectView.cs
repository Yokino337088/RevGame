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
        private static int _lane;
        /// <summary>供开发验收读取；不参与战斗规则。</summary>
        public bool HasActiveAnimation { get { return _animation != null && _animation.IsActive(); } }

        public void Show(BattleEffectKind kind, Vector3 position, int damage, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy)
        {
            ResetVisual(position, onFinished);
            Sprite[] frames = kind == BattleEffectKind.Death ? deathFrames : mergeFrames;
            bool number = kind == BattleEffectKind.Damage;
            damageText.gameObject.SetActive(number);
            spriteRenderer.enabled = !number && frames != null && frames.Length > 0;
            spriteRenderer.color = kind == BattleEffectKind.Merge ? new Color(1f, 0.75f, 0.18f) : Color.white;
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            if (number)
            {
                BattleText config = GameTextConfig.Require("Damage");
                damageText.fontSize = config.FontSize;
                foreach (TextMesh outline in damageOutlines) { outline.fontSize = config.FontSize; }
                // 三条通道轮换，连击时数字不完全重叠。红色=我方受击，金色=敌方受击。
                int lane = _lane++ % 3;
                float drift = (lane - 1) * 0.12f;
                // 顶排不能把三条通道都压到同一条上边界，否则连续伤害会叠成一团数字。
                // 每条通道各留约 6% 屏高；上浮撞到自己的边界就缩短距离，保持三行可读。
                float top = 0.88f - lane * 0.06f;
                transform.position = KeepTextOnScreen(transform.position + new Vector3(drift, 0.4f + lane * 0.16f, -0.5f), top);
                damageText.text = config.Text.Replace("{damage}", damage.ToString());
                damageText.color = targetSide == GameSide.Player ? new Color(1f, 0.28f, 0.22f) : GameTextConfig.ColorOf(config);
                foreach (TextMesh outline in damageOutlines)
                {
                    outline.text = damageText.text;
                }
                SetNumberAlpha(1f);
                Vector3 start = transform.position;
                transform.localScale = Vector3.one * 0.6f;
                Sequence sequence = DOTween.Sequence();
                sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * config.PeakScale, 0.16f).SetEase(Ease.OutBack));
                sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one, 0.18f).SetEase(Ease.OutQuad));
                Vector3 end = KeepTextOnScreen(start + new Vector3(drift, 1.3f, 0f), top);
                sequence.Insert(0f, DOTween.To(() => transform.position, value => transform.position = value, end, config.Duration).SetEase(Ease.OutQuad));
                sequence.Insert(config.Duration - 0.45f, DOTween.To(() => _progress, SetNumberAlpha, 0f, 0.45f).From(1f));
                _animation = sequence.OnComplete(Finish);
            }
            else
            {
                // 原有提子/合成帧图继续复用；Tween 进度选帧，不再为每个特效挂循环计时器。
                _animation = DOTween.To(() => _progress, value =>
                {
                    _progress = value;
                    if (frames != null && frames.Length > 0)
                    {
                        spriteRenderer.sprite = frames[Mathf.Min(frames.Length - 1, (int)(value * frames.Length))];
                    }
                    transform.localScale = Vector3.one * (1f + Mathf.Sin(value * Mathf.PI) * 0.25f);
                }, 1f, 0.75f).SetEase(Ease.Linear).OnComplete(Finish);
            }
        }

        /// <summary>显示攻击者口号；同一池同时复用文字和弹道对象，减少手机频繁创建物体的开销。</summary>
        public void ShowAttack(BattleText config, Vector3 position, System.Action<BattleEffectView> onFinished)
        {
            // 攻击口号在攻击者头顶弹出，与受击者的实际伤害数字分开，文案及参数来自导表。
            ResetVisual(KeepTextOnScreen(position + new Vector3(0f, 0.7f, -0.5f)), onFinished);
            spriteRenderer.enabled = false;
            damageText.gameObject.SetActive(true);
            damageText.text = config.Text;
            damageText.fontSize = config.FontSize;
            damageText.color = GameTextConfig.ColorOf(config);
            foreach (TextMesh outline in damageOutlines)
            {
                outline.text = config.Text;
                outline.fontSize = config.FontSize;
            }
            SetNumberAlpha(1f);
            Vector3 start = transform.position;
            transform.localScale = Vector3.one * 0.65f;
            Sequence sequence = DOTween.Sequence();
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * config.PeakScale, 0.18f).SetEase(Ease.OutBack));
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one, 0.16f));
            Vector3 end = KeepTextOnScreen(start + Vector3.up * 0.7f);
            sequence.Insert(0f, DOTween.To(() => transform.position, value => transform.position = value, end, config.Duration).SetEase(Ease.OutQuad));
            sequence.Insert(config.Duration - 0.3f, DOTween.To(() => _progress, SetNumberAlpha, 0f, 0.3f).From(1f));
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
            // 长度固定为半格，不让源图尺寸改变子弹大小。
            spriteRenderer.drawMode = SpriteDrawMode.Sliced;
            spriteRenderer.size = new Vector2(0.65f, 0.18f);
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
            transform.position = position;
            transform.localScale = Vector3.one;
            transform.rotation = Quaternion.identity;
        }

        private void SetNumberAlpha(float alpha)
        {
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
