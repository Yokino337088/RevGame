using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>为技能图片补上局部 UV 和出场进度。图集 UV 仍由 Image 生成，旋转或重打包后也能正确采样。</summary>
    public sealed class DuelCastReveal : BaseMeshEffect
    {
        // X/Y 由顶点坐标填写。Z 为从 0 到 1 的展开进度，W 区分推进与环形扩张。
        private Vector4 _settings = new Vector4(0, 0, 1, 0);

        /// <summary>每层保留自己的时间和形状，所有起手和命中仍复用角色材质。</summary>
        internal void Paint(float reveal, bool radial)
        {
            _settings.z = reveal;
            _settings.w = radial ? 1 : 0;
            graphic.SetVerticesDirty();
        }

        /// <summary>每张效果图只更新 Quad 顶点，Shader 用这组数据扫出轮廓而不裁错图集边缘。</summary>
        public override void ModifyMesh(VertexHelper mesh)
        {
            if (!IsActive())
            {
                return;
            }
            Rect rect = graphic.rectTransform.rect;
            var vertex = UIVertex.simpleVert;
            for (int index = 0; index < mesh.currentVertCount; index++)
            {
                mesh.PopulateUIVertex(ref vertex, index);
                _settings.x = Mathf.InverseLerp(rect.xMin, rect.xMax, vertex.position.x);
                _settings.y = Mathf.InverseLerp(rect.yMin, rect.yMax, vertex.position.y);
                vertex.uv1 = _settings;
                mesh.SetUIVertex(vertex, index);
            }
        }
    }
}
