using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>
    /// 检查已保存预制体的接线与“接触立即切爆炸”边界。加载隔离Prefab内容后必定释放，
    /// 不启动Play、不制造业务伤害，也不把这些编辑器断言当作真实相机或手机画面验收。
    /// </summary>
    public static class GameHelperVfxChecks
    {
        [Serializable] private sealed class Result { public string status, error; public List<string> assertions = new List<string>(); }

        public static string Run()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("帮手资源边界检查需要编辑态"); }
            var result = new Result { status = "running" };
            GameObject attack = null, healing = null;
            try
            {
                attack = PrefabUtility.LoadPrefabContents("Assets/GameRes/Effects/Helper/HelperAttack.prefab");
                var storm = attack.GetComponent<HelperStormVisual>();
                Require(storm != null, "攻击预制体绑定序列帧组件", result);
                CheckContact(storm, HelperWeapon.Missile, 1.35f, true, result);
                CheckContact(storm, HelperWeapon.Jellyfish, 1.3f, false, result);
                healing = PrefabUtility.LoadPrefabContents("Assets/GameRes/Effects/Helper/HelperHealing.prefab");
                var heal = healing.GetComponent<HelperHealingView>();
                var line = healing.GetComponent<LineRenderer>();
                Require(heal != null && line != null && line.sharedMaterial != null, "治疗池预制体已保存连线组件和材质", result);
                Require(line.sharedMaterial.shader.name == "RevGame/Helper/HealingBeam", "治疗束引用独立绿色加号Shader", result);
                Require(line.sharedMaterial.HasProperty("_PlusSize") && line.sharedMaterial.HasProperty("_BeamWidth"), "治疗加号大小与射线宽度可在材质调节", result);
                Require(healing.GetComponentInChildren<MeshRenderer>(true)?.sharedMaterial?.shader.name == "RevGame/Helper/HealingImpact",
                    "目标提示引用独立治疗环Shader", result);
                heal.OnPoolReturn();
                Require(!heal.IsConnected && !line.enabled && heal.TargetId == 0, "治疗回池清除目标Id并关闭射线", result);
                string[] sounds = { "HelperBarrage", "HelperBeam", "HelperMissile", "HelperJellyfish", "HelperExplosion", "HelperHeal", "HelperJellyImpact" };
                foreach (string sound in sounds)
                {
                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Audio/Sfx/" + sound + ".ogg");
                    Require(clip != null && clip.samples > 0 && clip.length > 0 && clip.channels == 1, "音频已导入且是有效单声道：" + sound, result);
                }
                result.status = "passed";
                return JsonUtility.ToJson(result);
            }
            catch (Exception error) { result.status = "failed"; result.error = error.ToString(); throw; }
            finally
            {
                if (attack != null) { PrefabUtility.UnloadPrefabContents(attack); }
                if (healing != null) { PrefabUtility.UnloadPrefabContents(healing); }
                string folder = Path.GetFullPath("../../../.codex/work/boss-helper-2026-10-29");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "helper-vfx-assets.json"), JsonUtility.ToJson(result, true));
            }
        }

        private static void CheckContact(HelperStormVisual storm, HelperWeapon weapon, float scale, bool mushroom, Result result)
        {
            storm.Begin(weapon, 3);
            storm.Present(Vector3.zero, Vector3.right * 10, .1f, .4f);
            Require(storm.ProjectileVisible && !storm.IsExplosion && Mathf.Abs(storm.BodyScale - scale) < .001f,
                weapon + "飞行阶段显示放大的原始弹体", result);
            // 此时中心只差0.33，比接触半径0.4更近，但仍早于固定0.6切换时间；验证碰到即消失。
            storm.Present(Vector3.zero, Vector3.right * 10, .58f, .4f);
            Require(storm.IsExplosion && !storm.ProjectileVisible, weapon + "首次接触即换爆炸，飞行弹体不残留", result);
            Require(storm.MushroomVisible == mushroom, weapon + "的蘑菇云类型正确", result);
            storm.Present(Vector3.zero, Vector3.right * 12, .75f, .4f);
            Require(storm.IsExplosion && !storm.ProjectileVisible, weapon + "爆炸后目标移动不重现飞行弹体", result);
            storm.Clear();
            Require(!storm.ProjectileVisible && !storm.MushroomVisible && !storm.IsExplosion, weapon + "回池清除所有爆炸和飞行状态", result);
        }

        private static void Require(bool condition, string message, Result result)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            result.assertions.Add(message);
        }
    }
}
