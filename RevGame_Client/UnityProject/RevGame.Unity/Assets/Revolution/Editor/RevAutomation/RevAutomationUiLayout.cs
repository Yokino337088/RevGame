using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution.Editor
{
    /// <summary>只读取 UI 布局和材质采样事实，供编辑态排版、Prefab 白块和 Canvas 排序排查。</summary>
    public static class RevAutomationUiLayout
    {
        [Serializable]
        private sealed class Node
        {
            public string id, name, drivenBy;
            public int siblingIndex;
            public bool activeSelf, activeInHierarchy;
            public Vector3 localScale, lossyScale;
            public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax;
            public float[] rect;
            public Group[] groups;
        }

        [Serializable]
        private sealed class Group
        {
            public float alpha;
            public bool enabled, interactable, blocksRaycasts, ignoreParentGroups;
        }

        [Serializable]
        private sealed class CanvasInfo
        {
            public string id, path, renderMode, sortingLayer, camera;
            public int sortingLayerId, sortingOrder, renderOrder, targetDisplay;
            public float scaleFactor;
            public bool overrideSorting;
        }

        [Serializable]
        private sealed class Sample
        {
            public string component, assignedImage, mainTexture, material, shader;
            public bool enabled, raycastTarget, defaultWhiteTexture, packed;
            public float alpha;
            public int textureWidth, textureHeight;
        }

        [Serializable]
        private sealed class Result
        {
            public string objectPath, scene, prefabStage, projection;
            public Node[] targetToRoot;
            public CanvasInfo canvas, rootCanvas;
            public Sample graphic;
            public Vector2[] screenCorners;
            public float[] screenAabb;
        }

        [RevAutomationAction("ui.layout", Environment = "either", Keys = new[] { "id", "objectPath", "scene" },
            Description = "读排版父链、锚点和Canvas排序、纹理绑定及可投影时的屏幕范围；支持Prefab Mode的实例id")]
        public static object Inspect(RevAutomationRequest request)
        {
            GameObject go = RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request));
            if (EditorUtility.IsPersistent(go) || !go.scene.IsValid() || !(go.transform is RectTransform rect))
            {
                throw new ArgumentException("请选择场景或 Prefab Mode 中带 RectTransform 的对象");
            }
            var parents = new List<Node>();
            for (Transform node = rect; node != null; node = node.parent)
            {
                if (parents.Count == 256)
                {
                    throw new ArgumentException("UI 父链超过 256 层");
                }
                parents.Add(DescribeNode(node));
            }
            var canvas = rect.GetComponentInParent<Canvas>(true);
            var root = canvas == null ? null : canvas.rootCanvas;
            var result = new Result
            {
                objectPath = RevAutomationEditorModel.HierarchyPath(rect), scene = go.scene.path,
                prefabStage = PrefabStageUtility.GetPrefabStage(go)?.assetPath, targetToRoot = parents.ToArray(),
                canvas = DescribeCanvas(canvas), rootCanvas = DescribeCanvas(root), graphic = DescribeGraphic(rect.GetComponent<Graphic>())
            };
            Project(rect, root, result);
            return result;
        }

        private static Node DescribeNode(Transform node)
        {
            var info = new Node
            {
                id = RevAutomationEditorModel.Identity(node.gameObject), name = node.name, siblingIndex = node.GetSiblingIndex(),
                activeSelf = node.gameObject.activeSelf, activeInHierarchy = node.gameObject.activeInHierarchy,
                localScale = node.localScale, lossyScale = node.lossyScale,
                groups = node.GetComponents<CanvasGroup>().Select(group => new Group
                {
                    alpha = group.alpha, enabled = group.enabled, interactable = group.interactable,
                    blocksRaycasts = group.blocksRaycasts, ignoreParentGroups = group.ignoreParentGroups
                }).ToArray()
            };
            if (node is RectTransform rect)
            {
                info.anchorMin = rect.anchorMin;
                info.anchorMax = rect.anchorMax;
                info.pivot = rect.pivot;
                info.anchoredPosition = rect.anchoredPosition;
                info.sizeDelta = rect.sizeDelta;
                info.offsetMin = rect.offsetMin;
                info.offsetMax = rect.offsetMax;
                info.rect = ToArray(rect.rect);
                var driver = rect.drivenByObject;
                info.drivenBy = driver == null ? null : driver.name;
            }
            return info;
        }

        private static CanvasInfo DescribeCanvas(Canvas canvas)
        {
            if (canvas == null)
            {
                return null;
            }
            return new CanvasInfo
            {
                id = RevAutomationEditorModel.Identity(canvas.gameObject), path = RevAutomationEditorModel.HierarchyPath(canvas.transform),
                renderMode = canvas.renderMode.ToString(), sortingLayer = canvas.sortingLayerName, sortingLayerId = canvas.sortingLayerID,
                sortingOrder = canvas.sortingOrder, renderOrder = canvas.renderOrder, targetDisplay = canvas.targetDisplay,
                scaleFactor = canvas.scaleFactor, overrideSorting = canvas.overrideSorting,
                camera = canvas.worldCamera == null ? null : RevAutomationEditorModel.Identity(canvas.worldCamera.gameObject)
            };
        }

        private static Sample DescribeGraphic(Graphic graphic)
        {
            if (graphic == null)
            {
                return null;
            }
            Texture texture = graphic.mainTexture;
            var material = graphic.material;
            var sample = new Sample
            {
                component = graphic.GetType().FullName, enabled = graphic.enabled, alpha = graphic.color.a, raycastTarget = graphic.raycastTarget,
                defaultWhiteTexture = texture == Texture2D.whiteTexture,
                mainTexture = texture == null ? null : texture.name, textureWidth = texture == null ? 0 : texture.width,
                textureHeight = texture == null ? 0 : texture.height, material = material == null ? null : material.name,
                shader = material == null || material.shader == null ? null : material.shader.name
            };
            if (graphic is Image image && image.overrideSprite != null)
            {
                sample.assignedImage = image.overrideSprite.name;
                sample.packed = image.overrideSprite.packed;
            }
            else if (graphic is RawImage raw && raw.texture != null)
            {
                sample.assignedImage = raw.texture.name;
            }
            return sample;
        }

        // 使用实际 Canvas 相机与四角。AABB 只表示投影范围，不能据此判断遮挡或点击。
        private static void Project(RectTransform rect, Canvas canvas, Result result)
        {
            if (canvas == null)
            {
                result.projection = "no_canvas";
                return;
            }
            if (canvas.targetDisplay != 0)
            {
                result.projection = "unsupported_display";
                return;
            }
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null)
            {
                result.projection = "camera_required";
                return;
            }
            if (camera != null && (camera.targetTexture != null || camera.targetDisplay != 0))
            {
                result.projection = "offscreen_camera";
                return;
            }

            var corners = new Vector3[4];
            var points = new Vector2[4];
            rect.GetWorldCorners(corners);
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = camera == null ? corners[i] : camera.WorldToScreenPoint(corners[i]);
                if (camera != null && point.z < camera.nearClipPlane)
                {
                    result.projection = "near_plane_intersection";
                    return;
                }
                if (float.IsNaN(point.x) || float.IsInfinity(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.y))
                {
                    result.projection = "invalid_geometry";
                    return;
                }
                points[i] = point;
            }
            result.projection = "projected";
            result.screenCorners = points;
            result.screenAabb = ToArray(Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y)));
        }

        private static float[] ToArray(Rect rect)
        {
            return new[] { rect.x, rect.y, rect.width, rect.height };
        }
    }
}
