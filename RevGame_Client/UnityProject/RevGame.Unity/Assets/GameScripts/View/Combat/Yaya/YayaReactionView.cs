// 立绘是场景SpriteRenderer，台词仿助阵帮手用WorldSpace Canvas；无Raycaster，不抢输入、不改棋局。
using System;
using DG.Tweening;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.View
{
    /// <summary>短时互动池对象，弹出、停留、缩回均拥有自己的Tween，回池先停止再清Sprite和文字。</summary>
    [RevPoolPrefab("Characters/Yaya/Reaction", "YayaReaction", Group = RevResGroup.Battle)]
    public sealed class YayaReactionView : MonoBehaviour, IRevPoolable
    {
        /// <summary>图片节点独立于根缩放，根只负责弹性动画；立绘尺寸来自安全区域布局。</summary>
        [SerializeField] private SpriteRenderer portrait;
        /// <summary>世界空间台词板，使用项目已有UI_Tip_imgBK九宫格图，无输入组件。</summary>
        [SerializeField] private Canvas bubble;
        /// <summary>本次纯文本台词，换句才排字，不支持富文本与交互链接。</summary>
        [SerializeField] private Text speech;
        /// <summary>只持有本实例的动画、目标缩放和完成回调，回池Kill(false)取消旧回调。</summary>
        private Tween _sequence;
        private Vector3 _baseScale;
        private Action _finished;
        private float _openedAt;
        /// <summary>本次完整尺寸停留秒数，不含0.24秒弹出与0.18秒缩回；状态查询用于核对Excel是否真正生效。</summary>
        public float HoldSeconds { get; private set; }
        /// <summary>本实例本次已显示的非缩放秒数；回池后不作为有效可见证据。</summary>
        public float VisibleSeconds => Visible ? Time.unscaledTime - _openedAt : 0;
        /// <summary>实际Renderer已能画图时为true，不用请求状态代替可见证据。</summary>
        public bool Visible => portrait != null && portrait.enabled && portrait.sprite != null;
        /// <summary>等待弹出Tween的0.24秒结束后才检查最终大小，不能拿首帧5%缩放作为完整立绘尺寸。</summary>
        public bool Settled => Visible && Time.unscaledTime - _openedAt >= .30f;
        /// <summary>只在新的互动发生时换Sprite/台词并排版；不逐帧GetSprite、不访问renderer.material。</summary>
        public void Play(Sprite sprite, YayaReactionLine line, Camera camera, Rect viewport, Action finished)
        {
            Stop();
            _finished = finished;
            _openedAt = Time.unscaledTime;
            portrait.sprite = sprite;
            portrait.enabled = true;
            bubble.gameObject.SetActive(true);
            bubble.worldCamera = camera;
            speech.text = line.Text;
            Layout(camera, viewport);
            _baseScale = transform.localScale;
            transform.localScale = _baseScale * .05f;
            float hold = line.Duration > 0 ? line.Duration : YayaReactionConfig.Value("PopupSeconds");
            HoldSeconds = hold;
            // Back弹出略微超过目标大小，布局已预留边距；单纯表现秒数不决定战斗冷却。
            _sequence = DOTween.Sequence().SetUpdate(true)
                .Append(transform.DOScale(_baseScale, .24f).SetEase(Ease.OutBack, 1.35f))
                .AppendInterval(hold)
                .Append(transform.DOScale(_baseScale * .04f, .18f).SetEase(Ease.InBack))
                .OnComplete(() =>
                {
                    _sequence = null;
                    Action callback = _finished;
                    _finished = null;
                    callback?.Invoke();
                });
        }
        /// <summary>区域为相机Viewport 0～1。人物占下方72%，对话框占上方28%，用相同尺子保证不盖脸。</summary>
        private void Layout(Camera camera, Rect area)
        {
            Vector3 low = camera.ViewportToWorldPoint(new Vector3(area.xMin, area.yMin, 10));
            Vector3 high = camera.ViewportToWorldPoint(new Vector3(area.xMax, area.yMax, 10));
            float width = high.x - low.x, height = high.y - low.y;
            transform.position = new Vector3((low.x + high.x) * .5f, low.y, 0);
            transform.localScale = Vector3.one;
            float bodyHeight = height * .72f;
            float scale = Mathf.Min(bodyHeight / portrait.sprite.bounds.size.y, width * .90f / portrait.sprite.bounds.size.x);
            portrait.transform.localScale = Vector3.one * scale;
            Vector3 bounds = portrait.sprite.bounds.center;
            portrait.transform.localPosition = new Vector3(-bounds.x * scale, bodyHeight * .5f - bounds.y * scale, 0);
            var rect = (RectTransform)bubble.transform;
            rect.sizeDelta = new Vector2(360, Mathf.Max(85, speech.preferredHeight + 28));
            float bubbleScale = Mathf.Min(width / 360, height * .25f / rect.sizeDelta.y);
            rect.localScale = Vector3.one * bubbleScale;
            rect.localPosition = new Vector3(0, height - rect.sizeDelta.y * bubbleScale * .5f, 0);
        }
        /// <summary>真实字体排版所需高度不超文字矩形，长台词仍能完整阅读。</summary>
        public bool SpeechFits => speech != null && speech.preferredHeight <= speech.rectTransform.rect.height + 1;
        /// <summary>停止自己的动画，切场景与回池可以重复调用，绝不KillAll误伤其它表现。</summary>
        private void Stop()
        {
            _sequence?.Kill(false);
            _sequence = null;
            _finished = null;
        }
        /// <summary>借出先恢复基础姿态，再等待Play提供新立绘与台词。</summary>
        public void OnPoolGet()
        {
            Stop();
            transform.localScale = Vector3.one;
        }
        /// <summary>归池清Sprite引用和气泡文本，使目录/图集能按引用计数释放；不把旧立绘留在空闲对象里。</summary>
        public void OnPoolReturn()
        {
            Stop();
            portrait.sprite = null;
            portrait.enabled = false;
            speech.text = "";
            bubble.gameObject.SetActive(false);
            transform.localScale = Vector3.one;
        }
        /// <summary>池清理真正销毁时仍停止本实例Tween。</summary>
        private void OnDestroy()
        {
            Stop();
        }
    }
}
