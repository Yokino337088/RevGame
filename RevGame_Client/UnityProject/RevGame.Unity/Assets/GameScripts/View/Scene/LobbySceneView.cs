// StartScene直接摆放的小屋与鸦鸦，HomePanel仅保留交互。资源通过Rev门面按场景寿命租用，不初始化第二份框架。
using System;
using System.Collections.Generic;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>大厅场景画面所有者；背景MeshRenderer和角色SpriteRenderer/Animator均为场景对象，无Canvas或Image。</summary>
    public sealed class LobbySceneView : MonoBehaviour
    {
        /// <summary>StartScene固定相机和画面节点，由Editor配方绑定；显示位置在原画圆台上。</summary>
        [SerializeField] private Camera lobbyCamera;
        [SerializeField] private MeshRenderer cottageRenderer;
        [SerializeField] private SpriteRenderer yayaRenderer;
        [SerializeField] private Animator yayaAnimator;
        /// <summary>本场景加载的材质和正式控制器，销毁前先停止使用，再成对Release。</summary>
        private RevResHandle _materialHandle, _controllerHandle;
        /// <summary>待机图集独立租约，不能归还故事表情图集；离场Dispose。</summary>
        private GameSpriteAtlasService.ScopedAtlasLease _atlas;
        /// <summary>加载版本与生命周期，迟到结果不提交到已经销毁的场景。</summary>
        private int _generation;
        /// <summary>Released表示对象销毁，Preparing合并重复进入请求；普通菜单切换不重开资源加载。</summary>
        private bool _released, _preparing;
        /// <summary>仅在视口变化时重新摆放场景对象，普通动画帧不改背景Transform。</summary>
        private Vector2Int _pixels;
        /// <summary>最近请求的局外界面，仅控制角色可见性，不持有玩法状态。</summary>
        private GameScreen _screen;
        /// <summary>材质、图集和首帧都可画后为true，过场据此等待。</summary>
        public bool Ready
        {
            get;
            private set;
        }
        /// <summary>由局外表现等待实际材质/图集/控制器可画，再显示场景；不决定GameFlow状态。</summary>
        public async RevTask PrepareAsync(GameScreen screen)
        {
            _screen = screen;
            // 快速切大厅/选关时共用本次在途加载，不重新Load或丢失原句柄，避免多租约泄漏。
            if (_preparing)
            {
                while (_preparing && !_released)
                {
                    await RevTask.Yield();
                }
                if (!Ready)
                {
                    throw new InvalidOperationException("大厅场景加载失败或已经退出");
                }
                ApplyVisibility();
                return;
            }
            if (Ready)
            {
                ApplyVisibility();
                return;
            }
            if (lobbyCamera == null || cottageRenderer == null || yayaRenderer == null || yayaAnimator == null)
            {
                throw new InvalidOperationException("StartScene大厅场景节点未装配");
            }
            int generation = ++_generation;
            _preparing = true;
            try
            {
                _materialHandle = RevResManager.LoadAsync("Lobby", "MagicCottageMaterial", typeof(Material), null, RevResGroup.Scene);
                _controllerHandle = RevResManager.LoadAsync("Characters/Yaya", "YayaIdle24Controller", typeof(RuntimeAnimatorController), null, RevResGroup.Scene);
                _atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas("YayaIdle24");
                await _atlas.WaitReadyAsync();
                while (!_released && generation == _generation && (_materialHandle.IsLoading || _controllerHandle.IsLoading))
                {
                    await RevTask.Yield();
                }
                if (_released || generation != _generation)
                {
                    return;
                }
                Material material = _materialHandle.Content as Material;
                RuntimeAnimatorController controller = _controllerHandle.Content as RuntimeAnimatorController;
                if (material == null || controller == null || material.shader.name != "RevGame/Background/LobbyMagicCottage")
                {
                    throw new InvalidOperationException("大厅小屋/动画加载失败：" + _materialHandle.ErrorReason + " / " + _controllerHandle.ErrorReason);
                }
                cottageRenderer.sharedMaterial = material;
                yayaAnimator.runtimeAnimatorController = controller;
                yayaAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
                yayaAnimator.enabled = true;
                yayaAnimator.Play(0, 0, 0);
                yayaAnimator.Update(0);
                if (yayaRenderer.sprite == null || yayaRenderer.sprite.texture == null)
                {
                    throw new InvalidOperationException("鸦鸦大厅待机没有可画Sprite");
                }
                Ready = true;
                _pixels = Vector2Int.zero;
                FitCamera();
                ApplyVisibility();
            }
            catch
            {
                // 某个依赖失败时归还本次已成功的引用；下一次重试不能叠加旧资源句柄。
                ReleaseResources();
                throw;
            }
            finally
            {
                _preparing = false;
            }
        }
        /// <summary>大厅与选关用同一原画，鸦鸦仅大厅显示；UI菜单叠在相机上，不持有角色画像。</summary>
        private void ApplyVisibility()
        {
            cottageRenderer.enabled = Ready && (_screen == GameScreen.Home || _screen == GameScreen.LevelSelect);
            yayaRenderer.enabled = Ready && _screen == GameScreen.Home;
            yayaAnimator.enabled = Ready && _screen == GameScreen.Home;
        }
        /// <summary>原画等比cover相机，角色全身站左侧圆台。结构比例为原画构图约定，不是游戏数值。</summary>
        public void FitCamera()
        {
            if (!Ready || lobbyCamera == null)
            {
                return;
            }
            var pixels = new Vector2Int(lobbyCamera.pixelWidth, lobbyCamera.pixelHeight);
            if (_pixels == pixels)
            {
                return;
            }
            _pixels = pixels;
            lobbyCamera.orthographic = true;
            lobbyCamera.orthographicSize = 6f;
            lobbyCamera.transform.position = new Vector3(0, 0, -10);
            Texture texture = cottageRenderer.sharedMaterial.GetTexture("_ChapterTex");
            float aspect = (float)texture.width / texture.height;
            float height = Mathf.Max(12f, 12f * lobbyCamera.aspect / aspect);
            cottageRenderer.transform.localScale = new Vector3(height * aspect, height, 1);
            cottageRenderer.transform.position = new Vector3(0, 0, 5);
            // 9.2世界单位使帽顶留在相机内；圆台落脚点仍随原画缩放，不能单独拉长人物。
            const float characterHeight = 9.2f;
            yayaRenderer.transform.localScale = Vector3.one * (characterHeight / yayaRenderer.sprite.bounds.size.y);
            Vector3 center = new Vector3(-height * aspect * .245f, -height * .305f + characterHeight * .5f, 0);
            yayaRenderer.transform.position = center - Vector3.Scale(yayaRenderer.sprite.bounds.center, yayaRenderer.transform.localScale);
        }
        /// <summary>只读真实场景、Shader遮罩与动画状态，不能用此入口更改图像或流程。</summary>
        public object Snapshot()
        {
            return new Dictionary<string, object>
            {
                ["ready"] = Ready,
                ["scene"] = gameObject.scene.name,
                ["sceneObjects"] = GetComponentInChildren<Canvas>(true) == null,
                ["backgroundShader"] = cottageRenderer.sharedMaterial == null ? "" : cottageRenderer.sharedMaterial.shader.name,
                ["maskUsed"] = cottageRenderer.sharedMaterial != null && cottageRenderer.sharedMaterial.GetTexture("_RegionMask") != null,
                ["sprite"] = yayaRenderer.sprite == null ? "" : yayaRenderer.sprite.name,
                ["animatorPlaying"] = yayaAnimator.enabled && yayaAnimator.runtimeAnimatorController != null,
                ["visible"] = cottageRenderer.enabled && yayaRenderer.enabled,
                ["portraitFullyVisible"] = yayaRenderer.bounds.max.y <= lobbyCamera.orthographicSize && yayaRenderer.bounds.min.y >= -lobbyCamera.orthographicSize
            };
        }
        /// <summary>场景销毁先解除Renderer/Animator引用，再释放资源和图集；不会清其它游戏域。</summary>
        private void OnDestroy()
        {
            _released = true;
            _generation++;
            Ready = false;
            ReleaseResources();
        }
        /// <summary>成功销毁和失败重试共用成对清理，先解绑原生组件再释放句柄，不卸其它场景资源。</summary>
        private void ReleaseResources()
        {
            Ready = false;
            if (yayaAnimator != null)
            {
                yayaAnimator.enabled = false;
                yayaAnimator.runtimeAnimatorController = null;
            }
            if (yayaRenderer != null)
            {
                yayaRenderer.sprite = null;
            }
            if (cottageRenderer != null)
            {
                cottageRenderer.sharedMaterial = null;
            }
            if (_materialHandle != null)
            {
                RevResManager.Release("Lobby", "MagicCottageMaterial");
                _materialHandle = null;
            }
            if (_controllerHandle != null)
            {
                RevResManager.Release("Characters/Yaya", "YayaIdle24Controller");
                _controllerHandle = null;
            }
            _atlas?.Dispose();
            _atlas = null;
        }
    }
}
