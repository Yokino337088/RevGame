using System;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>棋盘章节表现租约。只加载当前章，异步过期结果必归还；不改棋盘占格、AI或伤害规则。</summary>
    public sealed partial class BoardView
    {
        private static readonly string[] SurfaceNames = { "Chapter01_Tutorial", "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
        private const string SurfaceRoot = "Material/Board";
        private Material _surfaceMaterial;
        private string _surfaceName;
        private int _surfaceVersion;
        private int _surfaceRequestedChapter;
        private bool _surfaceReady;
        private float _nextEyeFrameAt;
        public int SurfaceChapter
        {
            get; private set;
        }
        public Material SurfaceMaterial
        {
            get
            {
                return _surfaceMaterial;
            }
        }
        /// <summary>初始化和每次准备阶段调用；同章幂等。先等新材质可用再替换全盘，不提前卸掉仍在显示的材质。</summary>
        private async void RequestBoardSurface()
        {
            int chapter = RevServices.GetRequired<IBattleService>().Chapter;

            if (_released || chapter == _surfaceRequestedChapter)
            {
                return;
            }
            _surfaceRequestedChapter = chapter;
            _surfaceReady = false;
            int version = ++_surfaceVersion;
            string name = null;
            bool requested = false;
            bool adopted = false;
            try
            {
                if (chapter < 1 || chapter > SurfaceNames.Length)
                {
                    throw new InvalidOperationException("棋盘章节超出美术配置范围");
                }
                name = SurfaceNames[chapter - 1] + "_Surface";
                requested = true;
                var handle = RevResManager.LoadAsync(SurfaceRoot, name, typeof(Material), null, RevResGroup.Scene);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (handle.IsLoading && !_released && version == _surfaceVersion)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        throw new TimeoutException("章节棋盘材质加载超时：" + name);
                    }
                    await RevTask.Yield();
                }
                if (_released || this == null || version != _surfaceVersion)
                {
                    return;
                }
                Material material = handle.Content as Material;

                if (!handle.IsLoaded || material == null || !material.shader.isSupported || material.shader.name != "RevGame/Board/" + SurfaceNames[chapter - 1])
                {
                    throw new InvalidOperationException("章节棋盘材质或Shader不可用：" + name + " / " + handle.ErrorReason);
                }
                string oldName = _surfaceName;
                _surfaceName = name;
                _surfaceMaterial = material;
                adopted = true;
                foreach (BoardCellView cell in _cells)
                {
                    cell.ApplySurface(material);
                }
                SurfaceChapter = chapter;
                _surfaceReady = true;

                if (oldName != null)
                {
                    RevResManager.Release(SurfaceRoot, oldName);
                }
            }
            catch (Exception error)
            {
                if (_released || version != _surfaceVersion)
                {
                    return;
                }
                LoadError = "棋盘魔法格面加载失败：" + error.Message;
                RevLog.Exception(error, LoadError, "RevGame");
                var flow = RevServices.Get<RevGame.Flow.GameFlow>();

                if (flow != null && !flow.IsTransitioning)
                {
                    flow.ShowChapterBackgroundFailure(LoadError);
                }
            }
            finally
            {
                // 本请求没有被当前棋盘接管时，自行归还引用；防止离场或快速换章造成泄漏。
                if (requested && !adopted)
                {
                    RevResManager.Release(SurfaceRoot, name);
                }
            }
        }
        /// <summary>常驻格面完全使用GPU时钟；CPU只驱动少量可见眼位，不给81格各挂Update或Tween。</summary>
        private void AnimateBoardEyes()
        {
            float now = Time.unscaledTime;

            if (_released || now < _nextEyeFrameAt)
            {
                return;
            }
            _nextEyeFrameAt = now + .05f;
            foreach (BoardCellView cell in _cells)
            {
                if (cell.IsEyeVisible)
                {
                    cell.AnimateEye(now);
                }
            }
        }
        /// <summary>格子已回池并摘掉材质后再释放；递增版本让在途加载在finally中自行归还。</summary>
        private void ReleaseBoardSurface()
        {
            ++_surfaceVersion;

            if (_surfaceName != null)
            {
                RevResManager.Release(SurfaceRoot, _surfaceName);
            }
            _surfaceName = null;
            _surfaceMaterial = null;
            SurfaceChapter = 0;
            _surfaceRequestedChapter = 0;
            _surfaceReady = false;
        }
    }
}
