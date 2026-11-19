using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GamePresentation
    {
        private Vector3 _bossFocusPosition;
        private float _bossFocusSize, _bossFocusBlend;
        /// <summary>特写作为正常相机布局的最后一层，不用DOZoom/DOMove与FitCamera竞争同一个Transform。</summary>
        public void SetBossFocus(Vector3 position, int bodySize, float blend)
        {
            _bossFocusPosition = position;
            _bossFocusSize = Mathf.Max(1.25f, bodySize * .83f);
            _bossFocusBlend = Mathf.Clamp01(blend);
            FitCamera();
        }
        /// <summary>从已叠加震动的基础姿态计算本帧特写。调用者统一提交结果，避免同一帧先写基础姿态再覆盖。</summary>
        private void ApplyBossFocus(ref Vector3 position, ref float size)
        {
            if (_bossFocusBlend <= 0)
            {
                return;
            }
            Vector3 focus = new Vector3(_bossFocusPosition.x, _bossFocusPosition.y, position.z);
            position = Vector3.Lerp(position, focus, _bossFocusBlend);
            size = Mathf.Lerp(size, _bossFocusSize, _bossFocusBlend);
        }
        /// <summary>只读镜头门禁，供实际Play验证弹幕不会在特写中提前弹出。</summary>
        public bool IsBossCameraRestored
        {
            get
            {
                return _bossFocusBlend <= 0f;
            }
        }
        public void ClearBossFocus()
        {
            _bossFocusBlend = 0;

            if (boardCamera != null)
            {
                FitCamera();
            }
        }
    }
}
