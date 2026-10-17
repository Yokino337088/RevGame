// ============================================================
// ABGUI.cs —— 打包窗口各页签共用的界面小件（图标 / 体积格式 / 定位 / 分隔条 / 分组标题）
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【为什么收拢到一处】
//   以前"体积格式化""定位资源""长路径缩短"在四个视图里各写了一份，格式还不一样（F1 / F2 / 有没有 GB）。
//   放一起之后：六个页签显示口径一致，改一处全生效。
//
// 【图标只用 Unity 内置的】
//   用 EditorGUIUtility.FindTexture 取（取不到返回 null、不刷警告）；深色皮肤优先取 d_ 版本。
//   某个版本的 Unity 恰好没有这张图时，行上只是少一个小图标，功能不受影响。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>打包窗口共用的界面小件</summary>
    internal static class ABGUI
    {
        // ============================================================
        // 图标
        // ============================================================

        private static readonly Dictionary<string, Texture2D> _icons = new Dictionary<string, Texture2D>();

        /// <summary>取内置图标（深色皮肤优先 d_ 版本；取不到返回 null，不报警告）</summary>
        public static Texture2D Icon(string name)
        {
            if (_icons.TryGetValue(name, out Texture2D cached) && cached != null) return cached;

            Texture2D tex = null;
            if (EditorGUIUtility.isProSkin) tex = EditorGUIUtility.FindTexture("d_" + name);
            if (tex == null) tex = EditorGUIUtility.FindTexture(name);

            _icons[name] = tex;
            return tex;
        }

        public static Texture2D FolderIcon => Icon("Folder Icon");
        public static Texture2D BundleIcon => Icon("PreMatCube");
        public static Texture2D WarnIcon => Icon("console.warnicon.sml");
        public static Texture2D ErrorIcon => Icon("console.erroricon.sml");
        public static Texture2D InfoIcon => Icon("console.infoicon.sml");
        public static Texture2D RefreshIcon => Icon("Refresh");
        public static Texture2D PlusIcon => Icon("Toolbar Plus");

        // ============================================================
        // 文字
        // ============================================================

        /// <summary>体积：B / KB / MB / GB（全窗口统一口径）</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / 1024f / 1024f / 1024f:F2} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / 1024f / 1024f:F2} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }

        /// <summary>"Assets/GameRes/Hero/1001.prefab" → "GameRes/Hero/1001.prefab"（列表里少一截）</summary>
        public static string Short(string assetPath)
            => assetPath != null && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                ? assetPath.Substring("Assets/".Length)
                : assetPath;

        // ============================================================
        // 定位 / 选中
        // ============================================================

        /// <summary>在 Project 里闪一下并选中（方便接着在 Inspector 里改）</summary>
        public static void Ping(string assetPath)
        {
            UnityEngine.Object obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj == null) return;

            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
        }

        /// <summary>在 Project 里选中一批资源（给它们批量设标记、批量移动都从这里开始）</summary>
        public static void SelectInProject(IEnumerable<string> assetPaths)
        {
            var objects = new List<UnityEngine.Object>();
            foreach (string path in assetPaths)
            {
                UnityEngine.Object obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj != null) objects.Add(obj);
            }

            if (objects.Count == 0) return;
            Selection.objects = objects.ToArray();
            EditorGUIUtility.PingObject(objects[0]);
        }

        // ============================================================
        // 版面
        // ============================================================

        /// <summary>
        /// 分组标题：可折叠、状态按 key 记在 SessionState（切页签 / 重编译都不丢，重启编辑器回到默认）。
        /// </summary>
        public static bool Foldout(string key, string title, bool defaultOpen = true, string badge = null)
        {
            bool open = SessionState.GetBool("RevAB.Fold." + key, defaultOpen);

            var content = new GUIContent(badge == null ? title : $"{title}    {badge}");
            bool now = EditorGUILayout.Foldout(open, content, true, FoldoutStyle);

            if (now != open) SessionState.SetBool("RevAB.Fold." + key, now);
            return now;
        }

        private static GUIStyle _foldout;

        private static GUIStyle FoldoutStyle
            => _foldout ??= new GUIStyle(EditorStyles.foldoutHeader) { fontStyle = FontStyle.Bold };

        /// <summary>
        /// 竖直分隔条：左右拖动改宽度比例（0~1）。返回新的比例。
        /// ★ 用热区 + 鼠标拖拽自己实现：IMGUI 没有现成的 SplitterGUI 公开接口。
        /// </summary>
        public static float VerticalSplitter(Rect area, float ratio, float minLeft, float minRight, ref bool dragging)
        {
            float x = area.x + area.width * ratio;
            var handle = new Rect(x - 2f, area.y, 5f, area.height);

            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);
            EditorGUI.DrawRect(new Rect(x, area.y, 1f, area.height), SplitterColor);

            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when handle.Contains(e.mousePosition):
                    dragging = true;
                    e.Use();
                    break;

                case EventType.MouseDrag when dragging:
                    float left = Mathf.Clamp(e.mousePosition.x - area.x, minLeft, Mathf.Max(minLeft, area.width - minRight));
                    ratio = area.width > 0 ? left / area.width : ratio;
                    GUI.changed = true;
                    e.Use();
                    break;

                case EventType.MouseUp when dragging:
                    dragging = false;
                    e.Use();
                    break;
            }

            return ratio;
        }

        /// <summary>水平分隔条：上下拖动改高度比例（上半部分占比）</summary>
        public static float HorizontalSplitter(Rect area, float ratio, float minTop, float minBottom, ref bool dragging)
        {
            float y = area.y + area.height * ratio;
            var handle = new Rect(area.x, y - 2f, area.width, 5f);

            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeVertical);
            EditorGUI.DrawRect(new Rect(area.x, y, area.width, 1f), SplitterColor);

            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when handle.Contains(e.mousePosition):
                    dragging = true;
                    e.Use();
                    break;

                case EventType.MouseDrag when dragging:
                    float top = Mathf.Clamp(e.mousePosition.y - area.y, minTop, Mathf.Max(minTop, area.height - minBottom));
                    ratio = area.height > 0 ? top / area.height : ratio;
                    GUI.changed = true;
                    e.Use();
                    break;

                case EventType.MouseUp when dragging:
                    dragging = false;
                    e.Use();
                    break;
            }

            return ratio;
        }

        private static Color SplitterColor
            => EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.6f, 0.6f, 0.6f);

        /// <summary>主操作按钮（打包）：加高 + 着色，一眼能找到</summary>
        public static bool PrimaryButton(GUIContent content, float height, params GUILayoutOption[] options)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);

            var layout = new List<GUILayoutOption>(options) { GUILayout.Height(height) };
            bool clicked = GUILayout.Button(content, PrimaryStyle, layout.ToArray());

            GUI.backgroundColor = old;
            return clicked;
        }

        private static GUIStyle _primary;

        private static GUIStyle PrimaryStyle
            => _primary ??= new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 };

        /// <summary>
        /// 把"会弹对话框 / 做重活"的动作挪到下一帧执行，并立刻结束本次 OnGUI。
        /// ★ 在 OnGUI 中途弹模态框会打乱 GUILayout 的 Begin/End 配对（控制台刷 "EndLayoutGroup" 报错），
        ///   延后到 delayCall 就没有这个问题。
        /// </summary>
        public static void Defer(Action action)
        {
            if (action == null) return;
            EditorApplication.delayCall += () => action();
        }
    }
}
