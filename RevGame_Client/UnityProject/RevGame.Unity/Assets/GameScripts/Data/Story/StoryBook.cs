using System;
using UnityEngine;

namespace RevGame.Data
{
    /// <summary>剧情的唯一文案资产。策划直接在Inspector编辑；不把剧情写进Excel或运行时代码。</summary>
    [CreateAssetMenu(menuName = "RevGame/剧情/史莱姆棋旅", fileName = "StoryBook")]
    public sealed class StoryBook : ScriptableObject
    {
        /// <summary>默认鸦鸦立绘，兼容已有资产字段；六种表情归YayaExpressions，不加载全角色库。</summary>
        public Sprite Guide;
        /// <summary>本版本为按编号索引的表情集合，和ExpressionIds一一对应；不同情绪不再作为动画循环。</summary>
        public Sprite[] GuideFrames = Array.Empty<Sprite>();
        /// <summary>鸦鸦表情的素材编号，与GuideFrames一一对应；只加载选用表情，不把全部差分/待机塞进剧情包。</summary>
        public int[] ExpressionIds = Array.Empty<int>();
        /// <summary>本次原创剧情迁移版本，仅编辑器使用，重跑制作配方不覆盖后续手工编辑。</summary>
        [HideInInspector] public int YayaRevision;
        /// <summary>编辑器一次性迁移旧教程文案的版本；不会参与玩家存档或战斗规则。</summary>
        [HideInInspector] public int TutorialRevision;
        /// <summary>每秒显示的文字元素数，只控制阅读表现，不改变游戏时间。</summary>
        [Range(10, 60)] public float CharactersPerSecond = 28;
        /// <summary>开场、教学引子、六章及尾声，以稳定Id索引，不依赖数组顺序。</summary>
        public Episode[] Episodes = Array.Empty<Episode>();

        /// <summary>加载后先检查必需篇章与空白台词，错误直接交给流程失败界面，不默默跳过剧情。</summary>
        public void Validate()
        {
            if (Guide == null || GuideFrames == null || GuideFrames.Length < 2 || Array.Exists(GuideFrames, f => f == null) || CharactersPerSecond <= 0)
            {
                throw new InvalidOperationException("剧情缺少向导或打字速度无效");
            }
            if (ExpressionIds == null || ExpressionIds.Length != GuideFrames.Length || Episodes == null)
            {
                throw new InvalidOperationException("剧情表情编号与图片必须一一对应，篇章数组不能为null");
            }
            // 编号唯一且非零，0仅供旧台词表示默认笑容；先校验，避免播放到中途才发现缺图。
            var expressions = new System.Collections.Generic.HashSet<int>();
            foreach (int expression in ExpressionIds)
            {
                if (expression <= 0 || !expressions.Add(expression))
                {
                    throw new InvalidOperationException("剧情表情编号必须为正数且不能重复");
                }
            }
            ExpressionSprite(0, out _);
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (Episode episode in Episodes)
            {
                if (episode == null || string.IsNullOrWhiteSpace(episode.Id) || !ids.Add(episode.Id) || episode.Lines == null || episode.Lines.Length == 0)
                {
                    throw new InvalidOperationException("剧情Id重复、空白或篇章没有台词");
                }
                if (episode.BackgroundChapter < 0 || episode.BackgroundChapter > 6)
                {
                    throw new InvalidOperationException("剧情背景章节必须为0～6：" + episode.Id);
                }
                foreach (Line line in episode.Lines)
                {
                    if (line == null || string.IsNullOrWhiteSpace(line.Text))
                    {
                        throw new InvalidOperationException("剧情存在空台词：" + episode.Id);
                    }
                    ExpressionSprite(line.Expression, out _);
                }
            }
            foreach (string id in new[] { "opening", "tutorial", "chapter1", "chapter2", "chapter3", "chapter4", "chapter5", "chapter6", "ending" })
            {
                Find(id);
            }
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                Find("boss_after_" + chapter);
            }
            if (Find("lesson").Lines.Length != 5)
            {
                throw new InvalidOperationException("教学说明必须对应五个教学步骤");
            }
        }

        /// <summary>用稳定篇章ID查询，未知ID明确报错；调用者应先检查再锁输入。</summary>
        public Episode Find(string id)
        {
            foreach (Episode episode in Episodes)
            {
                if (episode != null && episode.Id == id)
                {
                    return episode;
                }
            }
            throw new InvalidOperationException("找不到剧情：" + id);
        }
        /// <summary>一段可独立播放的篇章；Id供流程引用，Title是玩家看到的章节名。</summary>
        [Serializable]
        public sealed class Episode
        {
            /// <summary>稳定业务标识，如opening或chapter1；存档不依赖数组下标。</summary>
            public string Id;
            /// <summary>本篇在对话卡顶部显示的标题。</summary>
            public string Title;
            /// <summary>0使用大厅小屋，1～6复用对应关卡原画与Shader；只保存选择，不硬引用六章材质。</summary>
            public int BackgroundChapter;
            /// <summary>按播放顺序排列的对白，一次点击推进一项。</summary>
            public Line[] Lines;
        }
        /// <summary>一屏对话；点击整屏直接进入下一句，结尾点击才算本篇播放完成。</summary>
        [Serializable]
        public sealed class Line
        {
            /// <summary>对白署名，可为鸦鸦或冒险家；画像始终使用本句鸦鸦的反应表情。</summary>
            public string Speaker;
            /// <summary>本句鸦鸦表情素材编号；0兼容旧数据使用默认笑容，不由UI按字符串关键词猜测。</summary>
            public int Expression;
            /// <summary>原始中文对白，不解析富文本，打字按Unicode文字元素显示。</summary>
            [TextArea(2, 5)] public string Text;
        }
        /// <summary>按素材编号取表情，缺编号明确失败，避免对白与角色表情不一致。</summary>
        public Sprite ExpressionSprite(int id, out int index)
        {
            if (id == 0)
            {
                id = 3;
            }
            index = Array.IndexOf(ExpressionIds, id);
            if (index < 0 || index >= GuideFrames.Length)
            {
                throw new InvalidOperationException("鸦鸦表情未装配：" + id);
            }
            return GuideFrames[index];
        }
    }
}
