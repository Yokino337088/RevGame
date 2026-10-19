// ============================================================
// RevPackageSizePredictorWindow.cs —— Android APK / Windows 发布目录的包体预算窗口。
//
// 位置：Assets\Revolution\Editor\RevPlatform\
//
// 【为什么用真实产物作基准】没有 Player 构建报告时，仅凭原始资源大小无法推导
// Unity 引擎、IL2CPP、纹理导入与 APK 压缩后的体积。要求用户先提供同配置的
// 实际构建产物，再按内置资源变化给出有边界的预测；缺少基准时不伪造总量。
// 【边界】只读当前 Assets，不打包、不切平台、不更改项目设置；基准只存本机
// EditorPrefs，项目之间互不串用。脚本、场景、包版本与 Player Settings 的变化
// 不在增量模型内，必须重新构建并录入基准。
// ============================================================
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public sealed class RevPackageSizePredictorWindow : EditorWindow
    {
        private const double MiB = 1024d * 1024d;
        private const string PrefPrefix = "Revolution.PackageSizePredictor.";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [SerializeField] private int _tab;
        [SerializeField] private Vector2 _scroll;
        [NonSerialized] private AssetSizes _current;
        [NonSerialized] private bool _hasScan;
        [NonSerialized] private string _scanError;
        [NonSerialized] private Baseline _android;
        [NonSerialized] private Baseline _windows;

        private struct AssetSizes
        {
            public long Streaming;
            public long Resources;
            public int StreamingFiles;
            public int ResourceFiles;
        }

        private struct Baseline
        {
            public long Output;
            public long Streaming;
            public long Resources;
            public string CapturedAt;
            public bool IsValid;
        }

        [MenuItem("Revolution.Tools/平台/包体大小预测", false, 43)]
        private static void Open()
        {
            var window = GetWindow<RevPackageSizePredictorWindow>();
            window.titleContent = new GUIContent("包体大小预测");
            window.minSize = new Vector2(570f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _android = LoadBaseline(0);
            _windows = LoadBaseline(1);
        }

        private void OnGUI()
        {
            _tab = GUILayout.Toolbar(_tab, new[] { "Android APK", "Windows 发布目录" }, GUILayout.Height(28f));
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("包体大小预测", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("先构建一次真实的同配置产物作为基准；改动内置资源后点击“重新扫描”，以基准体积 + 资源增量估算。没有基准只显示资源统计，不输出虚构的包体数字。", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重新扫描当前内置资源", GUILayout.Height(28f))) { Scan(); }
                if (GUILayout.Button(_tab == 0 ? "选择基准 APK…" : "选择基准 Windows .exe…", GUILayout.Height(28f))) { SelectBaseline(); }
            }

            if (!_hasScan && string.IsNullOrEmpty(_scanError))
            {
                EditorGUILayout.HelpBox("尚未扫描。首次使用请点击“重新扫描当前内置资源”。", MessageType.None);
            }
            if (!string.IsNullOrEmpty(_scanError)) { EditorGUILayout.HelpBox(_scanError, MessageType.Error); }

            if (_hasScan)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("当前内置资源（原始文件，非最终包内大小）", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("StreamingAssets", Format(_current.Streaming) + "  /  " + _current.StreamingFiles + " 个文件");
                EditorGUILayout.LabelField("Resources", Format(_current.Resources) + "  /  " + _current.ResourceFiles + " 个文件");
                EditorGUILayout.HelpBox("只扫描 Assets 下的 StreamingAssets / Resources（排除 .meta 与 Editor 目录）；Assets 外的 AB 产物不会进入这里的首包统计。场景依赖、包依赖、代码、Unity 原生文件等不参与增量计算。", MessageType.None);
            }

            Baseline baseline = _tab == 0 ? _android : _windows;
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("实测产物基准（本机、当前工程）", EditorStyles.boldLabel);
            if (!baseline.IsValid)
            {
                EditorGUILayout.HelpBox(_tab == 0 ? "未录入基准 APK：请先实际构建一次 APK，再点击“选择基准 APK…”。" : "未录入 Windows 发布目录：请先实际构建，再选择与 _Data 文件夹同级的 .exe。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("基准实测", Format(baseline.Output));
                EditorGUILayout.LabelField("记录时间", baseline.CapturedAt);
                EditorGUILayout.LabelField("基准内置资源", "StreamingAssets " + Format(baseline.Streaming) + " / Resources " + Format(baseline.Resources));
                if (GUILayout.Button("清除本平台基准", GUILayout.Width(140f))) { ClearBaseline(); }
            }

            if (baseline.IsValid && _hasScan)
            {
                DrawPrediction(baseline);
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.HelpBox(_tab == 0
                ? "APK 是 ZIP 容器，StreamingAssets 原始增量暂按 20%～105%、Resources 按 0%～150% 的示意系数估算。这不是实测误差边界：压缩率和纹理导入可能超出范围。AAB、分包、PAD 与商店下载体积不适用。"
                : "Windows 口径为 .exe 所在发布目录的全部文件（未压缩，含 .pdb 等调试文件，如有）；请选择只放一份构建的目录。StreamingAssets 通常原样复制，Resources 导入后仍可能发生较大变化；不是安装器或压缩包的体积。",
                MessageType.Info);
            EditorGUILayout.HelpBox("仅在同一 Unity 版本、目标架构、脚本后端、裁剪级别、纹理设置、构建场景与代码基本不变时参考。模型不覆盖代码/场景/Packages/Player Settings 变更；有这类变更请重新构建并更新基准。系数仅作预算参考，不是保证上下限，最终以实测产物为准。", MessageType.Warning);
            EditorGUILayout.EndScrollView();
        }

        private void DrawPrediction(Baseline baseline)
        {
            double streamingDelta = (_current.Streaming - (double)baseline.Streaming) / MiB;
            double resourcesDelta = (_current.Resources - (double)baseline.Resources) / MiB;
            double output = baseline.Output / MiB;
            double streamingMin = _tab == 0 ? 0.20d : 1d;
            double streamingMax = _tab == 0 ? 1.05d : 1d;
            // ★ 减少资源时同样要交换上下界，否则“预测下限”会高于上限。
            double low = Math.Max(0d, output + Math.Min(streamingDelta * streamingMin, streamingDelta * streamingMax) + Math.Min(0d, resourcesDelta * 1.5d));
            double high = Math.Max(0d, output + Math.Max(streamingDelta * streamingMin, streamingDelta * streamingMax) + Math.Max(0d, resourcesDelta * 1.5d));

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("预测结果（仅内置资源增量）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("StreamingAssets 原始变化", Signed(streamingDelta));
            EditorGUILayout.LabelField("Resources 原始变化", Signed(resourcesDelta));
            EditorGUILayout.LabelField("预计" + (_tab == 0 ? " APK 文件" : " Windows 发布目录"), FormatMiB(low) + " ～ " + FormatMiB(high), EditorStyles.boldLabel);
            if (Math.Abs(streamingDelta) < 0.001d && Math.Abs(resourcesDelta) < 0.001d)
            {
                EditorGUILayout.HelpBox("原始字节总量与基准相同：显示上次实测大小。不代表资源内容没变；等大小替换、脚本或场景变化均无法由此发现。", MessageType.None);
            }
        }

        private void Scan()
        {
            try
            {
                var sizes = new AssetSizes();
                ScanDirectory(Path.GetFullPath("Assets"), false, false, ref sizes);
                _current = sizes;
                _hasScan = true;
                _scanError = null;
            }
            catch (Exception error)
            {
                _hasScan = false;
                _scanError = "资源扫描失败：" + error.Message;
            }
        }

        private static void ScanDirectory(string path, bool inStreaming, bool inResources, ref AssetSizes sizes)
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                var info = new DirectoryInfo(directory);
                // 不跟随链接进入工程外目录；Editor 资产不进入 Player。
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || (!inStreaming && info.Name.Equals("Editor", StringComparison.OrdinalIgnoreCase))) { continue; }
                bool streaming = inStreaming || info.Name.Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase);
                bool resources = inResources || info.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase);
                ScanDirectory(directory, streaming, resources, ref sizes);
            }
            if (!inStreaming && !inResources) { return; }
            foreach (string file in Directory.GetFiles(path))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) { continue; }
                long length = new FileInfo(file).Length;
                if (inStreaming)
                {
                    sizes.Streaming += length;
                    sizes.StreamingFiles++;
                }
                else
                {
                    sizes.Resources += length;
                    sizes.ResourceFiles++;
                }
            }
        }

        private void SelectBaseline()
        {
            // ★ 先扫描，只有取得完整快照才能将“实测包体”和“当时资源”配成一组。
            Scan();
            if (!_hasScan) { return; }
            string file = EditorUtility.OpenFilePanel(_tab == 0 ? "选择实测 APK" : "选择 Windows 发布目录中的 .exe", "", _tab == 0 ? "apk" : "exe");
            if (string.IsNullOrEmpty(file)) { return; }
            try
            {
                long output;
                if (_tab == 0)
                {
                    if (!file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("只能选择 APK 文件，不支持 AAB。"); }
                    output = new FileInfo(file).Length;
                }
                else
                {
                    if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("请选择构建产物的 .exe。"); }
                    string folder = Path.GetDirectoryName(file);
                    string data = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "_Data");
                    if (!Directory.Exists(data)) { throw new InvalidOperationException("缺少与 .exe 同名的 _Data 文件夹，请选择 Windows Player 发布产物。"); }
                    output = SumDirectory(folder);
                }
                if (output <= 0) { throw new InvalidOperationException("选中的产物为空，无法作为基准。"); }
                bool confirmed = EditorUtility.DisplayDialog("确认基准与资源匹配", "当前 Assets 扫描值会和所选产物体积一起保存。请确认该产物就是由当前工程、同一资源与构建配置生成的；若资源已改动，请先重新出包，否则预测差值无效。", "确认录入", "取消");
                if (!confirmed) { return; }
                var baseline = new Baseline
                {
                    Output = output,
                    Streaming = _current.Streaming,
                    Resources = _current.Resources,
                    CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Invariant),
                    IsValid = true
                };
                if (_tab == 0) { _android = baseline; } else { _windows = baseline; }
                SaveBaseline(_tab, baseline);
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("无法录入包体基准", error.Message, "确定");
            }
        }

        private static long SumDirectory(string folder)
        {
            long total = 0;
            foreach (string file in Directory.GetFiles(folder))
            {
                total = checked(total + new FileInfo(file).Length);
            }
            foreach (string directory in Directory.GetDirectories(folder))
            {
                // 避免 Windows 构建目录里的链接把工程外的大目录误算进去。
                if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) { continue; }
                total = checked(total + SumDirectory(directory));
            }
            return total;
        }

        private void ClearBaseline()
        {
            if (!EditorUtility.DisplayDialog("清除基准", "只清除当前工程当前平台在本机的基准记录，不删除任何构建文件。", "清除", "取消")) { return; }
            EditorPrefs.DeleteKey(PrefKey(_tab));
            if (_tab == 0) { _android = default; } else { _windows = default; }
        }

        private static string PrefKey(int tab) => PrefPrefix + Application.dataPath.Replace('\\', '/').ToLowerInvariant() + "." + tab;

        private static void SaveBaseline(int tab, Baseline baseline)
        {
            EditorPrefs.SetString(PrefKey(tab), baseline.Output.ToString(Invariant) + "|" + baseline.Streaming.ToString(Invariant) + "|" + baseline.Resources.ToString(Invariant) + "|" + baseline.CapturedAt);
        }

        private static Baseline LoadBaseline(int tab)
        {
            string[] parts = EditorPrefs.GetString(PrefKey(tab), "").Split('|');
            if (parts.Length != 4 || !long.TryParse(parts[0], NumberStyles.None, Invariant, out long output) || output <= 0 ||
                !long.TryParse(parts[1], NumberStyles.None, Invariant, out long streaming) ||
                !long.TryParse(parts[2], NumberStyles.None, Invariant, out long resources)) { return default; }
            return new Baseline { Output = output, Streaming = streaming, Resources = resources, CapturedAt = parts[3], IsValid = true };
        }

        private static string Signed(double mib) => (mib >= 0d ? "+" : "") + mib.ToString("F2", Invariant) + " MiB";
        private static string Format(long bytes) => FormatMiB(bytes / MiB);
        private static string FormatMiB(double mib) => mib.ToString("F2", Invariant) + " MiB";
    }
}
