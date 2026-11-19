using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Endless
{
    /// <summary>无尽竞技场的单材质四边形批次。竞技场在首次出池时创建，回池清可见范围，销毁时释放 Mesh。</summary>
    internal sealed class EndlessQuadBatch
    {
        /// <summary>一个顶点的原生上传布局。顺序必须与 Attributes 一致，颜色用浮点以保留血条填充精度。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            /// <summary>顶点在竞技场中的局部坐标，单位格。</summary>
            public Vector3 Position;
            /// <summary>顶点颜色。血条将剩余生命比例存于 Alpha。</summary>
            public Color Color;
            /// <summary>图集或血条材质使用的归一化纹理坐标。</summary>
            public Vector2 Uv;
        }

        /// <summary>顶点缓冲的唯一流。位置、颜色和 UV 一次写入，避免三个整数组分别提交。</summary>
        private static readonly VertexAttributeDescriptor[] Attributes =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
        };

        /// <summary>延续外围网格原有的保守包围盒，包含出生路线、盘外移动、棋盘及血条高度。</summary>
        private static readonly Bounds ArenaBounds = new Bounds(new Vector3(4, 4, 0), new Vector3(40, 40, 10));

        /// <summary>工作缓冲按最大并发一次分配。每帧只覆盖本帧要提交的连续前缀。</summary>
        private readonly Vertex[] _vertices;

        /// <summary>已设置到子网格的方块数。初始化为 -1，保证第一次写入得到空索引范围。</summary>
        private int _submitted = -1;

        /// <summary>由竞技场挂到既有 MeshFilter 上。材质、排序与对象池归属都由竞技场维护。</summary>
        internal Mesh Mesh
        {
            get;
        }

        /// <summary>初始化固定容量的顶点和索引缓冲。项目外围容量低于 16 位索引上限，沿用原设备兼容范围。</summary>
        internal EndlessQuadBatch(int capacity, string name)
        {
            if (capacity <= 0 || capacity > ushort.MaxValue / 4)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _vertices = new Vertex[capacity * 4];
            var indices = new ushort[capacity * 6];
            for (int quad = 0; quad < capacity; quad++)
            {
                int vertex = quad * 4;
                int index = quad * 6;
                // 保留旧方块的绕序。从同一份索引缓冲递增截取即可，无需随敌人数量重建索引。
                indices[index] = (ushort)vertex;
                indices[index + 1] = (ushort)(vertex + 1);
                indices[index + 2] = (ushort)(vertex + 2);
                indices[index + 3] = (ushort)vertex;
                indices[index + 4] = (ushort)(vertex + 2);
                indices[index + 5] = (ushort)(vertex + 3);
            }
            Mesh = new Mesh { name = name };
            try
            {
                Mesh.MarkDynamic();
                Mesh.SetVertexBufferParams(_vertices.Length, Attributes);
                Mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
                Mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
                Mesh.subMeshCount = 1;
                Submit(0);
                Mesh.bounds = ArenaBounds;
            }
            catch
            {
                // 初始化时还没有竞技场接管 Mesh。原生缓冲创建失败时由本构造入口释放，再交给场景装配报错。
                UnityEngine.Object.Destroy(Mesh);
                throw;
            }
        }

        /// <summary>向指定槽写一个方块。调用者必须从槽 0 连续写入，同时保持原有透明对象绘制顺序。</summary>
        internal void Write(int quad, float x, float y, float halfWidth, float halfHeight, float z, Vector4 uv, Color color)
        {
            int start = quad * 4;
            // 四角保持相同的世界位置与图集方向。Quad 与血条使用同一份索引布局。
            _vertices[start] = new Vertex { Position = new Vector3(x - halfWidth, y - halfHeight, z), Uv = new Vector2(uv.x, uv.y), Color = color };
            _vertices[start + 1] = new Vertex { Position = new Vector3(x + halfWidth, y - halfHeight, z), Uv = new Vector2(uv.z, uv.y), Color = color };
            _vertices[start + 2] = new Vertex { Position = new Vector3(x + halfWidth, y + halfHeight, z), Uv = new Vector2(uv.z, uv.w), Color = color };
            _vertices[start + 3] = new Vertex { Position = new Vector3(x - halfWidth, y + halfHeight, z), Uv = new Vector2(uv.x, uv.w), Color = color };
        }

        /// <summary>上传已写好的四边形并提交其索引前缀。数量变少时旧顶点可保留，索引不再引用这些槽。</summary>
        internal void Submit(int count)
        {
            if (count > 0)
            {
                // 先上传本帧有效数据，再扩大绘制范围。数量为零只收起索引，无需向 GPU 写一批零。
                Mesh.SetVertexBufferData(_vertices, 0, 0, count * 4, flags: MeshUpdateFlags.DontRecalculateBounds);
            }
            if (_submitted == count)
            {
                return;
            }
            var range = new SubMeshDescriptor(0, count * 6)
            {
                firstVertex = 0,
                vertexCount = count * 4,
                bounds = ArenaBounds
            };
            Mesh.SetSubMesh(0, range, MeshUpdateFlags.DontRecalculateBounds);
            _submitted = count;
        }
    }
}
