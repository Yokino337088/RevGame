// ============================================================
// GameUiPreviewAutomation.cs —— 编辑态 UI 预制体离屏截图（新手先读这段）
//
// 用途：在“预览场景”（EditorSceneManager.NewPreviewScene，不出现在层级面板、不改用户打开的场景）里
// 实例化一个 UI 预制体，用独立相机渲染到 1920×1080 的 RenderTexture 再存 PNG。
// 得到的画面就是编辑器里看到的样子，用来确认/验证“预制体显示成白块”这类编辑态问题，不靠人眼猜。
// 只读：不保存预制体，结束时关闭预览场景并销毁临时对象。
// ============================================================
using System;
using System.IO;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevGame.Editor
{
    public static class GameUiPreviewAutomation
    {
        [Serializable] private sealed class Args
        {
            public string prefab;
            public string output;
        }
        [Serializable] private sealed class Result
        {
            public string output;
            public int width, height;
        }

        [RevAutomationAction("game.ui.preview-capture", Environment = "edit", Keys = new[] { "prefab", "output" }, Description = "预览场景里离屏渲染一个UI预制体并存PNG，不改用户场景与资产")]
        public static object Capture(RevAutomationRequest request)
        {
            Args args = JsonUtility.FromJson<Args>(request.payloadJson);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(args.prefab);
            if (prefab == null)
            {
                throw new InvalidOperationException("预制体不存在：" + args.prefab);
            }
            const int width = 1920, height = 1080;
            Scene scene = EditorSceneManager.NewPreviewScene();
            var texture = new RenderTexture(width, height, 24);
            try
            {
                var cameraObject = new GameObject("预览相机", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.scene = scene;
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.13f, .2f, .3f);
                camera.targetTexture = texture;
                var canvasObject = new GameObject("预览画布", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(canvasObject, scene);
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 10;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.SetParent(canvasObject.transform, false);
                var rect = instance.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                }
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = texture;
                var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();
                RenderTexture.active = previous;
                Directory.CreateDirectory(Path.GetDirectoryName(args.output));
                File.WriteAllBytes(args.output, pixels.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(pixels);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                UnityEngine.Object.DestroyImmediate(texture);
            }
            return new Result { output = args.output, width = width, height = height };
        }
    }
}
