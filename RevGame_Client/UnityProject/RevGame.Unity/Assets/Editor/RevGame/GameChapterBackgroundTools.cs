// 章节美术的明确导入/分包入口与 Play 验收。不会重装 UI，也不更改章节解锁或存档。
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevGame.Editor
{
    public static class GameChapterBackgroundTools
    {
        private const string TextureRoot = "Assets/GameRes/ChapterBackground";
        private const string MaterialPath = "Assets/GameRes/Material/Background/ChapterAtmosphere.mat";
        private static readonly string[] Names = { "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
        private static readonly string[] Sources =
        {
            "棋盘_第5关_魔法村落_背景.png", "棋盘_第2关_魔法湖泊_湖面.png", "棋盘_第3关_死亡沼泽_湖面_v1.png",
            "棋盘_第4关_冰雪极地_背景.png", "棋盘_第6关_魔法神殿_背景_象牙金.png"
        };
        private static string SourceRoot { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../../../美术/AI生成")); } }
        private static bool _checking;
        private static string _lastResult = "未运行";
        private static int _checks;
        private static readonly List<string> Evidence = new List<string>();

        [Serializable] public sealed class ResultInfo { public string text; public bool abMode; public int checks; public string directory; }
        private static ResultInfo Result(string text) { return new ResultInfo { text = text, abMode = RevResBootstrap.UseABInEditor, checks = _checks, directory = Path.GetFullPath("Temp/ChapterBackgroundChecks") }; }
        public static ResultInfo Status() { return Result(_lastResult); }
        public static bool IsChecking() { return _checking; }

        [MenuItem("RevGame/美术/导入章节背景与主题特效")]
        public static void ImportMenu() { Debug.Log(Import().text); }

        public static ResultInfo Import()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("导入背景前请退出 Play"); }
            foreach (string source in Sources)
            {
                if (!File.Exists(Path.Combine(SourceRoot, source))) { throw new FileNotFoundException("指定背景原图缺失：" + source); }
            }
            Shader shader = Shader.Find("RevGame/Background/ChapterAtmosphere");
            if (shader == null) { throw new InvalidOperationException("主题 Shader 不存在"); }
            if (ShaderUtil.ShaderHasError(shader))
            {
                var messages = new List<string>();
                foreach (var message in ShaderUtil.GetShaderMessages(shader)) { messages.Add(message.line + ": " + message.message); }
                throw new InvalidOperationException("主题 Shader 编译失败：" + string.Join("; ", messages));
            }
            EnsureFolder(TextureRoot);
            for (int i = 0; i < Names.Length; i++)
            {
                string path = TextureRoot + "/" + Names[i] + ".png";
                File.Copy(Path.Combine(SourceRoot, Sources[i]), path, true);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = 2048;
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.DXT5 });
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.ETC2_RGBA8 });
                importer.SetAssetBundleNameAndVariant("background_" + Names[i].ToLowerInvariant(), string.Empty);
                importer.SaveAndReimport();
            }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, MaterialPath); }
            material.shader = shader;
            EditorUtility.SetDirty(material);
            AssetImporter.GetAtPath(MaterialPath).SetAssetBundleNameAndVariant("material_background", string.Empty);
            AssetDatabase.SaveAssets();
            SaveSceneInspector();
            string map = GameResourceBuild.Configure();
            Audit();
            return Result("五章背景导入完成（沼泽为 v1、神殿为象牙金），" + map);
        }

        public static ResultInfo Audit()
        {
            for (int i = 0; i < Names.Length; i++)
            {
                string path = TextureRoot + "/" + Names[i] + ".png";
                if (Digest(path) != Digest(Path.Combine(SourceRoot, Sources[i]))) { throw new InvalidOperationException("导入图片不是指定原图：" + Names[i]); }
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.textureType != TextureImporterType.Default || importer.isReadable || importer.mipmapEnabled
                    || !importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Clamp || importer.npotScale != TextureImporterNPOTScale.None)
                { throw new InvalidOperationException("背景纹理导入设置不符合独立采样：" + path); }
                if (AssetDatabase.GetImplicitAssetBundleName(path) != "background_" + Names[i].ToLowerInvariant())
                { throw new InvalidOperationException("章节纹理没有独立分包：" + path); }
            }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null || material.shader == null || !material.shader.isSupported || ShaderUtil.ShaderHasError(material.shader))
            { throw new InvalidOperationException("背景材质 Shader 无效"); }
            return Result("5 张背景原图 SHA256 一致；独立纹理设置、独立分包和主题 Shader 通过");
        }

        private static void SaveSceneInspector()
        {
            const string path = "Assets/Scenes/GameScene.unity";
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(path);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (!opened && scene.isDirty) { throw new InvalidOperationException("GameScene 有未保存修改，请先保存再导入"); }
            if (opened) { scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive); }
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (GamePresentation presentation in root.GetComponentsInChildren<GamePresentation>(true)) { EditorUtility.SetDirty(presentation); }
                }
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) { throw new IOException("章节背景 Inspector 保存失败"); }
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) { SceneManager.SetActiveScene(previous); }
                if (opened) { EditorSceneManager.CloseScene(scene, true); }
            }
        }

        private static string Digest(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) { return Convert.ToBase64String(sha.ComputeHash(stream)); }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) { return; }
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        [MenuItem("RevGame/验证/章节背景与主题特效（Play）")]
        public static void CheckMenu() { BeginCheck(); }

        public static async void BeginCheck()
        {
            if (!Application.isPlaying || _checking) { return; }
            _checking = true;
            _checks = 0;
            _lastResult = "运行中";
            Evidence.Clear();
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            string mode = RevResBootstrap.UseABInEditor ? "AB" : "Direct";
            string directory = "Temp/ChapterBackgroundChecks/" + mode;
            Directory.CreateDirectory(directory);
            try
            {
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "通过玩家共用流程进入第一章");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                GamePresentation presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
                Sprite original = presentation.BackgroundRenderer.sprite;
                Material legacy = presentation.BackgroundRenderer.sharedMaterial;
                Require(presentation.BackgroundChapter == 1 && legacy.shader.name == "RevGame/Background/ArcaneBackdrop", "第一章保留原背景与材质");
                BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
                for (int chapter = 2; chapter <= 6; chapter++)
                {
                    // 不写解锁状态：测试调用真实模型 StartRound 验证同场景准备事件，不能宣称这是解锁流程测试。
                    Require(battle.StartRound(chapter, 1), "真实模型开始章节 " + chapter);
                    float deadline = Time.realtimeSinceStartup + 30f;
                    while (!presentation.IsChapterBackgroundReady)
                    {
                        if (!string.IsNullOrEmpty(presentation.BackgroundLoadError)) { throw new InvalidOperationException(presentation.BackgroundLoadError); }
                        Require(Time.realtimeSinceStartup < deadline, "背景加载有界");
                        await RevTask.Yield();
                    }
                    Require(presentation.BackgroundChapter == chapter && presentation.BackgroundTextureName == Names[chapter - 2], "章节图片正确 " + chapter);
                    Require(presentation.BackgroundRenderer.sharedMaterial.shader.name == "RevGame/Background/ChapterAtmosphere", "主题材质真实接入");
                    Require(RevResManager.GetRefCount(GamePresentation.ChapterBackgroundRoot, Names[chapter - 2]) == 1, "当前背景恰好一份租约");
                    if (chapter > 2) { Require(RevResManager.GetRefCount(GamePresentation.ChapterBackgroundRoot, Names[chapter - 3]) == 0, "旧章节纹理已释放"); }
                    Shader shader = presentation.BackgroundRenderer.sharedMaterial.shader;
                    Require(shader.isSupported && !ShaderUtil.ShaderHasError(shader), "当前 GPU 编译支持主题 Shader");
                    await RevTask.Delay(950);
                    ScreenCapture.CaptureScreenshot(directory + "/Chapter" + chapter + "-Game.png");
                    presentation.SetBackgroundPreviewStrength(0f);
                    Color32[] stillA = RenderFrame(presentation, 0f, 960, 540, null);
                    Color32[] stillB = RenderFrame(presentation, 2.5f, 960, 540, null);
                    Require(Difference(stillA, stillB) < 0.00001, "特效强度 0 时原画没有时间相关变化");
                    presentation.SetBackgroundPreviewStrength(-1f);
                    Color32[] first = RenderFrame(presentation, 0f, 960, 540, directory + "/Chapter" + chapter + "-A.png");
                    Color32[] second = RenderFrame(presentation, 2.5f, 960, 540, directory + "/Chapter" + chapter + "-B.png");
                    double difference = Difference(first, second);
                    Require(difference > 0.0001, "GPU 连续时间像素有动态差异 " + chapter + " / " + difference.ToString("F6"));
                    foreach (Vector2Int size in new[] { new Vector2Int(1040, 480), new Vector2Int(800, 600) })
                    {
                        RenderFrame(presentation, 1f, size.x, size.y, null);
                        Require(CoversCamera(presentation.BackgroundRenderer, Camera.main), "背景等比铺满 " + size);
                    }
                    presentation.SetBackgroundPreviewTime(-1f);
                    await RevTask.Yield();
                }
                Require(battle.StartRound(1, 1), "重新开始第一章");
                Require(presentation.BackgroundRenderer.sprite == original && presentation.BackgroundRenderer.sharedMaterial == legacy, "切回第一章恢复原 Sprite 与材质");
                _lastResult = "通过：" + mode + "，" + _checks + " 条断言；六章图片、GPU 动态、多比例覆盖与租约";
            }
            catch (Exception error)
            {
                _lastResult = "失败：" + error.Message;
                RevLog.Exception(error, "章节背景验收失败", "RevGame");
            }
            finally
            {
                try
                {
                    await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                    foreach (string name in Names) { Require(RevResManager.GetRefCount(GamePresentation.ChapterBackgroundRoot, name) == 0, "退出释放 " + name); }
                    Require(RevResManager.GetRefCount(RevResPath.Material_Background, GamePresentation.ChapterBackgroundMaterialName) == 0, "退出释放主题材质");
                }
                catch (Exception error) { _lastResult = "失败：收尾 " + error.Message; }
                if (_lastResult.StartsWith("通过", StringComparison.Ordinal)) { _lastResult = "通过：" + mode + "，" + _checks + " 条断言；六章图片、GPU 动态/关闭、多比例覆盖与资源回收"; }
                File.WriteAllText(directory + "/Result.txt", _lastResult + "\n" + string.Join("\n", Evidence));
                _checking = false;
            }
        }

        private static Color32[] RenderFrame(GamePresentation presentation, float time, int width, int height, string path)
        {
            Camera camera = Camera.main;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                presentation.SetBackgroundPreviewTime(time);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();
                if (path != null) { File.WriteAllBytes(path, pixels.EncodeToPNG()); }
                Require(CoversCamera(presentation.BackgroundRenderer, camera), "当前目标比例四角覆盖 " + width + "x" + height);
                return pixels.GetPixels32();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                presentation.SetBackgroundPreviewTime(-1f);
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(pixels);
            }
        }

        private static bool CoversCamera(SpriteRenderer renderer, Camera camera)
        {
            float height = camera.orthographicSize * 2f;
            Vector3 center = camera.transform.position;
            Bounds bounds = renderer.bounds;
            return bounds.min.x <= center.x - height * camera.aspect * 0.5f && bounds.max.x >= center.x + height * camera.aspect * 0.5f
                && bounds.min.y <= center.y - height * 0.5f && bounds.max.y >= center.y + height * 0.5f;
        }

        private static double Difference(Color32[] a, Color32[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++) { sum += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b); }
            return sum / (a.Length * 3.0 * 255.0);
        }

        private static void Require(bool valid, string message)
        {
            _checks++;
            if (!valid) { throw new InvalidOperationException(message); }
            Evidence.Add(message);
        }
    }
}
