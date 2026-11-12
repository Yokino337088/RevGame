using System;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        // 绯焰红枪 Shader 和场景共享材质的编辑器逻辑路径。
        private const string EmberDuelShader = "Assets/GameShader/UI/EmberDuelSpear.shader";
        private const string EmberDuelMaterial = "Assets/Scenes/PvpDuelAnimations/EmberSpearGlow.mat";

        // 新版美术的七张待机图按 00 到 06 播放，前两张沿用原有资产名和 GUID。
        private static readonly string[] EmberIdleFrames =
        {
            "Ember_00", "Ember_01", "Ember_Idle_02", "Ember_Idle_03", "Ember_Idle_04", "Ember_Idle_05", "Ember_Idle_06"
        };
        // 从待机开始累计的结束秒数。素材说明给出的呼吸和眨眼停留时间不同，不能按均匀帧率切图。
        private static readonly float[] EmberIdleFrameEnds = { .21f, .37f, .60f, .67f, .75f, .82f, 1.04f };
        // 旧动画片段每 10 毫秒采样一次，足以准确保留素材给出的关键帧时点。
        private const int EmberIdleClipFrameRate = 100;

        // 只有绯焰的待机使用非等长时间表。其它角色继续用自己的片段采样率。
        private static float[] DuelIdleFrameEnds(string role)
        {
            return role == EmberClipPrefix ? EmberIdleFrameEnds : Array.Empty<float>();
        }

        // 两个黑方图层共用红枪材质，交给已有的 Sprite 图集与淡出播放。
        private static void BindDuelEmberEffects(PvpDuelView view)
        {
            Material material = GetDuelEffectMaterial(EmberDuelShader, EmberDuelMaterial);
            view.Effects.First.material = material;
            view.Effects.FirstImpact.material = material;
        }

        // 首次制作创建材质。已有材质通过原路径加载，保留引用和 GUID。
        private static Material GetDuelEffectMaterial(string shaderPath, string materialPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || !shader.isSupported)
            {
                throw new InvalidOperationException("当前编辑器无法使用决斗 Shader：" + shaderPath);
            }
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            return material;
        }
    }
}
