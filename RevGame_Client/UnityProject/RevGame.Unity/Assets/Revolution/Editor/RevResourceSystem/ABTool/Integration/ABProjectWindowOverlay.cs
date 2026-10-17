// ============================================================
// ABProjectWindowOverlay.cs —— 在 Project 窗口里直接标出"这个资源属于哪个 AB 包"
//
// 位置：Editor\RevResourceSystem\ABTool\Integration\
//
// 【要解决的问题】
//   分包标记藏在每个资源的 .meta 里：想知道"这个东西进没进包、进的哪个包"，
//   得一个个点开 Inspector 看右下角的 AssetBundle 栏 —— 分包乱不乱，这样是看不出来的。
//   这里把它画到 Project 窗口每行的右端，扫一眼就知道。
//
// 【挂在哪个 API 上】
//   EditorApplication.projectWindowItemOnGUI（Unity 官方给的"往 Project 窗口行里画东西"的口子）。
//   回调只由 Project 窗口自己发起，窗口不可见时不会被调用 —— 不做无用功。
//
// 【怎么取"这个资源属于哪个包"】
//   AssetDatabase.GetImplicitAssetBundleName(path)：**官方接口，且会把"给文件夹标标记、子文件继承"算进来**。
//   自己拼文件夹继承链当然也能算，但没必要 —— 而且容易和 Unity 的实际规则走偏。
//
// 【为什么要有缓存】
//   一次重绘 = 每个可见行都要问一次"属于哪个包"（几十次）。所以按路径缓存，
//   只在"确实可能变了"的时候清空：
//     ① ABMarkerWatcher.Revision 变了（它抓变更比这里更准、更早）→ 立刻清；
//     ② 兜底：每 1 秒比一次"包名清单指纹"，抓 Revision 抓不到的那类改动
//        （比如自动同步关了、或只是包内资源在包之间挪了位置）。
//
// 【显式 vs 继承：为什么要分开显示】
//   自己写了包名的资源，在分包浏览页签里可以单独「移出」；
//   靠父文件夹继承来的，"单独移不掉"（归文件夹管）。两者用不同颜色 + 记号区分，
//   排查"为什么这个资源在包里"时能少绕一步。
//
// 【开关】
//   分包浏览页签顶部的「Project 显示包名」。默认开；关掉后这个回调立刻什么都不做，
//   一个多余的 AssetDatabase 查询都不会发生。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>Project 窗口的分包角标（纯显示，绝不改动任何标记）</summary>
    [InitializeOnLoad]
    internal static class ABProjectWindowOverlay
    {
        /// <summary>开关的持久化键（和 ABMarkerWatcher 同样的做法：关了 Unity 再开还记得）</summary>
        private const string EnabledKey = "Revolution.RevAB.ProjectBadge";

        /// <summary>更名前（LiteAB）的键：第一次读时把旧值迁过来</summary>
        private const string LegacyEnabledKey = "Revolution.LiteAB.ProjectBadge";

        /// <summary>兜底指纹的比对间隔（秒）：足够追上外部改动，又不至于每帧分配字符串</summary>
        private const double PollInterval = 1.0;

        /// <summary>行高超过它 = 图标网格视图（画上去会糊在图标上）→ 不画</summary>
        private const float MaxRowHeight = 30f;

        /// <summary>角标最多占行宽的比例：左边是资源名，不能盖住</summary>
        private const float MaxWidthRatio = 0.45f;
        private const float MinWidth = 42f;
        private const float MaxWidth = 220f;

        private static readonly Color ExplicitColor = new Color(0.55f, 0.78f, 1.00f);   // 自己标的
        private static readonly Color InheritedColor = new Color(0.62f, 0.62f, 0.62f);  // 文件夹继承来的

        private static bool _enabled;

        /// <summary>自己一份样式：要改颜色 + 裁剪（不能动 EditorStyles 的共享实例）</summary>
        private static GUIStyle _style;

        /// <summary>
        /// 角标样式（懒建）：
        ///   · 必须 new 一份，因为要改颜色 + 把裁剪设成 Clip（默认的 Overflow 会往左溢出、盖住资源名）；
        ///   · 懒建而不是静态构造里建：域重载（每次脚本编译）后 GUIStyle 会失效，用之前现建最稳。
        /// </summary>
        private static GUIStyle Style
        {
            get
            {
                if (_style == null)
                {
                    _style = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight,
                        clipping = TextClipping.Clip
                    };
                }
                return _style;
            }
        }

        /// <summary>路径 → 生效包名（空串 = 没进任何包；空串也要缓存，否则没标包的行会反复查询）</summary>
        private static readonly Dictionary<string, string> _bundles = new Dictionary<string, string>();

        private static readonly HashSet<string> _inherited = new HashSet<string>();

        private static int _revision = -1;
        private static double _nextPoll;
        private static string _signature = "";

        static ABProjectWindowOverlay()
        {
            _enabled = ABBuildSetting.GetPrefBool(EnabledKey, LegacyEnabledKey, true);
            EditorApplication.projectWindowItemOnGUI += OnItemGUI;
        }

        // ==================== 开关 ====================

        internal static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;

                _enabled = value;
                EditorPrefs.SetBool(EnabledKey, value);

                if (value) Clear();                        // 刚打开：清掉旧缓存，重新读
                EditorApplication.RepaintProjectWindow();  // 立刻生效，不用手动点刷新
            }
        }

        // ==================== 画角标 ====================

        private static void OnItemGUI(string guid, Rect rect)
        {
            if (!_enabled) return;

            // 网格（图标）视图：行是一块正方形，右边没有"空白"可放文字 → 跳过
            if (rect.height > MaxRowHeight) return;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;

            EnsureFresh();

            if (!_bundles.TryGetValue(path, out string bundle)) bundle = Query(path);
            if (string.IsNullOrEmpty(bundle)) return;                 // 没进任何包：什么都不画

            float width = Mathf.Clamp(rect.width * MaxWidthRatio, MinWidth, MaxWidth);
            if (width < MinWidth) return;                             // 行太窄，塞不下

            bool inherited = _inherited.Contains(path);

            GUIStyle style = Style;
            style.normal.textColor = inherited ? InheritedColor : ExplicitColor;

            var area = new Rect(rect.xMax - width, rect.y, width, rect.height);
            string text = (inherited ? "▫ " : "▪ ") + bundle;
            string tip = inherited
                ? $"属于 AB 包：{bundle}\n（继承自父文件夹的标记 —— 单独「移出」移不掉，要改文件夹）"
                : $"属于 AB 包：{bundle}\n（这个资源自己标的标记）";

            GUI.Label(area, new GUIContent(text, tip), style);
        }

        private static void EnsureFresh()
        {
            // ① 监视器抓到变更（比这里的兜底轮询更早更准）
            int revision = ABMarkerWatcher.Revision;
            if (revision != _revision)
            {
                _revision = revision;
                Clear();
                EditorApplication.RepaintProjectWindow();
            }

            // ② 兜底轮询：自动同步关掉时 Revision 不会动，靠指纹也能追上
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;

            string signature = Signature();
            if (signature == _signature) return;

            _signature = signature;
            Clear();
        }

        /// <summary>读一次"这个资源属于哪个包"并缓存（顺带记下它是不是继承来的）</summary>
        private static string Query(string path)
        {
            string implicitName = AssetDatabase.GetImplicitAssetBundleName(path);
            if (string.IsNullOrEmpty(implicitName))
            {
                _bundles[path] = "";
                return "";
            }

            string variant = AssetDatabase.GetImplicitAssetBundleVariantName(path);
            string effective = string.IsNullOrEmpty(variant) ? implicitName : implicitName + "." + variant;

            // 显式标记 = 资源自己写着包名；为空却是"生效包名非空" → 必定来自父文件夹
            AssetImporter importer = AssetImporter.GetAtPath(path);
            bool explicitMark = importer != null && !string.IsNullOrEmpty(importer.assetBundleName);

            if (explicitMark) _inherited.Remove(path);
            else _inherited.Add(path);

            _bundles[path] = effective;
            return effective;
        }

        private static void Clear()
        {
            _bundles.Clear();
            _inherited.Clear();
        }

        /// <summary>包名清单指纹（排序后拼接，顺序稳定 → 不会"其实没变却报变了"）</summary>
        private static string Signature()
        {
            string[] names = AssetDatabase.GetAllAssetBundleNames();
            if (names == null || names.Length == 0) return "";

            Array.Sort(names, StringComparer.Ordinal);

            var sb = new StringBuilder(names.Length * 12);
            foreach (string name in names) sb.Append(name).Append('\n');
            return sb.ToString();
        }
    }
}
