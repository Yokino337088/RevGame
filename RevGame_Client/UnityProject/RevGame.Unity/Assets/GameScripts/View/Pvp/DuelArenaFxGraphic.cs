using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>决斗舞台结界、轨迹和火花共用的程序化四边形，材质通过 UV1 接收每个图形的播放进度。</summary>
    public sealed class DuelArenaFxGraphic : MaskableGraphic
    {
        /// <summary>与舞台短效 Shader 的三个形状分支对应。</summary>
        internal enum Shape
        {
            // 拦在场地尽头的结界柱。
            Ward,
            // 随弹幕飞行的拉长能量线。
            Streak,
            // 命中或触边时沿地面扩张的冲击环。
            Ring
        }

        // 本次租约的图形类型，图集和材质始终共享。
        private Shape _shape;
        // 归一化进度。边界处使用 1 表示未触发，火花随粒子寿命从 0 到 1。
        private float _phase = 1;

        /// <summary>形状或播放阶段变化时更新顶点，禁止为每条轨迹创建材质。</summary>
        internal void Paint(Shape shape, float phase)
        {
            if (_shape != shape || !Mathf.Approximately(_phase, phase))
            {
                _shape = shape;
                _phase = phase;
                SetVerticesDirty();
            }
        }

        /// <summary>生成一个带局部 UV 的四边形，图形和流光全部由共享 Shader 计算。</summary>
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            AddCorner(mesh, new Vector2(rect.xMin, rect.yMin), Vector2.zero);
            AddCorner(mesh, new Vector2(rect.xMin, rect.yMax), Vector2.up);
            AddCorner(mesh, new Vector2(rect.xMax, rect.yMax), Vector2.one);
            AddCorner(mesh, new Vector2(rect.xMax, rect.yMin), Vector2.right);
            mesh.AddTriangle(0, 1, 2);
            mesh.AddTriangle(2, 3, 0);
        }

        // UV0 是图形自身的坐标，UV1 的两个值不随 Quad 插值改变。
        private void AddCorner(VertexHelper mesh, Vector2 position, Vector2 uv)
        {
            UIVertex vertex = UIVertex.simpleVert;
            vertex.position = position;
            vertex.color = color;
            vertex.uv0 = uv;
            vertex.uv1 = new Vector4((int)_shape, _phase, 0, 0);
            mesh.AddVert(vertex);
        }
    }
}
