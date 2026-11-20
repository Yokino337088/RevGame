using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>将批准的浅色素材装配到对局按钮、战况牌和无尽菜单，供原配方和局部更新共用。</summary>
    internal static class GameCombatUiBuild
    {
        /// <summary>正式 UI 插画的根目录，图集与包由项目分包规则管理。</summary>
        private const string Art = "Assets/GameRes/Art/UI/";
        /// <summary>浅色文字区使用的深墨色。</summary>
        internal static readonly Color32 Ink = new Color32(0x14, 0x29, 0x30, 255);

        /// <summary>局部更新现有界面，沿用各节点的名字、业务绑定和预制体 GUID。</summary>
        public static string Configure()
        {
            Update("Pvp/PvpPanel", GamePvpBuild.ConfigureMatchButtons);
            Update("BattleHudPanel", root => ApplyArt(root.Find("infoPlate").GetComponent<Image>(), "BattleHud/BattleInfoPlate", false));
            Update("Endless/EndlessEntryPanel", GameEndlessBuild.ConfigureEntryArt);
            Update("Endless/EndlessHudPanel", GameEndlessBuild.ConfigureModalArt);
            AssetDatabase.SaveAssets();
            return "已装配对局按钮、战况信息牌与无尽菜单";
        }

        /// <summary>用整图显示准备好的圆角透明 Sprite，去掉旧材质和颜色覆盖，按用途设置点击。</summary>
        internal static void ApplyArt(Image image, string name, bool raycast)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Art + name + ".png");
            if (image == null || sprite == null)
            {
                throw new InvalidOperationException("缺少战斗 UI 图层或素材：" + name);
            }
            image.sprite = sprite;
            image.overrideSprite = null;
            image.material = null;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.color = Color.white;
            image.raycastTarget = raycast;
        }

        /// <summary>打开一个预制体副本并装配。装配成功后保存，再归还临时预览场景。</summary>
        private static void Update(string name, Action<Transform> apply)
        {
            string path = "Assets/GameRes/UI/Panel/" + name + ".prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                apply(root.transform);
                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                {
                    throw new InvalidOperationException("战斗 UI 保存失败：" + path);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
