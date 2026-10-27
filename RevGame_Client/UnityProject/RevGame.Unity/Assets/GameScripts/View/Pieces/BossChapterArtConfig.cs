// 每章Boss的纯美术配置：关键帧与播放节奏独立于普通棋子，不改变Boss技能、伤害和占格规则。
using System;
using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>由当前Boss回合的BoardView租约持有；同章多颗Boss共用同一组帧，离场后归还。</summary>
    [CreateAssetMenu(fileName = "BossChapterArt", menuName = "RevGame/Boss章节美术")]
    public sealed class BossChapterArtConfig : ScriptableObject
    {
        [Tooltip("所属章节1～6；外观映射不改模型棋子职业")]
        public int Chapter;
        [Tooltip("美术职业：剑士/弓手/水法师/刺客/盾卫/牧师")]
        public PieceKind Appearance;
        [Tooltip("只保留8张关键帧；按原时间顺序排列")]
        public Sprite[] Frames = Array.Empty<Sprite>();
        [Tooltip("每张关键帧停留秒数；纯动画表现，可在Inspector调整")]
        public float FrameSeconds = .18f;
        [Tooltip("往返播放可避免原视频首尾姿态不一致造成跳帧")]
        public bool PingPong = true;
        [Tooltip("身体子节点缩放；不改变占格、血条或根节点大小")]
        public float BodyScale = 1f;
        [Tooltip("美术自带圆底座和王冠时不再重复叠原底座/王冠")]
        public bool BuiltInDecorations = true;

        /// <summary>加载失败明确报告，不用普通敌方重剑图冒充新Boss；帧引用只能由配置持有。</summary>
        public void Validate(int chapter)
        {
            if (Chapter != chapter || Frames == null || Frames.Length != 8 || Array.Exists(Frames, sprite => sprite == null)
                || FrameSeconds < .05f || FrameSeconds > 1f || BodyScale <= 0 || BodyScale > 2f)
            { throw new InvalidOperationException("Boss关键帧美术配置无效：" + name); }
        }
        public static string ResourceName(int chapter) { return "BossChapter" + chapter.ToString("D2"); }
        public static string AtlasName(int chapter) { return "BossChapter" + chapter.ToString("D2") + "Frames"; }
    }
}
