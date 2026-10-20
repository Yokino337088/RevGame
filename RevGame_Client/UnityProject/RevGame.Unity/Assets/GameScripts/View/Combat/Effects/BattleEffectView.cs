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
    public enum BattleEffectKind { Damage, Projectile }

    [RevPoolPrefab(RevResPath.Effects_Combat, "BattleEffect", Group = RevResGroup.Battle)]
    public sealed class BattleEffectView : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TextMesh damageText;
        [SerializeField] private Sprite projectileSprite;
        [Tooltip("弹道本体的世界尺寸；不能直接依赖源美术图片的宽高。")]
        [SerializeField] private Vector2 projectileWorldSize = new Vector2(0.82f, 0.24f);
        [Tooltip("略延长可见飞行时间，让手机上能看清连续齐射；不改变真实伤害结算时间。")]
        [SerializeField] private float projectileDuration = 0.36f;
        [Tooltip("同一个池对象内的深色轮廓，保证浅色棋盘上也能看清弹头。")]
        [SerializeField] private SpriteRenderer projectileOutline;
        [Tooltip("同一个池对象内的短拖尾；不是另外申请的粒子、弹道或持续增长的轨迹。")]
        [SerializeField] private SpriteRenderer projectileTrail;
        [SerializeField] private TextMesh[] damageOutlines = new TextMesh[0];
        // 每个池对象只拥有一条动画。回池 Kill(false)：停止即可，不能强制完成再触发回池回调。
        private Tween _animation;
        private System.Action<BattleEffectView> _onFinished;
        private float _progress;
        // 透明度分成动画淡出与遮挡淡化两部分，相乘后落屏；避让刷新不能把快消失的旧字重新点亮。
        private float _fadeAlpha = 1f, _occlusionAlpha = 1f;
        private float _baseTextScale = 1f, _textPeak = 1f, _textRise;
        private Renderer _textRenderer;
        /// <summary>只在首次实例化时查找描边Renderer；反复出入池不再查找或分配数组。</summary>
        private Renderer[] _outlineRenderers;
        private Color _projectileColor;
        // 弹道额外持有寿命上限。即使外部意外 Kill/Pause 了 Tween，也不能留一根永远不动的黄条。
        // 使用游戏时间：正常暂停时整个战斗一起停，恢复后再继续回收。
        private bool _projectileActive;
        private float _projectileExpiresAt;
        public bool IsFloatingText { get; private set; }
        public bool IsAttackText { get; private set; }
        public int TextOrder { get; private set; }

        /// <summary>屏幕上真实文字的矩形，供整个棋盘统一避让；不使用渲染位置以外的固定“第几条通道”。</summary>
        public Rect TextScreenRect
        {
            get
            {
                Camera camera = Camera.main;
                if (camera == null)
                {
                    return new Rect();
                }
                if (_textRenderer == null)
                {
                    _textRenderer = damageText.GetComponent<Renderer>();
                }
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
            _textRenderer.sortingOrder = 100 + order * 2;
            foreach (Renderer outline in _outlineRenderers) { outline.sortingOrder = 99 + order * 2; }
        }

        /// <summary>缓存组件不复制材质；TextMesh及其描边继续共享字体材质，保留动态合批条件。</summary>
        private void Awake()
        {
            _textRenderer = damageText.GetComponent<Renderer>();
            _outlineRenderers = new Renderer[damageOutlines.Length];
            for (int i = 0; i < damageOutlines.Length; i++) { _outlineRenderers[i] = damageOutlines[i].GetComponent<Renderer>(); }
        }

        /// <summary>空间不足时，下面那条提示降至35%透明度；动画本身的淡出比例仍然保留。</summary>
        public void SetTextOccluded(bool covered)
        {
            float alpha = covered ? 0.35f : 1f;
            // 遮挡关系没有变化就不重复写5份TextMesh颜色。Tween淡出仍由SetNumberAlpha逐帧更新。
            if (Mathf.Approximately(alpha, _occlusionAlpha))
            {
                return;
            }
            _occlusionAlpha = alpha;
            ApplyNumberAlpha();
        }
        /// <summary>供开发验收读取；不参与战斗规则。</summary>
        public bool HasActiveAnimation { get { return _animation != null && _animation.IsActive(); } }

        /// <summary>伤害只显示已结算数值；arrange在创建Tween前安排位置，避免动画下一帧把避让位置覆盖掉。</summary>
        public void Show(BattleEffectKind kind, Vector3 position, int damage, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy, System.Action<BattleEffectView> arrange = null)
        {
            if (kind != BattleEffectKind.Damage)
            {
                throw new System.InvalidOperationException("数字特效只能显示已经结算的伤害；弹道应使用 ShowProjectile");
            }
            ResetVisual(position, onFinished);
            damageText.gameObject.SetActive(true);
            BattleText config = GameTextConfig.Require("Damage");
            Color color = targetSide == GameSide.Player ? new Color(1f, 0.28f, 0.22f) : GameTextConfig.ColorOf(config);
            ConfigureText(config, config.Text.Replace("{damage}", damage.ToString()), color);
            StartFloatingText(config, position + new Vector3(0f, 0.35f, -0.5f), false, arrange);
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
            // 本轮在原尺寸上整体放大20%；宽度上限与基础缩放一起改，避免长文案被旧上限重新压小。
            // BoardView随后用实际屏幕包围盒避让，所以放大后仍共享20条预算和重叠半透明规则。
            float maxWidth = camera == null ? 144f : camera.pixelWidth * (attack ? 0.072f : 0.048f);
            // 短文案也统一缩小，不能只有长句受限、两个字的口号仍然盖住整颗棋子。
            _baseTextScale = Mathf.Min(attack ? 0.864f : 0.984f, maxWidth / Mathf.Max(1f, TextScreenRect.width * _textPeak));
            transform.localScale = Vector3.one * _baseTextScale;
            arrange?.Invoke(this);
            Vector3 start = transform.position;
            Vector3 end = start + Vector3.up * _textRise;
            if (arrange == null)
            {
                end = KeepTextOnScreen(end);
            }
            float duration = Mathf.Min(config.Duration, attack ? 0.9f : 1.1f);
            transform.localScale = Vector3.one * (_baseTextScale * 0.72f);
            Sequence sequence = DOTween.Sequence();
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * (_baseTextScale * _textPeak), 0.14f).SetEase(Ease.OutQuad));
            sequence.Append(DOTween.To(() => transform.localScale, value => transform.localScale = value, Vector3.one * _baseTextScale, 0.14f).SetEase(Ease.OutBack));
            sequence.Insert(0f, DOTween.To(() => transform.position, value => transform.position = value, end, duration).SetEase(Ease.OutQuad));
            sequence.Insert(duration - 0.28f, DOTween.To(() => _fadeAlpha, SetNumberAlpha, 0f, 0.28f));
            _animation = sequence.OnComplete(Finish);
        }

        /// <summary>只播放已发生攻击的轨迹；扩大弹头、附带短尾与发射脉冲，动画绝不再次造成伤害。</summary>
        public void ShowProjectile(Vector3 source, Vector3 target, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy)
        {
            ResetVisual(source, onFinished);
            Vector3 direction = target - source;
            float distance = direction.magnitude;
            // ★ 同格/极短距离没有可飞行空间：直接结束，禁止在原点播放一根“没有位移的子弹”。
            if (distance < 0.05f)
            {
                Finish(); return;
            }
            Vector3 unit = direction / distance;
            float inset = Mathf.Min(Consts.CellSize * 0.46f, distance * 0.30f);
            source += unit * inset;
            target -= unit * inset;
            transform.position = source;
            // 固定时长会让相邻棋子之间的弹道长时间贴脸；短轨迹更快消失，长轨迹保留可读飞行时间。
            float duration = Mathf.Clamp(projectileDuration * distance / (Consts.CellSize * 4f), 0.10f, projectileDuration);
            _projectileActive = true;
            _projectileExpiresAt = Time.time + duration + 0.12f;
            damageText.gameObject.SetActive(false);
            spriteRenderer.enabled = true;
            spriteRenderer.sprite = projectileSprite;
            // 源Sprite本身宽约6.17个世界单位，不能直接把原图尺寸当成子弹尺寸。
            // 本体从0.38×0.10提高至0.82×0.24；再加轮廓和短尾，让小屏也能看到有方向的弹幕。
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            Vector3 flightScale = Vector3.one;
            if (projectileSprite != null)
            {
                Vector3 size = projectileSprite.bounds.size;
                // 弹头长度不能超过扣除身体后的飞行空间；相邻棋子之间不会出现一根比间隙还长的黄条。
                float length = Mathf.Min(projectileWorldSize.x, Vector3.Distance(source, target) * 0.8f);
                flightScale = new Vector3(length / Mathf.Max(0.01f, size.x), projectileWorldSize.y / Mathf.Max(0.01f, size.y), 1f);
                ConfigureProjectileLayer(projectileOutline, Vector3.zero, new Vector3(1.16f, 1.5f, 1f), spriteRenderer.sortingOrder - 2);
                ConfigureProjectileLayer(projectileTrail, Vector3.left * (size.x * 0.38f), new Vector3(0.48f, 0.55f, 1f), spriteRenderer.sortingOrder - 1);
            }
            // 敌我弹道用不同色相，密集交火时仍能分辨方向；不修改棋子或伤害数字的原有颜色。
            _projectileColor = targetSide == GameSide.Enemy ? new Color(1f, 0.92f, 0.28f) : new Color(1f, 0.38f, 0.22f);
            transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            Vector3 normalScale = flightScale;
            transform.localScale = normalScale * 0.88f;
            SetProjectileAlpha(1f);
            // 一条Tween同时推进飞行、发射脉冲和末段淡出；三个Sprite随同一个池对象回收，不额外创建计时器。
            _animation = DOTween.To(() => _progress, value =>
            {
                _progress = value;
                transform.position = Vector3.Lerp(source, target, value);
                float pulse = value < 0.25f ? Mathf.Lerp(0.88f, 1.08f, value / 0.25f) : Mathf.Lerp(1.08f, 1f, (value - 0.25f) / 0.75f);
                transform.localScale = normalScale * pulse;
                SetProjectileAlpha(Mathf.Clamp01((1f - value) / 0.18f));
            }, 1f, duration).SetEase(Ease.Linear).OnComplete(Finish);
        }

        /// <summary>生命周期保险：寿命到期先停止旧动画再归池；不能让失去Tween驱动的弹头永久停留在棋子上。</summary>
        private void LateUpdate()
        {
            if (_projectileActive && Time.time >= _projectileExpiresAt)
            {
                StopAnimation(); Finish();
            }
        }

        /// <summary>子图与本体共用Sprite和材质；局部尺寸跟随归一化根节点，尾巴始终短于一格。</summary>
        private void ConfigureProjectileLayer(SpriteRenderer layer, Vector3 offset, Vector3 scale, int order)
        {
            if (layer == null)
            {
                return;
            }
            layer.enabled = true;
            layer.sprite = projectileSprite;
            layer.drawMode = SpriteDrawMode.Simple;
            layer.sharedMaterial = spriteRenderer.sharedMaterial;
            layer.sortingLayerID = spriteRenderer.sortingLayerID;
            layer.sortingOrder = order;
            layer.transform.localPosition = offset;
            layer.transform.localRotation = Quaternion.identity;
            layer.transform.localScale = scale;
        }

        private void SetProjectileAlpha(float alpha)
        {
            Color color = _projectileColor;
            color.a = alpha;
            spriteRenderer.color = color;
            if (projectileOutline != null)
            {
                projectileOutline.color = new Color(0.12f, 0.07f, 0.02f, alpha * 0.85f);
            }
            if (projectileTrail != null)
            {
                color.a = alpha * 0.45f; projectileTrail.color = color;
            }
        }

        /// <summary>这个预制体承担数字、升星和弹道；复用为文字前必须关尾巴，避免旧弹道“粘”在文字上。</summary>
        private void HideProjectileLayers()
        {
            if (projectileOutline != null)
            {
                projectileOutline.enabled = false;
            }
            if (projectileTrail != null)
            {
                projectileTrail.enabled = false;
            }
        }

        /// <summary>文字离边缘留出可读空间；顶排命中时缩短上浮距离，防止数字被手机屏幕裁掉。</summary>
        private static Vector3 KeepTextOnScreen(Vector3 position, float top = 0.88f)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return position;
            }
            // 视口坐标 0~1 表示整块屏幕，与手机实际像素无关。上方还留有回合 HUD，不能飘进其区域。
            Vector3 viewport = camera.WorldToViewportPoint(position);
            viewport.x = Mathf.Clamp(viewport.x, 0.08f, 0.92f);
            viewport.y = Mathf.Clamp(viewport.y, 0.08f, top);
            return camera.ViewportToWorldPoint(viewport);
        }

        private void ResetVisual(Vector3 position, System.Action<BattleEffectView> onFinished)
        {
            StopAnimation();
            HideProjectileLayers();
            _projectileActive = false;
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
            _projectileActive = false;
            // 先撤掉所有图层，再通知棋盘和对象池。即使回池延迟到帧末，也不会再渲染最后一帧黄条。
            HideProjectileLayers();
            spriteRenderer.enabled = false;
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

        public void OnPoolGet() { StopAnimation(); HideProjectileLayers(); spriteRenderer.enabled = false; }
        public void OnPoolReturn()
        {
            StopAnimation();
            HideProjectileLayers();
            spriteRenderer.enabled = false;
            _projectileActive = false;
            _onFinished = null;
            IsFloatingText = IsAttackText = false;
            _fadeAlpha = _occlusionAlpha = 1f;
            damageText.text = string.Empty;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.text = string.Empty;
            }
        }
        private void OnDisable()
        {
            StopAnimation();
            HideProjectileLayers();
            spriteRenderer.enabled = false;
            _projectileActive = false;
        }
        private void OnDestroy() { StopAnimation(); }
    }
}
