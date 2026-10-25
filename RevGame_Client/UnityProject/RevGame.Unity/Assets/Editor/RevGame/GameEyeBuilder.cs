// ============================================================
// GameEyeBuilder.cs —— 将授权卡通双眼接入现有棋盘预制体。
// 仅替换眼标记，不重建场景/UI；图集与AB统一由GameResourceBuild配置，运行时不读磁盘。
// ============================================================
using System;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameEyeBuilder
    {
        public const string EyePath = "Assets/GameRes/Art/Status/CartoonEyes.png";
        public const string CellPath = "Assets/GameRes/Board/Tile/BoardCell.prefab";

        /// <summary>美术导入按Atlas V2约定配置，保留透明边缘；失败直接报路径，不留白图引用。</summary>
        public static string Configure()
        {
            if (Application.isPlaying) { throw new InvalidOperationException("眼位资产只能在编辑态配置"); }
            var importer = AssetImporter.GetAtPath(EyePath) as TextureImporter;
            if (importer == null) { throw new InvalidOperationException("缺少卡通双眼图片：" + EyePath); }
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(EyePath);
            if (sprite == null) { throw new InvalidOperationException("双眼Sprite导入失败"); }
            GameObject root = PrefabUtility.LoadPrefabContents(CellPath);
            try
            {
                var eye = root.transform.Find("眼").GetComponent<SpriteRenderer>();
                eye.sprite = sprite;
                eye.color = Color.white;
                eye.transform.localScale = Vector3.one * 0.76f;
                PrefabUtility.SaveAsPrefabAsset(root, CellPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            return "已替换卡通双眼；请继续统一图集配置与AB构建";
        }
    }
}
