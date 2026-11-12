// StartScene直接摆放的小屋与鸦鸦，HomePanel仅保留交互。资源通过Rev门面按场景寿命租用，不初始化第二份框架。
using System;
using System.Collections.Generic;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>大厅场景画面所有者；背景MeshRenderer和角色SpriteRenderer/Animator均为场景对象，无Canvas或Image。</summary>
    [ExecuteAlways]
    public sealed class LobbySceneView : MonoBehaviour
    {
        /// <summary>StartScene固定相机和画面节点，由Editor配方绑定；显示位置在原画圆台上。</summary>
        [SerializeField] private Camera lobbyCamera;
        [SerializeField] private MeshRenderer cottageRenderer;
        [SerializeField] private SpriteRenderer yayaRenderer;
        [SerializeField] private Animator yayaAnimator;
        /// <summary>独立的人工位移节点；相机适配只移动其子Sprite，保留用户用移动工具调整的Transform。</summary>
        [SerializeField] private Transform yayaPlacement;
#if UNITY_EDITOR
        /// <summary>只供编辑态构图预览，由配方设置；正式Play由Rev资源句柄替换，不初始化框架或改剧情。</summary>
        [HideInInspector][SerializeField] private Material editorBackdrop;
        [HideInInspector][SerializeField] private Sprite editorPortrait;
#endif
        /// <summary>原画中的脚底锚点，范围0～1，左下角为原点。属于美术构图而非玩法数值。</summary>
        [Header("看板娘构图（可在运行时调整）")]
        [InspectorName("脚底原画锚点")][SerializeField] private Vector2 yayaFootAnchor = new Vector2(.255f, .195f);
        /// <summary>人物全身高度，单位为场景世界单位；与相机半高6搭配，默认帽顶完整可见。</summary>
        [InspectorName("人物高度")][Range(1f, 12f)][SerializeField] private float yayaHeight = 9.2f;
        /// <summary>缓存构图输入。Inspector改变后下一次布局刷新，不每帧重新求Sprite边界。</summary>
        private Vector2 _lastFootAnchor;
        private float _lastHeight;
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
        /// <summary>从编辑态进入Play先撤静帧预览，实际资源可画之前不能拿编辑器直读图片冒充冷AB已就绪。</summary>
        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            if (cottageRenderer != null)
            {
                cottageRenderer.enabled = false;
                cottageRenderer.sharedMaterial = null;
            }
            if (yayaRenderer != null)
            {
                yayaRenderer.enabled = false;
                yayaRenderer.sprite = null;
            }
#if UNITY_EDITOR
            // 只清本次Play实例的编辑预览引用，退出Play由Unity恢复保存的场景字段；不写资产。
            editorBackdrop = null;
            editorPortrait = null;
#endif
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
                _materialHandle = RevResManager.LoadAsync("Material/Lobby", "MagicCottageMaterial", typeof(Material), null, RevResGroup.Scene);
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
                // 刚装入控制器时，重新建立 SpriteRenderer 的动画绑定，再采样待机首帧。
                yayaAnimator.Rebind();
                yayaAnimator.Play(0, 0, 0);
                yayaAnimator.Update(0);
                // 图集注册与 Animator 求值不一定在同一个 Unity 更新阶段。
                float spriteDeadline = Time.realtimeSinceStartup + 2f;
                while (!_released && generation == _generation &&
                    (yayaRenderer.sprite == null || yayaRenderer.sprite.texture == null) &&
                    Time.realtimeSinceStartup < spriteDeadline)
                {
                    await RevTask.Yield();
                }
                if (_released || generation != _generation)
                {
                    return;
                }
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
            if (lobbyCamera == null || cottageRenderer.sharedMaterial == null || yayaRenderer.sprite == null || (Application.isPlaying && !Ready))
            {
                return;
            }
            var pixels = new Vector2Int(lobbyCamera.pixelWidth, lobbyCamera.pixelHeight);
            if (_pixels == pixels && _lastFootAnchor == yayaFootAnchor && _lastHeight == yayaHeight)
            {
                return;
            }
            _pixels = pixels;
            _lastFootAnchor = yayaFootAnchor;
            _lastHeight = yayaHeight;
            lobbyCamera.orthographic = true;
            lobbyCamera.orthographicSize = 6f;
            lobbyCamera.transform.position = new Vector3(0, 0, -10);
            Texture texture = cottageRenderer.sharedMaterial.GetTexture("_ChapterTex");
            float aspect = (float)texture.width / texture.height;
            float height = Mathf.Max(12f, 12f * lobbyCamera.aspect / aspect);
            cottageRenderer.transform.localScale = new Vector3(height * aspect, height, 1);
            cottageRenderer.transform.position = new Vector3(0, 0, 5);
            // 用户配置人物高度与脚底锚点，人工位移留在YayaPlacement父节点，不被自动cover布局覆盖。
            float characterHeight = yayaHeight;
            yayaRenderer.transform.localScale = Vector3.one * (characterHeight / yayaRenderer.sprite.bounds.size.y);
            Vector3 center = new Vector3(height * aspect * (yayaFootAnchor.x - .5f), height * (yayaFootAnchor.y - .5f) + characterHeight * .5f, 0);
            yayaRenderer.transform.localPosition = center - Vector3.Scale(yayaRenderer.sprite.bounds.center, yayaRenderer.transform.localScale);
        }
