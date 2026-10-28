using System;
using System.IO;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>只生成新增帮手预制体与HUD新控件，保留用户既有面板布局、图片和引用。</summary>
    public static class GameHelperBuild
    {
        public static string Build()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("退出Play再制作帮手资源"); }
            string[] folders = { "Assets/GameRes/Art/Pieces/Helper", "Assets/GameRes/Pieces/Helper", "Assets/GameRes/Effects/Helper", "Assets/GameRes/Material/Helper", "Assets/GameRes/HelperTextures" };
            foreach (string folder in folders) { Directory.CreateDirectory(folder); }
            AssetDatabase.Refresh();
            string source = Path.GetFullPath("../../美术/原版美术资源/01_序列帧动画/棋子_特殊");
            var frames = new Sprite[4];
            for (int i = 0; i < frames.Length; i++)
            {
                string input = Path.Combine(source, "棋子_耄耋8级兵_序列_" + (i + 1).ToString("0000") + ".png");
                string path = folders[0] + "/HelperFrame" + (i + 1).ToString("00") + ".png";
                if (!File.Exists(path)) { File.Copy(input, path); }
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 150;
                importer.mipmapEnabled = false; importer.isReadable = false;
                importer.maxTextureSize = 512; importer.alphaIsTransparency = true;
                // 四帧原图158×114，保持完整像素与NPOT尺寸，避免平台默认设置把它压小后再放大。
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.filterMode = FilterMode.Bilinear;
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name="Standalone", overridden=true, maxTextureSize=512, format=TextureImporterFormat.RGBA32 });
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name="Android", overridden=true, maxTextureSize=512, format=TextureImporterFormat.RGBA32 });
                importer.SaveAndReimport();
                frames[i] = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
            for (int i = 1; i <= 7; i++)
            {
                string name = i <= 6 ? "HelperChapter" + i.ToString("00") : "HelperBlueBeam";
                string shaderPath = "Assets/GameShader/Helper/" + name + ".shader";
                if (AssetDatabase.LoadAssetAtPath<Shader>(shaderPath) != null)
                {
                    string error = AssetDatabase.MoveAsset(shaderPath, "Assets/GameShader/Helper/" + name + "_Shader.shader");
                    if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
                }
                Shader shader = Shader.Find("RevGame/Helper/" + name);
                if (shader == null) { throw new InvalidOperationException("帮手Shader不存在：" + name); }
                string path = folders[3] + "/" + name + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
                material.shader = shader;
                if (i == 7)
                {
                    const string texturePath = "Assets/GameRes/HelperTextures/HelperBlueLaser.png";
                    var textureImporter = (TextureImporter)AssetImporter.GetAtPath(texturePath);
                    if (textureImporter == null) { throw new InvalidOperationException("蓝色激光采样纹理缺失"); }
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.mipmapEnabled = false; textureImporter.isReadable = false;
                    textureImporter.wrapMode = TextureWrapMode.Clamp; textureImporter.maxTextureSize = 256;
                    textureImporter.SaveAndReimport();
                    material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
                }
                EditorUtility.SetDirty(material);
            }
            Material gold = MakeSharedMaterial("HelperGoldReflect", "RevGame/Helper/GoldReflect");
            Material rune = MakeSharedMaterial("HelperRune", "RevGame/Helper/Rune");
            Material impact = MakeSharedMaterial("HelperBeamImpact", "RevGame/Helper/BeamImpact");
            Material beamMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Helper/HelperBlueBeam.mat");
            // 这次用户明确要求更粗亮，升级一次默认值；后续重建保留用户调好的材质参数。
            if (!AssetImporter.GetAtPath("Assets/GameRes/Material/Helper/HelperBlueBeam.mat").userData.Contains("beam-v2"))
            {
                beamMaterial.SetFloat("_Intensity",3.8f); beamMaterial.SetFloat("_BeamWidth",1.05f);
                beamMaterial.SetFloat("_CoreWidth",.2f); beamMaterial.SetFloat("_ImpactScale",1.8f);
                AssetImporter.GetAtPath("Assets/GameRes/Material/Helper/HelperBlueBeam.mat").userData = "beam-v2";
                EditorUtility.SetDirty(beamMaterial);
            }
            const string runeTexturePath = "Assets/GameRes/HelperTextures/HelperMagicRune.png";
            var runeImporter = (TextureImporter)AssetImporter.GetAtPath(runeTexturePath);
            if (runeImporter == null) { throw new InvalidOperationException("菱形魔法符文素材不存在"); }
            runeImporter.textureType = TextureImporterType.Default; runeImporter.isReadable = runeImporter.mipmapEnabled = false;
            runeImporter.wrapMode = TextureWrapMode.Clamp; runeImporter.maxTextureSize = 256; runeImporter.SaveAndReimport();
            rune.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(runeTexturePath));
            EditorUtility.SetDirty(rune);
            var root = new GameObject("BattleHelper");
            var view = root.AddComponent<HelperView>();
            var body = new GameObject("Body").AddComponent<SpriteRenderer>();
            body.transform.SetParent(root.transform, false); body.sprite = frames[0];
            body.sharedMaterial = gold;
            // 身体约1.42个棋盘格，1920设计宽度下接近原158像素宽，不用数倍插值冒充细节。
            body.sortingOrder = 35; body.transform.localScale = Vector3.one * 1.55f;
            var canvas = new GameObject("Speech", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
            canvas.transform.SetParent(root.transform, false); canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 80;
            canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(360, 90);
            canvas.transform.localScale = Vector3.one * .01f;
            canvas.transform.localPosition = new Vector3(.3f, 1.12f, 0);
            var bg = GameAssetBuilder.Picture("Background", canvas.transform, GameAssetBuilder.Sprite("界面/新版/UI_Tip_imgBK.png"), new Vector2(360,90), Vector2.zero);
            bg.preserveAspect = false; bg.type = Image.Type.Sliced;
            bg.color = new Color(1f,1f,1f,.96f);
            bg.gameObject.AddComponent<AtlasImageBinding>();
            var text = GameAssetBuilder.Label("SpeechText", canvas.transform, "攻击敌人，为我积攒能量！", new Vector2(328,70), Vector2.zero, 23);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.color = new Color(1f,.96f,.8f);
            var serialized = new SerializedObject(view);
            serialized.FindProperty("body").objectReferenceValue = body;
            serialized.FindProperty("speech").objectReferenceValue = text;
            serialized.FindProperty("bubble").objectReferenceValue = canvas;
            serialized.FindProperty("frames").arraySize = frames.Length;
            for (int i = 0; i < frames.Length; i++) { serialized.FindProperty("frames").GetArrayElementAtIndex(i).objectReferenceValue = frames[i]; }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, folders[1] + "/BattleHelper.prefab");
            UnityEngine.Object.DestroyImmediate(root);
            root = new GameObject("HelperAttack");
            var attack = root.AddComponent<HelperAttackView>();
            // 序列帧已随工程入库；重建不依赖开发者机器上的外部素材目录。
            var stormVisual = root.AddComponent<HelperStormVisual>();
            var stormBody = new GameObject("StormProjectile").AddComponent<SpriteRenderer>();
            stormBody.transform.SetParent(root.transform, false); stormBody.sortingOrder = 64;
            stormBody.sharedMaterial = MakeSharedMaterial("HelperStorm", "RevGame/Helper/Storm");
            stormBody.enabled = false;
            var stormObject = new SerializedObject(stormVisual);
            stormObject.FindProperty("body").objectReferenceValue = stormBody;
            BindStormFrames(stormObject, "missileFrames", "Missile", 3);
            BindStormFrames(stormObject, "jellyFrames", "Jellyfish", 5);
            BindStormFrames(stormObject, "explosionFrames", "Explosion", 4);
            stormObject.ApplyModifiedPropertiesWithoutUndo();
            var line = root.AddComponent<LineRenderer>();
            line.useWorldSpace = true; line.positionCount = 2; line.sortingOrder = 60;
            line.numCapVertices = 4; line.textureMode = LineTextureMode.Stretch; line.enabled = false;
            Mesh mesh = MakeQuadMesh();
            MeshRenderer crystal = MakeQuad("Diamond", root.transform, mesh, null, 61);
            MeshRenderer runeView = MakeQuad("Rune", root.transform, mesh, rune, 62);
            MeshRenderer impactView = MakeQuad("BeamImpact", root.transform, mesh, impact, 63);
            serialized = new SerializedObject(attack); serialized.FindProperty("line").objectReferenceValue = line;
            serialized.FindProperty("stormVisual").objectReferenceValue = stormVisual;
            serialized.FindProperty("diamond").objectReferenceValue = crystal;
            serialized.FindProperty("rune").objectReferenceValue = runeView;
            serialized.FindProperty("impact").objectReferenceValue = impactView;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, folders[2] + "/HelperAttack.prefab");
            UnityEngine.Object.DestroyImmediate(root);
            const string hudPath = "Assets/GameRes/UI/Panel/BattleHudPanel.prefab";
            root = PrefabUtility.LoadPrefabContents(hudPath);
            try
            {
                var hud = root.GetComponent<BattleHudPanel>();
                Button remove = root.transform.Find("btnRemovePiece")?.GetComponent<Button>();
                if (remove == null)
                {
                    remove = GameAssetBuilder.MakeButton("btnRemovePiece", root.transform, "移除棋子", new Vector2(170,52), Vector2.zero);
                    remove.gameObject.AddComponent<AtlasImageBinding>();
                    remove.GetComponentInChildren<Text>().fontSize = 24;
                }
                remove.gameObject.SetActive(false);
                Text wallet = root.transform.Find("helperScoreLabel")?.GetComponent<Text>();
                if (wallet == null)
                {
                    wallet = GameAssetBuilder.Label("helperScoreLabel", root.transform, "帮手能量 0 · 0档", new Vector2(420,42), new Vector2(0,60), 25);
                    GameAssetBuilder.Anchor(wallet.rectTransform, new Vector2(.5f,0));
                }
                serialized = new SerializedObject(hud);
                serialized.FindProperty("removePieceButton").objectReferenceValue = remove;
                serialized.FindProperty("helperScoreLabel").objectReferenceValue = wallet;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, hudPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            return "四帧帮手、台词框、移除按钮、积分显示、七份材质/Shader已制作";
        }

        /// <summary>制作资产共享一次，运行时直接借用，不为每发子弹new Mesh/Material。</summary>
        private static Material MakeSharedMaterial(string name, string shaderName)
        {
            string path = "Assets/GameRes/Material/Helper/" + name + ".mat";
            Shader shader = Shader.Find(shaderName);
            if (shader == null) { throw new InvalidOperationException("新增帮手Shader缺失：" + shaderName); }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material,path); }
            material.shader = shader; EditorUtility.SetDirty(material); return material;
        }
        /// <summary>导入透明序列帧并绑定引用；图集由GameSpriteAtlasBuild统一打包，运行时不会逐帧GetSprite。</summary>
        private static void BindStormFrames(SerializedObject target, string field, string prefix, int count)
        {
            var array = target.FindProperty(field); array.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                string path = "Assets/GameRes/Art/Pieces/Helper/Storm/" + prefix + (i + 1).ToString("00") + ".png";
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) { throw new InvalidOperationException("风暴序列帧缺失：" + path); }
                importer.textureType = TextureImporterType.Sprite; importer.spritePixelsPerUnit = 150;
                importer.spriteImportMode = SpriteImportMode.Single; importer.mipmapEnabled = importer.isReadable = false;
                importer.alphaIsTransparency = true; importer.npotScale = TextureImporterNPOTScale.None;
                importer.filterMode = FilterMode.Bilinear; importer.maxTextureSize = 512;
                importer.SaveAndReimport();
                array.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
        }
        private static Mesh MakeQuadMesh()
        {
            const string path = "Assets/GameRes/Effects/Helper/HelperQuad.asset";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh != null) { return mesh; }
            mesh = new Mesh { name = "HelperQuad" };
            mesh.vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0,2,1,0,3,2 }; mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh,path); return mesh;
        }
        private static MeshRenderer MakeQuad(string name, Transform parent, Mesh mesh, Material material, int order)
        {
            var go = new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));
            go.transform.SetParent(parent,false); go.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.sortingOrder = order;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false; renderer.enabled = false;
            return renderer;
        }
    }
}
