using System;
using Revolution;
using UnityEngine;

namespace RevGame.Services
{
    /// <summary>真实通关与已阅状态一起写入本地存档。每章独立记账，重复通关不重新标为未阅。</summary>
    public sealed class YayaMemoryProgress
    {
        public const int ChapterCount = 6;
        public const string Changed = "RevGame.YayaMemories.ProgressChanged";
        private const int ChapterMask = (1 << ChapterCount) - 1;
        private const int CottageStoryVisited = 1 << (ChapterCount * 2);
        private const string SaveKey = "RevGame.YayaMemories.Progress.v1";
#if UNITY_EDITOR
        public const string RestartTestSession = "RevGame.YayaMemories.EditorRestartKey";
        public const string RestartTestPrefix = "RevGame.YayaMemories.IsolatedRestart.";
        private bool _previewAll;
#endif
        private readonly StoryProgress _story;
        private readonly string _key;
        private readonly string _isolatedDiskKey;
        private int _isolatedFlags;
        private string DiskKey => _story.IsIsolated ? _isolatedDiskKey : _key;
        private int Flags => DiskKey is string disk ? PlayerPrefs.GetInt(disk, 0) : _isolatedFlags;

        public YayaMemoryProgress() : this(RevServices.GetRequired<StoryProgress>(), SaveKey)
        {
        }

        /// <summary>显式键用于持久化验收，存档验证使用独立键。</summary>
        public YayaMemoryProgress(StoryProgress story, string key)
        {
            _story = story ?? throw new ArgumentNullException(nameof(story));
            _key = key;
#if UNITY_EDITOR
            // 重启验收时仅挂接由编辑工具创建的临时键，普通隔离 Play 继续使用内存。
            // 普通 Editor Play 默认可翻看六章，隔离验收仍从真实的锁页规则开始。
            _previewAll = key == SaveKey && !story.IsIsolated;
            string restart = UnityEditor.SessionState.GetString(RestartTestSession, "");
            if (key == SaveKey && story.IsIsolated && restart.StartsWith(RestartTestPrefix, StringComparison.Ordinal))
            {
                _isolatedDiskKey = restart;
            }
#else
            _isolatedDiskKey = null;
#endif
        }

        public int Cleared => Flags & ChapterMask;
        public int Read => (Flags >> ChapterCount) & Cleared;
        public int Unread => Cleared & ~Read;

        /// <summary>普通 Editor Play 默认可预览续篇。隔离验收与正式游戏仍按六章通关解锁。</summary>
        public bool IsCottageStoryUnlocked => IsCottageStoryPreviewOnly || Cleared == ChapterMask;

        /// <summary>仅靠编辑器预览开放时使用内存档案，保留玩家的实际通关和阅读记录。</summary>
        public bool IsCottageStoryPreviewOnly
        {
            get
            {
#if UNITY_EDITOR
                return _key == SaveKey && !_story.IsIsolated && Cleared != ChapterMask;
#else
                return false;
#endif
            }
        }

        /// <summary>第一次成功打开续篇后消除红点，通关和收藏书已阅位不受影响。</summary>
        public bool IsCottageStoryUnread => Cleared == ChapterMask && (Flags & CottageStoryVisited) == 0;

        public void MarkCottageStoryVisited()
        {
            int flags = Flags;
            if ((flags & ChapterMask) == ChapterMask)
            {
                Save(flags, flags | CottageStoryVisited);
            }
        }

        /// <summary>可看插画的章节位图。编辑器预览只影响展示，真实通关与已阅仍按存档记账。</summary>
        public int Unlocked
        {
            get
            {
#if UNITY_EDITOR
                if (_previewAll)
                {
                    return ChapterMask;
                }
#endif
                return Cleared;
            }
        }

        public bool IsUnlocked(int chapter)
        {
            return (Unlocked & ChapterBit(chapter)) != 0;
        }

#if UNITY_EDITOR
        /// <summary>为当前 Play 会话开放六章插画，不写入 PlayerPrefs。</summary>
        public void UnlockAllForPreview()
        {
            _previewAll = true;
        }
#endif

        public int OpeningChapter()
        {
            int unread = Unread;
            int preferred = unread != 0 ? unread : Unlocked;
            for (int chapter = 1; chapter <= ChapterCount; chapter++)
            {
                if ((preferred & ChapterBit(chapter)) != 0)
                {
                    return chapter;
                }
            }
            return 1;
        }

        public void RecordClear(int chapter)
        {
            int flags = Flags;
            Save(flags, flags | ChapterBit(chapter));
        }

        /// <summary>面板在插画实际绘制且翻页完成之后才调用。</summary>
        public void MarkRead(int chapter)
        {
            int flags = Flags;
            int bit = flags & ChapterBit(chapter);
            Save(flags, flags | (bit << ChapterCount));
        }

        private void Save(int previous, int next)
        {
            if (previous == next)
            {
                return;
            }
            if (DiskKey is string disk)
            {
                PlayerPrefs.SetInt(disk, next);
                PlayerPrefs.Save();
            }
            else
            {
                _isolatedFlags = next;
            }
            RevEvent.DispatchEvent(Changed);
        }

        private static int ChapterBit(int chapter)
        {
            return chapter >= 1 && chapter <= ChapterCount ? 1 << (chapter - 1) : 0;
        }
    }
}