#if UNITY_EDITOR
        /// <summary>编辑态仅显示固定预览图片并复用正式构图；不播放业务、不调RevInitialize，不克隆材质。</summary>
        private void Update()
        {
            if (Application.isPlaying || editorBackdrop == null || editorPortrait == null || cottageRenderer == null || yayaRenderer == null)
            {
                return;
            }
            if (cottageRenderer.sharedMaterial != editorBackdrop || yayaRenderer.sprite != editorPortrait)
            {
                cottageRenderer.sharedMaterial = editorBackdrop;
                yayaRenderer.sprite = editorPortrait;
                _pixels = Vector2Int.zero;
            }
            cottageRenderer.enabled = true;
            yayaRenderer.enabled = true;
            FitCamera();
        }
#endif
        /// <summary>只读真实场景、Shader遮罩与动画状态，不能用此入口更改图像或流程。</summary>
        public object Snapshot()
        {
            Material material = cottageRenderer.sharedMaterial;
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
                ["placementOffset"] = yayaPlacement == null ? Vector3.zero : yayaPlacement.localPosition,
                ["footAnchor"] = yayaFootAnchor,
                ["characterHeight"] = yayaHeight,
                ["windMaskUsed"] = cottageRenderer.sharedMaterial != null && cottageRenderer.sharedMaterial.GetTexture("_NatureMask") != null,
                ["waterAmount"] = material == null ? 0 : material.GetFloat("_WaterAmount"),
                ["waterLight"] = material == null ? 0 : material.GetFloat("_WaterLight"),
                ["windAmount"] = material == null ? 0 : material.GetFloat("_WindAmount"),
                ["magicGlow"] = material == null ? 0 : material.GetFloat("_MagicGlow"),
                ["cottageMagic"] = material == null ? 0 : material.GetFloat("_CottageMagic"),
                ["portraitFullyVisible"] = yayaRenderer.bounds.max.y <= lobbyCamera.orthographicSize && yayaRenderer.bounds.min.y >= -lobbyCamera.orthographicSize
            };
        }
#if UNITY_EDITOR
        /// <summary>仅开发态固定时钟与构图夹具，复用正式布局；PropertyBlock保持共享材质资产不变。</summary>
        public void ApplyPreview(Vector3 offset, float effectTime)
        {
            if (!Ready || yayaPlacement == null)
            {
                throw new InvalidOperationException("大厅画面未就绪");
            }
            yayaPlacement.localPosition = offset;
            var block = new MaterialPropertyBlock();
            cottageRenderer.GetPropertyBlock(block);
            block.SetFloat("_EffectTime", effectTime);
            cottageRenderer.SetPropertyBlock(block);
            _pixels = Vector2Int.zero;
            FitCamera();
        }
#endif
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
                RevResManager.Release("Material/Lobby", "MagicCottageMaterial");
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
