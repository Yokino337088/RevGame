using System;
using System.Collections.Generic;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>章节BGM制作与验证。压缩只发生在Unity导入阶段；播放仍通过RevSound和框架资源映射。</summary>
    public static class GameChapterAudioBuild
    {
        // 资源构建asmdef不能引用默认Assembly-CSharp；此处的固定名字属于资源制作清单。
        private static readonly string[] Names = { "Chapter01_CafeByTheSea", "Chapter02_AzureBlue", "Chapter03_ToTheSouth", "Chapter04_Adventure", "Chapter05_IceWalls", "Chapter06_Swing" };
        /// <summary>统一默认与PC/Android的长音乐设置；质量0.5为包体与听感的折中，保持立体声和原采样率。</summary>
        internal static void Configure(Dictionary<string, string> desired)
        {
            for (int chapter = 1; chapter <= Names.Length; chapter++)
            {
                string path = "Assets/GameRes/Audio/Bgm/" + Names[chapter - 1] + ".mp3";
                desired[path] = "audio_bgm_chapter0" + chapter;
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;

                if (importer == null)
                {
                    throw new InvalidOperationException("章节音乐缺失：" + path);
                }
                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.Streaming;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.5f;
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                importer.defaultSampleSettings = settings;
                importer.SetOverrideSampleSettings("Standalone", settings);
                importer.SetOverrideSampleSettings("Android", settings);
                importer.forceToMono = false;
                importer.loadInBackground = true;
                importer.SaveAndReimport();
            }
        }
        /// <summary>只读导入、时长与六章绑定断言，不切换当前场景，不启动Play。</summary>
        [RevAutomationAction("game.audio.check", Environment = "edit", Keys = new string[0], Description = "验证六章音乐绑定、流式Vorbis设置和有效音频时长")]
        public static object Check(RevAutomationRequest request)
        {
            var rows = new List<string>();
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                string name = Names[chapter - 1];
                // 与真实业务映射对照，防止制作清单和运行时各自改名后错播。反射目标固定，不接受外部输入。
                Type audioType = Type.GetType("RevGame.View.GameAudio, Assembly-CSharp", true);
                string runtimeName = (string)audioType.GetMethod("GetChapterMusicName").Invoke(null, new object[] { chapter });

                if (runtimeName != name)
                {
                    throw new InvalidOperationException("制作与运行时音乐绑定不一致：" + chapter);
                }
                string path = "Assets/GameRes/Audio/Bgm/" + name + ".mp3";
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);

                if (clip == null || clip.length < 1f || clip.channels != 2)
                {
                    throw new InvalidOperationException("无效音乐：" + path);
                }
                if (chapter <= Names.Length)
                {
                    var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                    AudioImporterSampleSettings settings = importer.defaultSampleSettings;

                    if (settings.loadType != AudioClipLoadType.Streaming || settings.compressionFormat != AudioCompressionFormat.Vorbis)
                    {
                        throw new InvalidOperationException("音乐未按流式Vorbis导入：" + path);
                    }
                    if (importer.assetBundleName != "audio_bgm_chapter0" + chapter)
                    {
                        throw new InvalidOperationException("音乐未独立分包：" + path);
                    }
                    foreach (string platform in new[] { "Standalone", "Android" })
                    {
                        AudioImporterSampleSettings target = importer.GetOverrideSampleSettings(platform);

                        if (target.loadType != AudioClipLoadType.Streaming || target.compressionFormat != AudioCompressionFormat.Vorbis
                            || Mathf.Abs(target.quality - 0.5f) > 0.001f || target.sampleRateSetting != AudioSampleRateSetting.PreserveSampleRate)
                        {
                            throw new InvalidOperationException("音乐平台压缩设置不一致：" + platform + "/" + path);
                        }
                    }
                }
                rows.Add(chapter + ":" + name + "," + clip.length.ToString("F2") + "秒");
            }
            return new Result { text = string.Join("；", rows) };
        }
        [Serializable]
        private sealed class Result
        {
            public string text;
        }
    }
}
