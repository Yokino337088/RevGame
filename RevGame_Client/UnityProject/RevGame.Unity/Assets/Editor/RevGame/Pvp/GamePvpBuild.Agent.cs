using System;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        /// <summary>已有官方标识的编辑器资产路径，菜单重制只读取已经导入的 Sprite。</summary>
        private const string AgentLogoPath = "Assets/GameRes/Art/UI/Pvp/DeepSeekLogo.png";

        /// <summary>重制 Agent 设置面板与对局入口，保留现有 Prefab GUID 和绑定。</summary>
        public static object BuildAgentAssets()
        {
            // 完整 Agent 配方负责素材导入。仅改菜单布局时无需重导入标识或写入对局 HUD。
            var importer = (TextureImporter)AssetImporter.GetAtPath(AgentLogoPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            _font = GameAssetBuilder.Font();
            BuildAgentMenu();
            // 只在旧 Prefab 缺入口时追加节点，保留现有对局控件配置。
            string path = "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab";
            GameObject panel = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform controls = panel.transform.Find("controls");
                if (controls.Find("agent") == null)
                {
                    Button("agent", controls, "Agent托管", .025f, .32f, .20f, .40f, 24);
                    Button("agentSettings", controls, "Agent设置", .025f, .22f, .20f, .30f, 24);
                }
                PrefabUtility.SaveAsPrefabAsset(panel, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(panel);
            }
            string entryPath = "Assets/GameRes/UI/Panel/Pvp/Menu/PvpEntryPanel.prefab";
            GameObject entry = PrefabUtility.LoadPrefabContents(entryPath);
            try
            {
                // 兼容较旧入口资产。新菜单配方已在安全框内生成同名按钮。
                if (entry.transform.Find("agentSettings") == null)
                {
                    Button("agentSettings", entry.transform, "Agent设置", .73f, .07f, .93f, .14f, 25);
                }
                PrefabUtility.SaveAsPrefabAsset(entry, entryPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(entry);
            }
            AssetDatabase.SaveAssets();
            return new { success = true, text = "Agent设置与托管入口已制作" };
        }

        /// <summary>只装配设置菜单，输入和按钮沿用现有节点契约，保存时由 Menu 统一换算安全框锚点。</summary>
        private static void BuildAgentMenu()
        {
            Sprite logoSprite = AssetDatabase.LoadAssetAtPath<Sprite>(AgentLogoPath);
            if (logoSprite == null)
            {
                throw new InvalidOperationException("缺少 Agent 设置使用的 DeepSeek 标识");
            }
            Menu("PvpAgentSettingsPanel", typeof(PvpAgentSettingsPanel), root =>
            {
                // 告知文案允许换行，Key 和模型各占一行。操作按钮放到底部同一行，避免挤压输入区。
                Label("heading", root, "DeepSeek Agent 设置", .16f, .83f, .84f, .93f, 37, true);
                Label("disclosure", root, "启用后人机对手和落子建议可调用DeepSeek并消耗API额度。\nKey在本机加密保存；本地人机你只手动落子。", .10f, .67f, .90f, .82f, 23, true);
                Label("keyLabel", root, "API Key（本机加密保存）", .17f, .61f, .83f, .67f, 20, true);
                Input("key", root, "", .17f, .50f, .83f, .60f, 512);
                Label("modelLabel", root, "DeepSeek V4.1 Flash · 1017（10月17日版）", .23f, .42f, .83f, .49f, 19, true);
                var logo = Rect("deepseekLogo", root, .14f, .31f, .22f, .41f).gameObject.AddComponent<Image>();
                logo.sprite = logoSprite;
                logo.preserveAspect = true;
                logo.raycastTarget = false;
                Input("model", root, "deepseek-flash", .24f, .31f, .83f, .41f, 128);
                Label("state", root, "填写API Key与模型", .14f, .18f, .86f, .29f, 22, true);
                Button("enable", root, "保存并授权云端调用", .28f, .06f, .59f, .16f, 24);
                Button("disable", root, "关闭云端并清除Key", .62f, .06f, .92f, .16f, 24);
            });
        }
    }
}
