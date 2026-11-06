using System;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        /// <summary>固定配方经RevMCP调用，复用项目Sprite及九宫格，不直接修改Prefab YAML。</summary>
        public static object BuildAgentAssets()
        {
            _font = GameAssetBuilder.Font();
            Menu("PvpAgentSettingsPanel", typeof(PvpAgentSettingsPanel), root =>
            {
                Label("heading", root, "DeepSeek Agent 设置", .22f, .79f, .78f, .9f, 38, true);
                Label("disclosure", root, "启用后棋局将发送到DeepSeek并消耗你的API额度。\nKey仅保存本次会话；本地人机你只手动落子。", .18f, .62f, .82f, .77f, 24, true);
                Label("keyLabel", root, "API Key（仅本次会话）", .22f, .59f, .78f, .625f, 18, true);
                Label("modelLabel", root, "模型名称", .22f, .47f, .78f, .505f, 18, true);
                Input("key", root, "", .22f, .51f, .78f, .59f, 512);
                Input("model", root, "deepseek-flash", .22f, .39f, .78f, .47f, 128);
                Label("state", root, "填写API Key与模型", .18f, .26f, .82f, .37f, 22, true);
                Button("enable", root, "保存并授权云端调用", .31f, .15f, .7f, .23f, 26);
                Button("disable", root, "关闭云端并清除Key", .31f, .06f, .7f, .14f, 25);
            });
            string path = "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab";
            GameObject panel = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform controls = panel.transform.Find("controls");
                if (controls.Find("agent") == null)
                {
                    Button("agent", controls, "Agent托管", .025f, .32f, .20f, .40f, 24);
                    Button("agentSettings", controls, "Agent设置", .025f, .22f, .20f, .30f, 24);
                    Label("agentState", panel.transform, "Agent 未启用", .22f, .11f, .8f, .17f, 20, true);
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
    }
}
