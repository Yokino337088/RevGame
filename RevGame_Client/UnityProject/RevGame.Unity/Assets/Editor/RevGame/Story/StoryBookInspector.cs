// 在Inspector按“编号+Sprite”编辑表情，避免两个平行数组错位；逐篇/逐句覆盖仍用Unity序列化字段保存。
using RevGame.Data;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>剧情配置的美术编辑入口，只修改用户明确编辑的字段，不重写对白或自动猜测表情。</summary>
    [CustomEditor(typeof(StoryBook))]
    public sealed class StoryBookInspector : UnityEditor.Editor
    {
        /// <summary>标准属性与配对表情映射共用SerializedObject，支持Undo、脏标记和资产持久化。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("立绘优先级：本句覆盖 > 篇章覆盖 > 表情映射。表情编号0使用默认立绘。修改后保存资产并重新构建AB；无需改代码。", MessageType.Info);
            // 显式遍历保留自动展开的篇章/对白，却只展示真正可编辑字段，派生图集列表仍隐藏。
            SerializedProperty iterator = serializedObject.GetIterator();
            bool children = true;
            while (iterator.NextVisible(children))
            {
                children = false;
                if (iterator.name != "m_Script")
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("表情编号与立绘", EditorStyles.boldLabel);
            SerializedProperty ids = serializedObject.FindProperty("ExpressionIds");
            SerializedProperty sprites = serializedObject.FindProperty("GuideFrameReferences");
            if (sprites.arraySize == 0 && ids.arraySize > 0)
            {
                sprites = serializedObject.FindProperty("GuideFrames");
            }
            if (ids.arraySize != sprites.arraySize)
            {
                EditorGUILayout.HelpBox("旧配置的编号与图片数量不同，请校验配置后修正，避免编号指向错误图片。", MessageType.Error);
            }
            for (int i = 0; i < Mathf.Min(ids.arraySize, sprites.arraySize); i++)
            {
                EditorGUILayout.BeginHorizontal();
                ids.GetArrayElementAtIndex(i).intValue = EditorGUILayout.IntField(ids.GetArrayElementAtIndex(i).intValue, GUILayout.Width(64));
                SerializedProperty sprite = sprites.GetArrayElementAtIndex(i);
                EditorGUILayout.PropertyField(sprite, GUIContent.none);
                if (GUILayout.Button("删除", GUILayout.Width(45)))
                {
                    // 清空Object引用后删一次，避免Unity第一次Delete只清引用而保留数组长度。
                    if (sprite.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        sprite.objectReferenceValue = null;
                    }
                    sprites.DeleteArrayElementAtIndex(i);
                    ids.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("添加表情映射"))
            {
                int index = ids.arraySize;
                ids.arraySize++;
                sprites.arraySize++;
                ids.GetArrayElementAtIndex(index).intValue = 1;
                var sprite = sprites.GetArrayElementAtIndex(index);
                if (sprite.propertyType == SerializedPropertyType.ObjectReference)
                {
                    sprite.objectReferenceValue = null;
                }
                else
                {
                    GamePortraitReferenceDrawer.Write(sprite, default);
                }
            }
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("校验剧情配置"))
            {
                try
                {
                    ((StoryBook)target).Validate();
                    EditorUtility.DisplayDialog("剧情配置", "校验通过。请保存并重新构建资源。", "确定");
                }
                catch (System.Exception error)
                {
                    EditorUtility.DisplayDialog("剧情配置错误", error.Message, "确定");
                }
            }
        }
    }

    /// <summary>用 Sprite 对象选择器编辑引用位置，SO 只序列化文本。</summary>
    [CustomPropertyDrawer(typeof(GamePortraitReference))]
    public sealed class GamePortraitReferenceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            try
            {
                var reference = new GamePortraitReference
                {
                    AssetGuid = property.FindPropertyRelative(nameof(GamePortraitReference.AssetGuid)).stringValue,
                    LocalId = property.FindPropertyRelative(nameof(GamePortraitReference.LocalId)).longValue
                };
                var sprite = StoryBook.EditorSprite(reference);
                EditorGUI.BeginChangeCheck();
                var selected = (Sprite)EditorGUI.ObjectField(position, label, sprite, typeof(Sprite), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Write(property, GameStoryPortraitAssets.ReferenceFor(selected));
                }
            }
            finally
            {
                EditorGUI.EndProperty();
            }
        }

        internal static void Write(SerializedProperty property, GamePortraitReference reference)
        {
            property.FindPropertyRelative(nameof(GamePortraitReference.AssetGuid)).stringValue = reference.AssetGuid ?? "";
            property.FindPropertyRelative(nameof(GamePortraitReference.LocalId)).longValue = reference.LocalId;
            property.FindPropertyRelative(nameof(GamePortraitReference.SpriteName)).stringValue = reference.SpriteName ?? "";
            property.FindPropertyRelative(nameof(GamePortraitReference.Atlas)).stringValue = reference.Atlas ?? "";
        }
    }
}
