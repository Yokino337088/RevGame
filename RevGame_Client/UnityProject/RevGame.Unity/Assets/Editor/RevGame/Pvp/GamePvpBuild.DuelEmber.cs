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
