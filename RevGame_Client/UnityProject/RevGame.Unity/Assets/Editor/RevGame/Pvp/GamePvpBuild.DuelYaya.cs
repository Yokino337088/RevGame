using System;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    // 白方角色资产装配。场景持有切片、材质和音效引用，运行期只按座位和动作号选择。
    public static partial class GamePvpBuild
    {
        // 鸦鸦切片属于决斗内置图集，独立于大厅的鸦鸦图集。
        private const string YayaArt = ArcadeArt + "Yaya/";
        private const string YayaPrefix = "Yaya";
        // 绯焰旧动画文件名保留，已有 Clip 的 GUID 不需要迁移。
        private const string EmberClipPrefix = "Swordsman";
        // 按 DuelPoses 顺序映射到美术目录。Attack1/5/6 对应普攻一二三段。
        private static readonly string[] YayaPoseFolders =
        {
            "Idle", "Run", "Jump", "LowKick", "Raven", "Flock", "Down", "Talon", "Kick", "Spin", "Hit"
        };
        // 和目录一一对应。任一帧缺失会中止装配，避免在场景中留下空引用。
        private static readonly int[] YayaFrameCounts = { 8, 12, 4, 4, 4, 4, 1, 4, 4, 4, 2 };
        // 所有腿法和施法都在第 3 张图片展示接触动作，实际开始时刻仍由原生年龄决定。
        private static readonly int[] YayaContactKeys = { 0, 0, 0, 2, 2, 2, 0, 2, 2, 2, 0 };
        // 动作号 1～6 对应的白方特效。两种踢法复用羽刃纹理，动作和音色各自独立。
        private static readonly string[] YayaCastArt = { "Toe", "Raven", "Flock", "Talon", "Kick", "Kick" };
        private static readonly string[] YayaImpactArt = { "Toe", "Impact", "Flock", "Impact", "Kick", "Impact" };
        // 音效按逻辑动作号或冲击等级排列，由场景唯一的声源播放。
        private static readonly string[] YayaCastAudio = { "LowKick", "Raven", "Flock", "Talon", "SnapKick", "SpinKick" };
        private static readonly string[] YayaImpactAudio = { "KickImpact", "FeatherImpact", "FlockImpact" };
        // 直接从用户差分图取样。顺序与白方 HUD 的状态索引对应。
        private static readonly int[] YayaPortraitExpressions = { 28, 23, 2, 19, 22 };
        // 白方特效共用一份场景材质，每帧只修改顶点颜色和位置。
        private const string YayaDuelShader = "Assets/GameShader/UI/YayaDuelFeather.shader";
        private const string YayaDuelMaterial = "Assets/Scenes/PvpDuelAnimations/YayaFeatherGlow.mat";

        // 黑白两席分别生成和绑定动画，交叉淡入层仍由原有 DuelActorAnimator 管理。
        private static void BindDuelActorRoster(PvpDuelView view)
        {
            ConfigureDuelActor(view.First, view.FirstAnimator, DuelPoses.Select(LoadDuelSequence).ToArray(),
                EmberClipPrefix, ArcadeContactKeys, false);
            ConfigureDuelActor(view.Second, view.SecondAnimator, DuelPoses.Select(LoadYayaDuelSequence).ToArray(),
                YayaPrefix, YayaContactKeys, true);
        }

        // 鸦鸦的十二张跑步图以 24 帧每秒播放，左右两步合计半秒。
        // 待机一秒一圈，跳跃的四个姿势覆盖原生起落时段。
        private static int DuelClipFrameRate(string role, string pose)
        {
            return role == YayaPrefix
                ? pose switch { "Idle" => 8, "Run" => 24, "Jump" => 7, _ => 15 }
                : 15;
        }

        // 真正循环的片段不能重复末图，否则每次换回首帧都会多停一拍。
        private static bool DuelLoopsWithoutPadding(string role, string pose)
        {
            return pose == "Run" || (role == YayaPrefix && pose == "Idle");
        }

        // 读取一个姿态的有序单图切片。资产名含角色前缀，图集校验时不会混到绯焰。
        private static Sprite[] LoadYayaDuelSequence(string pose)
        {
            int index = Array.IndexOf(DuelPoses, pose);
            string folder = YayaPoseFolders[index];
            Sprite[] frames = Enumerable.Range(0, YayaFrameCounts[index])
                .Select(frame => LoadDuelSpriteAtPath($"{YayaArt}{folder}/Yaya_{folder}_{frame:D2}.png")).ToArray();
            // 腿法在明确收腿后回到格斗架势，连段断开时不会长时间保持伸腿。
            return pose == "Attack1" || pose == "Attack5" || pose == "Attack6" ? frames.Append(frames[0]).ToArray() : frames;
        }

        // 读取一张已导入的乌鸦、羽刃或冲击 Sprite。
        private static Sprite LoadYayaDuelEffect(string name)
        {
            return LoadDuelSpriteAtPath($"{YayaArt}Effects/YayaFx_{name}.png");
        }

        // 白方头像从图二原画取样，和战斗切片一起随决斗场景加载。
        private static Sprite LoadYayaDuelPortrait(int expression)
        {
            return LoadDuelSpriteAtPath($"{YayaArt}Portrait/Yaya_Portrait_{expression:D2}.png");
        }

        // 预先引用短音效。进入游戏时无需按路径临时查找资源。
        private static AudioClip LoadYayaDuelAudio(string name)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>($"{YayaArt}Audio/Yaya_{name}.wav") ??
                throw new InvalidOperationException("缺少鸦鸦决斗音效：" + name);
        }

        // 绑定现有 HUD 节点和复用图层，无需重建整张决斗场景。
        private static void BindDuelYayaPresentation(PvpDuelView view)
        {
            view.Effects.YayaCastSprites = YayaCastArt.Select(LoadYayaDuelEffect).ToArray();
            view.Effects.YayaImpactSprites = YayaImpactArt.Select(LoadYayaDuelEffect).ToArray();
            view.Effects.YayaCastSounds = YayaCastAudio.Select(LoadYayaDuelAudio).ToArray();
            view.Effects.YayaImpactSounds = YayaImpactAudio.Select(LoadYayaDuelAudio).ToArray();
            Material material = GetDuelEffectMaterial(YayaDuelShader, YayaDuelMaterial);
            view.Effects.Second.material = material;
            view.Effects.SecondImpact.material = material;
            view.SecondPortrait = view.Hud.Find("WhitePortrait").GetComponent<Image>();
            view.YayaPortraits = YayaPortraitExpressions.Select(LoadYayaDuelPortrait).ToArray();
            view.SecondPortrait.sprite = view.YayaPortraits[0];
            view.SecondName.text = "鸦鸦";
            view.KeyboardLegend = view.KeyboardGuide.transform.Find("Keys").GetComponent<Text>();
            Button[] skills = { view.Skill, view.SkillTwo, view.Ultimate };
            Button[] actions = { view.Jab, view.Skill, view.SkillTwo, view.Ultimate };
            view.SkillCaptions = skills.Select(button => button.transform.Find("Caption").GetComponent<Text>()).ToArray();
            view.SkillGlyphs = actions.Select(button => button.transform.Find("Icon").GetComponent<Image>()).ToArray();
            view.EmberSkillGlyphs = new[] { "Jab", "Thrust", "Plunge", "Crimson" }.Select(LoadHudSprite).ToArray();
            view.YayaSkillGlyphs = new[] { "Kick", "Raven", "Talon", "Flock" }.Select(LoadYayaDuelEffect).ToArray();
        }

    }
}
