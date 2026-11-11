using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    // 由决斗场景持有的双人镜头。接收权威播放坐标与命中事件，变换只作用于擂台和角色层。
    public sealed class DuelCameraRig : MonoBehaviour
    {
        // 包含角色和技能图层的变换。轴心与角色锚点高度一致，缩放时脚下地平线保持稳定。
        public RectTransform Fighters;
        // 场景底图随镜头做较小幅度视差。HUD 在此组件控制的图层之外。
        public Image Backdrop;
        // 已应用的水平取景中心，单位为画布局部像素。
        private float _pan;
        // 已应用的擂台缩放，1 对应编辑器默认构图。
        private float _zoom = 1;
        // 本次命中震动总时长，单位为表现秒。
        private float _shakeDuration;
        // 当前震动剩余秒数。新事件可覆盖同级或较弱震动。
        private float _shakeLeft;
        // 当前震动的峰值位移，单位为画布局部像素。
        private float _shakePower;
        // 横向震动的起始方向，来自命中者面向。
        private int _shakeDirection;
        // 大招起手的短暂背景压暗和聚焦时间，仅影响画面。
        private float _ultimateFocus;

        // 权威大招开始后收紧镜头，角色分得很开时仍保留足够的画面宽度。
        public void FocusUltimate()
        {
            _ultimateFocus = .42f;
        }

        // 同一权威命中只进入这里一次。大招或技能震动持续时，轻击不会把强效果打断。
        public void Hit(int action, int facing)
        {
            // 普攻用命中特效和闪色反馈，技能与大招才叠加一次镜头冲量。
            float power = action == 3 ? 14 : action == 2 || action == 4 ? 7 : 0;
            if (power == 0)
            {
                return;
            }
            if (_shakeLeft > 0 && power < _shakePower)
            {
                return;
            }
            _shakePower = power;
            _shakeDirection = facing < 0 ? -1 : 1;
            _shakeDuration = action == 3 ? .28f : .16f;
            _shakeLeft = _shakeDuration;
        }

        // 以两席已确认的横坐标共同取景。预测位移预留在安全边距中，避免回滚拉扯全场镜头。
        public void Paint(float blackX, float whiteX, float height, float seconds)
        {
            float width = Fighters.rect.width;
            if (width <= 0)
            {
                return;
            }
            // 两人距离加角色和出招边距决定可用的最大倍率。手机与桌面按实际宽度计算。
            float span = Mathf.Abs(whiteX - blackX);
            float fit = width * .87f / (span + 560);
            _ultimateFocus = Mathf.Max(0, _ultimateFocus - seconds);
            float focus = Mathf.Clamp01(_ultimateFocus / .32f);
            float targetZoom = Mathf.Min(fit, Mathf.Lerp(1.17f, 1.25f, focus));
            float blend = 1 - Mathf.Exp(-9 * seconds);
            _zoom = Mathf.Min(fit, Mathf.Lerp(_zoom, targetZoom, blend));
            float middle = (blackX + whiteX) * .5f;
            _pan = Mathf.Lerp(_pan, -middle * _zoom, blend);
            // 中心平滑移动时也要保持双方在框内。大位移会先退到安全取景范围。
            float half = width * .48f;
            float low = -half - (Mathf.Min(blackX, whiteX) - 205) * _zoom;
            float high = half - (Mathf.Max(blackX, whiteX) + 205) * _zoom;
            _pan = Mathf.Clamp(_pan, low, high);

            _shakeLeft = Mathf.Max(0, _shakeLeft - seconds);
            float decay = _shakeDuration > 0 ? _shakeLeft / _shakeDuration : 0;
            // 单次偏移后平滑回中，避免短时间反复改变整个角色层的移动方向。
            float shake = 6.75f * (1 - decay) * decay * decay * _shakePower;
            float vertical = Mathf.Clamp(height * -.12f, -34, 0);
            Fighters.localScale = Vector3.one * _zoom;
            Fighters.anchoredPosition = new Vector2(_pan + shake * _shakeDirection, vertical + shake * .35f);

            // 底图保持出血范围，并随取景小幅平移。大招聚焦时压暗背景来衬托特效。
            RectTransform background = Backdrop.rectTransform;
            Rect art = Backdrop.sprite.rect;
            float aspectCover = Mathf.Max(1, background.rect.height * art.width / (width * art.height));
            float backdropZoom = 1.18f + .09f * focus;
            background.localScale = new Vector3(aspectCover * backdropZoom, backdropZoom, 1);
            float maxPan = width * (background.localScale.x - 1) * .48f;
            background.anchoredPosition = new Vector2(Mathf.Clamp(_pan * .19f + shake * .4f, -maxPan, maxPan), vertical * .44f);
            Backdrop.color = Color.Lerp(Color.white, new Color(.56f, .48f, .69f), focus * .56f);
        }
    }
}
