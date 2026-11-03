// 鸦鸦待机抽帧预览：只生成独立预览资产，不修改大厅或正式资源分包。
using System;
using System.IO;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameYayaIdlePreviewBuild
    {
        // 原图没有帧率元数据，暂按30帧/秒。属于美术播放参数，不是玩法数值。
        private const float SourceFps = 30f;
        private const string Root = "Assets/ArtPreviews/YayaIdle";

        [RevAutomationAction("game.yaya-idle.build", Environment = "edit", Keys = new string[0], Description = "从89张原图生成24/16帧独立Sprite动画与Prefab")]
        public static object Build(RevAutomationRequest request)
        {
            string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../.."));
            string source = Path.Combine(repo, "RevGame_Client/美术/AI生成/新版/鸦鸦待机摇_透明序列帧");
            string[] files = Directory.GetFiles(source, "frame_*.png");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length != 89)
            {
                throw new InvalidOperationException("原图数量变化，请重新核对抽帧输入：" + files.Length);
            }
            Directory.CreateDirectory(Root);
            Result result = new Result();
            result.sourceFrames = files.Length;
            result.duration = files.Length / SourceFps;
            result.version24 = Create(files, 24, result.duration);
            result.version16 = Create(files, 16, result.duration);
            AssetDatabase.SaveAssets();
            return result;
        }

        // 均匀覆盖完整周期；Unity会给最后一张保留一个采样间隔，因此不追加重复首图。
        private static string Create(string[] files, int count, float duration)
        {
            string folder = Root + "/Frames" + count;
            string clipPath = folder + "/YayaIdle" + count + ".anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) != null)
            {
                throw new InvalidOperationException("已有预览动画，拒绝覆盖：" + clipPath);
            }
            Directory.CreateDirectory(folder);
            Sprite[] sprites = new Sprite[count];
            string manifest = "序号,原始帧,时间秒\n";
            for (int i = 0; i < count; i++)
            {
                int index = Mathf.RoundToInt(i * files.Length / (float)count);
                string imagePath = folder + "/frame_" + i.ToString("D3") + ".png";
                File.Copy(files[index], imagePath, false);
                AssetDatabase.ImportAsset(imagePath, ImportAssetOptions.ForceSynchronousImport);
                TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(imagePath);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100;
                importer.spritePivot = new Vector2(0.5f, 0.5f);
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = 2048;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                sprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>(imagePath);
                manifest += i + "," + Path.GetFileName(files[index]) + "," + (i * duration / count).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "\n";
            }
            File.WriteAllText(folder + "/选帧清单.csv", manifest, new System.Text.UTF8Encoding(true));
            AnimationClip clip = new AnimationClip { name = "YayaIdle" + count, frameRate = count / duration };
            ObjectReferenceKeyframe[] keys = new ObjectReferenceKeyframe[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = new ObjectReferenceKeyframe { time = i * duration / count, value = sprites[i % count] };
            }
            EditorCurveBinding binding = EditorCurveBinding.PPtrCurve("", typeof(SpriteRenderer), "m_Sprite");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, clipPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/YayaIdle" + count + ".controller");
            controller.AddMotion(clip);
            GameObject preview = new GameObject("YayaIdle" + count);
            try
            {
                preview.AddComponent<SpriteRenderer>().sprite = sprites[0];
                preview.AddComponent<Animator>().runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(preview, folder + "/YayaIdle" + count + ".prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(preview);
            }
            return folder;
        }

        [RevAutomationAction("game.yaya-idle.check", Environment = "edit", Keys = new string[0], Description = "核对抽帧数量、Sprite曲线、循环时长与Prefab")]
        public static object Check(RevAutomationRequest request)
        {
            foreach (int count in new[] { 24, 16 })
            {
                string folder = Root + "/Frames" + count;
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + "/YayaIdle" + count + ".anim");
                if (clip == null)
                {
                    throw new InvalidOperationException("缺少动画：" + folder);
                }
                EditorCurveBinding[] bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, bindings[0]);
                // 修复早期预览多追加的终点帧，只改本工具独占的预览资产。
                if (keys.Length == count + 1)
                {
                    Array.Resize(ref keys, count);
                    AnimationUtility.SetObjectReferenceCurve(clip, bindings[0], keys);
                    AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.stopTime = 89f / SourceFps;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                    EditorUtility.SetDirty(clip);
                    AssetDatabase.SaveAssets();
                }
                if (keys.Length != count || Mathf.Abs(clip.length - 89f / SourceFps) > 0.002f || !AnimationUtility.GetAnimationClipSettings(clip).loopTime)
                {
                    throw new InvalidOperationException("帧数/时长/循环校验失败：" + folder);
                }
                for (int i = 0; i < keys.Length; i++)
                {
                    if (keys[i].value == null)
                    {
                        throw new InvalidOperationException("Sprite引用丢失：" + i);
                    }
                }
                if (AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/YayaIdle" + count + ".prefab") == null)
                {
                    throw new InvalidOperationException("缺少Prefab");
                }
            }
            return new { success = true, frames = new[] { 24, 16 }, duration = 89f / SourceFps, loop = true };
        }

        [Serializable]
        private sealed class Result
        {
            public int sourceFrames;
            public float duration;
            public string version24;
            public string version16;
        }
    }
}
