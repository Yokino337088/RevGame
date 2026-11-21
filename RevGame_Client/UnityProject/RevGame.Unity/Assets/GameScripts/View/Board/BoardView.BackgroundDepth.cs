using Revolution;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;

namespace RevGame.View
{
    /// <summary>普通远征棋盘的背景深度遮挡。棋盘只在当前相机与静止格面成立时持有这一份合并缓冲。</summary>
    public sealed partial class BoardView
    {
        /// <summary>深度材质沿用 Material/Board 的逻辑目录，只在当前战斗内借用。</summary>
        private const string BackgroundDepthMaterial = "BackgroundDepthMask";

        /// <summary>在格芯内再收 3%，使切片边界、细缝和相邻透明区继续由原背景渲染。</summary>
        private const float BackgroundDepthInset = 0.03f;

        /// <summary>接收预通道的 Built-in 正交相机；只给显式绑定的章节相机提交。</summary>
        private Camera _backgroundDepthCamera;

        /// <summary>世界 Z 平面，靠近背景且仍在前景之后，避免改变之前靠排序显示的棋盘效果。</summary>
        private float _backgroundDepthPlane;

        /// <summary>深度材质的当前租约，离场或失效时先摘命令再归还。</summary>
        private RevResLease _backgroundDepthLease;

        /// <summary>所有 Sliced 格芯合并为一个 Mesh。顶点生成一次，镜头移动不重算棋盘几何。</summary>
        private Mesh _backgroundDepthMesh;

        /// <summary>相机在普通不透明绘制前执行的单条命令。由本棋盘创建并回收。</summary>
        private CommandBuffer _backgroundDepthCommand;

        /// <summary>记录命令是否已挂在相机，重复演出回调不会重复登记。</summary>
        private bool _backgroundDepthAttached;

        /// <summary>胜利演出移动可见格层，暂时交给正常背景绘制，恢复静止格面再打开。</summary>
        private bool _backgroundDepthSuspended;

        /// <summary>背景交接完成后绑定真实相机。给不符合正交前向约束的场景传空，保留原来渲染。</summary>
        internal void BindBackgroundDepth(Camera camera, float backgroundPlane)
        {
            ReleaseBackgroundDepth();
            if (camera == null || GraphicsSettings.currentRenderPipeline != null || camera.actualRenderingPath != RenderingPath.Forward
                || (camera.targetTexture != null && camera.targetTexture.depth <= 0) || !camera.orthographic
                || Vector3.Dot(camera.transform.forward, Vector3.forward) < 0.999999f)
            {
                return;
            }

            // 与背景相隔一小段，比相等深度可预测。同时比棋子、棋盘和局内反馈靠后。
            _backgroundDepthCamera = camera;
            _backgroundDepthPlane = backgroundPlane - 0.125f;
            TryPrepareBackgroundDepth();
        }

        /// <summary>场景相机、81 格和章节格面谁最后就绪，谁启动一次可选的深度装配。</summary>
        private void TryPrepareBackgroundDepth()
        {
            if (_released || !_cellsReady || !_surfaceReady || _backgroundDepthCamera == null || _backgroundDepthLease != null)
            {
                return;
            }

            RevResLease lease = RevResManager.LoadOwnedAsync(SurfaceRoot, BackgroundDepthMaterial, typeof(Material),
                nameof(BoardView), group: RevResGroup.Scene);
            _backgroundDepthLease = lease;
            PrepareBackgroundDepthAsync(lease).Forget();
        }

        /// <summary>用真实图集和便宜材质提交深度。失败维持原画，迟到的任务不能接管新一局的相机。</summary>
        private async RevTask PrepareBackgroundDepthAsync(RevResLease lease)
        {
            bool prepared = false;
            try
            {
                // 租约先交给棋盘。等待时遇到退场，释放只按原租约所属的那一代进行。
                while (ReferenceEquals(lease, _backgroundDepthLease) && lease.Handle.IsLoading)
                {
                    await RevTask.Yield();
                }
                if (_released || !ReferenceEquals(lease, _backgroundDepthLease) || _backgroundDepthCamera == null)
                {
                    return;
                }

                Material material = lease.Handle.Content as Material;
                if (material == null || !material.shader.isSupported || !TryBuildBackgroundDepthMesh(out Texture texture))
                {
                    return;
                }

                // 单次提交。全屏背景在 Background 队列，所以必须在 ForwardOpaque 前执行。
                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_MainTex", texture);
                _backgroundDepthCommand = new CommandBuffer { name = "RevGame Board Background Depth" };
                _backgroundDepthCommand.DrawMesh(_backgroundDepthMesh, Matrix4x4.identity, material, 0, 0, properties);
                SetBackgroundDepthAttached(!_backgroundDepthSuspended);
                prepared = true;
            }
            finally
            {
                if (!prepared && ReferenceEquals(lease, _backgroundDepthLease))
                {
                    ReleaseBackgroundDepth();
                }
            }
        }

