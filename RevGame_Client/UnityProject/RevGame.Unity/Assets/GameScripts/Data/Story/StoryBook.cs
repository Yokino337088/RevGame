using System;
using UnityEngine;

namespace RevGame.Data
{
    /// <summary>剧情的唯一文案资产。策划直接在Inspector编辑；不把剧情写进Excel或运行时代码。</summary>
    [CreateAssetMenu(menuName = "RevGame/剧情/史莱姆棋旅", fileName = "StoryBook")]
    public sealed class StoryBook : ScriptableObject
    {
        /// <summary>所有故事使用同一向导精灵；源图仅归入StoryGuide图集，避免开场加载全部战斗帧。</summary>
        public Sprite Guide;
        /// <summary>向导真实序列帧，共享StoryGuide图集；只切Sprite，不在每帧加载资源。</summary>
        public Sprite[] GuideFrames = Array.Empty<Sprite>();
        /// <summary>编辑器一次性迁移旧教程文案的版本；不会参与玩家存档或战斗规则。</summary>
        [HideInInspector] public int TutorialRevision;
        /// <summary>每秒显示的文字元素数，只控制阅读表现，不改变游戏时间。</summary>
        [Range(10, 60)] public float CharactersPerSecond = 28;
        /// <summary>开场、教学引子、六章及尾声，以稳定Id索引，不依赖数组顺序。</summary>
        public Episode[] Episodes = Array.Empty<Episode>();

        /// <summary>加载后先检查必需篇章与空白台词，错误直接交给流程失败界面，不默默跳过剧情。</summary>
        public void Validate()
        {
            if (Guide == null || GuideFrames == null || GuideFrames.Length < 2 || Array.Exists(GuideFrames, f => f == null) || CharactersPerSecond <= 0) { throw new InvalidOperationException("剧情缺少向导或打字速度无效"); }
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (Episode episode in Episodes)
            {
                if (episode == null || string.IsNullOrWhiteSpace(episode.Id) || !ids.Add(episode.Id) || episode.Lines == null || episode.Lines.Length == 0)
                { throw new InvalidOperationException("剧情Id重复、空白或篇章没有台词"); }
                foreach (Line line in episode.Lines)
                {
                    if (line == null || string.IsNullOrWhiteSpace(line.Text)) { throw new InvalidOperationException("剧情存在空台词：" + episode.Id); }
                }
            }
            foreach (string id in new[] { "opening", "tutorial", "chapter1", "chapter2", "chapter3", "chapter4", "chapter5", "chapter6", "ending" }) { Find(id); }
            if (Find("lesson").Lines.Length != 5) { throw new InvalidOperationException("教学说明必须对应五个教学步骤"); }
        }

        public Episode Find(string id)
        {
            foreach (Episode episode in Episodes) { if (episode != null && episode.Id == id) { return episode; } }
            throw new InvalidOperationException("找不到剧情：" + id);
        }

        /// <summary>一段可独立播放的篇章；Id供流程引用，Title是玩家看到的章节名。</summary>
        [Serializable] public sealed class Episode
        {
            public string Id;
            public string Title;
            public Line[] Lines;
        }

        /// <summary>一屏对话；点击整屏直接进入下一句，结尾点击才算本篇播放完成。</summary>
        [Serializable] public sealed class Line
        {
            public string Speaker;
            [TextArea(2, 5)] public string Text;
        }
    }
}
