// 章节美术的明确导入/分包入口与 Play 验收。不会重装 UI，也不更改章节解锁或存档。
using System;
using System.Collections.Generic;
using System.IO;
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
        private const string MaterialRoot = "Assets/GameRes/Material/Background";
        private const string OriginalBackgroundName = "SceneBackdrop_0fc7e394";
        private const string OriginalBackgroundGuid = "0fc7e39451e2aad43b077b83d8417359";
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
            EnsureFolder(TextureRoot);
            EnsureFolder(MaterialRoot);
            // 资源系统按去扩展名的名字寻址；Shader与PNG同包时也不能同名，统一给Shader文件加后缀。
            foreach (string name in new[] { "Chapter01_Tutorial", "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" })
            {
                string previousShader = "Assets/GameShader/Background/" + name + ".shader";
                string shaderDestination = "Assets/GameShader/Background/" + name + "_Shader.shader";
                if (AssetDatabase.LoadAssetAtPath<Shader>(shaderDestination) == null && AssetDatabase.LoadAssetAtPath<Shader>(previousShader) != null)
                {
                    string error = AssetDatabase.MoveAsset(previousShader, shaderDestination);
                    if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
                }
            }
            string originalPath = AssetDatabase.GUIDToAssetPath(OriginalBackgroundGuid);
            string destination = TextureRoot + "/" + OriginalBackgroundName + ".png";
            if (string.IsNullOrEmpty(originalPath)) { throw new InvalidOperationException("第一章背景 GUID 不存在，不能复制新图冒充迁移"); }
            if (originalPath != destination)
            {
                // 移动资产而非复制 PNG：Unity 会连同 .meta 保留 GUID、Sprite fileID 和场景/图集引用。
                string moveError = AssetDatabase.MoveAsset(originalPath, destination);
                if (!string.IsNullOrEmpty(moveError)) { throw new InvalidOperationException("第一章背景迁移失败：" + moveError); }
            }
            Shader tutorialShader = Shader.Find("RevGame/Background/Chapter01_Tutorial");
            Material tutorialMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/ArcaneScene.mat");
            if (tutorialShader == null || ShaderUtil.ShaderHasError(tutorialShader) || tutorialMaterial == null)
            { throw new InvalidOperationException("第一章专用 Shader/原有材质不存在或编译失败"); }
            tutorialMaterial.shader = tutorialShader;
            EditorUtility.SetDirty(tutorialMaterial);
            for (int i = 0; i < Names.Length; i++)
            {
                Shader shader = Shader.Find("RevGame/Background/" + Names[i]);
                if (shader == null || ShaderUtil.ShaderHasError(shader))
                {
                    var messages = new List<string>();
                    if (shader != null)
                    {
                        foreach (var message in ShaderUtil.GetShaderMessages(shader)) { messages.Add(message.line + ": " + message.message); }
                    }
                    throw new InvalidOperationException("章节独立 Shader 缺失或编译失败：" + Names[i] + " / " + string.Join("; ", messages));
                }
                string path = TextureRoot + "/" + Names[i] + ".png";
                // 原画保留在美术目录，运行时生成RGB/16:9副本；重导不能再写回未满足块压缩的765/774高度。
                GameResourceCompressionBuild.PrepareBackground(Path.Combine(SourceRoot, Sources[i]), path);
                AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant("background_" + Names[i].ToLowerInvariant(), string.Empty);
                // 框架按不含扩展名的资源名寻址：同包 PNG 与材质不能同名，故材质统一加后缀。
                string materialName = Names[i] + "_Material";
                string materialPath = MaterialRoot + "/" + materialName + ".mat";
                string previousMaterialPath = MaterialRoot + "/" + Names[i] + ".mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) == null && AssetDatabase.LoadAssetAtPath<Material>(previousMaterialPath) != null)
                {
                    string moveError = AssetDatabase.MoveAsset(previousMaterialPath, materialPath);
                    if (!string.IsNullOrEmpty(moveError)) { throw new InvalidOperationException(moveError); }
                }
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, materialPath);
                }
                material.name = materialName;
                material.shader = shader;
                material.SetTexture("_ChapterTex", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                material.SetFloat("_Aspect", 16f / 9f);
                EditorUtility.SetDirty(material);
                // 图片+材质同章同包；若五个材质共包，材质的贴图依赖会把五章图片一起拖入内存。
                AssetImporter.GetAtPath(materialPath).SetAssetBundleNameAndVariant("background_" + Names[i].ToLowerInvariant(), string.Empty);
            }
            // 通用资源由独立主题替代，移除后让漏标/依赖校验及时暴露遗留引用。
            AssetDatabase.DeleteAsset(MaterialRoot + "/ChapterAtmosphere.mat");
            AssetDatabase.DeleteAsset("Assets/GameShader/Background/ChapterAtmosphere.shader");
            AssetDatabase.SaveAssets();
            SaveSceneInspector();
            string map = GameResourceBuild.Configure();
            Audit();
            return Result("六章图片/材质/Shader独立分包；第一章背景保留 GUID 与图集迁入 ChapterBackground，" + map);
        }

        public static ResultInfo Audit()
        {
            if (AssetDatabase.GUIDToAssetPath(OriginalBackgroundGuid) != TextureRoot + "/" + OriginalBackgroundName + ".png")
            { throw new InvalidOperationException("第一章背景没有保持原 GUID 迁移至 ChapterBackground"); }
            if (((TextureImporter)AssetImporter.GetAtPath(TextureRoot + "/" + OriginalBackgroundName + ".png")).textureType != TextureImporterType.Sprite)
            { throw new InvalidOperationException("第一章仍须使用 SpriteAtlas，不能改成未入图集的散图"); }
            string firstShaderPath = "Assets/GameShader/Background/Chapter01_Tutorial_Shader.shader";
            Material firstMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/ArcaneScene.mat");
            if (firstMaterial == null || firstMaterial.shader == null || firstMaterial.shader.name != "RevGame/Background/Chapter01_Tutorial"
                || ShaderUtil.ShaderHasError(firstMaterial.shader) || !firstMaterial.shader.isSupported)
            { throw new InvalidOperationException("第一章没有使用独立Shader"); }
            foreach (string path in new[] { TextureRoot + "/" + OriginalBackgroundName + ".png", "Assets/GameRes/Atlases/" + GameSpriteAtlasService.TutorialAtlasName + ".spriteatlasv2", MaterialRoot + "/ArcaneScene.mat", firstShaderPath })
            {
                if (AssetDatabase.GetImplicitAssetBundleName(path) != "background_chapter01_tutorial")
                { throw new InvalidOperationException("第一章图片/图集/材质/Shader没有全部同包：" + path); }
            }
            var shaders = new HashSet<Shader>();
            for (int i = 0; i < Names.Length; i++)
            {
                string path = TextureRoot + "/" + Names[i] + ".png";
                // 制作副本经过RGB采样，PNG字节不再与原画一致；改验源SHA标识和制作尺寸，不把转码当换图。
                GameResourceCompressionBuild.ValidateBackground(Path.Combine(SourceRoot, Sources[i]), path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.textureType != TextureImporterType.Default || importer.isReadable || importer.mipmapEnabled
                    || !importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Clamp || importer.npotScale != TextureImporterNPOTScale.None)
                { throw new InvalidOperationException("背景纹理导入设置不符合独立采样：" + path); }
                if (AssetDatabase.GetImplicitAssetBundleName(path) != "background_" + Names[i].ToLowerInvariant())
                { throw new InvalidOperationException("章节纹理没有独立分包：" + path); }
                Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "/" + Names[i] + "_Material.mat");
                if (material == null || material.shader == null || !material.shader.isSupported || ShaderUtil.ShaderHasError(material.shader)
                    || material.shader.name != "RevGame/Background/" + Names[i] || material.HasProperty("_Theme") || !shaders.Add(material.shader))
                { throw new InvalidOperationException("章节没有使用独立且可编译的 Shader：" + Names[i]); }
                if (material.GetTexture("_ChapterTex") != AssetDatabase.LoadAssetAtPath<Texture2D>(path))
                { throw new InvalidOperationException("材质没有绑定自己对应的原画：" + Names[i]); }
                if (AssetDatabase.GetImplicitAssetBundleName(AssetDatabase.GetAssetPath(material)) != AssetDatabase.GetImplicitAssetBundleName(path))
                { throw new InvalidOperationException("章节材质与图片没有同包：" + Names[i]); }
                if (AssetDatabase.GetImplicitAssetBundleName(AssetDatabase.GetAssetPath(material.shader)) != AssetDatabase.GetImplicitAssetBundleName(path))
                { throw new InvalidOperationException("章节Shader与图片没有同包：" + Names[i]); }
            }
            // 场景与大厅材质只能存逻辑名，不能有任何章节资产硬引用；否则加载场景就破坏按章加载边界。
            foreach (string root in new[] { "Assets/Scenes/StartScene.unity", "Assets/Scenes/GameScene.unity", "Assets/GameRes/UI" })
            {
                string[] assets = AssetDatabase.IsValidFolder(root)
                    ? Array.ConvertAll(AssetDatabase.FindAssets("", new[] { root }), AssetDatabase.GUIDToAssetPath)
                    : new[] { root };
                foreach (string asset in assets)
                {
                    foreach (string dependency in AssetDatabase.GetDependencies(asset, true))
                    {
                        if (AssetDatabase.GetImplicitAssetBundleName(dependency).StartsWith("background_chapter", StringComparison.Ordinal))
                        { throw new InvalidOperationException("场景/UI不应硬引用章节包：" + asset + " → " + dependency); }
                    }
                }
            }
            return Result("第一章 GUID/Sprite/图集保留；六个独立背景包；其余5张RGB副本源图SHA标识与16:9制作尺寸一致");
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
                    foreach (GamePresentation presentation in root.GetComponentsInChildren<GamePresentation>(true))
                    {
                        var data = new SerializedObject(presentation);
                        string[] fields = { "villageBackground", "lakeBackground", "swampBackground", "iceBackground", "templeBackground" };
                        for (int i = 0; i < fields.Length; i++)
                        {
                            data.FindProperty(fields[i]).FindPropertyRelative("materialName").stringValue = Names[i] + "_Material";
                            data.FindProperty(fields[i]).FindPropertyRelative("shaderName").stringValue = "RevGame/Background/" + Names[i];
                        }
                        SerializedProperty tutorial = data.FindProperty("tutorialBackground");
                        tutorial.FindPropertyRelative("textureName").stringValue = OriginalBackgroundName;
                        tutorial.FindPropertyRelative("materialName").stringValue = "ArcaneScene";
                        tutorial.FindPropertyRelative("shaderName").stringValue = "RevGame/Background/Chapter01_Tutorial";
                        data.ApplyModifiedPropertiesWithoutUndo();
                        // 场景只保存渲染节点与手调变换，不持有任何章节图片/材质，杜绝场景隐式拖入第一章包。
                        presentation.BackgroundRenderer.sprite = null;
                        presentation.BackgroundRenderer.sharedMaterial = null;
                        EditorUtility.SetDirty(presentation.BackgroundRenderer);
                        EditorUtility.SetDirty(presentation);
                    }
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
                CheckChapterReferences(0);
                await CheckLoadedChapterBundlesAsync(0);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "通过玩家共用流程进入第一章");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                GamePresentation presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
                await WaitForBackgroundAsync(presentation);
                CheckTutorial(presentation);
                CheckChapterReferences(1);
                await CheckLoadedChapterBundlesAsync(1);
                ScreenCapture.CaptureScreenshot(directory + "/Chapter1-Game.png");
                BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
                // 连续变更在同帧发生，不等待旧图回调；验证已取消请求不能覆盖第一章或多减引用。
                Require(battle.StartRound(2, 1) && battle.StartRound(4, 1) && battle.StartRound(3, 1) && battle.StartRound(1, 1), "冷加载时快速换章并取消");
                await WaitForBackgroundAsync(presentation);
                await RevTask.Delay(600);
                CheckTutorial(presentation);
                CheckChapterReferences(1);
                await CheckLoadedChapterBundlesAsync(1);
                for (int chapter = 2; chapter <= 6; chapter++)
                {
                    // 不写解锁状态：测试调用真实模型 StartRound 验证同场景准备事件，不能宣称这是解锁流程测试。
                    Require(battle.StartRound(chapter, 1), "真实模型开始章节 " + chapter);
                    await WaitForBackgroundAsync(presentation);
                    Require(presentation.BackgroundChapter == chapter && presentation.BackgroundTextureName == Names[chapter - 2], "章节图片正确 " + chapter);
                    Require(presentation.BackgroundRenderer.sharedMaterial.name == Names[chapter - 2] + "_Material", "章节独立材质真实接入 " + chapter);
                    Require(presentation.BackgroundRenderer.sharedMaterial.shader.name == "RevGame/Background/" + Names[chapter - 2], "章节独立 Shader 真实接入 " + chapter);
                    CheckChapterReferences(chapter);
                    await CheckLoadedChapterBundlesAsync(chapter);
                    Shader shader = presentation.BackgroundRenderer.sharedMaterial.shader;
                    Require(shader.isSupported && !ShaderUtil.ShaderHasError(shader), "当前 GPU 编译支持主题 Shader");
                    await RevTask.Delay(950);
                    ScreenCapture.CaptureScreenshot(directory + "/Chapter" + chapter + "-Game.png");
                    presentation.SetBackgroundPreviewStrength(0f);
                    Color32[] stillA = RenderFrame(presentation, 0f, 960, 540, null);
                    Color32[] stillB = RenderFrame(presentation, 2.5f, 960, 540, null);
                    Require(Difference(stillA, stillB) < 0.00001, "特效强度 0 时原画没有时间相关变化");
                    presentation.SetBackgroundPreviewStrength(-1f);
                    presentation.SetBackgroundPreviewSpeed(0f);
                    Color32[] pausedA = RenderFrame(presentation, 0f, 960, 540, null);
                    Color32[] pausedB = RenderFrame(presentation, 2.5f, 960, 540, null);
                    Require(Difference(pausedA, pausedB) < 0.00001, "速度 0 时该章节特效静止");
                    presentation.SetBackgroundPreviewSpeed(-1f);
                    Color32[] first = RenderFrame(presentation, 0f, 960, 540, directory + "/Chapter" + chapter + "-A.png");
                    Color32[] second = RenderFrame(presentation, 2.5f, 960, 540, directory + "/Chapter" + chapter + "-B.png");
                    double difference = Difference(first, second);
                    Require(difference > 0.0001, "GPU 连续时间像素有动态差异 " + chapter + " / " + difference.ToString("F6"));
                    if (chapter == 2)
                    {
                        Require(Brightness(first) > Brightness(stillA) + 0.002, "村落日照提高画面亮度，保持明朗");
                    }
                    foreach (Vector2Int size in new[] { new Vector2Int(1040, 480), new Vector2Int(800, 600) })
                    {
                        RenderFrame(presentation, 1f, size.x, size.y, null);
                        Require(CoversCamera(presentation.BackgroundRenderer, Camera.main), "背景等比铺满 " + size);
                    }
                    presentation.SetBackgroundPreviewTime(-1f);
                    await RevTask.Yield();
                }
                Require(battle.StartRound(1, 1), "重新开始第一章");
                await WaitForBackgroundAsync(presentation);
                CheckTutorial(presentation);
                CheckChapterReferences(1);
                await CheckLoadedChapterBundlesAsync(1);
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
                    CheckChapterReferences(0);
                    await CheckLoadedChapterBundlesAsync(0);
                }
                catch (Exception error) { _lastResult = "失败：收尾 " + error.Message; }
                if (_lastResult.StartsWith("通过", StringComparison.Ordinal)) { _lastResult = "通过：" + mode + "，" + _checks + " 条断言；六章图片、GPU 动态/关闭、多比例覆盖与资源回收"; }
                File.WriteAllText(directory + "/Result.txt", _lastResult + "\n" + string.Join("\n", Evidence));
                _checking = false;
            }
        }

        private static async RevTask WaitForBackgroundAsync(GamePresentation presentation)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!presentation.IsChapterBackgroundReady)
            {
                if (!string.IsNullOrEmpty(presentation.BackgroundLoadError)) { throw new InvalidOperationException(presentation.BackgroundLoadError); }
                if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException("背景加载未在30秒内完成"); }
                await RevTask.Yield();
            }
            Require(string.IsNullOrEmpty(presentation.BackgroundLoadError), "背景交接成功且无错误");
        }

        private static void CheckTutorial(GamePresentation presentation)
        {
            Require(presentation.BackgroundChapter == 1 && presentation.BackgroundTextureName == OriginalBackgroundName, "第一章仍使用原图");
            Require(presentation.BackgroundRenderer.sprite != null, "第一章图集Sprite可画");
            Material material = presentation.BackgroundRenderer.sharedMaterial;
            Require(material != null && material.name == "ArcaneScene" && material.shader.name == "RevGame/Background/Chapter01_Tutorial", "第一章保留原材质参数并使用专用Shader");
            if (RevResBootstrap.UseABInEditor) { Require(presentation.BackgroundRenderer.sprite.packed, "第一章真实AB使用图集像素"); }
        }

        private static void CheckChapterReferences(int active)
        {
            Require(RevResManager.GetRefCount(RevResPath.Material_Background, "ArcaneScene") == (active == 1 ? 1 : 0), "第一章材质引用正确，当前章=" + active);
            Require(RevResManager.GetRefCount(RevResPath.Atlases, GameSpriteAtlasService.TutorialAtlasName) == (active == 1 ? 1 : 0), "第一章图集引用正确，当前章=" + active);
            for (int i = 0; i < Names.Length; i++)
            {
                int expected = active == i + 2 ? 1 : 0;
                Require(RevResManager.GetRefCount(GamePresentation.ChapterBackgroundRoot, Names[i]) == expected, "图片引用 " + Names[i] + "=" + expected);
                Require(RevResManager.GetRefCount(RevResPath.Material_Background, Names[i] + "_Material") == expected, "材质引用 " + Names[i] + "=" + expected);
            }
        }

        private static async RevTask CheckLoadedChapterBundlesAsync(int active)
        {
            if (!RevResBootstrap.UseABInEditor) { return; }
            // 只在验收的流程空闲边界整理缓存，Release=0本身并不意味着物理AB已卸载。
            await RevTask.Delay(200);
            RevResManager.FlushUnused();
            await RevTask.Delay(200);
            int count = 0;
            string expected = active == 1 ? "background_chapter01_tutorial" : active > 1 ? "background_" + Names[active - 2].ToLowerInvariant() : string.Empty;
            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (!bundle.name.StartsWith("background_chapter", StringComparison.Ordinal)) { continue; }
                count++;
                Require(bundle.name == expected, "当前实际背景包=" + bundle.name);
            }
            Require(count == (active > 0 ? 1 : 0), "整理后实际背景包数量=" + count + "，当前章=" + active);
        }

        private static Color32[] RenderFrame(GamePresentation presentation, float time, int width, int height, string path)
        {
            Camera camera = Camera.main;
            int previousMask = camera.cullingMask;
            int previousLayer = presentation.BackgroundRenderer.gameObject.layer;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                presentation.SetBackgroundPreviewTime(time);
                // 固定时间的GPU比较只画背景，避免棋子待机/飘字动画让关闭特效测试出现假差异。
                presentation.BackgroundRenderer.gameObject.layer = 31;
                camera.cullingMask = 1 << 31;
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
                camera.cullingMask = previousMask;
                presentation.BackgroundRenderer.gameObject.layer = previousLayer;
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

        private static double Brightness(Color32[] pixels)
        {
            double sum = 0;
            foreach (Color32 pixel in pixels) { sum += pixel.r * 0.2126 + pixel.g * 0.7152 + pixel.b * 0.0722; }
            return sum / (pixels.Length * 255.0);
        }

        private static void Require(bool valid, string message)
        {
            _checks++;
            if (!valid) { throw new InvalidOperationException(message); }
            Evidence.Add(message);
        }
    }
}
