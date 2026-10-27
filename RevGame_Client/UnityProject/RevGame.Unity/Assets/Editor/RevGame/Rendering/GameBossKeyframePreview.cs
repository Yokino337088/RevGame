// 固定真实AB和PieceView的编辑态夹具：同一帧配置、计时算法、共享材质，输出动画证据。
// 不进入Play或切换用户场景；图集Sprite测试克隆只属于本次预览，统一在finally销毁。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RevGame.Core;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    public static class GameBossKeyframePreview
    {
        private const string Folder = "Temp/BossKeyframeChecks";
        /// <summary>测试只通过固定业务类型的方法推进动画，不提供任意反射调用。</summary>
        public static string Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("关键帧预览只能在编辑态执行"); }
            foreach (AssetBundle existing in AssetBundle.GetAllLoadedAssetBundles())
            { throw new InvalidOperationException("其它预览持有AB：" + existing.name); }
            Directory.CreateDirectory(Folder);
            var bundles = new List<AssetBundle>();
            var configs = new List<BossChapterArtConfig>();
            var sprites = new List<Sprite>();
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                AssetBundle main = Load("PC", bundles);
                AssetBundleManifest manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (manifest == null) { throw new InvalidOperationException("PC Manifest缺失"); }
                foreach (string dependency in manifest.GetAllDependencies("battle_units")) { Load(dependency, bundles); }
                AssetBundle units = Load("battle_units", bundles);
                GameObject prefab = units.LoadAsset<GameObject>("assets/gameres/pieces/unit/slimepiece.prefab");
                if (prefab == null) { throw new InvalidOperationException("真实棋子预制体缺失"); }
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance, scene);
                PieceView view = instance.GetComponent<PieceView>();
                SpriteRenderer body = instance.transform.Find("Body").GetComponent<SpriteRenderer>();
                // 非Play下Awake不一定运行；只给同一视图执行一次真实初始化，避免用新SpriteRenderer冒充业务。
                typeof(PieceView).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
                MethodInfo tick = typeof(PieceView).GetMethod("OnTick", BindingFlags.NonPublic | BindingFlags.Instance);
                var cameraObject = new GameObject("Boss关键帧验收相机", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 1.15f;
                camera.transform.position = new Vector3(0, 0, -5);
                camera.nearClipPlane = .1f;
                camera.farClipPlane = 15;
                camera.scene = scene;
                camera.backgroundColor = new Color(.08f, .14f, .18f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                target = new RenderTexture(640, 640, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                readback = new Texture2D(640, 640, TextureFormat.RGBA32, false);
                var results = new List<string>();
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    AssetBundle bundle = Load("atlas_" + BossChapterArtConfig.AtlasName(chapter).ToLowerInvariant(), bundles);
                    SpriteAtlas atlas = bundle.LoadAsset<SpriteAtlas>(("assets/gameres/atlases/" + BossChapterArtConfig.AtlasName(chapter) + ".spriteatlasv2").ToLowerInvariant());
                    BossChapterArtConfig asset = bundle.LoadAsset<BossChapterArtConfig>(("assets/gameres/config/boss/" + BossChapterArtConfig.ResourceName(chapter) + ".asset").ToLowerInvariant());
                    if (atlas == null || asset == null || atlas.spriteCount != 8) { throw new InvalidOperationException("真实Boss包缺少8帧图集或配置"); }
                    asset.Validate(chapter);
                    BossChapterArtConfig config = UnityEngine.Object.Instantiate(asset);
                    configs.Add(config);
                    config.Frames = new Sprite[8];
                    // 图集GetSprite仅在隔离测试有限调用；正式动画使用配置数组，不逐帧查图集或克隆Sprite。
                    for (int frame = 0; frame < 8; frame++)
                    {
                        Sprite sprite = atlas.GetSprite(asset.Frames[frame].name);
                        if (sprite == null || !sprite.packed || sprite.texture == null) { throw new InvalidOperationException("真实Boss帧无图集像素"); }
                        config.Frames[frame] = sprite;
                        sprites.Add(sprite);
                    }
                    view.OnPoolReturn();
                    view.Bind(chapter, PieceKind.HeavySword, StarTier.Three, GameSide.Enemy);
                    view.BindBossArt(config);
                    view.SetBodySize(2, true, true);
                    foreach (SpriteRenderer renderer in instance.GetComponentsInChildren<SpriteRenderer>(true)) { renderer.enabled = renderer == body; }
                    if (view.AnimationFrameCount != 8 || view.BossAppearanceChapter != chapter) { throw new InvalidOperationException("实际视图未绑定Boss8帧"); }
                    string prefix = "Chapter" + chapter.ToString("D2");
                    Color32[] first = Capture(camera, target, readback, prefix + "-00.png");
                    bool pixelsChanged = false;
                    var indices = new List<int> { view.AnimationFrameIndex };
                    for (int sample = 1; sample <= 14; sample++)
                    {
                        int previousFrame = view.AnimationFrameIndex;
                        for (int step = 0; step < 8 && view.AnimationFrameIndex == previousFrame; step++) { tick.Invoke(view, null); }
                        int frameIndex = view.AnimationFrameIndex;
                        if (frameIndex == previousFrame) { throw new InvalidOperationException("真实动画计时未换帧"); }
                        indices.Add(frameIndex);
                        // 和HUD/血量刷新相同：重复Bind、BindBossArt、SetBodySize不能把动画归零。
                        view.Bind(chapter, PieceKind.HeavySword, StarTier.Three, GameSide.Enemy);
                        view.BindBossArt(config);
                        view.SetBodySize(2, true, true);
                        if (view.AnimationFrameIndex != frameIndex || body.sprite != config.Frames[frameIndex])
                        { throw new InvalidOperationException("重复刷新重置了Boss动画"); }
                        Color32[] pixels = Capture(camera, target, readback, prefix + "-" + sample.ToString("D2") + ".png");
                        for (int pixel = 0; pixel < pixels.Length && !pixelsChanged; pixel++)
                        {
                            if (Math.Abs(first[pixel].r - pixels[pixel].r) + Math.Abs(first[pixel].g - pixels[pixel].g) + Math.Abs(first[pixel].b - pixels[pixel].b) > 10)
                            { pixelsChanged = true; }
                        }
                    }
                    if (!pixelsChanged) { throw new InvalidOperationException("帧索引变化但GPU画面没有变化：" + prefix); }
                    if (indices[7] != 7 || indices[14] != 0) { throw new InvalidOperationException("往返播放端点异常"); }
                    view.OnPoolReturn();
                    if (view.BossAppearanceChapter != 0 || view.AnimationFrameCount != 0 || body.sprite != null)
                    { throw new InvalidOperationException("回池未清Boss帧引用"); }
                    view.Bind(chapter + 100, PieceKind.Archer, StarTier.One, GameSide.Player);
                    if (view.BossAppearanceChapter != 0 || view.AnimationFrameCount == 8)
                    { throw new InvalidOperationException("复用普通棋子串入Boss动画"); }
                    results.Add(prefix + ":8帧、往返/刷新/回池/复用通过");
                }
                return string.Join("；", results) + "；" + Path.GetFullPath(Folder);
            }
            finally
            {
                RenderTexture.active = previous;
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (BossChapterArtConfig config in configs) { UnityEngine.Object.DestroyImmediate(config); }
                foreach (Sprite sprite in sprites) { UnityEngine.Object.DestroyImmediate(sprite); }
                if (readback != null) { UnityEngine.Object.DestroyImmediate(readback); }
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                for (int i = bundles.Count - 1; i >= 0; i--) { bundles[i].Unload(true); }
            }
        }

        private static AssetBundle Load(string name, List<AssetBundle> owned)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile("Assets/StreamingAssets/PC/" + name);
            if (bundle == null) { throw new InvalidOperationException("Boss预览AB加载失败：" + name); }
            owned.Add(bundle);
            return bundle;
        }
        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D readback, string name)
        {
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false, false);
            File.WriteAllBytes(Folder + "/" + name, readback.EncodeToPNG());
            return readback.GetPixels32();
        }
    }
}
