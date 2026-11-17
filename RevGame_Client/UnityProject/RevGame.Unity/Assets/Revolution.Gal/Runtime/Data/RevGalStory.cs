using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution.Gal
{
    public enum RevGalNodeKind
    {
        Line, Choice, Branch, Signal, End
    }

    public enum RevGalComparison
    {
        Equal, NotEqual, Greater, AtLeast, Less, AtMost
    }

    public enum RevGalOperation
    {
        Set, Add
    }

    /// <summary>剧情资产中的变量声明。旗标可用 0 与 1，数值可用于好感度和计数。</summary>
    [Serializable]
    public sealed class RevGalVariable
    {
        public string Id;
        public int Initial;
    }

    [Serializable]
    public sealed class RevGalCondition
    {
        public string Variable;
        public RevGalComparison Comparison;
        public int Value;
    }

    [Serializable]
    public sealed class RevGalEffect
    {
        public string Variable;
        public RevGalOperation Operation;
        public int Value;
    }

    /// <summary>选项和自动分支都用有序出口。Id 在同一节点内唯一，发布后可稳定标识玩家选择。</summary>
    [Serializable]
    public sealed class RevGalEdge
    {
        public string Id;
        public string Label;
        public string TextKey;
        public string Target;
        public bool ShowWhenLocked;
        public List<RevGalCondition> Conditions = new List<RevGalCondition>();
        public List<RevGalEffect> Effects = new List<RevGalEffect>();
    }

    /// <summary>常驻舞台字段使用 Revolution 逻辑路径。空值沿用上次状态，单个减号清空该位置。</summary>
    [Serializable]
    public sealed class RevGalStage
    {
        public const string Clear = "-";
        /// <summary>背景图片的 Sprite 逻辑路径。留空沿用，短横线清掉当前图片。</summary>
        public string Background;
        /// <summary>节点使用的背景 Material 逻辑路径。材质携带 Shader 和参数，短横线恢复界面的默认材质。</summary>
        public string BackgroundMaterial;
        public string Left;
        public string Center;
        public string Right;
        public string Music;

        internal RevGalStage Copy()
        {
            return (RevGalStage)MemberwiseClone();
        }

        internal void Apply(RevGalStage change)
        {
            if (change == null)
            {
                return;
            }
            Background = Merge(Background, change.Background);
            BackgroundMaterial = Merge(BackgroundMaterial, change.BackgroundMaterial);
            Left = Merge(Left, change.Left);
            Center = Merge(Center, change.Center);
            Right = Merge(Right, change.Right);
            Music = Merge(Music, change.Music);
        }

        private static string Merge(string current, string change)
        {
            return string.IsNullOrEmpty(change) ? current : change == Clear ? null : change;
        }
    }

    /// <summary>章节或小节共用的舞台方案。小节可通过 Parent 继承章节，再覆盖自己的素材。</summary>
    [Serializable]
    public sealed class RevGalStagePreset
    {
        /// <summary>方案的稳定 ID，由节点 StagePreset 引用。</summary>
        public string Id;
        /// <summary>工作台显示的章节或小节名称。</summary>
        public string Title;
        /// <summary>父方案 ID，空表示顶层章节。按父到子的顺序应用舞台覆盖。</summary>
        public string Parent;
        /// <summary>背景、Shader 材质、立绘和音乐的逻辑路径。</summary>
        public RevGalStage Stage = new RevGalStage();
    }

    /// <summary>节点 ID 是存档锚点，标题可自由改名。Next 在分支节点中是无条件保底出口。</summary>
    [Serializable]
    public sealed class RevGalNode
    {
        public string Id;
        public string Title;
        public RevGalNodeKind Kind;
        public Vector2 Position;
        /// <summary>进入本节点时应用的章节或小节方案。留空沿用当前舞台，节点 Stage 在方案之后覆盖。</summary>
        public string StagePreset;
        public string Speaker;
        public string SpeakerKey;
        [TextArea(3, 12)] public string Text;
        public string TextKey;
        public string Next;
        public string Voice;
        public string Sound;
        public string GalleryId;
        public string Signal;
        [TextArea] public string Argument;
        public RevGalStage Stage = new RevGalStage();
        public List<RevGalEffect> Effects = new List<RevGalEffect>();
        public List<RevGalEdge> Exits = new List<RevGalEdge>();
    }

    /// <summary>SO 和 RevCLI 的共同数据合同。Revision 由项目在破坏已有存档兼容性时升级。</summary>
    [Serializable]
    public sealed class RevGalDefinition
    {
        public int Schema = 1;
        public string Id;
        public string Title;
        public int Revision = 1;
        public string Start;
        /// <summary>剧情里的章节和小节舞台配置。旧剧情省略此字段时继续按节点舞台播放。</summary>
        public List<RevGalStagePreset> StagePresets = new List<RevGalStagePreset>();
        public List<RevGalVariable> Variables = new List<RevGalVariable>();
        public List<RevGalNode> Nodes = new List<RevGalNode>();
    }

    [CreateAssetMenu(menuName = "Revolution/Gal/剧情", fileName = "GalStory")]
    public sealed class RevGalStory : ScriptableObject
    {
        [SerializeField] private RevGalDefinition definition = new RevGalDefinition();

        public RevGalDefinition Definition => definition;

        /// <summary>编辑工具在校验通过后替换定义。正在播放的会话持有自己的数据快照。</summary>
        public void SetDefinition(RevGalDefinition value)
        {
            definition = value ?? throw new ArgumentNullException(nameof(value));
        }
    }
}
