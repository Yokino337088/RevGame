using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardCellView
    {
        private Transform[] _victoryVisuals;
        private Vector3[] _victoryRestPositions, _victoryRestScales;
        private SpriteRenderer _victorySurface, _victorySource;
        private bool _victoryWaveActive;
        private bool _victorySourceWasEnabled;
        private MaterialPropertyBlock _victorySurfaceProperties;
        /// <summary>按格缓存渲染层，避免每帧GetComponents；关闭/回池必须归位，不能让旧波浪位置继承下一局。</summary>
        public void SetVictoryWave(float progress, float height = .36f)
        {
            bool starting = !_victoryWaveActive;
            if (progress <= 0 || progress >= 1)
            {
                if (!_victoryWaveActive) { return; }
                _victoryWaveActive = false;
                for (int index = 0; index < _victoryVisuals.Length; index++)
                {
                    if (_victoryVisuals[index] == null) { continue; }
                    _victoryVisuals[index].localPosition = _victoryRestPositions[index];
                    _victoryVisuals[index].localScale = _victoryRestScales[index];
                }
                if (_victorySource != null) { _victorySource.enabled = _victorySourceWasEnabled; }
                if (_victorySurface != null) { _victorySurface.enabled = false; _victorySurface.sprite = null; _victorySurface.sharedMaterial = null; _victorySurface.SetPropertyBlock(null); }
                return;
            }
            if (_victorySurface == null)
            {
                // 根节点承载Collider，不能移动它；借一个池对象自带的可复用视觉层画根Sprite。
                _victorySource = GetComponent<SpriteRenderer>();
                var obj = new GameObject("VictorySurface"); obj.transform.SetParent(transform, false);
                _victorySurface = obj.AddComponent<SpriteRenderer>();
            }
            if (!_victoryWaveActive)
            {
                _victorySourceWasEnabled = _victorySource.enabled;
                _victorySurface.sprite = _victorySource.sprite;
                _victorySurface.sharedMaterial = _victorySource.sharedMaterial;
                _victorySurface.drawMode = _victorySource.drawMode;
                _victorySurface.size = _victorySource.size;
                _victorySurface.color = _victorySource.color;
                _victorySurface.flipX = _victorySource.flipX;
                _victorySurface.flipY = _victorySource.flipY;
                // 章节棋盘图集UV保存在参数块里；复制一次避免跳跃时采到同页其它图片。
                _victorySurfaceProperties ??= new MaterialPropertyBlock();
                _victorySource.GetPropertyBlock(_victorySurfaceProperties);
                _victorySurface.SetPropertyBlock(_victorySurfaceProperties);
                _victorySurface.sortingLayerID = _victorySource.sortingLayerID;
                _victorySurface.sortingOrder = _victorySource.sortingOrder;
                _victorySurface.enabled = _victorySourceWasEnabled;
                _victorySource.enabled = false;
                _victoryWaveActive = true;
            }
            // 同一个池实例可能在不同章节调整子图布局；每次开始取当前静止位置，避免沿用上一章。
            if (_victoryVisuals != null && starting)
            {
                for (int index = 0; index < _victoryVisuals.Length; index++)
                {
                    if (_victoryVisuals[index] == null) { continue; }
                    _victoryRestPositions[index] = _victoryVisuals[index].localPosition;
                    _victoryRestScales[index] = _victoryVisuals[index].localScale;
                }
            }
            if (_victoryVisuals == null)
            {
                SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>(true);
                int count = 0;
                foreach (SpriteRenderer renderer in renderers) { if (renderer.transform != transform) { count++; } }
                _victoryVisuals = new Transform[count]; _victoryRestPositions = new Vector3[count]; _victoryRestScales = new Vector3[count];
                int index = 0;
                foreach (SpriteRenderer renderer in renderers)
                {
                    if (renderer.transform == transform) { continue; }
                    _victoryVisuals[index] = renderer.transform;
                    _victoryRestPositions[index] = renderer.transform.localPosition;
                    _victoryRestScales[index] = renderer.transform.localScale;
                    index++;
                }
            }
            // height是纯视觉高度（世界单位），Boss冲击可比通关波浪更有弹性；仍不移动Collider所在根节点。
            float jump = Mathf.Sin(Mathf.Clamp01(progress) * Mathf.PI) * height;
            for (int index = 0; index < _victoryVisuals.Length; index++)
            {
                if (_victoryVisuals[index] == null) { continue; }
                _victoryVisuals[index].localPosition = _victoryRestPositions[index] + Vector3.up * jump;
                _victoryVisuals[index].localScale = _victoryRestScales[index] * (1f + jump * .15f);
            }
        }
    }
}
