using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Endless
{
    /// <summary>无尽外围身体和血条共用的批次。使用 CPU 位置或 GPU 端点，回池清绘制范围，竞技场销毁 Mesh。</summary>
    internal sealed class EndlessQuadBatch
    {
        /// <summary>按绘制顺序存的一枚方块。只在新样本或绘制归属变化时更新。</summary>
        private struct QuadMotion
        {
            /// <summary>插值起始中心，单位棋格。</summary>
            internal Vector2 From;
            /// <summary>本段目标中心，达到终点后使用这个精确值。</summary>
            internal Vector2 To;
            /// <summary>从起点到终点的偏移，更新样本时算一次。</summary>
            internal Vector2 Delta;
            /// <summary>方块的半宽和半高，血条及大体型在建批次时决定。</summary>
            internal Vector2 Extent;
            /// <summary>方块原本的渲染深度，位置补间不改变它。</summary>
            internal float Z;
        }

        /// <summary>顶点的外观流布局。顺序与 Attributes 的流 1 一致，颜色用浮点以保留血条填充精度。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Appearance
        {
            /// <summary>顶点颜色。血条将剩余生命比例存于 Alpha。</summary>
            public Color Color;
            /// <summary>图集或血条材质使用的归一化纹理坐标。</summary>
            public Vector2 Uv;
        }

        /// <summary>流 0 存位置，可随表现帧上传。流 1 存颜色和 UV，在显示内容变化时上传。</summary>
        private static readonly VertexAttributeDescriptor[] Attributes =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1)
        };

        /// <summary>显卡插值额外读取流 2。前两流保持原布局，让小批次仍能只提交 CPU 位置。</summary>
        private static readonly VertexAttributeDescriptor[] GpuAttributes =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, stream: 2)
        };

        /// <summary>延续外围网格原有的保守包围盒，包含出生路线、盘外移动、棋盘及血条高度。</summary>
        private static readonly Bounds ArenaBounds = new Bounds(new Vector3(4, 4, 0), new Vector3(40, 40, 10));

        /// <summary>按最大并发预分配的局部坐标缓冲，单位格。</summary>
        private readonly Vector3[] _positions;
        /// <summary>已紧凑排列的位移数据。普通表现帧无需查询槽身份、血量、材质或大小。</summary>
        private readonly QuadMotion[] _motion;
        /// <summary>同样容量的外观缓冲。稳定的相对顺序允许连续表现帧复用它。</summary>
        private readonly Appearance[] _appearance;

        /// <summary>可选流 2，每顶点保存目标中心 XY 和相对中心的四角偏移 ZW，单位格。低能力设备不分配。</summary>
        private readonly Vector4[] _gpuEndpoints;

        /// <summary>上一批的位置含义是否为 GPU 起点。路径切换时强制重写，避免把中心按四角绘制。</summary>
        private bool _submittedOnGpu;

        /// <summary>是否拥有 GPU 所需的流 2。竞技场只有在身体和血条都支持时才允许选择 GPU。</summary>
        internal bool CanInterpolateOnGpu => _gpuEndpoints != null;

        /// <summary>已设置到子网格的方块数。初始化为 -1，保证第一次写入得到空索引范围。</summary>
        private int _submitted = -1;
#if UNITY_EDITOR
        /// <summary>同一布局下每个方块每次提交的位置字节数，供 Editor 测量上传量。</summary>
        internal const int PositionBytesPerQuad = 4 * 3 * sizeof(float);
        /// <summary>同一布局下每个方块每次提交的颜色与 UV 字节数。</summary>
        internal const int AppearanceBytesPerQuad = 4 * 6 * sizeof(float);
        /// <summary>GPU 路径每个四边形新增的端点流字节数，供 Editor 量化实际提交。</summary>
        private const int EndpointsBytesPerQuad = 4 * 4 * sizeof(float);
        /// <summary>本次提交的几何字节数，包含按需写入的 GPU 端点。仅更新插值参数时为零。</summary>
        internal int LastPositionBytes
        {
            get;
            private set;
        }
        /// <summary>本次提交的颜色和 UV 字节数。沿用原有外观变化门禁。</summary>
        internal int LastAppearanceBytes
        {
            get;
            private set;
        }
        /// <summary>上次提交的活动方块数，仅用于 Editor 验证实际上传范围。</summary>
        internal int SubmittedCount => _submitted;