        /// <summary>只采纳同一批静止 Sliced 格芯。读取真实局部内框与图集 UV，透明或未知格式仍走完整背景。</summary>
        private bool TryBuildBackgroundDepthMesh(out Texture texture)
        {
            texture = null;
            if (_cells.Count == 0)
            {
                return false;
            }
            SpriteRenderer first = _cells[0].SurfaceRenderer;
            Sprite sprite = first == null ? null : first.sprite;
            if (sprite == null || (sprite.packed && sprite.packingRotation != SpritePackingRotation.None)
                || sprite.associatedAlphaSplitTexture != null || _surfaceMaterial == null || _surfaceMaterial.GetColor("_Color").a < 1f)
            {
                return false;
            }

            texture = sprite.texture;
            if (texture == null || texture.mipmapCount != 1)
            {
                return false;
            }

            // 内芯坐标不含九宫格边框。Sprite 的 border 是源像素，除以 PPU 后才能与局部 bounds 相减。
            Vector4 border = sprite.border / sprite.pixelsPerUnit;
            Vector4 uv = DataUtility.GetInnerUV(sprite);
            var vertices = new Vector3[_cells.Count * 4];
            var texcoords = new Vector2[vertices.Length];
            var indices = new int[_cells.Count * 6];
            for (int cell = 0; cell < _cells.Count; cell++)
            {
                SpriteRenderer renderer = _cells[cell].SurfaceRenderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || renderer.drawMode != SpriteDrawMode.Sliced || renderer.sprite != sprite
                    || renderer.flipX || renderer.flipY || renderer.color.a < 1f || renderer.sharedMaterial != _surfaceMaterial
                    || (_backgroundDepthCamera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                {
                    return false;
                }

                Bounds bounds = renderer.localBounds;
                float left = bounds.min.x + border.x;
                float bottom = bounds.min.y + border.y;
                float right = bounds.max.x - border.z;
                float top = bounds.max.y - border.w;
                if (left >= right || bottom >= top)
                {
                    return false;
                }

                // 同时内缩几何与 UV，仍采样同一个原图位置，避免一圈深度边缘伸到切片接缝上。
                float lx = Mathf.Lerp(left, right, BackgroundDepthInset), rx = Mathf.Lerp(right, left, BackgroundDepthInset);
                float by = Mathf.Lerp(bottom, top, BackgroundDepthInset), ty = Mathf.Lerp(top, bottom, BackgroundDepthInset);
                float u0 = Mathf.Lerp(uv.x, uv.z, BackgroundDepthInset), u1 = Mathf.Lerp(uv.z, uv.x, BackgroundDepthInset);
                float v0 = Mathf.Lerp(uv.y, uv.w, BackgroundDepthInset), v1 = Mathf.Lerp(uv.w, uv.y, BackgroundDepthInset);
                Transform pose = renderer.transform;
                int slot = cell * 4;
                vertices[slot] = DepthVertex(pose, lx, by);
                vertices[slot + 1] = DepthVertex(pose, rx, by);
                vertices[slot + 2] = DepthVertex(pose, rx, ty);
                vertices[slot + 3] = DepthVertex(pose, lx, ty);
                texcoords[slot] = new Vector2(u0, v0);
                texcoords[slot + 1] = new Vector2(u1, v0);
                texcoords[slot + 2] = new Vector2(u1, v1);
                texcoords[slot + 3] = new Vector2(u0, v1);
                int triangle = cell * 6;
                indices[triangle] = slot;
                indices[triangle + 1] = slot + 1;
                indices[triangle + 2] = slot + 2;
                indices[triangle + 3] = slot;
                indices[triangle + 4] = slot + 2;
                indices[triangle + 5] = slot + 3;
            }

            _backgroundDepthMesh = new Mesh { name = "RevGame Board Depth Cores", vertices = vertices, uv = texcoords, triangles = indices };
            _backgroundDepthMesh.UploadMeshData(true);
            return true;
        }

        /// <summary>保留格芯世界 XY，写到靠近背景的深度平面。只在正交前向相机下使用。</summary>
        private Vector3 DepthVertex(Transform pose, float x, float y)
        {
            Vector3 world = pose.TransformPoint(x, y, 0f);
            world.z = _backgroundDepthPlane;
            return world;
        }

        /// <summary>只对当前拥有的相机和命令修改登记，胜利演出期间按原顺序完整渲染。</summary>
        private void SetBackgroundDepthAttached(bool attached)
        {
            if (attached == _backgroundDepthAttached || _backgroundDepthCamera == null || _backgroundDepthCommand == null)
            {
                return;
            }
            if (attached)
            {
                _backgroundDepthCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _backgroundDepthCommand);
            }
            else
            {
                _backgroundDepthCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _backgroundDepthCommand);
            }
            _backgroundDepthAttached = attached;
        }

        /// <summary>棋盘失效时先使命令不再运行，再释放网格和资源租约，旧续体只会结束。</summary>
        private void ReleaseBackgroundDepth()
        {
            SetBackgroundDepthAttached(false);
            _backgroundDepthCommand?.Release();
            _backgroundDepthCommand = null;
            if (_backgroundDepthMesh != null)
            {
                Destroy(_backgroundDepthMesh);
                _backgroundDepthMesh = null;
            }
            _backgroundDepthCamera = null;
            _backgroundDepthLease?.Dispose();
            _backgroundDepthLease = null;
            _backgroundDepthAttached = false;
        }
    }
}
