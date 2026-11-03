#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 隔离动态证据：真实输入/特效在同一时间轴上连拍，原始PNG仅写.codex/work，不导出Profiler或ZIP。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Flow;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>一次只运行一份隔离连拍任务；MCP轮询只读State，退出/重载使旧作业失效。</summary>
    public static class EndlessVisualCapture
    {
        /// <summary>作业状态、证据目录、首个错误与捕获帧数，归当前开发态Play；不是玩家数据。</summary>
        private static string _status = "idle", _directory = "", _error = "";
        /// <summary>本次真实捕获的帧数，完成后与磁盘PNG数量核对；不作为业务帧率计数。</summary>
        private static int _frames;
        /// <summary>本次手势已经释放的标记；计划必须在完成与清理都为真后返回通过。</summary>
        private static bool _cleanup;
        /// <summary>开始固定四种画面连拍，mode只接受白名单，禁止传任意文件路径/命令。</summary>
        public static object Begin(string mode)
        {
            var service = RevServices.GetRequired<EndlessService>();
            if (!service.Isolated || RevServices.GetRequired<GameFlow>().IsTransitioning || _status == "running")
            {
                throw new InvalidOperationException("连拍需要已就绪的隔离局，且没有其它连拍运行");
            }
            if (mode != "upgrade" && mode != "thrust" && mode != "fire-endless" && mode != "fire-campaign")
            {
                throw new ArgumentException("不支持的视觉连拍模式");
            }
            _status = "running";
            _error = "";
            _frames = 0;
            _cleanup = false;
            _directory = Path.GetFullPath("../../../.codex/work/endless-feedback-2026-11-03/capture-" + mode + "-" + DateTime.Now.ToString("HHmmssfff"));
            Directory.CreateDirectory(_directory);
            Record(mode).Forget();
            return State();
        }
        /// <summary>真实时间推进输入与截图，不改模型时间/期望HP；失败仍释放本次手势。</summary>
        private static async RevTask Record(string mode)
        {
            try
            {
                var service = RevServices.GetRequired<EndlessService>();
                service.PreviewUpgradeDrag();
                await RevTask.Delay(200);
                if (mode == "upgrade")
                {
                    EndlessDragInput.Begin(0, 4, 2);
                }
                else if (mode == "thrust")
                {
                    service.PreviewThrust();
                }
                else
                {
                    UnityEngine.Object.FindObjectOfType<EndlessArena>().PreviewFire(mode == "fire-endless");
                }
                for (int i = 0; i < 16; i++)
                {
                    if (!Application.isPlaying || RevServices.GetRequired<GameFlow>().IsTransitioning)
                    {
                        throw new InvalidOperationException("连拍期间退出或开始切场景");
                    }
                    if (mode == "upgrade" && i == 5)
                    {
                        EndlessDragInput.Move(7, 1);
                    }
                    if (mode == "upgrade" && i == 8)
                    {
                        EndlessDragInput.End();
                    }
                    // ScreenCapture在帧末写真实GameView，持续时间约1.9秒覆盖箭头、松手和升级爆发。
                    ScreenCapture.CaptureScreenshot(Path.Combine(_directory, "frame_" + i.ToString("00") + ".png"));
                    _frames++;
                    await RevTask.Delay(120);
                }
                float deadline = Time.realtimeSinceStartup + 5;
                while (Directory.GetFiles(_directory, "*.png").Length < _frames)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        throw new IOException("截图文件未完成写入");
                    }
                    await RevTask.Yield();
                }
                _status = "passed";
            }
            catch (Exception error)
            {
                _status = "failed";
                _error = error.Message;
                RevLog.Exception(error, "无尽视觉连拍失败", "RevGame");
            }
            finally
            {
                EndlessDragInput.Cancel();
                _cleanup = true;
            }
        }
        /// <summary>只读作业证据，不推进动画或规则；rev_run可有界等待status=passed并检查cleanup。</summary>
        public static object State()
        {
            return new Dictionary<string, object> { ["status"] = _status, ["directory"] = _directory, ["error"] = _error, ["frames"] = _frames, ["cleanup"] = _cleanup };
        }
    }
}
#endif
