using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public sealed partial class GameUiPrefabPreview
    {
        /// <summary>扫描 Assets 内所有 Prefab，包括目录外的 UI 与工厂部件；只为疑似白块加载隔离实例。</summary>
        public static object Audit()
        {
            var result = new AuditState();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab", StringComparison.Ordinal))
                {
                    continue;
                }
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                {
                    continue;
                }
                result.prefabs++;
                var blank = new List<string>();
                var graphics = root.GetComponentsInChildren<Graphic>(true);
                if (graphics.Length > 0)
                {
                    result.uiPrefabs++;
                }
                foreach (Graphic graphic in graphics)
                {
                    if (graphic is not (Image or RawImage))
                    {
                        continue;
                    }
                    result.images++;
                    if (!IsVisible(graphic))
                    {
                        result.hiddenImages++;
                        continue;
                    }
                    string name = AnimationUtility.CalculateTransformPath(graphic.transform, root.transform);
                    if (HasAssignedImage(graphic) && !HasPixels(graphic))
                    {
                        result.missingTexture.Add(path + " / " + name);
                    }
                    else if (IsBlankWhite(graphic))
                    {
                        blank.Add(name);
                    }
                }
                if (blank.Count > 0)
                {
                    CheckSamples(path, blank, result);
                }
            }
            return result;
        }

        private static void CheckSamples(string path, List<string> blank, AuditState result)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                using var preview = Attach(path, root);
                foreach (string name in blank)
                {
                    var node = name.Length == 0 ? root.transform : root.transform.Find(name);
                    var graphic = node.GetComponent<Graphic>();
                    bool filled = (HasAssignedImage(graphic) && HasPixels(graphic))
                        || (preview?._backdrop != null && preview._backdrop.transform.parent == node && HasPixels(preview._backdrop));
                    (filled ? result.previewed : result.unresolved).Add(path + " / " + name);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool HasAssignedImage(Graphic graphic)
        {
            return graphic is Image image ? image.overrideSprite != null : ((RawImage)graphic).texture != null;
        }

        private static bool HasPixels(Graphic graphic)
        {
            Texture texture = graphic.mainTexture;
            return texture != null && texture != Texture2D.whiteTexture && texture.width > 1 && texture.height > 1;
        }

        private static bool IsBlankWhite(Graphic graphic)
        {
            // 深色底板与使用独立采样的 Shader 可以有意没有 Sprite。只筛默认材质的近白色空白。
            Color tint = graphic.color;
            return !HasAssignedImage(graphic) && graphic.material == graphic.defaultMaterial
                && tint.r >= .95f && tint.g >= .95f && tint.b >= .95f;
        }

        private static bool IsVisible(Graphic graphic)
        {
            // 资产对象没有实例场景，activeInHierarchy 对它们一律为 false；读取序列化启用状态。
            if (!graphic.enabled || graphic.color.a <= .001f)
            {
                return false;
            }
            bool ignoreParentGroups = false;
            for (Transform node = graphic.transform; node != null; node = node.parent)
            {
                if (!node.gameObject.activeSelf)
                {
                    return false;
                }
                if (ignoreParentGroups)
                {
                    continue;
                }
                bool ignoreParent = false;
                foreach (CanvasGroup group in node.GetComponents<CanvasGroup>())
                {
                    if (group.enabled)
                    {
                        if (group.alpha <= .001f)
                        {
                            return false;
                        }
                        ignoreParent |= group.ignoreParentGroups;
                    }
                }
                ignoreParentGroups = ignoreParent;
            }
            return true;
        }

        [Serializable]
        private sealed class AuditState
        {
            public int prefabs, uiPrefabs, images, hiddenImages;
            public List<string> previewed = new List<string>();
            public List<string> unresolved = new List<string>();
            public List<string> missingTexture = new List<string>();
        }
    }
}
