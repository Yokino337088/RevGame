using System;
using UnityEngine;

namespace RevGame.Data
{
    /// <summary>收藏书按章节顺序配置标题和对白，完成通关记录后显示对应对白。</summary>
    public sealed class YayaMemoryBook : ScriptableObject
    {
        [Serializable]
        public sealed class Chapter
        {
            public string Title;
            [TextArea(2, 4)] public string Caption;
        }

        public Chapter[] Chapters;
    }
}
