// ============================================================
// GameAtlasPreviewAutomation.cs —— 编辑态 UI 白图诊断与修复（新手先读这段）
//
// 【为什么会白】工程的 Sprite Packer 是“Sprite Atlas V2 - 启用”：编辑态里，进了图集的 Sprite 也从图集打包结果取纹理。
// 图集打包结果缓存在 Library 里；切图（改九宫格切割线）、按另一个平台构建 AB、重新导入图集之后，
// 编辑态缓存可能还没按当前平台重新打包，预制体编辑器里对应的 Image 就会显示成白块。运行时 AB 里的图集不受影响。
// 【做什么】按当前编辑器目标平台把全部图集重新打包一次，然后逐个扫描 UI 预制体里的 Image：
// 没有 Sprite（真正的白膜）或 Sprite 取不到打包纹理的都列出来。只读扫描，不改任何预制体。
// UGUI 程序集不在本 asmdef 引用里，所以用 SerializedObject 读 m_Sprite，对任何带 Sprite 字段的组件都适用。
// ============================================================
using System;
using System.Collections.Generic;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameAtlasPreviewAutomation
    {
        [Serializable] private sealed class Result
        {
            public string target;
            public int prefabs, images, missingSprite, missingTexture;
            public List<string> problems = new List<string>();
        }

        [RevAutomationAction("game.atlas.preview-repack", Environment = "edit", Keys = new string[0], Description = "按当前编辑器平台重打全部Sprite Atlas V2并扫描UI预制体白图，只读不改预制体")]
        public static object Repack(RevAutomationRequest request)
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            SpriteAtlasUtility.PackAllAtlases(target, false);
            var result = new Result { target = target.ToString() };
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/UI" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                result.prefabs++;
                foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null)
                    {
                        continue;
                    }
                    var serialized = new SerializedObject(behaviour);
                    SerializedProperty sprite = serialized.FindProperty("m_Sprite");
                    // 只统计 UGUI Image（带 m_Sprite 且是 Graphic 的组件）；RawImage 等没有这个字段。
                    if (sprite == null || serialized.FindProperty("m_Color") == null)
                    {
                        continue;
                    }
                    result.images++;
                    var value = sprite.objectReferenceValue as Sprite;
                    if (value == null)
                    {
                        // 程序星雾背景等是刻意不用 Sprite 的材质绘制，不算白膜。
                        SerializedProperty material = serialized.FindProperty("m_Material");
                        if (material == null || material.objectReferenceValue == null)
                        {
                            result.missingSprite++;
                            result.problems.Add("无Sprite：" + path + " / " + behaviour.name);
                        }
                        continue;
                    }
                    Texture texture = null;
                    string reason = "";
                    try
                    {
                        texture = UnityEditor.Sprites.SpriteUtility.GetSpriteTexture(value, true);
                    }
                    catch (ArgumentException error)
                    {
                        // “Sprite is not packed”：这张图属于某个图集，但编辑态的打包结果里没有它 → 预制体里显示白块。
                        reason = error.Message;
                    }
                    if (texture == null)
                    {
                        result.missingTexture++;
                        result.problems.Add("取不到图集纹理：" + path + " / " + behaviour.name + " → " + value.name + " " + reason);
                    }
                }
            }
            return result;
        }
    }
}
