using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    /// <summary>通过 Unity 的 V2 源资产接口维护图集成员。图集分组、导入规格和 AB 归属由调用方决定。</summary>
    public static class RevAutomationSpriteAtlas
    {
        [RevAutomationAction("atlas.inspect", Environment = "either", Keys = new[] { "path" },
            Description = "读取 Sprite Atlas V2 的 GUID、成员和 Include In Build")]
        public static object Inspect(RevAutomationRequest request)
        {
            return Describe(AtlasPath(request, false), out _, out _);
        }

        [RevAutomationAction("atlas.create", Keys = new[] { "path", "assets" },
            Description = "用已导入为 Sprite 的纹理创建 Sprite Atlas V2，不覆盖已有资产")]
        public static object Create(RevAutomationRequest request)
        {
            return Save(request, true);
        }

        [RevAutomationAction("atlas.set-packables", Keys = new[] { "path", "assets" }, Confirm = true,
            Description = "以指定 Sprite 纹理完整替换主图集成员，保留 GUID 和 Importer 设置，需要 confirm")]
        public static object SetPackables(RevAutomationRequest request)
        {
            return Save(request, false);
        }

        private static object Save(RevAutomationRequest request, bool create)
        {
            string path = AtlasPath(request, true);
            if (EditorSettings.spritePackerMode != SpritePackerMode.SpriteAtlasV2 &&
                EditorSettings.spritePackerMode != SpritePackerMode.SpriteAtlasV2Build)
            {
                throw new InvalidOperationException("工程需要启用 Sprite Atlas V2。该动作不会修改全局打包模式。");
            }

            Object[] previous = Array.Empty<Object>();
            object previousResult = null;
            if (create)
            {
                RevAutomationEditorModel.RequireNewAsset(path);
            }
            else
            {
                previousResult = Describe(path, out SpriteAtlas atlas, out previous);
                if (atlas.isVariant || previous.Any(item => item == null))
                {
                    throw new ArgumentException("仅可修改成员完整的主图集：" + path);
                }
            }

            // 检查完整成员列表后才触碰源资产。用对象身份比较，避免路径大小写绕过重复检查。
            Object[] desired = ReadTextures(request);
            if (!create && previous.SequenceEqual(desired))
            {
                return previousResult;
            }

            SpriteAtlasAsset source = create ? new SpriteAtlasAsset() : SpriteAtlasAsset.Load(path);
            if (source == null)
            {
                throw new InvalidOperationException("无法读取 Sprite Atlas V2 源资产：" + path);
            }
            try
            {
                source.Remove(previous);
                source.Add(desired);
                try
                {
                    SaveAndImport(source, path);
                    object result = Describe(path, out _, out Object[] actual);
                    if (!actual.SequenceEqual(desired))
                    {
                        throw new InvalidOperationException("图集导入后的成员与请求不一致：" + path);
                    }
                    return result;
                }
                catch (Exception saveError)
                {
                    try
                    {
                        if (create)
                        {
                            if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                            {
                                throw new IOException("无法清理新图集：" + path);
                            }
                        }
                        else
                        {
                            source.Remove(desired);
                            source.Add(previous);
                            SaveAndImport(source, path);
                        }
                    }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException("图集保存和恢复均失败：" + path, saveError, restoreError);
                    }
                    throw;
                }
            }
            finally
            {
                // Load 返回的是可编辑的 V2 源对象，AssetDatabase 中的导入产物由 Unity 持有。
                Object.DestroyImmediate(source);
            }
        }

        private static string AtlasPath(RevAutomationRequest request, bool write)
        {
            string path = RevAutomationEditorModel.AssetPath(RevAutomationEditorOptions.Read(request).path, write);
            if (!path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("需要 .spriteatlasv2 路径：" + path);
            }
            return path;
        }

        private static Object[] ReadTextures(RevAutomationRequest request)
        {
            var payload = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson));
            if (!payload.TryGetValue("assets", out object value) || !(value is List<object> entries))
            {
                throw new ArgumentException("assets 必须是 Sprite 纹理路径数组。");
            }
            var textures = new List<Object>();
            var seen = new HashSet<Object>();
            foreach (object entry in entries)
            {
                string path = RevAutomationEditorModel.AssetPath(entry as string, false);
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null || !(AssetImporter.GetAtPath(path) is TextureImporter importer) ||
                    importer.textureType != TextureImporterType.Sprite || !seen.Add(texture))
                {
                    throw new ArgumentException("成员必须是已导入为 Sprite 且不重复的纹理：" + path);
                }
                textures.Add(texture);
            }
            return textures.ToArray();
        }

        private static void SaveAndImport(SpriteAtlasAsset source, string path)
        {
            SpriteAtlasAsset.Save(source, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static object Describe(string path, out SpriteAtlas atlas, out Object[] members)
        {
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null || !(AssetImporter.GetAtPath(path) is SpriteAtlasImporter importer))
            {
                throw new ArgumentException("找不到已导入的 Sprite Atlas V2：" + path);
            }
            members = atlas.GetPackables();
            return new Dictionary<string, object>
            {
                ["path"] = AssetDatabase.GetAssetPath(atlas),
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["isVariant"] = atlas.isVariant,
                ["includeInBuild"] = importer.includeInBuild,
                ["packables"] = members.Select(item => item == null ? null : new Dictionary<string, object>
                {
                    ["path"] = AssetDatabase.GetAssetPath(item),
                    ["name"] = item.name,
                    ["type"] = item.GetType().Name
                }).ToArray()
            };
        }
    }
}
