// ============================================================
// FeedbackProjection.cs —— 整张棋盘当前帧共享一份相机投影收据。
// 由BoardView在LateUpdate渲染反馈前采样，每帧清楚更新；单个文字只重算自己的变换。
// 它不保存静态相机/资源引用，随棋盘释放；投影包含透视和像素视口，不假设固定1920宽度。
// ============================================================
using UnityEngine;

namespace RevGame.View
{
    /// <summary>BoardView独占的当前帧投影快照；减少最多20条文字重复读取相机原生属性，回池不保留引用。</summary>
    internal sealed class FeedbackProjection
    {
        // 仅当前棋盘借用相机，不静态保活；回池反馈置空本收据引用，退出随BoardView一起释放。
        internal Camera Camera;
        // View世界→相机，Projection相机→裁剪；VP的乘法顺序不能反，否则投影与Unity不一致。
        internal Matrix4x4 View, Projection, ViewProjection;
        // 像素视口含偏移/宽高，不能用Screen.width代替；同相机窗口缩放也会改变屏幕坐标。
        internal Rect Viewport;
        internal bool Orthographic;
        // 正交相机世界上浮一单位对应像素；透视场景回退原生投影，不把此比例套给透视。
        internal float PixelsPerWorldY;
        // 只在本帧有效，旧帧先于BoardView更新的查询回退原生相机，避免使用昨天/上一帧的变换。
        internal int Frame = -1;
        /// <summary>棋盘LateUpdate先调用，采一次原生矩阵/视口给本帧所有字读取；不修改镜头。</summary>
        internal void Capture(Camera camera)
        {
            Camera = camera;
            Frame = Time.frameCount;
            View = camera.worldToCameraMatrix; Projection = camera.projectionMatrix;
            ViewProjection = Projection * View; Viewport = camera.pixelRect;
            Orthographic = camera.orthographic;
            PixelsPerWorldY = Orthographic ? camera.pixelHeight / (2f * camera.orthographicSize) : 0f;
        }
        /// <summary>与相机投影同一矩阵计算屏幕像素；只用于前方飘字矩形，不改相机或世界位置。</summary>
        internal Vector3 WorldToScreen(Vector3 world)
        {
            Vector4 clip = ViewProjection * new Vector4(world.x, world.y, world.z, 1f);
            // 齐次透视除法；本项目文字位于相机前，极端w≈0时给有限结果，避免NaN污染避让成本。
            float inverseW = Mathf.Abs(clip.w) > 0.000001f ? 1f / clip.w : 0f;
            return new Vector3(Viewport.x + (clip.x * inverseW + 1f) * .5f * Viewport.width,
                Viewport.y + (clip.y * inverseW + 1f) * .5f * Viewport.height, -(View.MultiplyPoint(world)).z);
        }
    }
}
