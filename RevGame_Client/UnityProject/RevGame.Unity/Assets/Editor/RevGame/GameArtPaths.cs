// ============================================================
// GameArtPaths.cs —— 保留旧构建配方的美术路径兼容表。
// SpriteMigration.tsv记录GUID不变的搬迁；只在编辑器使用，运行时直接依赖预制体/配置的GUID引用。
// 不复制一份旧资源来兼容旧路径，否则又会出现双份贴图与重复AB依赖。
// ============================================================
using System.Collections.Generic;
using System.IO;

namespace RevGame.Editor
{
    internal static class GameArtPaths
    {
        private static Dictionary<string, string> _moved;

        /// <summary>原路径没迁移时原样返回；有迁移记录时返回精细目录下的新路径。缓存只活到编辑器域重载。</summary>
        internal static string Resolve(string original)
        {
            if (_moved == null)
            {
                _moved = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines("Assets/Editor/RevGame/SpriteMigration.tsv"))
                {
                    string[] columns = line.Split('\t');
                    if (columns.Length == 3) { _moved.Add(columns[0], columns[1]); }
                }
            }
            string path;
            return _moved.TryGetValue(original, out path) ? path : original;
        }
    }
}
