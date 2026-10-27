// 隔离验收压缩后的真实PC AB：读取专属材质，绘制背景与统一字体；不切换用户场景或进入Play。
// 所有AB、临时材质、预览场景和RenderTexture属于本次调用，finally先清视图再卸载资源。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevGame.Editor
{
    public static class GameResourceCompressionPreview
    {
        /// <summary>仅编辑器验收使用的固定目录；正式游戏仍走Revolution逻辑资源路径。</summary>
        private const string Output = "Temp/ResourceCompressionChecks";
        private static readonly string[] Names = { "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };

        public static string Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("压缩验收只能在编辑态运行"); }
            // 不卸载其它预览/工具持有的AB，防止为了验收破坏用户已有预览。
            foreach (AssetBundle existing in AssetBundle.GetAllLoadedAssetBundles())
            { throw new InvalidOperationException("已有AB由其它工具持有，不能进行隔离冷加载：" + existing.name); }
            Directory.CreateDirectory(Output);
            var bundles = new List<AssetBundle>();
            var materials = new List<Material>();
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D readback = null;
            Mesh mesh = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                var cameraObject = new GameObject("压缩资源验收相机", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = .5f;
                camera.transform.position = new Vector3(0, 0, -2);
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 10f;
                camera.backgroundColor = new Color(.08f, .12f, .18f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.scene = scene;
                target = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                readback = new Texture2D(960, 540, TextureFormat.RGBA32, false);
                var background = new GameObject("真实背景", typeof(MeshFilter), typeof(MeshRenderer));
                SceneManager.MoveGameObjectToScene(background, scene);
                mesh = new Mesh();
                mesh.vertices = new[] { new Vector3(-8f/9f, -.5f, 0), new Vector3(8f/9f, -.5f, 0), new Vector3(-8f/9f, .5f, 0), new Vector3(8f/9f, .5f, 0) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                background.GetComponent<MeshFilter>().sharedMesh = mesh;
                Renderer renderer = background.GetComponent<Renderer>();
                var results = new List<string>();
                foreach (string name in Names)
                {
                    AssetBundle bundle = Load("background_" + name.ToLowerInvariant(), bundles);
                    Material asset = bundle.LoadAsset<Material>(("Assets/GameRes/Material/Background/" + name + "_Material.mat").ToLowerInvariant());
                    if (asset == null || asset.shader == null || !asset.shader.isSupported) { throw new InvalidOperationException("AB材质Shader失效：" + name); }
                    var texture = asset.GetTexture("_ChapterTex") as Texture2D;
                    if (texture == null || texture.format != TextureFormat.DXT1) { throw new InvalidOperationException("真实PC背景未按DXT1压缩：" + name); }
                    var material = new Material(asset);
                    materials.Add(material);
                    material.SetFloat("_Transition", 1f);
                    material.SetFloat("_Aspect", 16f/9f);
                    renderer.sharedMaterial = material;
                    material.SetFloat("_PreviewTime", 0f);
                    Color32[] first = Capture(camera, target, readback, name + "-0.png");
                    material.SetFloat("_PreviewTime", 2.5f);
                    Color32[] second = Capture(camera, target, readback, name + "-1.png");
                    long delta = 0;
                    for (int i = 0; i < first.Length; i++) { delta += Math.Abs(first[i].r-second[i].r) + Math.Abs(first[i].g-second[i].g) + Math.Abs(first[i].b-second[i].b); }
                    if (delta == 0) { throw new InvalidOperationException("真实AB背景完全无动态：" + name); }
                    results.Add(name + "动态有效");
                }
                background.SetActive(false);
                AssetBundle main = Load("PC", bundles);
                AssetBundleManifest manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (manifest == null) { throw new InvalidOperationException("PC主包Manifest缺失"); }
                var loadedNames = new HashSet<string>();
                foreach (string dependency in manifest.GetAllDependencies("battle_feedback"))
                {
                    Load(dependency, bundles);
                    loadedNames.Add(dependency);
                }
                if (!loadedNames.Contains("font_cjk")) { throw new InvalidOperationException("特效包未依赖统一字体包"); }
                AssetBundle feedback = Load("battle_feedback", bundles);
                GameObject prefab = feedback.LoadAsset<GameObject>("assets/gameres/effects/combat/battleeffect.prefab");
                if (prefab == null) { throw new InvalidOperationException("真实AB飘字预制体缺失"); }
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance, scene);
                // 编辑态不执行表现业务，只借真实TextMesh/字体/描边渲染，避免创建Tween或启动战斗。
                TextMesh[] texts = instance.GetComponentsInChildren<TextMesh>(true);
                int outlineIndex = 0;
                foreach (TextMesh text in texts)
                {
                    if (text.font == null || text.font.name != "NotoSansCJKsc-Bold") { throw new InvalidOperationException("真实AB文本字体错误"); }
                    text.gameObject.SetActive(true);
                    text.text = "暴击 250";
                    text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
                    text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
                    // 展示夹具只调整实例的摆放；五份文字按同一尺寸重叠，保留正文+四向描边语义。
                    // 原预制体中描边是正文的子节点，逐个乘缩放会造成描边重复缩小，不能据此判断字体坏了。
                    text.transform.SetParent(instance.transform, false);
                    text.transform.localScale = Vector3.one * .12f;
                    text.transform.localPosition = Vector3.zero;
                    if (text.name.StartsWith("outline", StringComparison.Ordinal))
                    {
                        Vector3[] offsets = { Vector3.left, Vector3.right, Vector3.up, Vector3.down };
                        text.transform.localPosition = offsets[outlineIndex++] * .003f;
                        text.color = Color.black;
                    }
                    else { text.color = new Color(1f, .72f, .2f); }
                }
                Color32[] pixels = Capture(camera, target, readback, "Noto-FloatingText.png");
                int visible = 0;
                foreach (Color32 pixel in pixels) { if (pixel.r > 100 || pixel.g > 100 || pixel.b > 100) { visible++; } }
                if (visible < 100) { throw new InvalidOperationException("统一字体未产生可见文字像素"); }
                return string.Join("；", results) + "；真实AB五处Noto文字/描边可绘制；" + Path.GetFullPath(Output);
            }
            finally
            {
                RenderTexture.active = previous;
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (Material material in materials) { UnityEngine.Object.DestroyImmediate(material); }
                if (mesh != null) { UnityEngine.Object.DestroyImmediate(mesh); }
                if (readback != null) { UnityEngine.Object.DestroyImmediate(readback); }
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                for (int i = bundles.Count - 1; i >= 0; i--) { bundles[i].Unload(true); }
            }
        }

        private static AssetBundle Load(string name, List<AssetBundle> owned)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile("Assets/StreamingAssets/PC/" + name);
            if (bundle == null) { throw new InvalidOperationException("隔离验收AB加载失败：" + name); }
            owned.Add(bundle);
            return bundle;
        }

        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D readback, string name)
        {
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false, false);
            File.WriteAllBytes(Output + "/" + name, readback.EncodeToPNG());
            return readback.GetPixels32();
        }
    }
}
