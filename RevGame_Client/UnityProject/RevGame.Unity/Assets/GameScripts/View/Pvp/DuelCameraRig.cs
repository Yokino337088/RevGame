using RevGame.Pvp.Duel;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>双人镜头使用权威播放坐标平滑构图，舞台、角色和结界一起移动，RevUI 留在屏幕上。</summary>
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
        // 镜头尺寸与其速度，缩远较快，回到近景较慢。
        private float _zoom = 1f, _zoomSpeed;
        // 起跳时稍向上看，落地平缓回到同一个地平线。
        private float _lift, _liftSpeed;
        // 第一次采样直接取开局位置，避免从编辑器构图突然划过场地。
        private bool _initialized;
        // 命中冲量的当前剩余秒数、总秒数和横向峰值。
        private float _hitLeft, _hitDuration, _hitPower;
        // 由攻击方向决定的镜头冲量符号。
        private int _hitDirection;
        // 大招起手的聚焦播放头，短推进后按曲线平缓还原。
        private float _focusLeft;
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

        /// <summary>权威大招起手时请求短时聚焦，双方距离仍限制最大推进。</summary>
        public void FocusUltimate()
        {
            _focusLeft = .62f;
        }

        /// <summary>实际命中后触发一次定向冲量。用强度覆盖弱效果，快速连击也不会连续硬切镜头。</summary>
        public void Hit(int action, int facing)
        {
            float power = action == 3 ? 18f : action is 2 or 4 ? 10f : 3f;
            if (_hitLeft > 0 && power < _hitPower)
            {
                return;
            }
            _hitPower = power;
            _hitDirection = facing < 0 ? -1 : 1;
            _hitDuration = action == 3 ? .27f : .17f;
            _hitLeft = _hitDuration;
        }

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

        /// <summary>以同一帧两席横坐标和跳跃高度构图，先确保角色安全边距，再限制背景不露出画布边缘。</summary>
        public void Paint(float blackX, float whiteX, float height, float seconds)
        {
            float width = Fighters.rect.width;
            float canvasHeight = Fighters.rect.height;
            if (width < 1 || canvasHeight < 1)
            {
                return;
            }

            float left = Mathf.Min(blackX, whiteX);
            float right = Mathf.Max(blackX, whiteX);
            float span = right - left;
            float dt = Mathf.Min(seconds, .05f);
            float focusTime = 1 - _focusLeft / .62f;
            float focus = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0, .23f, focusTime)) *
                (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.44f, 1, focusTime)));
            _focusLeft = Mathf.Max(0, _focusLeft - dt);

            // 留出身体宽度和小幅冲量。远距即时退到安全倍率，近距用较慢速度回到中景。
            const float bodyMargin = 198;
            float safeZoom = width * .92f / (span + bodyMargin * 2);
            float desiredZoom = Mathf.Min(safeZoom, 1.04f + .085f * focus);
            float middle = (blackX + whiteX) * .5f;
            if (!_initialized)
            {
                _zoom = desiredZoom;
                _center = middle;
                _initialized = true;
            }
            _zoom = Mathf.SmoothDamp(_zoom, desiredZoom, ref _zoomSpeed, desiredZoom < _zoom ? .12f : .28f, Mathf.Infinity, dt);
            if (_zoom > safeZoom)
            {
                _zoom = safeZoom;
                _zoomSpeed = Mathf.Min(0, _zoomSpeed);
            }

            // 小幅步法落在取景死区。贴边时仍把两席和舞台端点留在构图内。
            float deadZone = Mathf.Lerp(32, 10, focus);
            float shift = middle - _center;
            float desiredCenter = middle - Mathf.Clamp(shift, -deadZone, deadZone);
            _center = Mathf.SmoothDamp(_center, desiredCenter, ref _centerSpeed, .17f, Mathf.Infinity, dt);
            float halfView = width * .46f / _zoom;
            _center = Mathf.Clamp(_center, right + bodyMargin - halfView, left - bodyMargin + halfView);

            float stageWidth = DuelConfig.ArenaExtent * PvpDuelView.ArenaScale * 2f / PillarSpan;
            float artWidth = Mathf.Max(stageWidth, (width + 46) / _zoom);
            float scrollLimit = Mathf.Max(0, (artWidth - (width + 40) / _zoom) * .5f);
            _center = Mathf.Clamp(_center, -scrollLimit, scrollLimit);

            // 蓄力推进和命中冲量使用各自的曲线，冲量结束速度也回到零。
            _hitLeft = Mathf.Max(0, _hitLeft - dt);
            float decay = _hitDuration > 0 ? _hitLeft / _hitDuration : 0;
            float kick = 6.75f * (1 - decay) * decay * decay * _hitPower;
            _lift = Mathf.SmoothDamp(_lift, -Mathf.Clamp(height * .16f, 0, 54), ref _liftSpeed, .18f, Mathf.Infinity, dt);
            float floor = _lift - Mathf.Max(0, 1 - _zoom) * 220;
            Fighters.localScale = new Vector3(_zoom, _zoom, 1);
            Fighters.anchoredPosition = new Vector2(-_center * _zoom - kick * _hitDirection, floor + kick * .24f);

            // 预留屏幕出血，用地面和天空各自所需的高度确定大图尺寸。宽屏缩远时仍铺满。
            RectTransform art = Backdrop.rectTransform;
            const float ground = .225f;
            const float artFloor = -172f;
            float anchorY = canvasHeight * Fighters.pivot.y + floor;
            float bottom = (anchorY + 22) / _zoom + artFloor;
            float top = (canvasHeight - anchorY + 22) / _zoom - artFloor;
            Rect sprite = Backdrop.sprite.rect;
            art.sizeDelta = new Vector2(artWidth, Mathf.Max(artWidth * sprite.height / sprite.width, Mathf.Max(bottom / ground, top / (1 - ground))));
            art.anchoredPosition = new Vector2(0, artFloor);
            Backdrop.color = Color.Lerp(Color.white, new Color(.75f, .82f, .91f), focus * .65f);

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
