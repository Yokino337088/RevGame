using UnityEditor;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>工作台、主题设计器共享的编辑器样式，按资产查找以支持 Assets 或 UPM 安装。</summary>
    internal static class RevGalEditorStyles
    {
        public static void Apply(VisualElement root)
        {
            root.AddToClassList("gal-shell");
            foreach (string guid in AssetDatabase.FindAssets("RevGalEditor t:StyleSheet"))
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (sheet != null)
                {
                    root.styleSheets.Add(sheet);
                    break;
                }
            }
        }
    }
}
