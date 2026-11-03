// 音频包体控制：原始WAV/MP3/OGG与GUID不改，只由Unity导入器制作运行期压缩数据。
// 长音乐保留立体声流式读取，短音效单声道并预解码；规则接入正式构建避免新增音频回到默认高体积。
using System;
using System.Collections.Generic;
using System.IO;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>项目音频导入与体积观察入口。只使用Unity公开Editor API，不改变RevSound或资源引用计数。</summary>
    public static class GameAudioCompressionBuild
    {
        /// <summary>音乐/音效的有损压缩质量与采样率；是音频制作配置，不是玩法数值。音乐保立体声，音效单声道。</summary>
        public const float MusicQuality = .28f, EffectQuality = .45f;
        public const uint MusicRate = 32000, EffectRate = 22050;
        /// <summary>默认和两个发布平台采用同一规则；显式覆盖避免旧Android设置绕过默认压缩。</summary>
        private static readonly string[] Platforms = { "Standalone", "Android" };

        /// <summary>对指定音频应用用途规则，完全一致时不重新导入，保持正式构建幂等。</summary>
        public static bool ConfigureClip(string path)
        {
            AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("音频导入器缺失：" + path);
            }
            bool music = path.Contains("/Bgm/");
            AudioImporterSampleSettings desired = importer.defaultSampleSettings;
            desired.compressionFormat = AudioCompressionFormat.Vorbis;
            desired.quality = music ? MusicQuality : EffectQuality;
            desired.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
            desired.sampleRateOverride = music ? MusicRate : EffectRate;
            desired.loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            desired.preloadAudioData = !music;
            bool changed = !Same(importer.defaultSampleSettings, desired) || importer.forceToMono != !music || importer.loadInBackground != music;
            foreach (string platform in Platforms)
            {
                changed |= !importer.ContainsSampleSettingsOverride(platform) || !Same(importer.GetOverrideSampleSettings(platform), desired);
            }
            if (!changed)
            {
                return false;
            }
            importer.defaultSampleSettings = desired;
            foreach (string platform in Platforms)
            {
                importer.SetOverrideSampleSettings(platform, desired);
            }
            importer.forceToMono = !music;
            importer.loadInBackground = music;
            importer.SaveAndReimport();
            return true;
        }
        /// <summary>只比较本管线拥有的字段，保留其他Unity原始设置；浮点质量使用容差。</summary>
        private static bool Same(AudioImporterSampleSettings a, AudioImporterSampleSettings b)
        {
            return a.loadType == b.loadType && a.compressionFormat == b.compressionFormat && Mathf.Approximately(a.quality, b.quality)
                && a.sampleRateSetting == b.sampleRateSetting && a.sampleRateOverride == b.sampleRateOverride && a.preloadAudioData == b.preloadAudioData;
        }
        /// <summary>遍历GameRes音频制作库，更新有变化的设置；不批量修改项目外的原始素材或框架资产。</summary>
        public static string ConfigureAll()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请在编辑态压缩音频");
            }
            int changed = 0, count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/GameRes/Audio" }))
            {
                count++;
                changed += ConfigureClip(AssetDatabase.GUIDToAssetPath(guid)) ? 1 : 0;
            }
            return "音频" + count + "项，重导入" + changed + "项；音乐Vorbis0.28/32kHz立体声流式，音效Vorbis0.45/22.05kHz单声道预解码";
        }
        /// <summary>窄Editor动作：应用固定项目音频规则，不接受任意路径或Importer字段。</summary>
        [RevAutomationAction("game.audio.compress", Environment = "edit", Keys = new string[0], Description = "按用途压缩项目音乐与音效，并为Standalone/Android设置显式覆盖")]
        public static object Compress(RevAutomationRequest request)
        {
            return new Dictionary<string, object> { ["summary"] = ConfigureAll() };
        }
        /// <summary>只读列出真实AudioClip参数与源大小，供压缩前后比对；不把原始文件大小当AB大小。</summary>
        [RevAutomationAction("game.audio.audit", Environment = "edit", Keys = new string[0], Description = "只读音频导入参数、时长/声道和实际包标记，用于压缩前后对比")]
        public static object Audit(RevAutomationRequest request)
        {
            var rows = new List<object>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/GameRes/Audio" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                AudioImporter importer = (AudioImporter)AssetImporter.GetAtPath(path);
                AudioImporterSampleSettings setting = importer.defaultSampleSettings;
                rows.Add(new Dictionary<string, object>
                {
                    ["path"] = path, ["guid"] = guid, ["sourceBytes"] = new FileInfo(path).Length,
                    ["seconds"] = clip.length, ["channels"] = clip.channels, ["frequency"] = clip.frequency,
                    ["quality"] = setting.quality, ["format"] = setting.compressionFormat.ToString(), ["loadType"] = setting.loadType.ToString(),
                    ["mono"] = importer.forceToMono, ["bundle"] = AssetDatabase.GetImplicitAssetBundleName(path)
                });
            }
            return new Dictionary<string, object> { ["count"] = rows.Count, ["clips"] = rows };
        }
        /// <summary>构建前检查全部使用音频的用途/平台设置和时长，不自动修正，错误明确指出资源。</summary>
        [RevAutomationAction("game.audio.compression-check", Environment = "edit", Keys = new string[0], Description = "严格检查全部音频压缩、采样率、加载策略及两个平台覆盖，不修改资源")]
        public static object Check(RevAutomationRequest request)
        {
            int musicCount = 0, effectCount = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/GameRes/Audio" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                bool music = path.Contains("/Bgm/");
                var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                if (clip == null || clip.length <= 0 || settings.compressionFormat != AudioCompressionFormat.Vorbis
                    || !Mathf.Approximately(settings.quality, music ? MusicQuality : EffectQuality)
                    || settings.sampleRateSetting != AudioSampleRateSetting.OverrideSampleRate
                    || settings.sampleRateOverride != (music ? MusicRate : EffectRate)
                    || settings.loadType != (music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad)
                    || importer.forceToMono != !music || importer.loadInBackground != music)
                {
                    throw new InvalidOperationException("音频压缩/时长策略不一致：" + path);
                }
                foreach (string platform in Platforms)
                {
                    if (!importer.ContainsSampleSettingsOverride(platform) || !Same(settings, importer.GetOverrideSampleSettings(platform)))
                    {
                        throw new InvalidOperationException("音频平台覆盖不一致：" + platform + "/" + path);
                    }
                }
                if (music)
                {
                    musicCount++;
                }
                else
                {
                    effectCount++;
                }
            }
            return new Dictionary<string, object> { ["success"] = true, ["music"] = musicCount, ["effects"] = effectCount };
        }
        /// <summary>
        /// 构建后按音乐总时长预算包体，避免增加歌曲后仅看总MB误判；短效用每秒与最低包预算兼顾容器开销。
        /// 音乐上限96kbps加32KB包头，短效32kbps加32KB；这是交付预算不是编码码率，超标必须复核用途/听感。
        /// </summary>
        public static string ValidateBundleBudget(string directory)
        {
            var seconds = new Dictionary<string, float>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/GameRes/Audio" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string bundle = AssetDatabase.GetImplicitAssetBundleName(path);
                if (string.IsNullOrEmpty(bundle))
                {
                    throw new InvalidOperationException("音频没有包标记：" + path);
                }
                seconds.TryGetValue(bundle, out float total);
                seconds[bundle] = total + AssetDatabase.LoadAssetAtPath<AudioClip>(path).length;
                counts.TryGetValue(bundle, out int count);
                counts[bundle] = count + 1;
            }
            long bytes = 0;
            foreach (var entry in seconds)
            {
                string file = Path.Combine(directory, entry.Key);
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException("构建后音频包缺失", file);
                }
                long actual = new FileInfo(file).Length;
                // 多段短音效各有编码头/对齐成本；每段额外4KB预算，不能把几十段声音当成一条连续流估算。
                long budget = 32768L + counts[entry.Key]*4096L + (long)Math.Ceiling(entry.Value * (entry.Key.StartsWith("audio_bgm", StringComparison.Ordinal) ? 12000 : 4000));
                if (actual > budget)
                {
                    throw new InvalidOperationException("音频包超预算：" + entry.Key + "，实际" + actual + "B，上限" + budget + "B，请检查压缩/声道/资源归属");
                }
                bytes += actual;
            }
            return "音频" + seconds.Count + "包，" + bytes + "B，时长归一化体积门禁通过";
        }
        /// <summary>只读检查已构建两个平台产物；缺平台或超过预算明确失败，不以导入设置冒充包体已达标。</summary>
        [RevAutomationAction("game.audio.budget-check", Environment = "edit", Keys = new string[0], Description = "检查已构建PC/Android音频包的时长归一化体积预算")]
        public static object BudgetCheck(RevAutomationRequest request)
        {
            return new Dictionary<string, object>
            {
                ["pc"] = ValidateBundleBudget(Path.GetFullPath("AssetBundles/PC")),
                ["android"] = ValidateBundleBudget(Path.GetFullPath("AssetBundles/Android"))
            };
        }
    }
}
