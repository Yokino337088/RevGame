// 剧情和局内互动复用同一张正式差分，不复制低清/高清两套文件；美术编号由SO映射，图集由资源配方统一管理。
using System;
using UnityEngine;
namespace RevGame.Data
{
    /// <summary>局内使用的有限图片目录，仅被互动租约加载，默认不在大厅预热。</summary>
    [CreateAssetMenu(menuName = "RevGame/鸦鸦/互动图片目录")]
    public sealed class YayaExpressionCatalog : ScriptableObject
    {
        /// <summary>编号和Sprite一项配对，Inspector可换图；编号来自原美术文件名，不是数组下标。</summary>
        [Serializable]
        public sealed class Entry
        {
            /// <summary>正式美术编号，如1、8、23，与Excel Expression一致。</summary>
            public int Id;
            /// <summary>唯一正式Sprite引用，压缩与图集设置由项目资源配方维护。</summary>
            public Sprite Sprite;
        }
        /// <summary>只包含当前台词使用的图片，不把全部表情素材库带入AB。</summary>
        public Entry[] Entries = Array.Empty<Entry>();
        /// <summary>活动图集名称，制作时按正式差分目录的唯一归属设置。</summary>
        public string[] AtlasNames = { "YayaExpressions", "YayaCombatExpressions" };
        /// <summary>触发时取一次Sprite，不逐帧GetSprite；缺图片明确失败。</summary>
        public Sprite Find(int id)
        {
            foreach (Entry entry in Entries)
            {
                if (entry.Id == id && entry.Sprite != null)
                {
                    return entry.Sprite;
                }
            }
            throw new InvalidOperationException("鸦鸦互动缺立绘：" + id);
        }
    }
}
