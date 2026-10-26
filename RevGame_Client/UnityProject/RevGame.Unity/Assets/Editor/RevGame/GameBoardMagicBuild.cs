using System;
using System.IO;
using RevGame.Core;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>幂等装配棋盘美术资产；只编辑材质和BoardCell，不保存当前场景。AB统一交给GameResourceBuild。</summary>
    public static class GameBoardMagicBuild
    {
        private static readonly string[] Names = { "Chapter01_Tutorial", "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
        /// <summary>纯表现配色，不参与玩法数值；每份材质的强度和速度可在Inspector单独微调。</summary>
        private static readonly Color[] Tints = { new Color(.62f,.57f,.76f), new Color(.62f,.72f,.53f), new Color(.48f,.65f,.78f), new Color(.65f,.55f,.40f), new Color(.72f,.82f,.88f), new Color(.80f,.71f,.55f) };
        private static readonly Color[] Glows = { new Color(.68f,.49f,1f), new Color(.82f,.92f,.38f), new Color(.34f,.85f,1f), new Color(.63f,.76f,.31f), new Color(.7f,.94f,1f), new Color(1f,.8f,.36f) };
        // 这些只改变画面，不改变玩法；留在各章节材质供美术单独调节。
        private static readonly float[] Strengths = { .42f, .42f, .36f, .48f, .50f, .53f };
        private static readonly float[] Speeds = { .45f, .48f, .50f, .62f, .68f, .72f };

        public static string Configure()
        {
            if (Application.isPlaying) { throw new InvalidOperationException("请退出Play再装配棋盘美术"); }
            Directory.CreateDirectory("Assets/GameRes/Material/Board");
            AssetDatabase.Refresh();
            // 同包资源不能仅扩展名不同：框架按资源名索引，保留GUID迁移为明确的材质/Shader名称。
            foreach (string name in Names)
            {
                MoveIfNeeded("Assets/GameShader/Board/" + name + ".shader", "Assets/GameShader/Board/" + name + "_SurfaceShader.shader");
                MoveIfNeeded("Assets/GameRes/Material/Board/" + name + ".mat", "Assets/GameRes/Material/Board/" + name + "_Surface.mat");
            }
            for (int i = 0; i < Names.Length; i++) { ConfigureMaterial(Names[i], Tints[i], Glows[i], Strengths[i], Speeds[i]); }
            Material placement = ConfigureMaterial("PlacementMagic", Color.white, new Color(.18f, .95f, .46f), .9f, 1.1f);
            var frames = new Sprite[4];
            for (int i = 0; i < frames.Length; i++)
            {
                string path = "Assets/GameRes/Art/Status/EyeBlink/EyeBlink_" + i.ToString("D2") + ".png";
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) { throw new InvalidOperationException("缺少眼睛帧：" + path); }
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 128f;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                frames[i] = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (frames[i] == null) { throw new InvalidOperationException("眼睛帧导入失败：" + path); }
            }
            const string cellPath = "Assets/GameRes/Board/Tile/BoardCell.prefab";
            GameObject cell = PrefabUtility.LoadPrefabContents(cellPath);
            try
            {
                SpriteRenderer surface = cell.GetComponent<SpriteRenderer>();
                SpriteRenderer placementView = cell.transform.Find("可落点").GetComponent<SpriteRenderer>();
                // 复用原格子的矩形网格；新Shader不采样纹理，不再显示旧绿色图片。
                placementView.sprite = surface.sprite;
                placementView.drawMode = SpriteDrawMode.Sliced;
                placementView.size = new Vector2(Consts.CellSize, Consts.CellSize);
                placementView.transform.localScale = Vector3.one;
                placementView.color = Color.white;
                placementView.sharedMaterial = placement;
                var eye = cell.transform.Find("眼").GetComponent<SpriteRenderer>();
                eye.sprite = frames[0];
                eye.color = Color.white;
                eye.transform.localScale = Vector3.one * .84f;
                var data = new SerializedObject(cell.GetComponent<BoardCellView>());
                SerializedProperty list = data.FindProperty("eyeBlinkFrames");
                list.arraySize = frames.Length;
                for (int i = 0; i < frames.Length; i++) { list.GetArrayElementAtIndex(i).objectReferenceValue = frames[i]; }
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(cell, cellPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(cell); }
            AssetDatabase.SaveAssets();
            return "已配置六章独立棋盘Shader/材质、绿色程序落点和四帧双眼动画；" + GameResourceBuild.Configure();
        }

        private static void MoveIfNeeded(string from, string to)
        {
            if (AssetDatabase.LoadMainAssetAtPath(from) == null) { return; }
            string error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
        }

        private static Material ConfigureMaterial(string name, Color tint, Color glow, float strength, float speed)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/Board/" + name + (name == "PlacementMagic" ? "" : "_SurfaceShader") + ".shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) { throw new InvalidOperationException("棋盘Shader编译失败：" + name); }
            string path = "Assets/GameRes/Material/Board/" + name + (name == "PlacementMagic" ? "" : "_Surface") + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                // 只有首次创建才写美术默认值。已存在的材质由用户在Inspector持有调参结果，
                // 重跑装配或构建不能把用户调好的强度、配色与速度悄悄重置。
                material = new Material(shader);
                material.SetColor("_Tint", tint);
                material.SetColor("_Glow", glow);
                material.SetFloat("_Strength", strength);
                material.SetFloat("_Speed", speed);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetFloat("_CellSize", Consts.CellSize);
            material.SetFloat("_Origin", Consts.BoardOriginOffset);
            material.SetFloat("_PreviewTime", -1f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
