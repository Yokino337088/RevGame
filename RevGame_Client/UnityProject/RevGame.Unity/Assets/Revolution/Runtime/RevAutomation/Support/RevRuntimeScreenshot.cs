// 在游戏帧绘制完成后读回实际画面，输出短期 PNG，交付后由客户端或 Player HTTP 即时回收。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Revolution
{
    /// <summary>一张游戏截图的短生命周期协程宿主，由工单创建并在收尾时销毁。</summary>
    public sealed class RevRuntimeScreenshot : MonoBehaviour
    {
        /// <summary>单个 PNG 的传输上限，避免超高分辨率占满临时空间或传输通道。</summary>
        public const int MaxPngBytes = 8 * 1024 * 1024;

        /// <summary>在主线程取得临时截图目录。Editor 使用本工程 Library，Player 使用临时缓存空间。</summary>
        public static string ScreenshotFolder()
        {
            if (Application.isEditor)
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "RevolutionAutomation", "screenshots"));
            }
            return Path.Combine(Application.temporaryCachePath, "RevolutionAutomation", "screenshots");
        }

        /// <summary>用通过校验的截图 ID 构成固定文件名，供主线程写入和传输线程领取。</summary>
        internal static string ScreenshotPath(string directory, Guid id)
        {
            return Path.Combine(directory, "rev-shot-" + id.ToString("N") + ".png");
        }
        /// <summary>由运行时能力入口在主线程启动一张截图。未渲染时直接返回明确失败。</summary>
        internal static RevRuntimeOperation StartCapture()
        {
            if (!Application.isPlaying || Application.isBatchMode)
            {
                throw new InvalidOperationException("游戏截图需要正在渲染的 Play 或开发版 Player");
            }
            var owner = new GameObject("[RevRuntimeScreenshot]") { hideFlags = HideFlags.HideAndDontSave };
            var host = owner.AddComponent<RevRuntimeScreenshot>();
            return new CaptureOperation(host);
        }

        /// <summary>跨帧等待真实画面，完成和取消均由工单统一释放宿主。</summary>
        private sealed class CaptureOperation : RevRuntimeOperation
        {
            /// <summary>只在当前截图期间有效，结束后销毁以停止尚未完成的协程。</summary>
            private readonly RevRuntimeScreenshot _host;
            /// <summary>帧末已得到终态时置位，下一次轮询发布结果。</summary>
            private bool _finished;

            /// <summary>把当前截图安排在本帧的渲染结束阶段，给相机和界面留出绘制机会。</summary>
            internal CaptureOperation(RevRuntimeScreenshot host)
            {
                _host = host;
                host.StartCoroutine(Capture());
            }

            /// <summary>帧末读回画面与编码 PNG，任何结果都释放本次 Unity 纹理。</summary>
            private IEnumerator Capture()
            {
                yield return new WaitForEndOfFrame();
                Texture2D texture = null;
                try
                {
                    texture = ScreenCapture.CaptureScreenshotAsTexture();
                    if (texture == null)
                    {
                        throw new InvalidOperationException("当前帧没有可读取的游戏画面");
                    }
                    byte[] png = texture.EncodeToPNG();
                    if (png.Length > MaxPngBytes)
                    {
                        throw new InvalidOperationException("截图超过 8 MiB，请降低分辨率");
                    }
                    string folder = ScreenshotFolder();
                    Directory.CreateDirectory(folder);
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("截图暂存目录不能是链接");
                    }

                    // 先写完整文件再发布路径。出错时删除本次新文件，成功时移交给调用者消费。
                    Guid id = Guid.NewGuid();
                    string path = ScreenshotPath(folder, id);
                    try
                    {
                        File.WriteAllBytes(path, png);
                        Result = new Dictionary<string, object>
                        {
                            ["captureId"] = id.ToString("N"), ["path"] = path, ["mimeType"] = "image/png",
                            ["bytes"] = png.Length, ["width"] = texture.width, ["height"] = texture.height
                        };
                    }
                    catch
                    {
                        File.Delete(path);
                        throw;
                    }
                }
                catch (Exception error)
                {
                    Success = false;
                    Message = error.Message;
                }
                finally
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                    _finished = true;
                }
            }

            /// <summary>工单轮询仅观察结束标记；场景提前卸载时结束等待并报告失败。</summary>
            public override bool Tick()
            {
                if (!_finished && _host == null)
                {
                    Success = false;
                    Message = "画面在截图完成前关闭";
                    return true;
                }
                return _finished;
            }

            /// <summary>成功、超时和取消都回收宿主，已经发布的 PNG 由领取者读完后删除。</summary>
            public override void Dispose()
            {
                if (_host != null)
                {
                    _host.StopAllCoroutines();
                    UnityEngine.Object.Destroy(_host.gameObject);
                }
            }
        }
    }
}
#endif
