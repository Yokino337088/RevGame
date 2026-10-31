// 元素特效的共享资产只在编辑器制作并绑定到既有池预制体，避免运行时Shader.Find或每发new Material。
// 载体继续复用现有CombatFrames图集中的FullRect精灵，不为程序火焰额外引入噪声贴图或资源加载接口。
using System;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>制作火、水、圣光三份共享材质并幂等绑定BattleEffect预制体，不修改场景或玩法配置。</summary>
    public static class GameElementalEffectBuild
    {
        /// <summary>仅编辑器制作路径；运行期通过既有RevPoolPrefab逻辑路径取得整个预制体及依赖。</summary>
        private const string PrefabPath = "Assets/GameRes/Effects/Combat/BattleEffect.prefab";
        private const string ShaderPath = "Assets/GameShader/Combat/ElementalImpact.shader";
        private const string MaterialFolder = "Assets/GameRes/Material/Combat/";

        /// <summary>仅在编辑态调用；缺Shader或错误引用会抛出中文原因，保存的是共享资产，不实例化运行时材质。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请退出Play再制作元素特效资产");
            }
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                throw new InvalidOperationException("缺少元素特效Shader：" + ShaderPath);
            }
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var view = root.GetComponent<BattleEffectView>();
                var so = new SerializedObject(view);
                Bind(so, "firePatternMaterial", CreateMaterial(shader, "ElementalFireImpact", 0f));
                Bind(so, "waterPatternMaterial", CreateMaterial(shader, "ElementalWaterImpact", 1f));
                Bind(so, "holyPatternMaterial", CreateMaterial(shader, "ElementalHolyImpact", 2f));
                if (so.ApplyModifiedPropertiesWithoutUndo())
                {
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                }
                AssetDatabase.SaveAssets();
                return Validate();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>编辑态制作门禁；实际AB、可见像素、分帧与回收仍需运行期验证，不能以此代替Play验收。</summary>
        public static string Validate()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var so = new SerializedObject(prefab.GetComponent<BattleEffectView>());
            foreach (string field in new[] { "firePatternMaterial", "waterPatternMaterial", "holyPatternMaterial" })
            {
                var material = so.FindProperty(field).objectReferenceValue as Material;
                if (material == null || material.shader == null || material.shader.name != "RevGame/Combat/ElementalImpact" || !material.HasProperty("_Progress"))
                {
                    throw new InvalidOperationException("元素材质或离散帧参数缺失：" + field);
                }
                if (!material.shader.isSupported || ShaderUtil.ShaderHasError(material.shader))
                {
                    throw new InvalidOperationException("元素Shader尚未编译通过或当前GPU不支持：" + field);
                }
            }
            var sprite = so.FindProperty("shieldImpact").objectReferenceValue as Sprite;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sprite)) as TextureImporter;
            var settings = new TextureImporterSettings();
            if (importer != null)
            {
                importer.ReadTextureSettings(settings);
            }
            if (sprite == null || importer == null || importer.spriteImportMode != SpriteImportMode.Single
                || settings.spriteMeshType != SpriteMeshType.FullRect)
            {
                throw new InvalidOperationException("程序元素特效必须复用FullRect精灵，Tight网格会裁掉火舌或光柱");
            }
            return "火焰/水浪/圣光三份共享材质已绑定；复用FullRect图集载体，无新增纹理";
        }

        /// <summary>共享材质按固定名称创建一次；_Element只是美术分型，属于资源配方而非战斗数值。</summary>
        private static Material CreateMaterial(Shader shader, string name, float element)
        {
            string path = MaterialFolder + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material.shader != shader || !Mathf.Approximately(material.GetFloat("_Element"), element))
            {
                material.shader = shader;
                material.SetFloat("_Element", element);
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        /// <summary>通过序列化字段改预制体引用，Apply后由调用方保存；引用相同不重复标脏，保持制作入口幂等。</summary>
        private static void Bind(SerializedObject so, string field, Material material)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property.objectReferenceValue != material)
            {
                property.objectReferenceValue = material;
            }
        }
    }
}
