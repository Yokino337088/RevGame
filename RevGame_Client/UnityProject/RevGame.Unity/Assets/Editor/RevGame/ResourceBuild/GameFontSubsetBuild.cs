// 精简字体的构建门禁：字体制作在独立Python工具执行，这里只读校验，不在构建时修改文案或原字体。
// 保留原GUID和统一Font材质，原件在Unity Assets之外；失败阻止AB构建，避免发布后才发现缺字。
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameFontSubsetBuild
    {
        [Serializable]
        private sealed class Input { public string path; public string sha256; }
        [Serializable]
        private sealed class Manifest
        {
            public string sourceSha256, outputSha256;
            public int characterCount;
            public int[] codepoints;
            public Input[] inputs;
        }

        /// <summary>源字体、运行时字库和全部输入必须来自同一批制作结果；新增文件也要重新生成，不只查旧清单。</summary>
        public static string Validate()
        {
            string repository = Path.GetFullPath("../../..");
            string client = Path.GetFullPath("../..");
            string fontSource = Path.Combine(client, "美术/第三方素材/字体/NotoSansCJKsc-Bold.full.otf");
            string manifestPath = Path.Combine(client, "美术/第三方素材/字体/字体精简清单.json");
            const string target = "Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf";
            if (!File.Exists(manifestPath)) { throw new InvalidOperationException("缺少精简字库清单，请运行 python tools/font-subset.py"); }
            Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.inputs == null || manifest.codepoints == null || manifest.codepoints.Length != manifest.characterCount)
            { throw new InvalidOperationException("精简字库清单损坏"); }
            if (Hash(fontSource) != manifest.sourceSha256 || Hash(target) != manifest.outputSha256)
            { throw new InvalidOperationException("原字体或精简字体已改变，请运行 python tools/font-subset.py"); }
            var actual = new HashSet<string>(StringComparer.Ordinal);
            var extensions = new HashSet<string> { ".cs", ".txt", ".asset", ".prefab", ".unity" };
            foreach (string folder in new[] { "Assets/GameScripts", "Assets/GameRes", "Assets/Scenes" })
            {
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string path = file.Replace('\\', '/');
                    if (extensions.Contains(Path.GetExtension(path)) && !path.Contains("/Generated/"))
                    { actual.Add("RevGame_Client/UnityProject/RevGame.Unity/" + path); }
                }
            }
            foreach (string file in Directory.GetFiles(Path.Combine(client, "配置表"), "*.xlsx"))
            {
                if (!Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal))
                { actual.Add("RevGame_Client/配置表/" + Path.GetFileName(file)); }
            }
            actual.Add("RevGame_Client/美术/第三方素材/字体/额外保留字符.txt");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (Input input in manifest.inputs)
            {
                expected.Add(input.path);
                if (Hash(Path.Combine(repository, input.path)) != input.sha256)
                { throw new InvalidOperationException("字库输入变更，请重新生成：" + input.path); }
            }
            if (!actual.SetEquals(expected)) { throw new InvalidOperationException("新增或删除字体输入文件，请运行 python tools/font-subset.py"); }
            Font font = AssetDatabase.LoadAssetAtPath<Font>(target);
            if (font == null) { throw new InvalidOperationException("Unity未导入精简字体"); }
            foreach (int codepoint in manifest.codepoints)
            {
                if (codepoint > char.MaxValue || !font.HasCharacter((char)codepoint))
                { throw new InvalidOperationException("Unity精简字体缺字：U+" + codepoint.ToString("X4")); }
            }
            return manifest.characterCount + "个字符全部可用，原件/字库SHA256及输入清单一致";
        }

        /// <summary>文件流哈希用于构建期，不在游戏每帧检查；文件缺失即明确失败。</summary>
        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            { return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant(); }
        }

        [RevAutomationAction("game.font-subset.check", Environment = "edit", Keys = new string[0], Description = "检查精简字库输入和全部字符，缺字拒绝构建")]
        public static object Check(RevAutomationRequest request) { return new Result { text = Validate() }; }

        [Serializable]
        private sealed class Result { public string text; }
    }
}
