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
        /// <summary>每格准备一套波浪容器。预热只分配组件和缓存，实际波浪才绑定会显示的精灵和材质。</summary>
        private void EnsureVictoryVisuals()
        {
            if (_victorySurface == null)
            {
                // 根节点承载 Collider，后续由这个可复用的子层显示移动的格面。
                _victorySource = GetComponent<SpriteRenderer>();
                var obj = new GameObject("VictorySurface");
                obj.transform.SetParent(transform, false);
                _victorySurface = obj.AddComponent<SpriteRenderer>();
                _victorySurface.enabled = false;
            }

            _victorySurfaceProperties ??= new MaterialPropertyBlock();
            if (_victoryVisuals != null)
            {
                return;
            }
            SpriteRenderer[] renderers = GetComponentsInChildren<SpriteRenderer>(true);
            int count = 0;
            foreach (SpriteRenderer renderer in renderers)
            {
                if (renderer.transform != transform)
                {
                    count++;
                }
            }
            _victoryVisuals = new Transform[count];
            _victoryRestPositions = new Vector3[count];
            _victoryRestScales = new Vector3[count];
            int index = 0;
            foreach (SpriteRenderer renderer in renderers)
            {
                if (renderer.transform != transform)
                {
                    _victoryVisuals[index++] = renderer.transform;
                }
            }
        }

        /// <summary>只在可见波浪开始时同步渲染状态和静止位置。结束和回池时恢复静止格面。</summary>
        public void SetVictoryWave(float progress, float height = .36f)
        {
            bool starting = !_victoryWaveActive;

            if (progress <= 0 || progress >= 1)
            {
                if (!_victoryWaveActive)
                {
                    return;
                }
                _victoryWaveActive = false;
                for (int index = 0; index < _victoryVisuals.Length; index++)
                {
                    if (_victoryVisuals[index] == null)
                    {
                        continue;
                    }
                    _victoryVisuals[index].localPosition = _victoryRestPositions[index];
                    _victoryVisuals[index].localScale = _victoryRestScales[index];
                }
                if (_victorySource != null)
                {
                    _victorySource.enabled = _victorySourceWasEnabled;
                }
                if (_victorySurface != null)
                {
                    _victorySurface.enabled = false;
                    _victorySurface.sprite = null;
                    _victorySurface.sharedMaterial = null;
                    _victorySurface.SetPropertyBlock(null);
                }
                return;
            }
            if (starting)
            {
                EnsureVictoryVisuals();
                _victorySourceWasEnabled = _victorySource.enabled;
                _victorySurface.sprite = _victorySource.sprite;
                _victorySurface.sharedMaterial = _victorySource.sharedMaterial;
                SpriteDrawMode drawMode = _victorySource.drawMode;
                // 已同步的模式不用再次提交给 SpriteRenderer。
                if (_victorySurface.drawMode != drawMode)
                {
                    _victorySurface.drawMode = drawMode;
                }
                // 普通 Sprite 由 Transform 缩放。只有分片或平铺模式读取填充尺寸。
                if (drawMode != SpriteDrawMode.Simple)
                {
                    _victorySurface.size = _victorySource.size;
                }
                _victorySurface.color = _victorySource.color;
                _victorySurface.flipX = _victorySource.flipX;
                _victorySurface.flipY = _victorySource.flipY;
                // 章节棋盘图集UV保存在参数块里；复制一次避免跳跃时采到同页其它图片。
                _victorySource.GetPropertyBlock(_victorySurfaceProperties);
                _victorySurface.SetPropertyBlock(_victorySurfaceProperties);
                _victorySurface.sortingLayerID = _victorySource.sortingLayerID;
                _victorySurface.sortingOrder = _victorySource.sortingOrder;
                _victorySurface.enabled = _victorySourceWasEnabled;
                _victorySource.enabled = false;
                _victoryWaveActive = true;
            }
            // 同一个池实例可能在不同章节调整子图布局；每次开始取当前静止位置，避免沿用上一章。
            if (starting)
            {
                for (int index = 0; index < _victoryVisuals.Length; index++)
                {
                    if (_victoryVisuals[index] == null)
                    {
                        continue;
                    }
                    _victoryRestPositions[index] = _victoryVisuals[index].localPosition;
                    _victoryRestScales[index] = _victoryVisuals[index].localScale;
                }
            }

            // height是纯视觉高度（世界单位），Boss冲击可比通关波浪更有弹性；仍不移动Collider所在根节点。
            float jump = Mathf.Sin(Mathf.Clamp01(progress) * Mathf.PI) * height;
            for (int index = 0; index < _victoryVisuals.Length; index++)
            {
                if (_victoryVisuals[index] == null)
                {
                    continue;
                }
                _victoryVisuals[index].localPosition = _victoryRestPositions[index] + Vector3.up * jump;
                _victoryVisuals[index].localScale = _victoryRestScales[index] * (1f + jump * .15f);
            }
        }
    }
}
