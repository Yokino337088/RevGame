using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>
    /// 在独立预览场景中用真实UGUI网格与共享材质绘制HUD。只读资源，不保存用户场景；
    /// 纹理非空并不代表GPU画面正确，因此同时保存像素证据和每个贴图控件的采样信息。
    /// 临时场景、相机、RenderTexture和读回纹理均由本次检查持有，异常时也统一清理。
    /// </summary>
    public static class GameUiPreviewChecks
    {
        public static string Capture()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("预览检查只能在编辑态运行"); }
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                var cameraObject = new GameObject("UI预览验收相机", typeof(Camera));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 540;
                camera.transform.position = new Vector3(0, 0, -100);
                camera.nearClipPlane = 1;
                camera.farClipPlane = 500;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.035f, .065f, .1f, 1);
                camera.allowHDR = camera.allowMSAA = false;
                camera.scene = scene;
                target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;

                var canvasObject = new GameObject("UI预览验收画布", typeof(RectTransform), typeof(Canvas));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(canvasObject, scene);
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1;
                ((RectTransform)canvas.transform).sizeDelta = new Vector2(1920, 1080);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameRes/UI/Panel/BattleHudPanel.prefab");
                var root = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                root.transform.SetParent(canvas.transform, false);
                root.transform.localPosition = Vector3.zero;
                root.transform.localScale = Vector3.one;
                RectTransform rect = (RectTransform)root.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.sizeDelta = Vector2.zero;
                rect.anchoredPosition = Vector2.zero;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = target;
                readback = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                readback.Apply();
                Directory.CreateDirectory("Temp/UiPreview");
                File.WriteAllBytes("Temp/UiPreview/hud.png", readback.EncodeToPNG());
                var lines = new List<string>();
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                lines.Add("打开的Prefab=" + (stage == null ? "无" : stage.assetPath));
                foreach (SceneView view in SceneView.sceneViews)
                {
                    lines.Add("SceneView=" + view.titleContent.text + " mode=" + view.cameraMode.name + " drawMode=" + view.cameraMode.drawMode);
                }
                foreach (Image item in root.GetComponentsInChildren<Image>(true))
                {
                    if (item.sprite == null) { continue; }
                    Texture texture = item.mainTexture;
                    Vector4 uv = UnityEngine.Sprites.DataUtility.GetOuterUV(item.sprite);
                    lines.Add(item.name + " texture=" + (texture == null ? "null" : texture.name + "/" + texture.width + "x" + texture.height)
                        + " packed=" + item.sprite.packed + " uv=" + uv.ToString("F4") + " material=" + item.materialForRendering.name);
                }
                string result = "真实UGUI预览：Temp/UiPreview/hud.png\n" + string.Join("\n", lines);
                File.WriteAllText("Temp/UiPreview/state.txt", result);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                EditorSceneManager.ClosePreviewScene(scene);
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                if (readback != null) { Object.DestroyImmediate(readback); }
            }
        }
    }
}
