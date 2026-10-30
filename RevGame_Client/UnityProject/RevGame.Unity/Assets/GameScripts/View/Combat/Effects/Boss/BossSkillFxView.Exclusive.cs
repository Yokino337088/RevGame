// 专属技能使用已有图集的光晕网格承载程序化图案。无外部贴图依赖、无材质克隆，仍按池对象整体回收。
using DG.Tweening;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using UnityEngine;
namespace RevGame.View
{
    public sealed partial class BossSkillFxView
    {
        /// <summary>六章颜色是美术结构常量，不影响命中/伤害。预警、身体与格面统一色语义。</summary>
        public static Color ExclusiveTint(BossSkillKind kind)
        {
            switch (BossSkillCatalog.ThemeChapter(kind) - 1)
            {
                case 0:
                    return new Color(.57f, .25f, 1f);
                case 1:
                    return new Color(1f, .37f, .035f);
                case 2:
                    return new Color(.035f, .7f, 1f);
                case 3:
                    return new Color(.35f, .9f, .12f);
                case 4:
                    return new Color(.3f, .88f, 1f);
                default:
                    return new Color(1f, .79f, .16f);
            }
        }
        /// <summary>所有格子只从业务几何取集合。黑洞持续到返场，圣域持续到减益结束，不能提前淡出误导玩家。</summary>
        private float BuildExclusive(BossSkillInfo info, bool warning)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            int theme = BossSkillCatalog.ThemeChapter(info.Kind) - 1;
            Color tint = ExclusiveTint(info.Kind);
            float duration = warning ? info.Windup : Mathf.Max(.85f, def.Duration);
            BoardPos focus = info.Kind == BossSkillKind.SunBrand ? (info.Index % 3 == 0 ? info.A : info.Index % 3 == 1 ? info.B : info.C) : info.A;
            Vector3 center = Center(focus, 1);
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var pos = new BoardPos(x, y);
                    bool hit = false;
                    for (int index = warning ? 0 : info.Index; index < (warning ? info.Count : info.Index + 1); index++)
                    {
                        if (BossExclusiveGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, index, pos))
                        {
                            hit = true;
                            break;
                        }
                    }

                    if (!hit)
                    {
                        continue;
                    }
                    if (warning)
                    {
                        Zone(Center(pos, 1), Consts.CellSize * .94f, 1f, tint, duration);
                    }
                    else
                    {
                        int tile = Take(glow, (FxMode)(10 + theme), tint, .15f, GroundOrder + 1);
                        Place(tile, Center(pos, 1), Consts.CellSize * 1.1f);
                        TweenProgress(tile, 0f, 1f, duration);
                        _sequence.Insert(0f, _renderers[tile].transform.DOPunchScale(Vector3.one * .15f, .45f, 4));
                    }
                }
            }
            // 中心主形态负责远读：紫色吸积盘、日冕、蓝色涡流、沼泽旋口、冰晶、金色法印。
            int core = Take(glow, (FxMode)(10 + theme), tint, .25f, ImpactOrder);

            // 黑洞中心使用不透明暗视界；仅主形态启用，格面仍保留可读的紫色危险标记。
            if (info.Kind == BossSkillKind.BlackHole)
            {
                _shapes[core] = 2f;
                _additive[core] = 0f;
                Apply(core);
            }
            float diameter = info.Kind == BossSkillKind.StarStorm ? (info.Index + 1) * 2f
                : info.Kind == BossSkillKind.GlacialAegis ? Consts.BoardSize : def.Radius * 2f + 2f;
            Place(core, center, diameter * Consts.CellSize);
            _sequence.Insert(0f, _renderers[core].transform.DORotate(new Vector3(0, 0, theme == 5 ? 45 : 160), duration, RotateMode.LocalAxisAdd));
            _sequence.Insert(0f, _renderers[core].transform.DOScale(_renderers[core].transform.localScale * (warning ? .8f : 1.25f), duration).SetEase(Ease.OutCubic));
            TweenProgress(core, warning ? .12f : 0f, warning ? .5f : 1f, duration);

            if (!warning && info.Kind == BossSkillKind.TidalSurge)
            {
                PatternBeam(Center(new BoardPos(0, info.A.Y), 1), Center(new BoardPos(Consts.BoardSize - 1, info.A.Y), 1), tint, .8f);
            }
            if (!warning && info.Kind == BossSkillKind.DivineCross)
            {
                PatternBeam(Center(new BoardPos(0, info.A.Y), 1), Center(new BoardPos(Consts.BoardSize - 1, info.A.Y), 1), tint, .8f);
                PatternBeam(Center(new BoardPos(info.A.X, 0), 1), Center(new BoardPos(info.A.X, Consts.BoardSize - 1), 1), tint, .8f);
            }
            if (!warning && info.Kind != BossSkillKind.BlackHole && info.Kind != BossSkillKind.SwampMaw)
            {
                Shockwave(center, diameter * Consts.CellSize, tint, .8f);
            }
            return duration;
        }
    }
}
