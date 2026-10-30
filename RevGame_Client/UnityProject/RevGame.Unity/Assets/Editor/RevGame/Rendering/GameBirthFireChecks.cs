// ============================================================
// GameBirthFireChecks.cs —— 用真实SpriteRenderer和GPU读回检查落子燃烧。
// 为什么不能只检查_Birth数值：数值正确也可能采到白图、停止换帧或在清理参数块时闪白。
// 本工具只在Play中运行，临时相机远离棋盘，不修改场景资产、模型、共享材质或AB。
// ============================================================
using System;
using System.IO;
using System.Reflection;
using RevGame.Core;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>编辑器直读模式的燃烧回归；临时纹理/相机归本次检查所有，finally统一销毁。</summary>
    public static class GameBirthFireChecks
    {
        // 128像素足以检查颜色/透明度连续性；GPU读回只用于验收，正式战斗绝不执行这段代码。
        private const int Size = 128;
        private static int _checks;
        public static string LastResult { get; private set; } = "未运行";

        /// <summary>在StartScene的大厅Play后运行；失败保留具体原因，不把“进入Play”当作验收通过。</summary>
        [MenuItem("RevGame/验收/落子身体燃烧（Play）")]
        public static async void Begin()
        {
            if (LastResult == "运行中")
            {
                return;
            }
            LastResult = "运行中";
            _checks = 0;
            PieceView piece = null;
            GameObject cameraObject = null;
            Texture2D readback = null;
            Texture2D sheet = null;
            RenderTexture target = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                Require(Application.isPlaying, "请先在StartScene进入Play");
                Require(!RevResBootstrap.UseABInEditor, "此次只验收编辑器直读，请关闭编辑器AB加载模式");
                var source = new RevTaskCompletionSource<PieceView>();
                RevPool.GetAsync<PieceView>(item => source.SetResult(item));
                piece = await source.Task;
                Require(piece != null, "RevPool未返回棋子");
                piece.Bind(99119, PieceKind.Archer, StarTier.One, GameSide.Player);
                piece.transform.position = new Vector3(1000f, 1000f, 0f);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var body = (SpriteRenderer)typeof(PieceView).GetField("bodyRenderer", flags).GetValue(piece);
                Require(body.sprite != null && piece.AnimationFrameCount > 1, "弓箭手序列帧未就绪");
                Material shared = body.sharedMaterial;
                Require(shared != null && shared.shader.isSupported, "棋子Shader未编译或不受支持");
                foreach (SpriteRenderer renderer in piece.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (renderer != body)
                    {
                        renderer.enabled = false;
                    }
                }
                cameraObject = new GameObject("燃烧验收临时相机", typeof(Camera));
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(body.bounds.extents.x, body.bounds.extents.y) * 1.12f;
                camera.transform.position = body.bounds.center + Vector3.back * 10f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                sheet = new Texture2D(Size * 4, Size * 2, TextureFormat.RGBA32, false);

                // 同一帧内采样：排除待机动画、全局时间变化造成的差异，专门比较燃烧参数。
                Color32[] normal = Capture(camera, target, readback);
                piece.PlayBirthFire();
                var setProgress = typeof(PieceView).GetMethod("SetBirthProgress", flags);
                float[] samples = { 0.001f, 0.12f, 0.3f, 0.5f, 0.7f, 0.9f, 0.98f, 1f };
                for (int sample = 0; sample < samples.Length; sample++)
                {
                    setProgress.Invoke(piece, new object[] { samples[sample] });
                    Color32[] pixels = Capture(camera, target, readback);
                    int changed = 0;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Require(Mathf.Abs(pixels[i].a - normal[i].a) <= 1, "燃烧改变了原图透明轮廓");
                        bool newlyWhite = pixels[i].a > 100 && pixels[i].r > 240 && pixels[i].g > 240 && pixels[i].b > 240;
                        bool originallyWhite = normal[i].r > 238 && normal[i].g > 238 && normal[i].b > 238;
                        Require(!newlyWhite || originallyWhite, "火焰把原本有颜色的身体烧成了白色");
                        int delta = Difference(pixels[i], normal[i]);

                        if (delta > 10)
                        {
                            changed++;
                        }
                        if (sample == samples.Length - 1)
                        {
                            Require(delta <= 2, "燃烧终点与普通身体不连续");
                        }
                    }

                    if (sample == 3)
                    {
                        Require(changed > 100, "燃烧中段没有可见的身体火焰");
                    }
                    sheet.SetPixels32(sample % 4 * Size, (1 - sample / 4) * Size, Size, Size, pixels);
                }
                piece.StopTransientVisuals();
                Color32[] cleaned = Capture(camera, target, readback);
                int maximumCleanupDifference = 0;
                for (int i = 0; i < cleaned.Length; i++)
                {
                    maximumCleanupDifference = Mathf.Max(maximumCleanupDifference, Difference(cleaned[i], normal[i]));
                }
                File.WriteAllBytes("Temp/BirthFirePreview.png", sheet.EncodeToPNG());
                Require(maximumCleanupDifference <= 2, "清除参数块时出现白图或跳色，最大RGB差=" + maximumCleanupDifference);

                // 再走真实时间轴，检查燃烧时确实换帧、每张帧的图集纹理和UV都同步更新。
                piece.PlayBirthFire();
                int startFrame = piece.AnimationFrameIndex;
                bool advanced = false;
                float deadline = Time.realtimeSinceStartup + 4f;
                var block = new MaterialPropertyBlock();
                while (piece.BirthProgress > 0f && Time.realtimeSinceStartup < deadline)
                {
                    await RevTask.Yield();

                    if (piece.BirthProgress <= 0f)
                    {
                        break;
                    }
                    advanced |= piece.AnimationFrameIndex != startFrame;
                    body.GetPropertyBlock(block);
                    Require(block.GetTexture("_MainTex") == body.sprite.texture, "换帧后仍采样旧纹理");
                    Vector4 uv = UnityEngine.Sprites.DataUtility.GetOuterUV(body.sprite);
                    Vector4 expected = new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y);
                    Require((block.GetVector("_DissolveUV") - expected).sqrMagnitude < 0.000001f, "换帧后火焰UV未同步");
                }
                Require(advanced, "燃烧期间棋子序列帧没有继续播放");
                Require(piece.BirthProgress == 0f, "燃烧结束没有清理进度");
                Require(body.sharedMaterial == shared, "燃烧克隆或替换了共享材质");
                piece.PlayBirthFire();
                RevPool.Return(piece, 0);
                Require(piece.BirthProgress == 0f, "中途回池残留燃烧进度");
                piece = null;
                await RevTask.Delay(1500);
                LastResult = "通过 " + _checks + " 条检查：8段GPU像素采样、首尾连续/无新增白像素/透明轮廓、燃烧中换帧与图集UV、共享材质、回池清理；Editor DX11，未打AB";
                RevLog.Info(LastResult, "BirthFireChecks");
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error;
                RevLog.Exception(error, "身体燃烧验收失败", "BirthFireChecks");
            }
            finally
            {
                RenderTexture.active = previous;

                if (piece != null)
                {
                    RevPool.Return(piece, 0);
                }
                if (cameraObject != null)
                {
                    UnityEngine.Object.Destroy(cameraObject);
                }
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }
                if (readback != null)
                {
                    UnityEngine.Object.Destroy(readback);
                }
                if (sheet != null)
                {
                    UnityEngine.Object.Destroy(sheet);
                }
                File.WriteAllText("Temp/BirthFireChecks.txt", LastResult);
            }
        }
        /// <summary>读取真正渲染结果；仅用于小尺寸验收图，不把昂贵的ReadPixels带进战斗热路径。</summary>
        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D readback)
        {
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            readback.Apply();
            return readback.GetPixels32();
        }
        private static int Difference(Color32 left, Color32 right)
        {
            return Mathf.Abs(left.r - right.r) + Mathf.Abs(left.g - right.g) + Mathf.Abs(left.b - right.b);
        }
        private static void Require(bool condition, string message)
        {
            _checks++;

            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
