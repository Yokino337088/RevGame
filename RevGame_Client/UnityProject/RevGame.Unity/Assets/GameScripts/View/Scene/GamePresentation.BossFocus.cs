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
        private void ApplyBossFocus()
        {
            if (_bossFocusBlend <= 0 || boardCamera == null) { return; }
            Vector3 focus = new Vector3(_bossFocusPosition.x, _bossFocusPosition.y, boardCamera.transform.position.z);
            boardCamera.transform.position = Vector3.Lerp(boardCamera.transform.position, focus, _bossFocusBlend);
            boardCamera.orthographicSize = Mathf.Lerp(boardCamera.orthographicSize, _bossFocusSize, _bossFocusBlend);
        }
        public void ClearBossFocus() { _bossFocusBlend = 0; if (boardCamera != null) { FitCamera(); } }
    }
}
