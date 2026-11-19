using System;

namespace RevGame.Data
{
    /// <summary>配置用文字记录立绘归属。加载配置本身时，Unity 不会把引用的表情包一并载入。</summary>
    [Serializable]
    public struct GamePortraitReference
    {
        /// <summary>Unity 编辑器资产和切片的稳定身份，移动文件后仍可找到所选图片。</summary>
        public string AssetGuid;
        public long LocalId;
        /// <summary>运行时切片名称，构建时随选图刷新。</summary>
        public string SpriteName;
        /// <summary>生成器根据唯一包归属写入的图集名，运行时经 Revolution 加载。</summary>
        public string Atlas;

        public bool IsSet => !string.IsNullOrEmpty(AssetGuid);
    }
}
