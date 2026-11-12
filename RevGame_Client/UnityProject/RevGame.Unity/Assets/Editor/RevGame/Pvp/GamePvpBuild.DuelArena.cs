using System;
using RevGame.Pvp.Duel;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        // 片段和内置角色图集随场景交付，放在角色动画资源下。
        private const string DuelAnimationFolder = "Assets/GameRes/Art/Pvp/Duel/Animations";
        // 记录旧位置只用于升级已有工作区，移动文件夹同时保留内部全部 GUID。
        private const string OldDuelAnimationFolder = "Assets/Scenes/PvpDuelAnimations";
        // 轨迹和舞台结界共用的程序材质，仅供场景引用。
        private const string DuelEnergyShader = "Assets/GameShader/Pvp/PvpDuelArenaEnergy.shader";
        // 场景专用材质按类型归档，由场景和粒子模板直接引用。
        private const string DuelMaterials = "Assets/GameRes/" + GameResourceUsage.DuelSceneMaterials;
        // 轨迹实例按这个模板向 RevPool 借用。
        private const string DuelParticlePrefab = "Assets/GameRes/" + GameResourceUsage.DuelSceneEffects + "/DuelEnergyParticle.prefab";

        // 只迁移三个已知目录。已迁移的项目和全新项目也能重新运行同一配方。
        private static void MigrateDuelAssets()
        {
            MoveDuelFolder(OldDuelAnimationFolder, DuelAnimationFolder);
            if (!AssetDatabase.IsValidFolder(DuelAnimationFolder))
            {
                AssetDatabase.CreateFolder(DuelArt.TrimEnd('/'), "Animations");
            }
            if (!AssetDatabase.IsValidFolder(DuelHudArt.TrimEnd('/')))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/Art/UI", "PvpDuelHud");
            }
            MoveDuelFolder(ArcadeArt + "Hud", DuelHudArt + "Controls");
            MoveDuelFolder(YayaArt + "Portrait", DuelHudArt + "Portrait");
        }

        // Unity 返回非空错误时立即结束，保留已有引用并给出冲突路径。
        private static void MoveDuelFolder(string from, string to)
        {
            if (!AssetDatabase.IsValidFolder(from))
            {
                return;
            }
            string error = AssetDatabase.MoveAsset(from, to);
            if (error.Length != 0)
            {
                throw new InvalidOperationException($"移动决斗资源失败 {from} → {to}。{error}");
            }
        }

        // 初始构图采用相同的舞台锚点，进入对局后由双人镜头按画布尺寸细调。
        private static Image BuildDuelBackdrop(RectTransform parent)
        {
            RectTransform rect = Rect(DuelBackdropName, parent, .5f, .425f, .5f, .425f);
            rect.pivot = new Vector2(.5f, .225f);
            rect.sizeDelta = new Vector2(2684, 1300);
            rect.anchoredPosition = new Vector2(0, -172);
            Image backdrop = rect.gameObject.AddComponent<Image>();
            ApplyDuelBackdrop(backdrop);
            return backdrop;
        }

        // 结界左右与原生坐标对应，淡青的法纹常驻，触边亮度由镜头表现组件推进。
        private static DuelArenaFxGraphic BuildDuelWard(RectTransform parent, int side, Material material)
        {
            RectTransform rect = Rect(side < 0 ? "WestWard" : "EastWard", parent, .5f, .425f, .5f, .425f);
            rect.sizeDelta = new Vector2(130, 410);
            // 横向单位来自原生场地，使用与角色位置相同的舞台换算比例。
            rect.anchoredPosition = new Vector2(side * DuelConfig.ArenaExtent * PvpDuelView.ArenaScale, 14);
            var ward = rect.gameObject.AddComponent<DuelArenaFxGraphic>();
            ward.material = material;
            ward.color = new Color(.30f, .96f, .84f, .14f);
            ward.raycastTarget = false;
            return ward;
        }

        // 生成统一拖尾和冲击环模板。RevPool 保持它的引用和租约分离。
        private static DuelArenaFxGraphic BuildDuelParticle()
        {
            Material material = GetDuelEffectMaterial(DuelEnergyShader, DuelMaterials + "/DuelArenaEnergy.mat");
            RectTransform root = Rect("DuelEnergyParticle", null, .5f, .425f, .5f, .425f);
            try
            {
                var particle = root.gameObject.AddComponent<DuelArenaFxGraphic>();
                particle.raycastTarget = false;
                particle.material = material;
                GameObject asset = PrefabUtility.SaveAsPrefabAsset(root.gameObject, DuelParticlePrefab);
                if (asset == null)
                {
                    throw new InvalidOperationException("决斗粒子模板保存失败。");
                }
                return asset.GetComponent<DuelArenaFxGraphic>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }
    }
}
