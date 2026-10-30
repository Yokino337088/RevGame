using System;
using System.IO;
using RevGame.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>在独立预览场景绘制真实棋格，检查六章材质的GPU画面；不切换或保存用户正在编辑的场景。</summary>
    public static class GameBoardMagicPreview
    {
        private static readonly string[] Names = { "Chapter01_Tutorial", "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
        // 针对本轮新增的材质调节项逐个比较GPU输出；使用克隆材质，不改用户保存的参数。
        private static readonly string[][] IntensityControls =
        {
            new[] { "_NebulaStrength", "_StarStrength", "_RimStrength" },
            new[] { "_SunlightStrength", "_DappleStrength", "_FireflyStrength", "_RimStrength" },
            new[] { "_WaveStrength", "_CausticStrength", "_ReflectionStrength", "_RippleStrength", "_RimStrength" },
            new[] { "_MudStrength", "_MudContrast", "_EddyStrength", "_BubbleStrength", "_RimStrength" },
            new[] { "_CrystalStrength", "_GlintStrength", "_CrackStrength", "_FrostStrength", "_SnowStrength", "_ColdShade", "_IceContrast", "_RimStrength" },
            new[] { "_RayStrength", "_HaloStrength", "_DiamondStrength", "_ShaftStrength", "_LightContrast", "_RimStrength", "_RimPulse" }
        };
        private const int Size = 960;

        public static string Capture()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("棋盘离屏预览只能在编辑态运行");
            }
            const string folder = "Temp/BoardMagicPreview";
            Directory.CreateDirectory(folder);
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D readback = null;
            Material surface = null;
            Material placement = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameRes/Board/Tile/BoardCell.prefab");
                Material placementAsset = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Board/PlacementMagic.mat");

                if (prefab == null || placementAsset == null)
                {
                    throw new InvalidOperationException("棋格预制体或落点材质缺失");
                }
                placement = new Material(placementAsset) { hideFlags = HideFlags.HideAndDontSave };
                var cells = new SpriteRenderer[81];
                for (int y = 0; y < 9; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        // 真实预制体保留九宫格网格和Sprite UV；只对本次预览的实例替换材质。
                        GameObject cell = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        cell.transform.position = new Vector3((x - 4) * Consts.CellSize, (y - 4) * Consts.CellSize, 0);
                        cells[y * 9 + x] = cell.GetComponent<SpriteRenderer>();
                        SpriteRenderer marker = cell.transform.Find("可落点").GetComponent<SpriteRenderer>();
                        marker.sharedMaterial = placement;
                        marker.enabled = (x == 1 && y == 2) || (x == 3 && y == 4) || (x == 5 && y == 6) || (x == 7 && y == 3);
                    }
                }
                var cameraObject = new GameObject("棋盘离屏验收相机", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 5.42f;
                camera.transform.position = new Vector3(0, 0, -10);
                camera.nearClipPlane = .1f;
                camera.farClipPlane = 30f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.06f, .09f, .13f, 1);
                camera.scene = scene;
                target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                var results = new string[Names.Length];
                int controlCount = 0;
                for (int chapter = 0; chapter < Names.Length; chapter++)
                {
                    string name = Names[chapter];
                    Material asset = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Board/" + name + "_Surface.mat");

                    if (asset == null || asset.shader == null || !asset.shader.isSupported || ShaderUtil.ShaderHasError(asset.shader))
                    {
                        throw new InvalidOperationException("章节材质或Shader不可用：" + name);
                    }
                    surface = new Material(asset) { hideFlags = HideFlags.HideAndDontSave };
                    foreach (SpriteRenderer cell in cells)
                    {
                        cell.sharedMaterial = surface;
                    }
                    Color32[] first = Render(camera, target, readback, surface, placement, 0, folder + "/" + name + "_t0.png");
                    Color32[] second = Render(camera, target, readback, surface, placement, 4, folder + "/" + name + "_t4.png");
                    double change = Difference(first, second);

                    if (change < .0005)
                    {
                        throw new InvalidOperationException("四秒前后几乎无像素变化：" + name + " / " + change.ToString("F5"));
                    }
                    results[chapter] = name + "=" + change.ToString("F4");

                    if (chapter < IntensityControls.Length)
                    {
                        foreach (string property in IntensityControls[chapter])
                        {
                            if (!surface.HasProperty(property))
                            {
                                throw new InvalidOperationException("材质缺少调节项：" + name + "/" + property);
                            }
                            float original = surface.GetFloat(property);
                            surface.SetFloat(property, 0f);
                            Color32[] disabled = Render(camera, target, readback, surface, placement, 0, null);
                            surface.SetFloat(property, 1f);
                            Color32[] enabled = Render(camera, target, readback, surface, placement, 0, null);

                            if (Difference(disabled, enabled) < .00003)
                            {
                                throw new InvalidOperationException("材质调节没有改变GPU输出：" + name + "/" + property);
                            }
                            surface.SetFloat(property, original);
                            controlCount++;
                        }
                    }

                    Object.DestroyImmediate(surface);
                    surface = null;
                }
                return "真实格子离屏预览：" + folder + "；六章" + controlCount + "项材质强度调节均改变GPU输出；四秒格面差异 " + string.Join(", ", results);
            }
            finally
            {
                RenderTexture.active = previous;

                if (surface != null)
                {
                    Object.DestroyImmediate(surface);
                }
                if (placement != null)
                {
                    Object.DestroyImmediate(placement);
                }
                if (readback != null)
                {
                    Object.DestroyImmediate(readback);
                }
                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        private static Color32[] Render(Camera camera, RenderTexture target, Texture2D readback, Material surface, Material placement, float seconds, string path)
        {
            surface.SetFloat("_PreviewTime", seconds);
            // 落点保持同一帧，时间差异只来自章节格面，不能靠落点自身的旋转冒充格面动态。
            placement.SetFloat("_PreviewTime", 0f);
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            readback.Apply();
            Color32[] pixels = readback.GetPixels32();

            if (path != null)
            {
                File.WriteAllBytes(path, readback.EncodeToPNG());
            }
            return pixels;
        }
        private static double Difference(Color32[] first, Color32[] second)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < first.Length; i += 8)
            {
                sum += Math.Abs(first[i].r - second[i].r) + Math.Abs(first[i].g - second[i].g) + Math.Abs(first[i].b - second[i].b);
                count++;
            }
            return sum / (count * 3d * 255d);
        }
    }
}
