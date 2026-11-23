using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Endless
{
    /// <summary>盾牌、护盾、突刺、剑气和升星环共用的固定容量网格。竞技场持有网格，回池撤掉绘制范围，销毁时释放。</summary>
    internal sealed class EndlessEffectBatch
    {
        /// <summary>每个动态顶点沿 Shader 原通道存位置、颜色和效果参数，颜色保留浮点精度。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct EffectVertex
        {
            /// <summary>原效果顶点，单位棋格，Z 值保留原层次。</summary>
            internal Vector3 Position;
            /// <summary>颜色和闪光强度。Alpha 含义由当前效果的 Shader 决定。</summary>
            internal Color Tint;
            /// <summary>原 UV2 值。当前效果从这里读进度、相位或刃数等数据。</summary>
            internal Vector2 Parameters;
        }

        /// <summary>流 0 对应 EffectVertex。流 1 是固定的四角纹理坐标，建网格时只写一次。</summary>
        private static readonly VertexAttributeDescriptor[] Attributes =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1)
        };

        /// <summary>沿用竞技场的保守包围盒和已验证的四角索引，无需在每次写入时扫描顶点。</summary>
        private const MeshUpdateFlags UpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
        /// <summary>本批次唯一的动态数据，调用方每帧按绘制顺序从第 0 个方块写入。</summary>
        private readonly EffectVertex[] _vertices;
        /// <summary>上次参与绘制的方块数，-1 表示初次创建，需要先发布空范围。</summary>
        private int _submittedCount = -1;
        /// <summary>本次改动最靠前的方块。大于最后一个改动表示没有变化。</summary>
        private int _firstChanged = int.MaxValue;
        /// <summary>本次改动最靠后的方块，-1 表示尚无变化。</summary>
        private int _lastChanged = -1;

        /// <summary>挂到竞技场预制体原 MeshFilter，保留原材质和绘制顺序。</summary>
        internal Mesh Mesh { get; }

#if UNITY_EDITOR
        /// <summary>每个方块四个顶点，位置、颜色、效果参数共 9 个浮点数每顶点。</summary>
        internal const int DynamicBytesPerQuad = 4 * 9 * sizeof(float);
        /// <summary>旧画法整批提交的位置、颜色、效果参数字节数，固定纹理坐标不在统计中。</summary>
        internal int FullUploadBytes => _vertices.Length / 4 * DynamicBytesPerQuad;
        /// <summary>上次送到 Unity 网格接口的动态字节数。这里不测驱动或显存传输。</summary>
        internal int LastUploadBytes { get; private set; }
        /// <summary>本次真正交给网格绘制的方块数，供实战和退场检查。</summary>
        internal int DrawnCount => _submittedCount;
#endif

        /// <summary>一次分配最大容量和固定纹理坐标。构造失败时释放还未交给竞技场的网格。</summary>
        internal EndlessEffectBatch(int capacity, string name)
        {
            ushort[] indices = EndlessQuadBatch.CreateIndices(capacity);
            _vertices = new EffectVertex[capacity * 4];
            var uv = new Vector2[_vertices.Length];
            for (int i = 0; i < uv.Length; i += 4)
            {
                uv[i] = Vector2.zero;
                uv[i + 1] = Vector2.right;
                uv[i + 2] = Vector2.one;
                uv[i + 3] = Vector2.up;
            }

            Mesh = new Mesh { name = name };
            try
            {
                Mesh.MarkDynamic();
                Mesh.SetVertexBufferParams(uv.Length, Attributes);
                Mesh.SetVertexBufferData(uv, 0, 0, uv.Length, 1, UpdateFlags);
                Mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
                Mesh.SetIndexBufferData(indices, 0, 0, indices.Length, UpdateFlags);
                Mesh.subMeshCount = 1;
                Mesh.bounds = EndlessQuadBatch.ArenaBounds;
                Submit(0);
            }
            catch
            {
                UnityEngine.Object.Destroy(Mesh);
                throw;
            }
        }

        /// <summary>按旧绕序展开围绕中心的方形。沿用精确坐标，棋子移动缓动期间仍可每帧跟随。</summary>
        internal void WriteSquare(int slot, Vector2 center, float half, float z, Color tint, Vector2 parameters)
        {
            WriteQuad(slot, new Vector3(center.x - half, center.y - half, z), new Vector3(center.x + half, center.y - half, z),
                new Vector3(center.x + half, center.y + half, z), new Vector3(center.x - half, center.y + half, z), tint, parameters);
        }

        /// <summary>写连续绘制槽的四个角。一个方块共用颜色和 UV2，精确比较防止吞掉细微的动画变化。</summary>
        internal void WriteQuad(int slot, Vector3 first, Vector3 second, Vector3 third, Vector3 fourth, Color tint, Vector2 parameters)
        {
            int start = slot * 4;
            ref EffectVertex head = ref _vertices[start];
            if (slot < _submittedCount && head.Position.Equals(first) && head.Tint.Equals(tint) && head.Parameters.Equals(parameters) &&
                _vertices[start + 1].Position.Equals(second) && _vertices[start + 2].Position.Equals(third) &&
                _vertices[start + 3].Position.Equals(fourth))
            {
                return;
            }

            head = new EffectVertex { Position = first, Tint = tint, Parameters = parameters };
            _vertices[start + 1] = new EffectVertex { Position = second, Tint = tint, Parameters = parameters };
            _vertices[start + 2] = new EffectVertex { Position = third, Tint = tint, Parameters = parameters };
            _vertices[start + 3] = new EffectVertex { Position = fourth, Tint = tint, Parameters = parameters };
            _firstChanged = Math.Min(_firstChanged, slot);
            _lastChanged = Math.Max(_lastChanged, slot);
        }

        /// <summary>一次上传活动范围内的变化段。退场传零，只撤掉索引范围并清记录，下次进场重新上传用到的槽。</summary>
        internal void Submit(int count)
        {
#if UNITY_EDITOR
            LastUploadBytes = 0;
#endif
            int lastVisibleChange = Math.Min(_lastChanged, count - 1);
            if (_firstChanged <= lastVisibleChange)
            {
                int firstVertex = _firstChanged * 4;
                int vertexCount = (lastVisibleChange - _firstChanged + 1) * 4;
                Mesh.SetVertexBufferData(_vertices, firstVertex, firstVertex, vertexCount, 0, UpdateFlags);
#if UNITY_EDITOR
                LastUploadBytes = (lastVisibleChange - _firstChanged + 1) * DynamicBytesPerQuad;
#endif
            }
            if (count != _submittedCount)
            {
                // 缩小范围不必再把旧四角挤成一个点。后续重新启用的槽由 WriteQuad 强制写回。
                Mesh.SetSubMesh(0, new SubMeshDescriptor(0, count * 6)
                {
                    vertexCount = count * 4,
                    bounds = EndlessQuadBatch.ArenaBounds
                }, UpdateFlags);
                _submittedCount = count;
            }
            _firstChanged = int.MaxValue;
            _lastChanged = -1;
        }
    }
}
