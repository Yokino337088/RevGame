using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Revolution.Editor
{
    /// <summary>保证 Player 只携带与 Editor 可读表一致的压缩副本。所有 Player 构建入口共用这个检查。</summary>
    internal sealed class ABResMapBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>在 Unity 收集 Player 资产之前迁移和校验映射表。</summary>
        public int callbackOrder
        {
            get => -100;
        }

        /// <summary>写入已经由项目资源标记生成的映射文本与派生副本。</summary>
        internal static void Write(string text)
        {
            string canonical = RevResMapCodec.CanonicalText(text);
            byte[] packed = RevResMapCodec.Pack(canonical);
            MoveLegacyText();
            EnsureDirectory(ABBuildSetting.MapAssetPath);
            EnsureDirectory(ABBuildSetting.PackedMapAssetPath);
            WriteAsset(ABBuildSetting.MapAssetPath, Encoding.UTF8.GetBytes(canonical));
            WriteAsset(ABBuildSetting.PackedMapAssetPath, packed);
        }

        /// <summary>进入出包时验证现有产物。首次升级可从原文本生成，现有派生副本不一致时中止出包。</summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                // 先迁移旧表，避免 Resources 在真正收集资源时又包含一份可读文本。
                MoveLegacyText();
                bool hasText = File.Exists(ABBuildSetting.MapAssetPath);
                bool hasPack = File.Exists(ABBuildSetting.PackedMapAssetPath);
                if (!hasText && !hasPack)
                {
                    return; // 只用 Resources 或纯远端的项目可以没有首包映射。
                }
                if (!hasText)
                {
                    throw new InvalidDataException("缺少编辑器 ResMap 源表");
                }

                string text = RevResMapCodec.DecodeText(File.ReadAllBytes(ABBuildSetting.MapAssetPath));
                if (!hasPack)
                {
                    byte[] packed = RevResMapCodec.Pack(text);
                    EnsureDirectory(ABBuildSetting.PackedMapAssetPath);
                    WriteAsset(ABBuildSetting.PackedMapAssetPath, packed);
                    return;
                }

                // 先校验现有副本再比对内容。禁止把坏文件静默重封为看似正确的首包数据。
                string unpacked = RevResMapCodec.Unpack(File.ReadAllBytes(ABBuildSetting.PackedMapAssetPath));
                RevResMapCodec.ParseText(unpacked);
                if (!string.Equals(unpacked, RevResMapCodec.CanonicalText(text), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("可读映射与压缩映射不属于同一次生成");
                }
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is IOException || exception is ArgumentException)
            {
                throw new BuildFailedException("ResMap 出包检查失败。请通过 RevAB 重新生成映射。 " + exception.Message);
            }
        }

        /// <summary>旧表迁入 Editor 目录并保留原 GUID。遇到两份文本由项目先解决冲突。</summary>
        private static void MoveLegacyText()
        {
            if (!File.Exists(ABBuildSetting.LegacyMapAssetPath))
            {
                return;
            }
            if (File.Exists(ABBuildSetting.MapAssetPath))
            {
                throw new InvalidDataException("检测到新旧两份 ResMap 文本，请先确定项目的有效源表");
            }
            EnsureDirectory(ABBuildSetting.MapAssetPath);
            // 先验证内容再改资产路径，迁移出错时保持旧项目仍能检查原文件。
            RevResMapCodec.ParseText(RevResMapCodec.DecodeText(File.ReadAllBytes(ABBuildSetting.LegacyMapAssetPath)));
            string error = AssetDatabase.MoveAsset(ABBuildSetting.LegacyMapAssetPath, ABBuildSetting.MapAssetPath);
            if (!string.IsNullOrEmpty(error))
            {
                throw new IOException("ResMap 路径迁移失败：" + error);
            }
        }

        /// <summary>只在版本内容变化时写文件，随后同步导入，令当前构建读到这一次的内容。</summary>
        private static void WriteAsset(string path, byte[] bytes)
        {
            if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
            {
                File.WriteAllBytes(path, bytes);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>确保 Unity 识别新建的目标目录，供后续迁移或导入使用。</summary>
        private static void EnsureDirectory(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }
    }
}