#endif

        /// <summary>由竞技场挂到既有 MeshFilter 上。材质、排序与对象池归属都由竞技场维护。</summary>
        internal Mesh Mesh
        {
            get;
        }

        /// <summary>初始化固定容量缓冲。只有设备和实际材质都允许时预留端点流，索引始终使用 16 位原有布局。</summary>
        internal EndlessQuadBatch(int capacity, string name, bool gpuCapable = false)
        {
            if (capacity <= 0 || capacity > ushort.MaxValue / 4)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _positions = new Vector3[capacity * 4];
            _motion = new QuadMotion[capacity];
            _appearance = new Appearance[capacity * 4];
            if (gpuCapable)
            {
                _gpuEndpoints = new Vector4[capacity * 4];
            }
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
                Mesh.SetVertexBufferParams(_positions.Length, gpuCapable ? GpuAttributes : Attributes);
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

        /// <summary>重建时从槽 0 连续写入轨迹、大小和外观。后续补间直接消费这份紧凑队列。</summary>
        internal void Write(int quad, Vector2 from, Vector2 to, float halfWidth, float halfHeight, float z, Vector4 uv, Color color)
        {
            _motion[quad] = new QuadMotion { From = from, To = to, Delta = to - from, Extent = new Vector2(halfWidth, halfHeight), Z = z };
            int start = quad * 4;
            _appearance[start] = new Appearance { Uv = new Vector2(uv.x, uv.y), Color = color };
            _appearance[start + 1] = new Appearance { Uv = new Vector2(uv.z, uv.y), Color = color };
            _appearance[start + 2] = new Appearance { Uv = new Vector2(uv.z, uv.w), Color = color };
            _appearance[start + 3] = new Appearance { Uv = new Vector2(uv.x, uv.w), Color = color };
        }

        /// <summary>生成一枚方块四角的位置顶点，保持原有绕序。</summary>
        private void WritePosition(int quad, float x, float y, float halfWidth, float halfHeight, float z)
        {
            int start = quad * 4;
            _positions[start] = new Vector3(x - halfWidth, y - halfHeight, z);
            _positions[start + 1] = new Vector3(x + halfWidth, y - halfHeight, z);
            _positions[start + 2] = new Vector3(x + halfWidth, y + halfHeight, z);
            _positions[start + 3] = new Vector3(x - halfWidth, y + halfHeight, z);
        }

        /// <summary>CPU 每帧写四角。GPU 仅在样本、范围或路径改变时写端点，渲染器另行更新 0～1 的插值参数。</summary>
        internal void Submit(int count, float blend = 1f, bool appearanceChanged = false, bool onGpu = false)
        {
            onGpu = onGpu && CanInterpolateOnGpu;
            bool rangeChanged = _submitted != count;
#if UNITY_EDITOR
            LastPositionBytes = 0;
            LastAppearanceBytes = 0;
#endif
            if (count > 0 && (!onGpu || appearanceChanged || rangeChanged || !_submittedOnGpu))
            {
                if (onGpu)
                {
                    UploadGpuEndpoints(count);
                }
                else
                {
                    // 每个槽只插值一次中心，再沿原绕序展开。端点帧直接使用原目标值。
                    for (int quad = 0; quad < count; quad++)
                    {
                        ref readonly QuadMotion motion = ref _motion[quad];
                        Vector2 position = blend < 1f ? motion.From + motion.Delta * blend : motion.To;
                        WritePosition(quad, position.x, position.y, motion.Extent.x, motion.Extent.y, motion.Z);
                    }
                }
                Mesh.SetVertexBufferData(_positions, 0, 0, count * 4, stream: 0, flags: MeshUpdateFlags.DontRecalculateBounds);
#if UNITY_EDITOR
                LastPositionBytes = count * (PositionBytesPerQuad + (onGpu ? EndpointsBytesPerQuad : 0));
#endif
            }
            if (count > 0 && (appearanceChanged || rangeChanged))
            {
                Mesh.SetVertexBufferData(_appearance, 0, 0, count * 4, stream: 1, flags: MeshUpdateFlags.DontRecalculateBounds);
#if UNITY_EDITOR
                LastAppearanceBytes = count * AppearanceBytesPerQuad;
#endif
            }
            _submittedOnGpu = onGpu && count > 0;
            if (!rangeChanged)
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

        /// <summary>按原四角顺序保存位置起点与端点。显卡先插值中心再加局部偏移，深度、颜色和 UV 与 CPU 路径相同。</summary>
        private void UploadGpuEndpoints(int count)
        {
            for (int quad = 0; quad < count; quad++)
            {
                ref readonly QuadMotion motion = ref _motion[quad];
                int start = quad * 4;
                var center = new Vector3(motion.From.x, motion.From.y, motion.Z);
                _positions[start] = _positions[start + 1] = _positions[start + 2] = _positions[start + 3] = center;
                float x = motion.To.x, y = motion.To.y;
                float w = motion.Extent.x, h = motion.Extent.y;
                _gpuEndpoints[start] = new Vector4(x, y, -w, -h);
                _gpuEndpoints[start + 1] = new Vector4(x, y, w, -h);
                _gpuEndpoints[start + 2] = new Vector4(x, y, w, h);
                _gpuEndpoints[start + 3] = new Vector4(x, y, -w, h);
            }
            Mesh.SetVertexBufferData(_gpuEndpoints, 0, 0, count * 4, stream: 2, flags: MeshUpdateFlags.DontRecalculateBounds);
        }
    }
}
