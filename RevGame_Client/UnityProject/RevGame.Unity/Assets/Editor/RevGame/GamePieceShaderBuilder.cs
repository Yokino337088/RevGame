// ============================================================
// GamePieceShaderBuilder.cs —— 为棋子身体装配同一份轻量魔幻材质。
// 只改预制体的bodyRenderer，不给血条/王冠/被围图套颜色，不访问renderer.material制造实例。
// ============================================================
using System;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GamePieceShaderBuilder
    {
        /// <summary>MCP/重建工具共用；幂等保存材质与身体引用，已有强度调参保持不变。</summary>
        public static string Apply()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("请退出Play后保存棋子材质"); }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/Pieces/ArcanePiece.shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) { throw new InvalidOperationException("棋子Shader不存在或编译失败"); }
            string folder = "Assets/GameRes/Material/Pieces";
            if (!AssetDatabase.IsValidFolder(folder)) { AssetDatabase.CreateFolder("Assets/GameRes/Material", "Pieces"); }
            string path = folder + "/ArcanePiece.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader;
            EditorUtility.SetDirty(material);
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/Pieces" }))
            {
                string prefab = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(prefab);
                try
                {
                    // 兼容只带根SpriteRenderer的展示用预制体；实际战斗统一使用SlimePiece。
                    SpriteRenderer rootBody = root.GetComponent<SpriteRenderer>();
                    if (rootBody != null) { rootBody.sharedMaterial = material; }
                    foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null) { continue; }
                        var serialized = new SerializedObject(component);
                        SerializedProperty body = serialized.FindProperty("bodyRenderer");
                        var renderer = body == null ? null : body.objectReferenceValue as SpriteRenderer;
                        if (renderer == null) { continue; }
                        renderer.sharedMaterial = material;
                        count++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, prefab);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            return "已绑定身体渲染器" + count + "个，共享材质1份";
        }
    }
}
