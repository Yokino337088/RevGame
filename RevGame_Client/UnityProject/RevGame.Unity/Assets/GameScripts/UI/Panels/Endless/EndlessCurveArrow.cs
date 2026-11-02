using DG.Tweening;
using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace RevGame.Endless
{
    /// <summary>
    /// 一块UGUI网格生成可任意伸缩的三次贝塞尔箭头，使用真实箭头尖与丝带Sprite。
    /// 端点由手指输入，DOTween弹性驱动伸展；手指快速改方向时中心弯曲由速度偏移形成回弹感。
    /// 顶点在固定48段内重用，不生成LineRenderer/材质克隆；UI射线关闭，箭头不会挡住落点。
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class EndlessCurveArrow : MaskableGraphic
    {
        /// <summary>两张真实Sprite由无尽箭头图集统一绑定；必须在同一页，网格只提交一次UI材质。</summary>
        [SerializeField] private Sprite ribbon, arrowHead;
        /// <summary>设计画布单位的宽度和箭头尖大小，纯表现结构，不参与棋盘判定。</summary>
        private const float Width = 12f, HeadSize = 52f;
        /// <summary>固定采样段数，足够平滑又限制网格规模；两侧98顶点加箭头尖4顶点。</summary>
        private const int Segments = 48;
        /// <summary>全部以画布局部单位表示：卡牌中心、指针目标、缓动端点与每帧位移。</summary>
        private Vector2 _origin, _wanted, _follow, _velocity;
        /// <summary>0隐藏、1正常伸展，OutElastic可短暂超过1形成回弹；不改变实际落格。</summary>
        private float _extension;
        /// <summary>松手后停止跟手，仅播放收回动画，取消后立即失效。</summary>
        private bool _following;
        /// <summary>当前租约唯一伸缩Tween，关闭/取消时Kill(false)，不允许旧onComplete隐藏新箭头。</summary>
        private Tween _stretch;
        /// <summary>图集页内的UV边界；只在绑定版本变化时读取，禁止逐帧GetSprite。</summary>
        private Vector4 _ribbonUv, _headUv;
        private int _bindingVersion = -1;
        /// <summary>最近一次网格中点相对直线的距离（画布单位），用来验收确实弯曲。</summary>
        private float _curveDeviation;
        /// <summary>开发诊断计数：网格调用次数和最近实际提交顶点数，不参与表现计算。</summary>
        private int _meshCalls, _meshVertices;
        /// <summary>只读曲率判定，偏离超过10画布单位才算明显曲线。</summary>
        public bool HasCurve => _curveDeviation > 10f;
        /// <summary>控件已启用且伸展量非零；仅Tween存在不代表屏幕已经可画。</summary>
        public bool IsVisible => enabled && _extension > .001f;
        /// <summary>卡牌中心，显示租约开始后固定不动。</summary>
        public Vector2 Origin => _origin;
        /// <summary>缓动显示端点，业务松手时使用真实指针，不使用这个滞后位置。</summary>
        public Vector2 Tip => _follow;
        /// <summary>使用图集Sprite所属纹理；未装配时的默认纹理只保证UGUI契约，网格会拒绝绘制。</summary>
        public override Texture mainTexture => ribbon != null ? ribbon.texture : Texture2D.whiteTexture;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>读实际网格提交与坐标，定位Canvas未渲染的原因；不会修改曲线或业务。</summary>
        public object Snapshot()
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["origin"] = _origin, ["wanted"] = _wanted, ["follow"] = _follow,
                ["extension"] = _extension, ["deviation"] = _curveDeviation,
                ["meshCalls"] = _meshCalls, ["vertices"] = _meshVertices,
                ["culled"] = canvasRenderer.cull, ["depth"] = canvasRenderer.absoluteDepth,
                ["rect"] = rectTransform.rect, ["samePage"] = ribbon != null && arrowHead != null && ribbon.texture == arrowHead.texture
            };
        }
