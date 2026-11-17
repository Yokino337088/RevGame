using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>RevCLI 与 RevMCP 的节点查询和修改入口。所有修改先在副本校验，再保存同一个 SO。</summary>
    public static class RevGalNodeAuthoring
    {
        // 传输层使用小写参数。document 内部的节点字段与剧情 JSON 使用同一契约。
        [Serializable]
        private sealed class Request
        {
            public string path;
            public string id;
            public string query;
            public string kind;
            public string document;
            public string redirect;
            public string expectedHash;
            public bool checkOnly;
            public int offset;
            public int limit;
        }

        /// <summary>在指定剧情里按精确 ID 或文本找节点，并给出可供改线使用的入边。</summary>
        [RevAutomationAction("gal.node.find", Environment = "edit", Keys = new[] { "path", "id", "query", "kind", "offset", "limit" },
            Description = "按精确 ID、类型或文本查剧情节点，返回节点数据、入边、剧情版本摘要")]
        public static object Find(RevAutomationRequest request)
        {
            var args = Read(request);
            var story = RevGalAssets.Read(args.path);
            var definition = story.Definition;
            int limit = args.limit == 0 ? 20 : args.limit;
            if (args.offset < 0 || limit < 1 || limit > 200)
            {
                throw new ArgumentException("offset 需要非负，limit 需要在 1 到 200 之间");
            }
            RevGalNodeKind? kind = null;
            if (!string.IsNullOrWhiteSpace(args.kind))
            {
                if (!Enum.TryParse(args.kind, true, out RevGalNodeKind value) || !Enum.IsDefined(typeof(RevGalNodeKind), value))
                {
                    throw new ArgumentException("未知剧情节点类型：" + args.kind);
                }
                kind = value;
            }

            string query = args.query?.Trim();
            var matches = (definition.Nodes ?? new List<RevGalNode>()).Where(node => node != null
                && (string.IsNullOrEmpty(args.id) || node.Id == args.id)
                && (!kind.HasValue || kind.Value == node.Kind)
                && Matches(node, query)).ToArray();
            return new Dictionary<string, object>
            {
                ["path"] = args.path, ["storyId"] = definition.Id, ["start"] = definition.Start, ["revision"] = definition.Revision,
                ["hash"] = Hash(JsonUtility.ToJson(definition)), ["dirty"] = EditorUtility.IsDirty(story),
                ["total"] = matches.Length, ["offset"] = args.offset, ["limit"] = limit,
                ["truncated"] = matches.Length - args.offset > limit,
                ["items"] = matches.Skip(args.offset).Take(limit).Select(node => new Dictionary<string, object>
                {
                    ["node"] = RevGalJson.NodeDocument(node),
                    ["incoming"] = Incoming(definition, node.Id).ToArray()
                }).ToArray()
            };
        }

        [RevAutomationAction("gal.node.add", Environment = "edit", Keys = new[] { "path", "document", "checkOnly", "expectedHash" },
            Description = "向现有剧情添加一个节点 JSON，整图校验后保存")]
        public static object Add(RevAutomationRequest request)
        {
            var args = Read(request);
            var operation = new Dictionary<string, object> { ["op"] = "add", ["node"] = ParseDocument(args) };
            return Edit(args, new[] { operation });
        }

        [RevAutomationAction("gal.node.update", Environment = "edit", Keys = new[] { "path", "id", "document", "checkOnly", "expectedHash" },
            Description = "按 ID 局部更新剧情节点，保留未指定字段")]
        public static object UpdateNode(RevAutomationRequest request)
        {
            var args = Read(request);
            var operation = new Dictionary<string, object> { ["op"] = "update", ["id"] = args.id, ["changes"] = ParseDocument(args) };
            return Edit(args, new[] { operation });
        }

        [RevAutomationAction("gal.node.remove", Environment = "edit", Keys = new[] { "path", "id", "redirect", "checkOnly", "expectedHash" },
            Description = "删除指定节点，可显式改接起点、下一节点与选项入边")]
        public static object Remove(RevAutomationRequest request)
        {
            var args = Read(request);
            var operation = new Dictionary<string, object> { ["op"] = "remove", ["id"] = args.id };
            if (args.redirect != null)
            {
                operation["redirect"] = args.redirect;
            }
            return Edit(args, new[] { operation });
        }

        [RevAutomationAction("gal.node.patch", Environment = "edit", Keys = new[] { "path", "document", "checkOnly", "expectedHash" },
            Description = "按顺序执行多项节点修改，可指定起点或存档修订，全部验证后一次保存")]
        public static object Patch(RevAutomationRequest request)
        {
            var args = Read(request);
            var patch = ParseDocument(args);
            CheckKeys(patch, "operations", "start", "revision", "stagePresets");
            if (!patch.TryGetValue("operations", out var value) || !(value is List<object> operations) ||
                operations.Count == 0 && patch.Count == 1)
            {
                throw new ArgumentException("节点修改文档需要 operations 数组，并至少包含一项修改");
            }
            return Edit(args, operations.Select(RevAutomationJson.Object), patch);
        }

        /// <summary>来源摘要用于保护查询与提交之间的修改。校验失败或者纯预检均不写资产。</summary>
        private static object Edit(Request args, IEnumerable<Dictionary<string, object>> operations, Dictionary<string, object> patch = null)
        {
            var story = RevGalAssets.Read(args.path);
            string original = JsonUtility.ToJson(story.Definition);
            string hash = Hash(original);
            if (!string.IsNullOrEmpty(args.expectedHash) && !string.Equals(hash, args.expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("剧情内容已变更，请用 gal.node.find 重新读取节点和 hash");
            }
            bool dirty = EditorUtility.IsDirty(story);
            var draft = RevGalJson.CopyDefinition(story.Definition);
            draft.Nodes ??= new List<RevGalNode>();
            var changes = operations.Select(operation => Apply(draft, operation)).ToArray();
            if (patch != null && patch.TryGetValue("stagePresets", out var stagePresets))
            {
                draft.StagePresets = RevGalJson.ReadStagePresets(stagePresets);
            }
            if (patch != null && patch.ContainsKey("start"))
            {
                draft.Start = ReadId(patch, "start");
            }
            if (patch != null && patch.TryGetValue("revision", out var value))
            {
                if (!(value is double revision) || revision < 1 || revision > int.MaxValue || revision != Math.Truncate(revision))
                {
                    throw new ArgumentException("revision 需要为正整数");
                }
                draft.Revision = (int)revision;
            }

            var result = RevGalAuthoring.Describe(args.path, draft);
            string proposed = JsonUtility.ToJson(draft);
            bool changed = !string.Equals(original, proposed, StringComparison.Ordinal);
            result["changed"] = changed;
            result["written"] = false;
            result["dirty"] = dirty;
            result["hash"] = hash;
            result["revision"] = draft.Revision;
            result["operations"] = changes;
            if (!(bool)result["valid"])
            {
                return new RejectedEdit(result);
            }
            if (!args.checkOnly && changed)
            {
                var saved = RevGalAssets.Write(args.path, draft, true);
                result["written"] = true;
                result["dirty"] = false;
                result["hash"] = Hash(JsonUtility.ToJson(saved.Definition));
            }
            return result;
        }

        private static Dictionary<string, object> Apply(RevGalDefinition draft, Dictionary<string, object> operation)
        {
            string verb = ReadId(operation, "op");
            switch (verb)
            {
                case "add":
                    CheckKeys(operation, "op", "node");
                    var created = RevGalJson.ReadNode(ReadObject(operation, "node"));
                    if (!RevGalValidation.IsId(created.Id) || draft.Nodes.Any(node => node?.Id == created.Id))
                    {
                        throw new ArgumentException("新增节点 ID 无效或已存在：" + created.Id);
                    }
                    draft.Nodes.Add(created);
                    return DescribeChange(verb, created.Id);
                case "update":
                    CheckKeys(operation, "op", "id", "changes");
                    var current = RequireNode(draft, ReadId(operation, "id"));
                    var fields = ReadObject(operation, "changes");
                    var updated = RevGalJson.PatchNode(current, fields);
                    if (updated.Id != current.Id)
                    {
                        throw new ArgumentException("节点 ID 用于已读和存档定位。局部修改时请保留：" + current.Id);
                    }
                    draft.Nodes[draft.Nodes.IndexOf(current)] = updated;
                    var updateResult = DescribeChange(verb, current.Id);
                    updateResult["fields"] = fields.Keys.ToArray();
                    return updateResult;
                case "remove":
                    CheckKeys(operation, "op", "id", "redirect");
                    var removed = RequireNode(draft, ReadId(operation, "id"));
                    draft.Nodes.Remove(removed);
                    var removeResult = DescribeChange(verb, removed.Id);
                    removeResult["incoming"] = Incoming(draft, removed.Id).ToArray();
                    if (operation.ContainsKey("redirect"))
                    {
                        string redirect = ReadId(operation, "redirect");
                        RequireNode(draft, redirect);
                        Redirect(draft, removed.Id, redirect);
                        removeResult["redirect"] = redirect;
                    }
                    return removeResult;
                default:
                    throw new ArgumentException("未知节点操作：" + verb);
            }
        }

        private static Dictionary<string, object> DescribeChange(string verb, string id)
        {
            return new Dictionary<string, object> { ["op"] = verb, ["id"] = id };
        }

        private static bool Matches(RevGalNode node, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }
            bool Contains(string text)
            {
                return text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return new[] { node.Id, node.Title, node.Speaker, node.SpeakerKey, node.Text, node.TextKey, node.Signal, node.GalleryId }
                .Any(Contains) || (node.Exits != null && node.Exits.Any(exit => Contains(exit?.Label) || Contains(exit?.TextKey)));
        }

        private static RevGalNode RequireNode(RevGalDefinition draft, string id)
        {
            var matches = draft.Nodes.Where(node => node?.Id == id).Take(2).ToArray();
            if (matches.Length != 1)
            {
                throw new ArgumentException(matches.Length == 0 ? "节点不存在：" + id : "节点 ID 重复，请先修复剧情：" + id);
            }
            return matches[0];
        }

        /// <summary>入边信息与删除改线使用相同的起点、Next 和 Exits.Target 契约。</summary>
        private static IEnumerable<Dictionary<string, object>> Incoming(RevGalDefinition definition, string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                yield break;
            }
            if (definition.Start == target)
            {
                yield return new Dictionary<string, object> { ["field"] = "Start" };
            }
            foreach (var node in definition.Nodes)
            {
                if (node == null)
                {
                    continue;
                }
                if (node.Next == target)
                {
                    yield return new Dictionary<string, object> { ["node"] = node.Id, ["field"] = "Next" };
                }
                foreach (var exit in node.Exits ?? new List<RevGalEdge>())
                {
                    if (exit?.Target == target)
                    {
                        yield return new Dictionary<string, object> { ["node"] = node.Id, ["field"] = "Target", ["exit"] = exit.Id };
                    }
                }
            }
        }

        private static void Redirect(RevGalDefinition draft, string id, string target)
        {
            if (draft.Start == id)
            {
                draft.Start = target;
            }
            foreach (var node in draft.Nodes.Where(node => node != null))
            {
                if (node.Next == id)
                {
                    node.Next = target;
                }
                foreach (var exit in node.Exits ?? new List<RevGalEdge>())
                {
                    if (exit?.Target == id)
                    {
                        exit.Target = target;
                    }
                }
            }
        }

        private static Request Read(RevAutomationRequest request)
        {
            return JsonUtility.FromJson<Request>(request.payloadJson);
        }

        private static Dictionary<string, object> ParseDocument(Request args)
        {
            if (string.IsNullOrWhiteSpace(args.document))
            {
                throw new ArgumentException("请提供节点修改 JSON 文档");
            }
            return RevAutomationJson.Object(RevAutomationJson.Parse(args.document));
        }

        private static Dictionary<string, object> ReadObject(Dictionary<string, object> document, string key)
        {
            if (!document.TryGetValue(key, out var value) || !(value is Dictionary<string, object> item))
            {
                throw new ArgumentException(key + " 需要 JSON 对象");
            }
            return item;
        }

        private static string ReadId(Dictionary<string, object> document, string key)
        {
            if (!document.TryGetValue(key, out var value) || !(value is string id) || !RevGalValidation.IsId(id))
            {
                throw new ArgumentException(key + " 需要有效 ID 字符串");
            }
            return id;
        }

        private static void CheckKeys(Dictionary<string, object> document, params string[] keys)
        {
            foreach (string key in document.Keys)
            {
                if (!keys.Contains(key))
                {
                    throw new ArgumentException("节点修改文档包含未知字段：" + key);
                }
            }
        }

        private static string Hash(string snapshot)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>拒绝请求时让 CLI 与 RevMCP 都收到失败，并保留结构化问题明细。</summary>
        private sealed class RejectedEdit : RevAutomationOperation
        {
            public RejectedEdit(object result)
            {
                Success = false;
                Message = "节点修改未通过整图校验，请查 data.issues";
                Result = result;
            }

            public override bool Tick()
            {
                return true;
            }
        }
    }
}
