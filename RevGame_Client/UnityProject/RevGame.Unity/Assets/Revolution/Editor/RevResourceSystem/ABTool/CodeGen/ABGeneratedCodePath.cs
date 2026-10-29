// ABGeneratedCodePath —— 统一处理路径常量的输出位置与迁移，避免生成两份同名类导致编译冲突。
using System;
using System.IO;
using System.Text;
using UnityEditor;

namespace Revolution.Editor
{
    public static class ABGeneratedCodePath
    {
        /// <summary>
        /// 输入可为 Assets 下的目录或固定文件名，也可粘贴本工程绝对路径；留空使用默认位置。
        /// 生成的是运行期可用的 C#，不能放到 Editor/ 或工程外；类名/文件名保持一致，原业务引用无需改名。
        /// </summary>
        public static string Resolve(string configured, string fallback, string fileName)
        {
            string path = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim().Replace('\\', '/');
            string assets = Path.GetFullPath("Assets").Replace('\\', '/');
            if (Path.IsPathRooted(path))
            {
                string absolute = Path.GetFullPath(path).Replace('\\', '/');
                if (!absolute.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase) && !absolute.Equals(assets, StringComparison.OrdinalIgnoreCase))
                { throw new ArgumentException("生成路径必须位于当前 Unity 工程的 Assets 目录内。"); }
                path = "Assets" + absolute.Substring(assets.Length);
            }
            path = path.TrimEnd('/');
            if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))
            { throw new ArgumentException("请填写 Assets/... 目录或当前工程的绝对路径；留空使用默认路径。"); }
            foreach (string segment in path.Split('/'))
            {
                if (segment == "." || segment == ".." || segment.Equals("Editor", StringComparison.OrdinalIgnoreCase)
                    || segment.Length == 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                { throw new ArgumentException("生成路径不能含 ../、Editor 目录或非法字符。"); }
            }
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(path) != fileName) { throw new ArgumentException("文件名需要保持为 " + fileName + "；也可只填目标目录。"); }
                return path;
            }
            if (Path.HasExtension(path)) { throw new ArgumentException("请指定目录或 " + fileName + " 文件。"); }
            return path + "/" + fileName;
        }

        /// <summary>
        /// 内容不变不写盘。输出改位置时只移动本生成器带标记的旧文件，保留 GUID；绝不覆盖手写 C#。
        /// lastPath 是本工具上次成功的位置；首次升级可从默认路径识别旧生成物。
        /// </summary>
        public static bool Write(string target, string previous, string code, string generator)
        {
            // lastPath 也是可编辑配置，不能把它当可信绝对路径；写盘/移动前再次确认都在 Assets 内。
            string fileName = Path.GetFileName(target);
            target = Resolve(target, target, fileName);
            previous = Resolve(previous, target, fileName);
            string marker = "由 " + generator + " 自动生成";
            if (File.Exists(target) && !File.ReadAllText(target).Contains(marker))
            { throw new InvalidOperationException("目标文件不是本工具的生成物，已保留：" + target); }
            string directory = Path.GetDirectoryName(target);
            Directory.CreateDirectory(directory);
            if (previous != target && File.Exists(previous))
            {
                if (!File.ReadAllText(previous).Contains(marker))
                { throw new InvalidOperationException("旧位置存在手写同名文件，请先确认引用与文件位置：" + previous); }
                if (File.Exists(target))
                { throw new InvalidOperationException("新旧位置都存在生成物，请先保留一份再生成，避免同名类冲突。"); }
                AssetDatabase.ImportAsset(directory.Replace('\\', '/'), ImportAssetOptions.ForceSynchronousImport);
                string error = AssetDatabase.MoveAsset(previous, target);
                if (!string.IsNullOrEmpty(error)) { throw new IOException("移动生成代码失败：" + error); }
            }
            if (File.Exists(target) && File.ReadAllText(target) == code) { return false; }
            File.WriteAllText(target, code, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(target);
            return true;
        }
    }
}
