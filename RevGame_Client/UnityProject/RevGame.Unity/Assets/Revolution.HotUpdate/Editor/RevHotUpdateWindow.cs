// ============================================================
// RevHotUpdateWindow.cs —— 热更清单工具窗口（打完 AB 后的"下一步"）
//
// 位置：Assets\Revolution.HotUpdate\Editor\
//
// 【操作顺序】
//   ① 用框架的打包工具打一次 AB（AssetBundles/&lt;平台&gt;/）
//   ② 打开本窗口：确认平台 / 版本号 → 点「① 生成清单」
//   ③ 点「② 自检」—— 有问题当场拦下（缺包 / 大小不符 / 依赖缺失）
//   ④ 点「打开产物目录」→ 把整个目录上传到 CDN（★ 先传内容、最后传 RevHotManifest.txt）
//
// 【布局原则（面向新手）】
//   · 按"流程步骤"分区（环境 → 配置 → 生成/自检 → 上传），而不是一股脑平铺字段；
//   · 容易出错的两处给实时校验：大版本与 Player Settings 不一致、资源版本格式不对；
//   · 上传顺序用"看得见的清单"而不是只在文字提示里说一句——这是热更最容易翻车的一步。
// ============================================================
using Revolution.Editor;
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.HotUpdate.Editor
{
    /// <summary>热更清单工具窗口（流程化布局：环境 → 配置 → 生成/自检 → 上传）。</summary>
    public sealed class RevHotUpdateWindow : EditorWindow
    {
        private string _outputDir = "";
        private string _appVersion = "1.0.0";
        private string _resVersion = "1.0.0.1";
        private string _channel = "";
        private string _env = "";
        private bool _advancedFoldout;

        private RevHotManifest _manifest;

        // 生成结果（成功后常驻展示，直到下次生成才刷新；不和自检结果混在一起）
        private bool _generateOk;
        private string _generateSummary = "";
        private string _generateError = "";

        // 自检结果（独立展示，点「②自检」才刷新）
        private bool _checkDone;
        private bool _checkPassed;
        private string _checkReport = "";

        private Vector2 _scroll;

        /// <summary>菜单入口。</summary>
        [MenuItem("Revolution.Tools/热更新/热更清单窗口", false, 11)]
        public static void Open()
        {
            var win = GetWindow<RevHotUpdateWindow>("RevHotUpdate");
            win.minSize = new Vector2(460, 560);
        }

        private void OnEnable()
        {
            // 默认输出目录 = 框架打包工具的约定路径（AssetBundles/&lt;当前平台&gt;）
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            _outputDir = ABBuildSetting.GetOutputDir(target).Replace('\\', '/');

            // 默认版本号 = 打包配置里填的那个（ABBuildConfig 是框架的打包配置资产）
            ABBuildConfig config = ABBuildConfig.Instance;
            if (config != null && string.IsNullOrEmpty(config.version) == false)
            {
                _appVersion = config.version;
                _resVersion = config.version + ".1";
            }

            // 产物目录下已经有清单的话（比如隔天重新打开窗口），直接把它加载进来，
            // 避免新手看到"自检"报错"还没有可检查的清单"而不知所措。
            TrySilentReload();
        }

        private void OnGUI()
        {
            DrawHeader();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawEnvironmentSection();
            EditorGUILayout.Space(10);
            DrawConfigSection();
            EditorGUILayout.Space(10);
            DrawGenerateAndCheckSection();
            EditorGUILayout.Space(10);
            DrawUploadSection();

            EditorGUILayout.Space(16);

            EditorGUILayout.EndScrollView();
        }

        // ============================================================
        // 分区①：顶部说明
        // ============================================================
        private void DrawHeader()
        {
            EditorGUILayout.HelpBox(
                "整体流程：先用框架的打包工具打出 AB → 回到这里生成热更清单 → 自检通过后上传 CDN。\n" +
                "上传顺序非常重要：先传所有内容（AB 包 + ResMap），最后才传 RevHotManifest.txt。",
                MessageType.Info);
        }

        // ============================================================
        // 分区②：环境信息（只读，帮新手确认"打的是哪个平台、哪个版本"）
        // ============================================================
        private void DrawEnvironmentSection()
        {
            DrawSectionTitle("环境信息");

            EditorGUILayout.BeginVertical("box");

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string platform = ABBuildSetting.GetPlatformName(target);
            string playerVersion = PlayerSettings.bundleVersion;

            EditorGUILayout.LabelField("当前 Build Target", target + "　（映射平台名：" + platform + "）");
            EditorGUILayout.LabelField("Player Settings 版本号", string.IsNullOrEmpty(playerVersion) ? "（未填写）" : playerVersion);

            EditorGUILayout.HelpBox("切平台或改 Player Settings 版本号后，建议重新打开本窗口（或点下方「重新加载」）。", MessageType.None);

            EditorGUILayout.EndVertical();
        }

        // ============================================================
        // 分区③：配置版本信息（核心输入区）
        // ============================================================
        private void DrawConfigSection()
        {
            DrawSectionTitle("① 配置版本信息");

            EditorGUILayout.BeginVertical("box");

            // ---- 产物目录：文本框 + 浏览按钮，新手不用自己拼路径 ----
            EditorGUILayout.BeginHorizontal();
            _outputDir = EditorGUILayout.TextField("产物目录", _outputDir);
            if (GUILayout.Button("浏览…", GUILayout.Width(60)))
            {
                string picked = EditorUtility.OpenFolderPanel("选择 AB 产物目录", _outputDir, "");
                if (string.IsNullOrEmpty(picked) == false)
                {
                    // 统一转成相对 / 正斜杠风格，和框架其它路径保持一致
                    _outputDir = picked.Replace('\\', '/');
                }
            }
            EditorGUILayout.EndHorizontal();

            if (Directory.Exists(_outputDir) == false)
            {
                EditorGUILayout.HelpBox("目录不存在 —— 请先用框架的打包工具打一次 AB。", MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            // ---- 大版本：与 Player Settings 做一致性校验 ----
            string playerVersion = PlayerSettings.bundleVersion;
            _appVersion = EditorGUILayout.TextField("大版本（AppVersion）", _appVersion);

            bool mismatch = string.IsNullOrEmpty(playerVersion) == false
                            && string.IsNullOrEmpty(_appVersion) == false
                            && _appVersion != playerVersion;

            if (mismatch)
            {
                EditorGUILayout.HelpBox(
                    "与 Player Settings 的版本号（" + playerVersion + "）不一致！\n" +
                    "客户端用 Application.version 做大版本锚定，不一致会导致玩家一更新就被判定为\"需要强更\"。\n" +
                    "只有确实要跨大版本发布（出新包）时，这里才应该和 Player Settings 不同。",
                    MessageType.Warning);

                if (GUILayout.Button("一键同步为 " + playerVersion))
                {
                    _appVersion = playerVersion;
                    _resVersion = playerVersion + ".1";
                }
            }

            EditorGUILayout.Space(4);

            // ---- 资源版本：文本框 + 一键 +1（方便连续发版） ----
            EditorGUILayout.BeginHorizontal();
            _resVersion = EditorGUILayout.TextField("资源版本（ResVersion）", _resVersion);
            if (GUILayout.Button("+1", GUILayout.Width(30)))
            {
                IncrementResVersion();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(" ", "格式建议：大版本.序号（如 " + SafeAppVersion() + ".3），每次发版把末尾序号 +1。", EditorStyles.miniLabel);

            EditorGUILayout.Space(4);

            // ---- 渠道 / 环境：大多数项目用不到，折叠起来减少新手的认知负担 ----
            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "高级选项（渠道 / 环境，大多数项目留空即可）", true);
            if (_advancedFoldout)
            {
                EditorGUI.indentLevel++;
                _channel = EditorGUILayout.TextField("渠道（可空）", _channel);
                _env = EditorGUILayout.TextField("环境（可空）", _env);
                EditorGUILayout.HelpBox("填了会拼进 CDN 路径：{root}/{环境}/{平台}/{大版本}/{渠道}/… ，用于测试桶与正式桶隔离、分渠道发包。", MessageType.None);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        // ============================================================
        // 分区④：生成 + 自检（核心操作区，主按钮高亮）
        // ============================================================
        private void DrawGenerateAndCheckSection()
        {
            DrawSectionTitle("② 生成清单 & 自检");

            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();

            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);     // 主操作：绿色，告诉新手"先点这个"
            if (GUILayout.Button("① 生成清单", GUILayout.Height(32)))
            {
                Generate();
            }
            GUI.backgroundColor = old;

            GUI.enabled = _manifest != null || File.Exists(Path.Combine(_outputDir, "RevHotManifest.txt"));
            if (GUILayout.Button("② 自检", GUILayout.Height(32)))
            {
                Check();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // ---- 生成结果：成功 = 绿色摘要常驻；失败 = 红色原因 ----
            if (_generateOk)
            {
                EditorGUILayout.HelpBox(_generateSummary, MessageType.Info);
            }
            else if (string.IsNullOrEmpty(_generateError) == false)
            {
                EditorGUILayout.HelpBox(_generateError, MessageType.Error);
            }

            // ---- 自检结果：独立展示，不和生成结果互相覆盖 ----
            if (_checkDone)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("自检结果", EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox(_checkReport, _checkPassed ? MessageType.Info : MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        // ============================================================
        // 分区⑤：上传 CDN（可见的顺序清单，防止"顺序搞反"这个最容易翻车的操作）
        // ============================================================
        private void DrawUploadSection()
        {
            DrawSectionTitle("③ 上传 CDN");

            EditorGUILayout.BeginVertical("box");

            bool ready = _generateOk;

            EditorGUILayout.LabelField(ready ? "上传顺序（必须按序，否则玩家可能拿到半新半旧的版本）：" : "先完成「① 生成清单」再上传。", EditorStyles.miniBoldLabel);

            using (new EditorGUI.DisabledScope(ready == false))
            {
                EditorGUILayout.LabelField("  1. 全部 AB 包 + " + RevHotManifestBuilder.ResMapFileName + "（内容，先传）");
                EditorGUILayout.LabelField("  2. RevHotManifest.txt（清单，★ 最后传 —— 它是\"新版本已就绪\"的开关）");
            }

            EditorGUILayout.Space(6);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("打开产物目录", GUILayout.Height(26)))
            {
                string full = Path.GetFullPath(_outputDir);
                if (Directory.Exists(full)) EditorUtility.RevealInFinder(full);
                else EditorUtility.DisplayDialog("RevHotUpdate", "目录不存在：" + full, "好");
            }

            if (GUILayout.Button("重新加载", GUILayout.Height(26)))
            {
                ReloadManifest();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ============================================================
        // 小工具
        // ============================================================
        private static void DrawSectionTitle(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private string SafeAppVersion()
        {
            return string.IsNullOrEmpty(_appVersion) ? "1.0.0" : _appVersion;
        }

        /// <summary>资源版本末段 +1（"1.0.0.3" → "1.0.0.4"）；格式不是"任意.数字"就不动，不瞎猜。</summary>
        private void IncrementResVersion()
        {
            int lastDot = _resVersion.LastIndexOf('.');
            if (lastDot < 0 || lastDot == _resVersion.Length - 1) return;

            string head = _resVersion.Substring(0, lastDot + 1);
            string tail = _resVersion.Substring(lastDot + 1);

            int n;
            if (int.TryParse(tail, out n))
            {
                _resVersion = head + (n + 1);
            }
        }

        private void Generate()
        {
            // 新一轮生成使旧自检与成功状态失效；异常不能继续显示旧清单可上传。
            _checkDone = false;
            _generateOk = false;
            _generateSummary = "";
            _manifest = null;
            try
            {
                string error;
                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
                _manifest = RevHotManifestBuilder.Build(target, _outputDir, _appVersion, _resVersion, _channel, _env, out error);
                if (_manifest == null)
                {
                    _generateError = error;
                    return;
                }

                RevHotManifestBuilder.WriteOutputs(_manifest, _outputDir);
                AssetDatabase.Refresh();

                _generateOk = true;
                _generateError = "";
                _generateSummary =
                    "✓ 已生成（" + DateTime.Now.ToString("HH:mm:ss") + "）\n" +
                    _outputDir + "/RevHotManifest.txt（上传 CDN，最后传）\n" +
                    _outputDir + "/" + RevHotManifestBuilder.BuiltinManifestFileName + "（首包基线，随包体 / StreamingAssets）\n" +
                    "包数量：" + _manifest.Bundles.Count + " · 总大小：" + RevHotProgress.FormatBytes(_manifest.TotalBytes());
            }
            catch (Exception e)
            {
                _manifest = null;
                _generateError = "生成或写出失败：" + e.Message + "。发布目录可能已部分更新，请重新加载并自检后再上传。";
            }
        }

        private void Check()
        {
            if (_manifest == null)
            {
                ReloadManifest();
            }

            if (_manifest == null)
            {
                _checkDone = true;
                _checkPassed = false;
                _checkReport = "还没有可检查的清单：先「① 生成清单」，或确认产物目录里有 RevHotManifest.txt";
                return;
            }

            string problems = RevHotManifestBuilder.SelfCheck(_manifest, _outputDir);
            _checkDone = true;
            _checkPassed = problems.Length == 0;
            _checkReport = _checkPassed ? "✓ 自检通过：AB 与 ResMap 的大小及 SHA-256 一致、映射和依赖有效、平台主包基线可读 —— 可以上传了。" : problems;
        }

        /// <summary>静默重载：OnEnable 时用，加载不到不报错（新手首次打开窗口时目录可能还是空的）。</summary>
        private void TrySilentReload()
        {
            string path = Path.Combine(_outputDir, "RevHotManifest.txt");
            if (File.Exists(path) == false) return;

            string error;
            RevHotManifest loaded = RevHotManifest.Parse(File.ReadAllText(path), out error);
            if (loaded == null) return;

            _manifest = loaded;
            _generateOk = true;
            _generateSummary = "✓ 已加载已有清单：" + loaded.ResVersion + "（" + loaded.Bundles.Count + " 个包 · " + RevHotProgress.FormatBytes(loaded.TotalBytes()) + "）";
        }

        private void ReloadManifest()
        {
            string path = Path.Combine(_outputDir, "RevHotManifest.txt");
            if (File.Exists(path) == false)
            {
                _generateOk = false;
                _generateError = "产物目录里没有 RevHotManifest.txt：先生成一次";
                return;
            }

            string error;
            _manifest = RevHotManifest.Parse(File.ReadAllText(path), out error);

            if (_manifest == null)
            {
                _generateOk = false;
                _generateError = error;
                return;
            }

            _generateOk = true;
            _generateError = "";
            _generateSummary = "✓ 已加载清单：" + _manifest.ResVersion + "（" + _manifest.Bundles.Count + " 个包 · " + RevHotProgress.FormatBytes(_manifest.TotalBytes()) + "）";
        }
    }
}
