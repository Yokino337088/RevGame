// ============================================================
// ArcaneImageCoordinates.cs —— 给九宫格和图集图片提供独立的0~1特效坐标。
// UV0仍由UGUI采样原图；UV1只给流光使用，否则九宫格边框和图集中的卡牌会出现流光断裂。
// 同一材质供所有同类控件共享，不在Update里创建Material，也不每帧重建网格。
// ============================================================
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    public sealed class ArcaneImageCoordinates : BaseMeshEffect
    {
        protected override void OnEnable()
        {
            base.OnEnable();
            EnableCoordinates();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            EnableCoordinates();
        }

        /// <summary>Canvas默认不传UV1；只增加通道，不覆盖其他组件已启用的通道。</summary>
        private void EnableCoordinates()
        {
            if (graphic != null && graphic.canvas != null)
            {
                graphic.canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            }
        }

        /// <summary>仅在UGUI原本要更新网格时执行；按控件矩形归一化，卡片变大、九宫格拉伸后仍是一道完整流光。</summary>
        public override void ModifyMesh(VertexHelper vertices)
        {
            if (!IsActive() || vertices.currentVertCount == 0) { return; }
            Rect rect = graphic.rectTransform.rect;
            float width = Mathf.Max(1f, rect.width);
            float height = Mathf.Max(1f, rect.height);
            // 按槽位错开相位，六张牌不会像频闪灯一样同时闪白；不需要为每张牌复制材质。
            float phase = Mathf.Repeat(transform.GetSiblingIndex() * 0.173f, 1f);
            UIVertex vertex = new UIVertex();
            for (int i = 0; i < vertices.currentVertCount; i++)
            {
                vertices.PopulateUIVertex(ref vertex, i);
                vertex.uv1 = new Vector4((vertex.position.x - rect.xMin) / width, (vertex.position.y - rect.yMin) / height, phase, width / height);
                vertices.SetUIVertex(vertex, i);
            }
        }
    }
}
