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
        /// <summary>第四章常驻泥流亮纹的倍率。设置为零时保留独立的格子边线和 Boss 实例脉冲。</summary>
        private static readonly int SwampMudHighlightStrength = Shader.PropertyToID("_MudStrength");
        /// <summary>第四章环境泥涡出现比例，零让片元着色器跳过泥涡分支。</summary>
        private static readonly int SwampEddyDensity = Shader.PropertyToID("_EddyDensity");
        /// <summary>第四章环境气泡出现比例，零让片元着色器跳过气泡分支。</summary>
        private static readonly int SwampBubbleDensity = Shader.PropertyToID("_BubbleDensity");
        /// <summary>当前 81 格共用的材质。通常来自资源包，第四章使用本棋盘的实例。</summary>
        private Material _surfaceMaterial;
        /// <summary>仅第四章持有的一份运行时材质，格子解绑后随棋盘销毁。</summary>
        private Material _ownedSurfaceMaterial;
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
        /// <summary>初始化和准备阶段使用；同章只加载一次。新材质就绪后统一替换，再释放旧材质及租约。</summary>
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
            Material nextOwned = null;
            Material previousOwned = null;
            string previousName = null;
            try
            {
                if (chapter < 1 || chapter > SurfaceNames.Length)
                {
                    throw new InvalidOperationException("棋盘章节超出美术配置范围");
                }
                name = SurfaceNames[chapter - 1] + "_Surface";
                requested = true;
                // 材质由棋盘视图的版本和释放钩子持有，可以随场景对象先于业务域销毁。
                var handle = RevResManager.LoadAsync(SurfaceRoot, name, typeof(Material), null, RevResGroup.Scene, lifetime: RevResLifetime.Manual);
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
                if (chapter == 4)
                {
                    // 只复制一份供整盘共享，资源包里的母材质照旧留给预览和后续对照。
                    // 格线也使用材质的整体发光倍率。只关泥流亮纹、泥涡和气泡，保留格线、底色起伏与 Boss 脉冲。
                    nextOwned = new Material(material);
                    nextOwned.SetFloat(SwampMudHighlightStrength, 0f);
                    nextOwned.SetFloat(SwampEddyDensity, 0f);
                    nextOwned.SetFloat(SwampBubbleDensity, 0f);
                }
                previousName = _surfaceName;
                previousOwned = _ownedSurfaceMaterial;
                _surfaceName = name;
                _ownedSurfaceMaterial = nextOwned;
                _surfaceMaterial = nextOwned != null ? nextOwned : material;
                adopted = true;
                foreach (BoardCellView cell in _cells)
                {
                    cell.ApplySurface(_surfaceMaterial);
                }
                SurfaceChapter = chapter;
                _surfaceReady = true;
                TryPrepareBackgroundDepth();
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
                if (previousOwned != null)
                {
                    Destroy(previousOwned);
                }
                if (previousName != null)
                {
                    RevResManager.Release(SurfaceRoot, previousName, RevResLifetime.Manual);
                }
                if (!adopted && nextOwned != null)
                {
                    Destroy(nextOwned);
                }
                if (requested && !adopted)
                {
                    RevResManager.Release(SurfaceRoot, name, RevResLifetime.Manual);
                }
            }
        }
        /// <summary>最多每秒采样 20 次。棋盘刷新时已整理可见眼位，平时只推进这些格子的眨眼。</summary>
        private void AnimateBoardEyes()
        {
            if (_released || _visibleEyeCells.Count == 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextEyeFrameAt)
            {
                return;
            }

            _nextEyeFrameAt = now + .05f;
            foreach (BoardCellView cell in _visibleEyeCells)
            {
                cell.AnimateEye(now);
            }
        }
        /// <summary>格子摘掉引用后销毁自有材质并归还资源；在途加载用版本检查自行收尾。</summary>
        private void ReleaseBoardSurface()
        {
            ++_surfaceVersion;

            if (_ownedSurfaceMaterial != null)
            {
                Destroy(_ownedSurfaceMaterial);
                _ownedSurfaceMaterial = null;
            }
            if (_surfaceName != null)
            {
                RevResManager.Release(SurfaceRoot, _surfaceName, RevResLifetime.Manual);
            }
            _surfaceName = null;
            _surfaceMaterial = null;
            SurfaceChapter = 0;
            _surfaceRequestedChapter = 0;
            _surfaceReady = false;
        }
    }
}