#endif

        /// <summary>开启新显示租约，先停止旧Tween，再从卡牌中心弹性展开；支持无时间缩放UI。</summary>
        public void Begin(Vector2 origin, Vector2 destination)
        {
            _stretch?.Kill(false);
            _origin = origin;
            _wanted = destination;
            _follow = origin;
            _velocity = Vector2.zero;
            _extension = 0;
            _following = true;
            enabled = true;
            _bindingVersion = -1;
            SetAllDirty();
            raycastTarget = false;
            color = new Color(.3f, .82f, .65f, .72f);
            _stretch = DOTween.To(ReadExtension, WriteExtension, 1f, .55f).SetEase(Ease.OutElastic, .85f, .22f).SetUpdate(true).OnComplete(ClearStretch);
        }
        /// <summary>输入实际指针目标与合法性，只改变目标及颜色，不动手牌与战斗状态。</summary>
        public void Aim(Vector2 destination, bool valid)
        {
            _wanted = destination;
            color = valid ? new Color(.3f, .82f, .65f, .72f) : new Color(1f, .55f, .42f, .5f);
        }
        /// <summary>收线时先停止跟手，DOTween让箭头沿曲线缩回起点；业务已经在松手时提交，动画不再扣牌。</summary>
        public void Retract()
        {
            _following = false;
            _stretch?.Kill(false);
            _stretch = DOTween.To(ReadExtension, WriteExtension, 0f, .16f).SetEase(Ease.InBack).SetUpdate(true).OnComplete(Hide);
        }
        /// <summary>立即撤销并Kill(false)，旧回调不能再隐藏下次拖曳；可重复调用。</summary>
        public void Cancel()
        {
            _following = false;
            _stretch?.Kill(false);
            _stretch = null;
            _extension = 0;
            enabled = false;
        }
        /// <summary>DOTween读取当前伸展量，使用方法委托便于明确动画只控制显示。</summary>
        private float ReadExtension()
        {
            return _extension;
        }
        /// <summary>动画每次写入只标脏顶点，纹理与材质绑定不需要随伸缩更新。</summary>
        private void WriteExtension(float value)
        {
            _extension = value;
            SetVerticesDirty();
        }
        /// <summary>自然结束立即清除Tween引用，避免DOTween回收复用后误停其它动画。</summary>
        private void ClearStretch()
        {
            _stretch = null;
        }
        /// <summary>收回自然结束才隐藏；所有取消入口已停止旧Tween，因此不影响新租约。</summary>
        private void Hide()
        {
            _stretch = null;
            _extension = 0;
            enabled = false;
        }
        /// <summary>指数平滑跟随指针，使用不受暂停影响的时间；速度只用于表现回弹。</summary>
        private void Update()
        {
            if (!_following)
            {
                return;
            }
            Vector2 old = _follow;
            // 轻微跟随滞后提供拉橡皮筋的手感；此计算只改变显示，松手始终使用真实指针坐标。
            _follow = Vector2.Lerp(_follow, _wanted, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 24f));
            _velocity = Vector2.Lerp(_velocity, _follow - old, .35f);
            SetVerticesDirty();
        }
        /// <summary>固定三次贝塞尔公式：四个控制点连续变化，末端切线给箭头尖朝向，任意长度仍保持曲线。</summary>
        private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float u = 1f - t;
            return a * (u * u * u) + b * (3f * u * u * t) + c * (3f * u * t * t) + d * (t * t * t);
        }
        /// <summary>贝塞尔两侧构造连续丝带，末端切线构造箭头尖；纹理缺失或长度过短时不提交网格。</summary>
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            _meshCalls++;
            _meshVertices = 0;
            _curveDeviation = 0;
            if (ribbon == null || arrowHead == null || _extension <= .001f)
            {
                return;
            }
            if (_bindingVersion != RevGame.View.GameSpriteAtlasService.BindingVersion)
            {
                _bindingVersion = RevGame.View.GameSpriteAtlasService.BindingVersion;
                _ribbonUv = DataUtility.GetOuterUV(ribbon);
                _headUv = DataUtility.GetOuterUV(arrowHead);
                // 纹理由同一图集页提供；在Begin时SetAllDirty提交材质，Populate期间只写网格。
            }
            Vector2 end = _origin + (_follow - _origin) * _extension;
            Vector2 delta = end - _origin;
            float distance = delta.magnitude;
            if (distance < 2f)
            {
                return;
            }
            Vector2 direction = delta / distance;
            Vector2 normal = new Vector2(-direction.y, direction.x);
            // 保证短距离也有弧度；拉得越长弯曲越明显，方向改变带出轻微侧向回弹。
            float bend = Mathf.Clamp(distance * .18f, 20f, 130f);
            bend += Mathf.Clamp(Vector2.Dot(_velocity, normal) * 2f, -35f, 35f);
            Vector2 controlA = _origin + delta * .28f + normal * bend;
            Vector2 controlB = _origin + delta * .7f + normal * bend * .7f;
            _curveDeviation = Mathf.Abs(Vector2.Dot(Bezier(_origin, controlA, controlB, end, .5f) - (_origin + end) * .5f, normal));
            Color32 tint = color;
            for (int i = 0; i <= Segments; i++)
            {
                float t = i / (float)Segments;
                Vector2 center = Bezier(_origin, controlA, controlB, end, t);
                Vector2 derivative = Bezier(_origin, controlA, controlB, end, Mathf.Min(1f, t + .01f)) - Bezier(_origin, controlA, controlB, end, Mathf.Max(0, t - .01f));
                Vector2 cross = new Vector2(-derivative.y, derivative.x).normalized * Width;
                float u = Mathf.Lerp(_ribbonUv.x, _ribbonUv.z, t);
                mesh.AddVert(center - cross, tint, new Vector2(u, _ribbonUv.y));
                mesh.AddVert(center + cross, tint, new Vector2(u, _ribbonUv.w));
                if (i > 0)
                {
                    int vertex = i * 2;
                    mesh.AddTriangle(vertex - 2, vertex - 1, vertex);
                    mesh.AddTriangle(vertex, vertex - 1, vertex + 1);
                }
            }
            Vector2 tangent = (end - controlB).normalized;
            Vector2 side = new Vector2(-tangent.y, tangent.x) * HeadSize * .5f;
            int head = mesh.currentVertCount;
            mesh.AddVert(end - tangent * HeadSize - side, tint, new Vector2(_headUv.x, _headUv.y));
            mesh.AddVert(end - tangent * HeadSize + side, tint, new Vector2(_headUv.z, _headUv.y));
            mesh.AddVert(end + side, tint, new Vector2(_headUv.z, _headUv.w));
            mesh.AddVert(end - side, tint, new Vector2(_headUv.x, _headUv.w));
            mesh.AddTriangle(head, head + 1, head + 2);
            mesh.AddTriangle(head, head + 2, head + 3);
            _meshVertices = mesh.currentVertCount;
        }
        /// <summary>UGUI停用/面板回池兜底停止当前Tween，然后保留Graphic原有清理流程。</summary>
        protected override void OnDisable()
        {
            _stretch?.Kill(false);
            _stretch = null;
            base.OnDisable();
        }
    }
}
