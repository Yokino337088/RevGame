// ============================================================
// PieceArtConfig.cs
// 把美术命名差异留在配置中，游戏运行时按种类、星级和阵营直接取帧，不扫描磁盘。
// ============================================================
using System;
using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 棋子美术配置表：把「棋子种类 + 星级 + 阵营」映射到一组动图帧与配件图。
    ///
    /// 【为什么要有这张表，而不是每种棋子一个预制体】
    /// 原项目一开始是"每个角色一个预制体"（玩家/敌方两套，共 56 个），结构完全一样、
    /// 只有图和帧不同 —— 改一次层级要改 56 份。现在的做法是：
    /// **全场景只用 1 个棋子预制体（GameRes/Pieces/Unit/SlimePiece.prefab）**，生成棋子时按这张表把
    /// 立绘/帧序列/底座/被围标识"装"进去。
    ///
    /// 【为什么不用 Animator】
    /// 一个 Animator 就是一台状态机，几十颗棋子同时播待机动画时它是纯粹的浪费；
    /// 这些动画本质是"每 N 秒换一张 Sprite"，由 <see cref = "PieceView"/> 用框架的 RevTimer 统一推进即可。
    ///
    /// 表内容由编辑器工具扫描 ArtRes 自动生成（见 README「棋子美术」一节），美术换图后重跑一次。
    /// </summary>
    [CreateAssetMenu(fileName = "棋子美术配置", menuName = "RevGame/棋子美术配置", order = 0)]
    public sealed class PieceArtConfig : ScriptableObject
    {
        /// <summary>一个"棋子外观"的全部素材。</summary>
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("棋子种类（9 种外形之一）")]
            public PieceKind kind;
            [Tooltip("星级（1~3 星，立绘不同）")]
            public StarTier star;
            [Tooltip("阵营（玩家/敌方的待机帧是两套图）")]
            public GameSide side;
            [Tooltip("待机动画帧（按顺序播放；为空则只显示首帧）")]
            public Sprite[] idleFrames;
            [Tooltip("待机动画每帧停留秒数")]
            public float frameInterval = 0.2f;
            [Tooltip("底座图（可选，为空表示不显示底座）")]
            public Sprite baseSprite;
            [Tooltip("被锁死时叠在图上的标识帧（可选）")]
            public Sprite[] lockedFrames;
            [Tooltip("被锁死标识每帧停留秒数")]
            public float lockedInterval = 0.14f;
        }

        [SerializeField]
        private Entry[] entries = new Entry[0];
        /// <summary>按（种类, 星级, 阵营）查表；没有就返回 null（调用方判空，不抛异常）。</summary>
        public Entry Find(PieceKind kind, StarTier star, GameSide side)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                Entry e = entries[i];
                if (e == null)
                {
                    continue;
                }

                if (e.kind != kind)
                {
                    continue;
                }

                if (e.star != star)
                {
                    continue;
                }

                if (e.side != side)
                {
                    continue;
                }

                return e;
            }

            return null;
        }

        /// <summary>条目总数（编辑器工具/自检用）。</summary>
        public int EntryCount
        {
            get
            {
                return entries != null ? entries.Length : 0;
            }
        }

        /// <summary>给编辑器工具批量填表用（运行期不要调）。</summary>
        public void SetEntries(Entry[] newEntries)
        {
            entries = newEntries ?? new Entry[0];
        }
    }
}
