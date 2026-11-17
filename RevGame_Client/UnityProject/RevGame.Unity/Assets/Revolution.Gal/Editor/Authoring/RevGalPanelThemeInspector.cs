using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>按部位编辑同一份主题。切片选择器读取图集源素材，未打包时也可选择。</summary>
    [CustomEditor(typeof(RevGalPanelTheme))]
    internal sealed class RevGalPanelThemeInspector : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.Add(new PropertyField(serializedObject.FindProperty("atlas"), "共用 Sprite Atlas"));
            root.Add(new PropertyField(serializedObject.FindProperty("font"), "共用字体"));
            root.Add(new HelpBox("布局相对父节点。关闭覆盖时使用 Prefab 布局。主题改动在下次打开剧情面板时生效。",
                HelpBoxMessageType.Info));
            var parts = serializedObject.FindProperty("parts");
            for (int index = 0; index < parts.arraySize; index++)
            {
                var style = parts.GetArrayElementAtIndex(index);
                var part = style.FindPropertyRelative("Part");
                var role = (RevGalThemePart)part.intValue;
                var foldout = new Foldout { value = false, text = part.enumDisplayNames[part.enumValueIndex] };
                root.Add(foldout);
                if (RevGalPartStyle.UsesRectLayout(role))
                {
                    foldout.Add(new PropertyField(style.FindPropertyRelative("Layout"), "布局"));
                }
                if (role == RevGalThemePart.ChoiceButton)
                {
                    foldout.Add(new PropertyField(style.FindPropertyRelative("ChoiceHeight"), "选项高度"));
                }
                if (RevGalPartStyle.UsesThemeSprite(role))
                {
                    foldout.Add(new PropertyField(style.FindPropertyRelative("Sprite"), "单独指定图片"));
                    foldout.Add(new PropertyField(style.FindPropertyRelative("AtlasSprite"), "图集切片名"));
                    foldout.Add(new Button(() => PickSprite(style)) { text = "从图集中选图" });
                    foldout.Add(new PropertyField(style.FindPropertyRelative("Sliced"), "九宫格"));
                }
                foldout.Add(new PropertyField(style.FindPropertyRelative("OverrideColor"), "覆盖颜色"));
                foldout.Add(new PropertyField(style.FindPropertyRelative("Tint"), "颜色"));
            }
            return root;
        }

        private void PickSprite(SerializedProperty style)
        {
            var theme = (RevGalPanelTheme)target;
            var menu = new GenericMenu();
            var names = SpriteNames(theme);
            if (names.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("共用图集中没有可选 Sprite"));
            }
            var sprite = style.FindPropertyRelative("AtlasSprite");
            foreach (string name in names)
            {
                menu.AddItem(new GUIContent(name), sprite.stringValue == name, () =>
                {
                    serializedObject.Update();
                    sprite.stringValue = name;
                    style.FindPropertyRelative("Sprite").objectReferenceValue = null;
                    serializedObject.ApplyModifiedProperties();
                });
            }
            menu.ShowAsContext();
        }

        private static SortedSet<string> SpriteNames(RevGalPanelTheme theme)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var atlas = theme.Atlas;
            if (atlas == null)
            {
                return names;
            }
            var source = atlas.GetMasterAtlas() ?? atlas;
            foreach (var packable in source.GetPackables())
            {
                if (packable is Sprite sprite)
                {
                    names.Add(sprite.name);
                    continue;
                }
                string path = AssetDatabase.GetAssetPath(packable);
                var paths = AssetDatabase.IsValidFolder(path)
                    ? AssetDatabase.FindAssets("t:Texture2D", new[] { path }).Select(AssetDatabase.GUIDToAssetPath)
                    : new[] { path };
                foreach (string assetPath in paths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    names.UnionWith(AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>().Select(image => image.name));
                }
            }
            return names;
        }
    }
}
