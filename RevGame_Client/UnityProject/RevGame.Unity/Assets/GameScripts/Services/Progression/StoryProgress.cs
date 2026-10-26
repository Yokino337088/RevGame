using UnityEngine;

namespace RevGame.Services
{
    /// <summary>开场与教学分别保存。只在正常完成时记账，中途退出不会跳过未学内容。</summary>
    public sealed class StoryProgress
    {
        private const string Key = "RevGame.Story.Introduction.v1";
        /// <summary>默认生产键固定；构造注入仅用于隔离的持久化回归。</summary>
        private readonly string _storageKey;
        /// <summary>开发测试用内存进度，绝不删除、覆盖或临时改写真实玩家PlayerPrefs。</summary>
        private bool _isolated;
        private int _memory;
        private int Flags { get { return _isolated ? _memory : PlayerPrefs.GetInt(_storageKey, 0); } }
        public bool OpeningComplete { get { return (Flags & 1) != 0; } }
        public bool TutorialComplete { get { return (Flags & 2) != 0; } }
        public bool IsIsolated { get { return _isolated; } }

        public StoryProgress() : this(Key) { }
        public StoryProgress(string storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey)) { throw new System.ArgumentException("剧情进度键不能为空"); }
            _storageKey = storageKey;
#if UNITY_EDITOR
            // 编辑器SessionState不进入构建、不写玩家存档；由明确的测试准备入口启用。
            _isolated = storageKey == Key && UnityEditor.SessionState.GetBool("RevGame.Story.IsolatedTest", false);
            if (_isolated) { _memory = UnityEditor.SessionState.GetInt("RevGame.Story.TestFlags", 0); }
#endif
        }

        public void CompleteOpening() { Save(Flags | 1); }
        public void CompleteTutorial() { Save(Flags | 2); }
        private void Save(int flags)
        {
            if (_isolated) { _memory = flags; return; }
            PlayerPrefs.SetInt(_storageKey, flags);
            PlayerPrefs.Save();
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>仅开发接口：开始/结束隔离会话，不更改生产键。调用方须在finally结束隔离。</summary>
        public void SetTestSession(bool enabled) { _isolated = enabled; _memory = 0; }
#endif
    }
}
