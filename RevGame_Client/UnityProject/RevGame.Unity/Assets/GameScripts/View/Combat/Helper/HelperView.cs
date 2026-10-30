using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>帮手序列帧、蜘蛛式轻爬行与台词框。四帧在预制体中引用，图集绑定完成后只切Sprite，不GetSprite。</summary>
    [RevPoolPrefab("Pieces/Helper", "BattleHelper", Group = RevResGroup.Battle)]
    public sealed class HelperView : MonoBehaviour, IRevPoolable
    {
        [Tooltip("共享棋子图集中的身体，只切Sprite，不创建Material。")]
        [SerializeField] private SpriteRenderer body;
        [Tooltip("指定特殊棋子目录四帧，资源配方一次绑定；由BoardView租用图集。")]
        [SerializeField] private Sprite[] frames;
        [Tooltip("最近动作短反馈；只在文字改变时刷新。")]
        [SerializeField] private Text speech;
        [Tooltip("世界空间的小台词框，无GraphicRaycaster，不阻挡棋盘输入。")]
        [SerializeField] private Canvas bubble;
        private Tween _movement;
        private BoardPos _lastPosition;
        private bool _placed;
        private float _animationTime;
        private int _lastFrame = -1;
        private MaterialPropertyBlock _goldProperties;
        /// <summary>唯一帮手拥有的台词布局缓存。只在换台词时排字，LateUpdate仅约束屏幕边缘。</summary>
        private readonly Vector3[] _bubbleCorners = new Vector3[4];
        private Vector3 _bubbleAnchor;
        private Camera _bubbleCamera;
        private bool _bubbleLayoutReady;
        private static readonly int FrameUvId = Shader.PropertyToID("_FrameUV"), MainTexId = Shader.PropertyToID("_MainTex");
        /// <summary>只读验收：当前身体仍采样当前帧图集，金光参数块不能把它换成默认白纹理。</summary>
        public bool GoldReflectionReady => body != null && body.sprite != null && body.sprite.texture != null
            && body.sharedMaterial != null && body.sharedMaterial.shader.name == "RevGame/Helper/GoldReflect"
            && _goldProperties != null && _goldProperties.GetTexture(MainTexId) == body.sprite.texture;
        public int VisibleFrameIndex => _lastFrame;
        public float BodyScale => body.transform.localScale.x;
        public bool FramesReady
        {
            get
            {
                if (frames == null || frames.Length != 4)
                {
                    return false;
                }
                foreach (Sprite frame in frames)
                {
                    if (frame == null || frame.texture == null)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
        /// <summary>按服务快照落屏；不碰积分、不改位置规则。移动被新目标覆盖时先停止旧Tween。</summary>
        public void Present(HelperSnapshot snapshot)
        {
            gameObject.SetActive(snapshot.Active);

            if (!snapshot.Active)
            {
                return;
            }
            Vector3 target = snapshot.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);

            if (!_placed)
            {
                transform.position = target;
                _placed = true;
                _lastPosition = snapshot.Position;
            }
            else

                if (_lastPosition != snapshot.Position)
                {
                    _movement?.Kill(false);
                    _lastPosition = snapshot.Position;
                    Vector3 start = transform.position;
                    float progress = 0;
                    // 用小幅起伏表现攀爬，跨盘位移增加跳跃高度；纯动画参数不影响移动冷却/战斗规则。
                    float height = Vector3.Distance(start, target) > Consts.CellSize * 2 ? .28f : .09f;
                    _movement = DOTween.To(() => progress, value =>
                    {
                        progress = value;
                        transform.position = Vector3.Lerp(start, target, value) + Vector3.up * (Mathf.Sin(value * Mathf.PI) * height);
                    }, 1f, .42f).SetEase(Ease.InOutSine).OnComplete(() => _movement = null);
                }
            if (speech != null && (speech.text != snapshot.Speech || !_bubbleLayoutReady))
            {
                speech.text = snapshot.Speech;
                RefreshSpeechLayout();
            }
        }

        /// <summary>按当前文案测宽，再在有限宽度内换行测高；不缩小字体，避免长句被固定高度截掉。</summary>
        private void RefreshSpeechLayout()
        {
            if (bubble == null || speech == null)
            {
                return;
            }
            var rect = (RectTransform)bubble.transform;

            if (!_bubbleLayoutReady)
            {
                _bubbleAnchor = rect.localPosition;
            }
            speech.horizontalOverflow = HorizontalWrapMode.Wrap;
            speech.verticalOverflow = VerticalWrapMode.Truncate;
            speech.resizeTextForBestFit = false;
            float width = Mathf.Clamp(speech.preferredWidth + 32f, 270f, 390f);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            // 文字与底板都用拉伸锚点，旧AB实例同样能够得到正确内边距。
            speech.rectTransform.anchorMin = Vector2.zero;
            speech.rectTransform.anchorMax = Vector2.one;
            speech.rectTransform.offsetMin = new Vector2(16f, 10f);
            speech.rectTransform.offsetMax = new Vector2(-16f, -10f);
            Transform background = rect.Find("Background");

            if (background is RectTransform bg)
            {
                bg.anchorMin = Vector2.zero;
                bg.anchorMax = Vector2.one;
                bg.offsetMin = bg.offsetMax = Vector2.zero;
            }
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(72f, speech.preferredHeight + 24f));
            _bubbleLayoutReady = true;
        }
        /// <summary>只读验收完整字形所需高度；独立于截图，能抓到“背景变大但文字仍截断”。</summary>
        public bool SpeechFits => speech != null && speech.preferredHeight <= speech.rectTransform.rect.height + 1f;

        private void LateUpdate()
        {
            if (bubble == null || !_bubbleLayoutReady)
            {
                return;
            }
            if (_bubbleCamera == null)
            {
                _bubbleCamera = Camera.main;
            }
            if (_bubbleCamera == null || !_bubbleCamera.orthographic)
            {
                return;
            }
            var rect = (RectTransform)bubble.transform;
            rect.localPosition = _bubbleAnchor;
            rect.GetWorldCorners(_bubbleCorners);
            Vector3 low = _bubbleCamera.WorldToViewportPoint(_bubbleCorners[0]);
            Vector3 high = _bubbleCamera.WorldToViewportPoint(_bubbleCorners[2]);
            float dx = low.x < .02f ? .02f - low.x : high.x > .98f ? .98f - high.x : 0f;
            float dy = low.y < .02f ? .02f - low.y : high.y > .98f ? .98f - high.y : 0f;
            // 帮手会爬到棋盘外；将气泡移回镜头安全区，不移动棋子也不改变台词内容。
            rect.position += _bubbleCamera.transform.right * (dx * _bubbleCamera.orthographicSize * 2f * _bubbleCamera.aspect)
                + _bubbleCamera.transform.up * (dy * _bubbleCamera.orthographicSize * 2f);
        }
        private void Update()
        {
            if (!FramesReady)
            {
                body.enabled = false;
                return;
            }
            body.enabled = true;
            _animationTime += Time.deltaTime;
            int frame = (int)(_animationTime / .14f) % frames.Length;

            if (_lastFrame != frame)
            {
                body.sprite = frames[frame];
                _lastFrame = frame;

                if (_goldProperties == null)
                {
                    _goldProperties = new MaterialPropertyBlock();
                }
                // 自定义参数块须显式写当前图集贴图；否则SpriteRenderer会退回白图，金光变成白色剪影。
                Vector4 uv = UnityEngine.Sprites.DataUtility.GetOuterUV(body.sprite);
                _goldProperties.SetTexture(MainTexId, body.sprite.texture);
                _goldProperties.SetVector(FrameUvId, new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
                body.SetPropertyBlock(_goldProperties);
            }
            body.transform.localPosition = new Vector3(0, Mathf.Sin(_animationTime * 7f) * .022f, 0);
            // 正交2D相机无需每帧LookAt；台词框固定朝向屏幕，且不阻挡点击敌棋。
        }
        public void OnPoolGet()
        {
            _placed = false;
            _lastFrame = -1;
            _animationTime = 0;
        }
        public void OnPoolReturn()
        {
            _movement?.Kill(false);
            _movement = null;
            _placed = false;
        }
        private void OnDisable()
        {
            _movement?.Kill(false);
            _movement = null;
        }
    }
}
