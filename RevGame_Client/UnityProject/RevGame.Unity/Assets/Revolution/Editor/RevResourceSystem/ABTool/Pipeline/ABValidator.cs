// ============================================================
// ABValidator.cs —— 打包前校验
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【设计理念】"能在编辑器里报错，就绝不留到运行时。"
//   运行时发现资源名写错 / 包为空，排查成本极高。
//   在打包前用几毫秒扫一遍，把问题拦在源头。
//
// 【校验项】
//   errors（阻止打包）：命名非法、逻辑路径冲突、同包重名、空包、漏标（没有 AB 标记）
//   warnings（不阻止）  ：文件名含大写、被排除的后缀、单资源包、大资源
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Revolution.Editor
{
    public class ABValidateResult
    {
        public readonly List<string> errors = new List<string>();     // 必须修，否则不许打包
        public readonly List<string> warnings = new List<string>();   // 建议修，仅提示

        public int bundleCount;    // 包数量
        public int assetCount;     // 资源数量
        public long totalBytes;    // 资源总字节（粗略估算，未压缩）

        /// <summary>是否可以继续打包</summary>
        public bool CanBuild => errors.Count == 0;
    }

    public static class ABValidator
    {
        // 只允许：字母 数字 下划线 短横线 斜杠 点
        // 不允许中文 / 空格 / 括号 —— 它们在不同平台/系统中容易被转义或截断
        private static readonly Regex LegalName =
            new Regex(@"^[A-Za-z0-9_\-/\.]+$", RegexOptions.Compiled);

        /// <summary>单个资源超过 8MB 就告警（通常意味着该拆分或该压缩）</summary>
        private const long BigAssetBytes = 8 * 1024 * 1024;

        public static ABValidateResult Validate(ABCollectResult collect)
        {
            var result = new ABValidateResult
            {
                bundleCount = collect.bundleToAssets.Count,
                assetCount = collect.logicToAssetPath.Count
            };

            // ---------- ① 命名规范 ----------
            foreach (string logic in collect.logicToAssetPath.Keys)
            {
                if (!LegalName.IsMatch(logic))
                {
                    result.errors.Add($"命名非法（含中文/空格/特殊符号）：{logic}");
                    continue;
                }

                // 文件名含大写：Windows 不区分大小写、Linux/Android 区分，
                // 同一份代码在不同平台可能引用到不同文件，建议全小写。
                if (Path.GetFileName(logic).Any(char.IsUpper))
                    result.warnings.Add($"文件名含大写，建议全小写（跨平台安全）：{logic}");
            }

            // ---------- ①.5 逻辑路径冲突 ----------
            // 最典型：Hero/1001.prefab 与 Hero/1001.png 都算成 "Hero/1001"
            foreach (string dup in collect.duplicateLogics)
                result.errors.Add($"逻辑路径冲突（同名不同扩展名的资源会撞车，请改名）：{dup}");

            // ---------- ①.6 同包内资源重名 ----------
            // 例：包 "ui" 里既有 A/Icon.png 又有 B/Icon.png → LoadAsset("Icon") 取到哪个不确定
            foreach (string dup in collect.duplicateAssets)
                result.errors.Add($"同一 AB 包内有重名资源：{dup}（请改名，或把它们拆到不同包）");

            // ---------- ①.7 空包 ----------
            foreach (string empty in collect.emptyBundles)
                result.errors.Add($"空包（有包名但没有任何资源，通常是资源被删/改名残留）：{empty}");

            // ---------- ①.8 被排除规则跳过、不会进包的资源 ----------
            // ★ 这类问题最咬人：资源明明在、AB 名也标了，却因为"后缀被排除"而不进 ResMap，
            //   运行时只表现为 PathNotMapped，很难往"后缀"上想（数据表就是 .txt，最容易中招）。
            //   所以必须让它出声 —— 绝不能静默丢掉。
            if (collect.skipped.Count > 0)
            {
                const int Show = 5;
                result.warnings.Add(
                    $"有 {collect.skipped.Count} 个资源被 excludeExtensions 排除，不会进包" +
                    $"（运行时必然 PathNotMapped）：" +
                    string.Join("，", collect.skipped.Take(Show)) +
                    (collect.skipped.Count > Show ? " …" : ""));
            }

            // ---------- ①.9 漏标（资源根目录下没有任何 AB 标记）----------
            // ★ 手动分包模式下最典型的失误：资源明明在工程里、编辑器直读也读得到，
            //   却因为没标 AB 名而不进任何包 —— 运行时（AB / 真机）只会 FileNotExist。
            //   "编辑器里好好的、真机才炸"正是这类问题最坑的地方，必须在打包前拦住。
            //
            // 【为什么能关】确实会有故意不进包的资源（源文件、参考图等）。
            //   处理姿势：加进 excludeFolders / excludeExtensions（推荐，精细），
            //   或者把 checkUnmarkedAssets 关掉（整个检查都不要了）。
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            // ↑ 只读加载，不用 ABBuildConfig.Instance：后者会顺手创建配置资产，校验这种只读动作不该有副作用
            if (cfg != null && cfg.checkUnmarkedAssets && collect.unmarkedAssets.Count > 0)
            {
                const int Show = 5;
                result.errors.Add(
                    $"有 {collect.unmarkedAssets.Count} 个资源没有 AB 标记，不会进任何包（运行时必然加载失败）：" +
                    string.Join("，", collect.unmarkedAssets.Take(Show)) +
                    (collect.unmarkedAssets.Count > Show ? " …" : "") +
                    "\n→ 去 Project 里给它们（或它们所在的文件夹）设 AssetBundle 名；" +
                    "确实不该进包的，把目录加进 excludeFolders、或后缀加进 excludeExtensions。" +
                    "\n（详细清单见「检查」页签 ①，那里可以一键全部加入一个包）");
            }

            // ---------- ② 空包 / 单资源包 ----------
            foreach (var kv in collect.bundleToAssets)
            {
                if (kv.Value.Count == 0)
                    result.errors.Add($"空包（标记了但没有资源）：{kv.Key}");
                else if (kv.Value.Count == 1)
                    result.warnings.Add($"单资源包（{kv.Key} 只有 1 个资源），合并可减少包数量");
            }

            // ---------- ③ 大资源告警 + ④ 总量统计 ----------
            foreach (var kv in collect.logicToAssetPath)
            {
                string abs = Path.GetFullPath(kv.Value);
                if (!File.Exists(abs)) continue;                  // 资源被删但清单还在，跳过

                long size = new FileInfo(abs).Length;
                result.totalBytes += size;

                if (size > BigAssetBytes)
                    result.warnings.Add($"大资源 {size / 1024 / 1024}MB（{kv.Key}），建议压缩或拆分");
            }

            return result;
        }

        /// <summary>
        /// Android 包里的材质即使随 Prefab 进入 AB，它引用的 Shader 也可能既不在 AB 依赖里，
        /// 又被 Player 构建剔除；此时 Prefab 能加载，却会整片变紫。
        /// 打包前检查显式材质引用：有项目 Shader 资源时 Unity 可将其作为 AB 依赖打入包，
        /// 或由 Graphics → Always Included Shaders 保证进入 Player；没有可打包资源的内置 Shader
        /// 则必须由 Player 包含。这里只检查，不擅自修改项目图形设置。
        /// </summary>
        public static void ValidateAndroidShaders(ABCollectResult collect, ABValidateResult result, BuildTarget target)
        {
            if (target != BuildTarget.Android || !result.CanBuild) return;

            UnityEngine.Object graphicsSettings = GraphicsSettings.GetGraphicsSettings();
            if (graphicsSettings == null)
            {
                result.errors.Add("无法读取 Graphics Settings，不能确认 Android Shader 是否进入 Player，已中止打包。");
                return;
            }
            var settings = new SerializedObject(graphicsSettings);
            SerializedProperty included = settings.FindProperty("m_AlwaysIncludedShaders");
            if (included == null || !included.isArray)
            {
                result.errors.Add("无法读取 Graphics Settings 的 Always Included Shaders；为防止 Android 真机材质变紫，已中止打包。请检查当前 Unity 版本的 Graphics Settings。");
                return;
            }

            var includedShaders = new HashSet<Shader>();
            for (int i = 0; i < included.arraySize; i++)
            {
                Shader shader = included.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader != null) includedShaders.Add(shader);
            }

            var checkedAssets = new HashSet<string>(StringComparer.Ordinal);
            var checkedMaterials = new HashSet<Material>();
            foreach (string assetPath in collect.logicToAssetPath.Values)
            {
                // 包含没有单独打 AB 的依赖材质、FBX 内嵌材质和嵌套 Prefab。
                foreach (string dep in AssetDatabase.GetDependencies(assetPath, true))
                {
                    if (!checkedAssets.Add(dep)) continue;
                    string ext = Path.GetExtension(dep).ToLowerInvariant();
                    if (ext == ".mat" || ext == ".fbx")
                    {
                        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(dep))
                            if (asset is Material material)
                                CheckMaterial(material, dep, includedShaders, checkedMaterials, result);
                    }
                    if (ext != ".prefab") continue;
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dep);
                    if (prefab == null) continue;
                    foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                        foreach (Material material in renderer.sharedMaterials)
                            CheckMaterial(material, dep, includedShaders, checkedMaterials, result);
                    foreach (UnityEngine.UI.Graphic graphic in prefab.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    {
                        // UGUI 没有显式材质时使用引擎自己的默认材质；不要把它误判成需业务手工包含的 Shader。
                        Material uiMaterial = graphic.material;
                        if (uiMaterial != graphic.defaultMaterial)
                            CheckMaterial(uiMaterial, dep, includedShaders, checkedMaterials, result);
                    }
                }
            }
        }

        private static void CheckMaterial(Material material, string source, HashSet<Shader> included,
            HashSet<Material> checkedMaterials, ABValidateResult result)
        {
            if (material == null || !checkedMaterials.Add(material)) return;
            Shader shader = material.shader;
            if (shader == null || shader.name == "Hidden/InternalErrorShader")
            {
                result.errors.Add($"Android AB 材质的 Shader 引用丢失：{source}（{material.name}）。请修复材质的 Shader 后重新打包。");
                return;
            }
            if (included.Contains(shader)) return;
            string shaderPath = AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(shaderPath) && shaderPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                // 未单独标 AB 的项目 Shader 会作为材质的隐式依赖打进 AB，不应误报为缺失；
                // 单独标 AB 则由 Unity 的包 Manifest 记录依赖并在运行时先加载。
                return;
            }
            if (!string.IsNullOrEmpty(shaderPath) && shaderPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                // 包内 Shader 同样有资产路径；是否能用于 Android 仍须在目标 Player 中验证。
                return;
            }

            result.errors.Add($"Android AB 材质可能变紫：{source}（材质 {material.name}，Shader {shader.name}）没有可随材质进入 AB 的 Shader 资产，也不在 Graphics → Always Included Shaders。" +
                              "请将此 Shader 加入 Always Included；并确保 Android Player/AB 使用相同的渲染管线与图形 API，运行时关键词变体未被剔除。");
        }
    }
}
