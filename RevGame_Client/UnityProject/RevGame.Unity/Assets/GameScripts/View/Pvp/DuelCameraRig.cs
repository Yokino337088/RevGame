using RevGame.Pvp.Duel;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>双人镜头只平移舞台的横轴。画布尺寸决定固定比例，RevUI 不随舞台移动。</summary>
    public sealed class DuelCameraRig : MonoBehaviour
    {
        // 决斗舞台的全屏矩形，轴心与角色锚点一致。
        public RectTransform Fighters;
        // 单张小镇全景放在舞台的第一层；横向结界位置对应画中的两根晶柱。
        public Image Backdrop;
        // 与原生边界对齐的两道结界，常驻舞台，触边才提高亮度。
        public DuelArenaFxGraphic LeftWard, RightWard;

        // 全景里两根晶柱中心的横向归一化间距，属于美术定位。
        private const float PillarSpan = .654f;
        // 结界扫亮、扩散和回落共占用的表现秒数。
        private const float WardSeconds = .58f;
        // 相机中心是相对舞台原点的设计像素；速度状态由 SmoothDamp 连续积累。
        private float _center, _centerSpeed;
        // 第一次采样直接取开局位置，避免从编辑器构图突然划过场地。
        private bool _initialized;
        // 两席上一次碰到的边界，0 为场内；持续顶住时不会每帧触发。
        private int _firstEdge, _secondEdge;
        // 两道结界上次触发的绝对表现秒数。
        private float _leftAt = float.NegativeInfinity, _rightAt = float.NegativeInfinity;
        // 结界平时是浅青，触发后用更明亮的白青轮廓压住场地边缘。
        private static readonly Color WardColor = new Color(.30f, .96f, .84f);

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 以真实播放帧计数，验收能核对撞边只触发一次。
        internal int LeftTouchesForCheck { get; private set; }
        internal int RightTouchesForCheck { get; private set; }
#endif

        /// <summary>按权威横坐标识别从场内碰到左右边界的事件，传入单位为千分格。</summary>
        internal void ObserveBounds(int firstX, int secondX)
        {
            int first = Edge(firstX);
            int second = Edge(secondX);
            if ((first == -1 && first != _firstEdge) || (second == -1 && second != _secondEdge))
            {
                _leftAt = Time.unscaledTime;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                LeftTouchesForCheck++;
#endif
            }
            if ((first == 1 && first != _firstEdge) || (second == 1 && second != _secondEdge))
            {
                _rightAt = Time.unscaledTime;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                RightTouchesForCheck++;
#endif
            }
            _firstEdge = first;
            _secondEdge = second;
        }

        // 视觉边界使用同一份原生配置，不拿图片边缘判断能否继续移动。
        private static int Edge(int x)
        {
            return x <= -DuelConfig.ArenaExtent ? -1 : x >= DuelConfig.ArenaExtent ? 1 : 0;
        }

        /// <summary>以两席横坐标构图。角色跳跃由舞台内坐标表现，镜头高度保持贴地。</summary>
        public void Paint(float blackX, float whiteX, float seconds)
        {
            float width = Fighters.rect.width;
            float canvasHeight = Fighters.rect.height;
            if (width < 1 || canvasHeight < 1)
            {
                return;
            }

            float left = Mathf.Min(blackX, whiteX);
            float right = Mathf.Max(blackX, whiteX);
            float dt = Mathf.Min(seconds, .05f);
            const float bodyMargin = 170;
            float arenaWidth = DuelConfig.ArenaExtent * PvpDuelView.ArenaScale * 2f;
            // 按双方同时站在最远边界计算比例。跳跃、出招或对手靠近都不会改变它。
            float scale = Mathf.Min(1, width * .96f / (arenaWidth + bodyMargin * 2));
            float middle = (blackX + whiteX) * .5f;
            if (!_initialized)
            {
                _center = middle;
                _initialized = true;
            }

            // 给普通步法留出死区。双人中点持续向一侧移动时才缓慢横移，边界校正负责留住双方。
            const float deadZone = 96;
            float shift = middle - _center;
            float desiredCenter = middle - Mathf.Clamp(shift, -deadZone, deadZone);
            float halfView = width * .48f / scale;
            float artWidth = Mathf.Max(arenaWidth / PillarSpan, (width + 46) / scale);
            float scrollLimit = Mathf.Max(0, (artWidth - (width + 40) / scale) * .5f);
            float minCenter = Mathf.Max(-scrollLimit, right + bodyMargin - halfView);
            float maxCenter = Mathf.Min(scrollLimit, left - bodyMargin + halfView);
            desiredCenter = Mathf.Clamp(desiredCenter, minCenter, maxCenter);
            _center = Mathf.Clamp(Mathf.SmoothDamp(_center, desiredCenter, ref _centerSpeed, .35f, 580, dt), minCenter, maxCenter);
            Fighters.localScale = new Vector3(scale, scale, 1);
            Fighters.anchoredPosition3D = new Vector3(-_center * scale, 0, 0);

            // 背景跟随同一舞台水平移动。按上下留边确定大图尺寸，地平线高度固定。
            RectTransform art = Backdrop.rectTransform;
            const float ground = .225f;
            const float artFloor = -172f;
            float anchorY = canvasHeight * Fighters.pivot.y;
            float bottom = (anchorY + 22) / scale + artFloor;
            float top = (canvasHeight - anchorY + 22) / scale - artFloor;
            Rect sprite = Backdrop.sprite.rect;
            art.sizeDelta = new Vector2(artWidth, Mathf.Max(artWidth * sprite.height / sprite.width, Mathf.Max(bottom / ground, top / (1 - ground))));
            art.anchoredPosition = new Vector2(0, artFloor);

            PaintWard(LeftWard, _leftAt);
            PaintWard(RightWard, _rightAt);
        }

        // 触发后立即闪亮，法纹向上扫过，再缓慢回落到常驻的浅色提示。
        private static void PaintWard(DuelArenaFxGraphic ward, float started)
        {
            float phase = Mathf.Clamp01((Time.unscaledTime - started) / WardSeconds);
            Color tint = WardColor;
            tint.a = .14f + .86f * (1 - Mathf.SmoothStep(0, 1, phase));
            ward.color = tint;
            ward.Paint(DuelArenaFxGraphic.Shape.Ward, phase);
        }
    }
}
