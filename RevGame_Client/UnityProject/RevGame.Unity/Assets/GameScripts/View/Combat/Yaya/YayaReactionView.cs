// 立绘是场景SpriteRenderer，台词仿助阵帮手用WorldSpace Canvas；无Raycaster，不抢输入、不改棋局。
using System;
using DG.Tweening;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.View
{
        /// <summary>短时互动池对象，显示期间固定尺寸，回池先停止自己的计时再清Sprite和文字。</summary>
    [RevPoolPrefab("Characters/Yaya/Reaction", "YayaReaction", Group = RevResGroup.Battle)]
    public sealed class YayaReactionView : MonoBehaviour, IRevPoolable
    {
        /// <summary>图片节点独立于根缩放；立绘尺寸来自固定区域布局，不随入场、退场或候选位置改变。</summary>
        [SerializeField] private SpriteRenderer portrait;
        /// <summary>世界空间台词板，使用项目已有UI_Tip_imgBK九宫格图，无输入组件。</summary>
        [SerializeField] private Canvas bubble;
        /// <summary>本次纯文本台词，换句才排字，不支持富文本与交互链接。</summary>
        [SerializeField] private Text speech;
        /// <summary>只持有本实例的显示计时和完成回调，回池Kill(false)取消旧回调。</summary>
        private Tween _sequence;
        private Action _finished;
        private float _openedAt;
        /// <summary>本次主停留秒数；保留原有前后共0.42秒显示时间，但整个过程都不缩放。</summary>
        public float HoldSeconds
        {
            get; private set;
        }
        /// <summary>本实例本次已显示的非缩放秒数；回池后不作为有效可见证据。</summary>
        public float VisibleSeconds => Visible ? Time.unscaledTime - _openedAt : 0;
        /// <summary>实际Renderer已能画图时为true，不用请求状态代替可见证据。</summary>
        public bool Visible => portrait != null && portrait.enabled && portrait.sprite != null;
        /// <summary>没有缩放过渡，图片首次可画即为最终尺寸，供自动化跨帧核对大小恒定。</summary>
        public bool Settled => Visible;
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
            float hold = line.Duration > 0 ? line.Duration : YayaReactionConfig.Value("PopupSeconds");
            HoldSeconds = hold;
            // 立绘从出现到收回始终使用固定尺寸；不再弹性放大、缩小，计时只控制本次显示寿命。
            _sequence = DOTween.Sequence().SetUpdate(true)
                .AppendInterval(.24f + hold + .18f)
                .OnComplete(() =>
                {
                    _sequence = null;
                    Action callback = _finished;
                    _finished = null;
                    callback?.Invoke();
                });
        }
        /// <summary>区域为相机Viewport 0～1。人物尺寸固定，对话框从立绘顶部向上排，保留小间距，避免悬在远处。</summary>
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
            // 使用实际立绘上沿定位，宽屏或窄侧栏下也贴近头顶；透明画布顶部另有少量原画留白。
            float portraitTop = bodyHeight * .5f + portrait.sprite.bounds.size.y * scale * .5f;
            rect.localPosition = new Vector3(0, portraitTop + height * .015f + rect.sizeDelta.y * bubbleScale * .5f, 0);
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
