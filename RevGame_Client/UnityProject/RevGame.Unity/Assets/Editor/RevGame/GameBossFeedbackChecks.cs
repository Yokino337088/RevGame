// 反馈验收分层：此处只证明资源映射、Shader属性和事件筛选契约；画面、声音与手机马达必须另有Play/设备证据。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameBossFeedbackChecks
    {
        [Serializable]
        private sealed class Result
        {
            public bool passed;
            public string error;
            public List<string> assertions = new List<string>();
        }
        /// <summary>编辑态只读审计，失败直接抛出并落原始证据；不操作场景、不播放声音、不写用户存档。</summary>
        public static string Audit()
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Boss反馈资源审计要求编辑态");
            }
            var result = new Result();
            void Check(bool ok, string message)
            {
                if (!ok)
                {
                    throw new InvalidOperationException(message);
                }
                result.assertions.Add(message);
            }
            try
            {
                GameConfigTestSupport.Ensure();
                var names = new HashSet<string>();
                foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                {
                    string resource = GameAudio.GetBossSkillSoundName(kind);
                    Check(names.Add(resource), kind + "音效映射不与其它技能重复");
                    Check(!string.IsNullOrEmpty(resource), kind + "存在明确声音映射");
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Audio/Sfx/" + GameAudio.GetBossSkillSoundPath(kind) + ".ogg");
                    Check(clip != null && clip.samples > 0 && clip.length > 0, kind + "实际声音资源非空：" + resource);
                }
                Check(GameAudio.GetBossSkillSoundName((BossSkillKind)(-1)) == null, "负数技能拒绝声音映射");
                Check(GameAudio.GetBossSkillSoundName((BossSkillKind)999) == null, "未知技能拒绝声音映射");
                var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Combat/BossSkill.mat");
                Check(material != null && material.shader != null && material.shader.isSupported, "Boss技能共享材质与当前设备Shader可用");
                foreach (string property in new[] { "_Intensity", "_Contrast", "_OutlineWidth" })
                {
                    Check(material.HasProperty(property) && material.GetFloat(property) > 0, "材质可配置且启用：" + property);
                }
                foreach (BattleDamageKind kind in Enum.GetValues(typeof(BattleDamageKind)))
                {
                    bool expected = kind >= BattleDamageKind.BossCleave && kind <= BattleDamageKind.BossFinalEdict;
                    var hit = new BattleHitInfo(1, default, 1, GameSide.Player, 10, true, default, fromSkill: true, damageKind: kind);
                    Check(GamePresentation.IsBossSkillPlayerHit(hit) == expected, "震动只接Boss来源：" + kind);
                }
                Check(!GamePresentation.IsBossSkillPlayerHit(new BattleHitInfo(1, default, 1, GameSide.Player, 0, true, default,
                    fromSkill: true, damageKind: BattleDamageKind.BossCleave)), "零伤害不触发Boss震动");
                Check(!GamePresentation.IsBossSkillPlayerHit(new BattleHitInfo(1, default, 1, GameSide.Enemy, 10, true, default,
                    fromSkill: true, damageKind: BattleDamageKind.BossCleave)), "敌方受伤不触发玩家受击震动");
                Check(!GamePresentation.IsBossSkillPlayerHit(new BattleHitInfo(1, default, 1, GameSide.Player, 10, true, default,
                    fromSkill: false, damageKind: BattleDamageKind.BossCleave)), "非技能事件不伪装Boss震动");
                result.passed = true;
            }
            catch (Exception error)
            {
                result.error = error.ToString();
                throw;
            }
            finally
            {
                string folder = Path.GetFullPath("../../../.codex/work/boss-support-2026-10-29");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "boss-feedback-audit.json"), JsonUtility.ToJson(result, true));
            }
            return JsonUtility.ToJson(result);
        }
        [Serializable]
        private sealed class Observation
        {
            public int bossFx, animatedCells, hitFeedbackCount, audioRequests;
            public float cameraAmplitude;
            public string audioResource;
        }
        /// <summary>只读观察当前真实画面对象；AudioRequest不是已发声证明，Play测试还须观察AudioSource.clip/isPlaying。</summary>
        public static string State()
        {
            var result = new Observation();
            var board = UnityEngine.Object.FindObjectOfType<BoardView>();
            var presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
            var audio = UnityEngine.Object.FindObjectOfType<GameAudio>();
            result.bossFx = board == null ? 0 : board.ActiveBossFxCount;

            if (presentation != null)
            {
                result.hitFeedbackCount = presentation.BossHitFeedbackCount;
                result.cameraAmplitude = presentation.ImpactAmplitude;
            }
            if (audio != null)
            {
                result.audioRequests = audio.BossAudioRequestCount;
                result.audioResource = audio.LastBossAudioResource;
            }
            foreach (var cell in UnityEngine.Object.FindObjectsOfType<BoardCellView>())
            {
                if (cell.HasBossSkillAnimation)
                {
                    result.animatedCells++;
                }
            }
            return JsonUtility.ToJson(result);
        }
    }
}
