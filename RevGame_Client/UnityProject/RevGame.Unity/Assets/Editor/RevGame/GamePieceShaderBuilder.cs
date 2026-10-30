// ============================================================
// GamePieceShaderBuilder.cs —— 为棋子身体装配同一份轻量魔幻材质。
// 只改预制体的bodyRenderer，不给血条/王冠/被围图套颜色，不访问renderer.material制造实例。
// ============================================================
using System;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GamePieceShaderBuilder
    {
        /// <summary>MCP/重建工具共用；幂等保存材质与身体引用，已有强度调参保持不变。</summary>
        public static string Apply()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请退出Play后保存棋子材质");
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/Pieces/ArcanePiece.shader");

            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException("棋子Shader不存在或编译失败");
            }
            string folder = "Assets/GameRes/Material/Pieces";

            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/Material", "Pieces");
            }
            string path = folder + "/ArcanePiece.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            EditorUtility.SetDirty(material);
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/Pieces" }))
            {
                string prefab = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(prefab);
                try
                {
                    // 重建材质前先落实身体层级，旧预制体不能在下一次装配时又把攻击缩放写到占格根节点上。
                    if (root.GetComponent<PieceView>() != null)
                    {
                        EnsureBodyChild(root);
                    }
                    // 兼容只带根SpriteRenderer的展示用预制体；实际战斗统一使用SlimePiece。
                    SpriteRenderer rootBody = root.GetComponent<SpriteRenderer>();

                    if (rootBody != null)
                    {
                        rootBody.sharedMaterial = material;
                    }
                    foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null)
                        {
                            continue;
                        }
                        var serialized = new SerializedObject(component);
                        SerializedProperty body = serialized.FindProperty("bodyRenderer");
                        var renderer = body == null ? null : body.objectReferenceValue as SpriteRenderer;

                        if (renderer == null)
                        {
                            continue;
                        }
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
        /// <summary>
        /// 保证真实棋子身体位于独立的 Body 子节点。根 Transform 只负责占格大小与位置；
        /// 身体压缩/回弹只能改子节点，否则血条、标识和大棋尺寸都会跟着变形。
        /// 调用方传入 LoadPrefabContents 得到的临时预制体根，并负责保存、卸载；这里不写磁盘、不运行玩法。
        /// 已是正确子节点时原样返回；缺少身体引用则明确报错，禁止保存一个看不见的棋子。
        /// </summary>
        public static SpriteRenderer EnsureBodyChild(GameObject root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            PieceView view = root.GetComponent<PieceView>();

            if (view == null)
            {
                throw new InvalidOperationException("身体层级装配需要棋子根节点上的 PieceView");
            }
            var serialized = new SerializedObject(view);
            SerializedProperty reference = serialized.FindProperty("bodyRenderer");
            SpriteRenderer previous = reference == null ? null : reference.objectReferenceValue as SpriteRenderer;

            if (previous == null)
            {
                throw new InvalidOperationException("棋子缺少 bodyRenderer 引用：" + root.name);
            }
            if (previous.transform != root.transform)
            {
                if (!previous.transform.IsChildOf(root.transform))
                {
                    throw new InvalidOperationException("棋子身体引用必须属于自己的预制体层级");
                }
                return previous;
            }
            Transform child = root.transform.Find("Body");

            if (child == null)
            {
                child = new GameObject("Body").transform;
                child.SetParent(root.transform, false);
            }
            // 子节点使用单位局部变换，因此迁移前后画面保持同样大小/位置，根的占格缩放也完整保留。
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
            child.localScale = Vector3.one;
            SpriteRenderer body = child.GetComponent<SpriteRenderer>();

            if (body == null)
            {
                body = child.gameObject.AddComponent<SpriteRenderer>();
            }
            // 完整复制原渲染器的序列化设置：Sprite、共享材质、颜色、排序与裁剪参数都不能在修层级时丢失。
            EditorUtility.CopySerialized(previous, body);
            reference.objectReferenceValue = body;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // ★ 先改引用再移除旧组件。否则 PieceView 会留下 Missing 引用，下次 Bind 无法显示序列帧。
            UnityEngine.Object.DestroyImmediate(previous);
            return body;
        }
    }
}
