using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>展示帮手序列帧和爬行动画。四帧由预制体引用，规则和支援行为由服务持有。</summary>
    [RevPoolPrefab("Pieces/Helper", "BattleHelper", Group = RevResGroup.Battle)]
    public sealed class HelperView : MonoBehaviour, IRevPoolable
    {
        [Tooltip("共享棋子图集中的身体，只切Sprite，不创建Material。")]
        [SerializeField] private SpriteRenderer body;
        [Tooltip("指定特殊棋子目录四帧，资源配方一次绑定；由BoardView租用图集。")]
        [SerializeField] private Sprite[] frames;
        /// <summary>旧预制体的对白画布。新预制体为空，旧实例在 Awake 时禁用。</summary>
        [SerializeField] private Canvas bubble;
        private Tween _movement;
        private BoardPos _lastPosition;
        private bool _placed;
        private float _animationTime;
        private int _lastFrame = -1;
        private MaterialPropertyBlock _goldProperties;
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

        /// <summary>实例创建时关闭旧 AB 残留的台词框，在对象池复用前就切断其渲染。</summary>
        private void Awake()
        {
            if (bubble != null)
            {
                bubble.gameObject.SetActive(false);
            }
        }

        /// <summary>只读验收旧预制体是否仍存在可见对白，新的帮手没有对白画布。</summary>
        public bool SpeechVisible => bubble != null && bubble.isActiveAndEnabled;

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
            else if (_lastPosition != snapshot.Position)
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
        }

        /// <summary>只推进已预载的身体帧和爬行动画，帧变更时同步金光采样。</summary>
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
