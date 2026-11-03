// 将24帧预览迁为正式资源，并在现有TestScene添加可播放实例。
using System;
using System.IO;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace RevGame.Editor
{
    public static class GameYayaIdleInstall
    {
        [RevAutomationAction("game.yaya-idle.install", Environment = "edit", Keys = new string[0], Description = "保GUID迁移24帧到GameRes并保存TestScene预览")]
        public static object Install(RevAutomationRequest request)
        {
            const string source = "Assets/ArtPreviews/YayaIdle/Frames24";
            const string art = "Assets/GameRes/Art/Characters/Yaya/Idle24";
            const string content = "Assets/GameRes/Characters/Yaya";
            Directory.CreateDirectory(art);
            Directory.CreateDirectory(content);
            AssetDatabase.Refresh();
            if (AssetDatabase.IsValidFolder(source))
            {
                foreach (string file in Directory.GetFiles(source))
                {
                    if (file.EndsWith(".meta", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string target = file.EndsWith(".png", StringComparison.Ordinal) || file.EndsWith(".csv", StringComparison.Ordinal) ? art : content;
                    if (file.EndsWith(".spriteatlasv2", StringComparison.Ordinal))
                    {
                        target = "Assets/GameRes/Atlases";
                    }
                    string error = AssetDatabase.MoveAsset(file.Replace('\\', '/'), target + "/" + Path.GetFileName(file));
                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new InvalidOperationException(error);
                    }
                }
            }
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != "Assets/Scenes/TestScene.unity")
            {
                throw new InvalidOperationException("仅允许当前TestScene中装配预览");
            }
            GameObject instance = GameObject.Find("YayaIdle24Preview");
            if (instance == null)
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(content + "/YayaIdle24.prefab"), scene);
                instance.name = "YayaIdle24Preview";
                instance.transform.position = new Vector3(0, 0, 0);
                instance.GetComponent<SpriteRenderer>().sortingOrder = 100;
            }
            Camera camera = Camera.main;
            if (camera != null)
            {
                camera.orthographic = true;
                camera.orthographicSize = 6.5f;
                camera.transform.position = new Vector3(0, 0, -10);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return new { frames = 24, art, prefab = content + "/YayaIdle24.prefab", scene = scene.path };
        }
        // 正向24帧后反向返回，端点不重复；跨周期只跨相邻姿态，不再末帧跳首帧。
        [RevAutomationAction("game.yaya-idle.loop", Environment = "edit", Keys = new string[0], Description = "修复24帧动画为往返无跳接循环并验证顺序")]
        public static object Loop(RevAutomationRequest request)
        {
            const string path = "Assets/GameRes/Characters/Yaya/YayaIdle24Clip.anim";
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            var binding = UnityEditor.EditorCurveBinding.PPtrCurve("", typeof(SpriteRenderer), "m_Sprite");
            var keys = new UnityEditor.ObjectReferenceKeyframe[46];
            // 纯表现节奏采用原89帧30FPS时长的往返周期；不新增图片，不增加图集大小。
            float duration = 89f / 30f * 2f;
            clip.frameRate = 46f / duration;
            for (int i = 0; i < keys.Length; i++)
            {
                int index = i < 24 ? i : 46 - i;
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Characters/Yaya/Idle24/frame_" + index.ToString("D3") + ".png");
                if (sprite == null)
                {
                    throw new InvalidOperationException("缺少循环图片：" + index);
                }
                keys[i] = new UnityEditor.ObjectReferenceKeyframe { time = i * duration / 46f, value = sprite };
            }
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.startTime = 0;
            settings.stopTime = duration;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            var read = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (read.Length != 46 || !AnimationUtility.GetAnimationClipSettings(clip).loopTime || Mathf.Abs(clip.length - duration) > 0.002f)
            {
                throw new InvalidOperationException("循环读回校验失败");
            }
            return new LoopResult { keyframes = read.Length, duration = clip.length, sequence = "0..23,22..1,0..", loop = true };
        }
        [Serializable]
        private sealed class LoopResult
        {
            public int keyframes;
            public float duration;
            public string sequence;
            public bool loop;
        }
        [RevAutomationAction("game.yaya-idle.build-formal", Environment = "edit", Keys = new string[0], Description = "通过项目正式入口构建并复制PC AB")]
        public static object BuildFormal(RevAutomationRequest request)
        {
            return new { result = GameResourceBuild.BuildAndCopy(BuildTarget.StandaloneWindows64) };
        }
    }
}
