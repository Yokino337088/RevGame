// 新增技能沿用共享光晕/光束/星芒图集：规则预警精确到格，爆发主体负责远读与夸张动势。
using DG.Tweening;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using UnityEngine;
namespace RevGame.View
{
    public sealed partial class BossSkillFxView
    {
        /// <summary>
        /// 同一池对象承载一段预警或爆发。最多81格加主环、星芒和三条光束，不超过90片固定预算。
        /// 主体放大只影响视觉，精确危险区始终由生产几何标记；动画被回池取消不会追加伤害。
        /// </summary>
        private float BuildExpanded(BossSkillInfo info, bool warning)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            int theme = BossSkillCatalog.ThemeChapter(info.Kind) - 1;
            bool special = BossSkillCatalog.IsSpecial(info.Kind);
            Color tint = ExclusiveTint(info.Kind);
            float duration = warning ? info.Windup + (def.Count - 1) * def.Interval : Mathf.Max(1.05f, def.Duration);
            Vector3 focus = Center(info.A, 1);
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    BoardPos cell = new BoardPos(x, y);
                    bool affected = false;
                    int first = warning ? 0 : info.Index, last = warning ? info.Count : info.Index + 1;
                    for (int index = first; index < last; index++)
                    {
                        if (BossExpandedGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, index, cell))
                        {
                            affected = true;
                            break;
                        }
                    }

                    if (!affected)
                    {
                        continue;
                    }
                    Vector3 position = Center(cell, 1);

                    if (warning)
                    {
                        Zone(position, Consts.CellSize * .94f, 1f, tint, duration);
                    }
                    else
                    {
                        int tile = Take(glow, (FxMode)(10 + theme), tint, .18f, GroundOrder + 1);
                        Place(tile, position, Consts.CellSize * 1.18f);
                        TweenProgress(tile, 0f, 1f, duration);
                        _sequence.Insert(0f, _renderers[tile].transform.DOPunchScale(Vector3.one * .32f, .5f, 5));
                    }
                }
            }
            // 两层反向旋转与快速膨胀形成“大招”轮廓。颜色与几何不同，避免每招只换名称。
            float diameter = Mathf.Max(3f, def.Radius * 2f + 2f) * Consts.CellSize;
            int core = Take(glow, (FxMode)(10 + theme), tint, .18f, ImpactOrder);
            Place(core, focus, diameter * (warning ? .65f : 1.2f));
            _sequence.Insert(0f, _renderers[core].transform.DORotate(new Vector3(0f, 0f, (info.Index % 2 == 0 ? 1 : -1) * 240f), duration, RotateMode.LocalAxisAdd));
            _sequence.Insert(0f, _renderers[core].transform.DOScale(_renderers[core].transform.localScale * (warning ? 1.25f : 1.55f), duration).SetEase(Ease.OutExpo));
            TweenProgress(core, warning ? .12f : 0f, warning ? .55f : 1f, duration);
            int corona = Take(ring, FxMode.Shockwave, tint, .12f, ImpactOrder - 1);
            Place(corona, focus, diameter * .45f);
            _sequence.Insert(0f, _renderers[corona].transform.DOScale(_renderers[corona].transform.localScale * (warning ? 1.8f : 3.4f), warning ? duration : .7f).SetEase(Ease.OutExpo));
            TweenProgress(corona, 0f, warning ? .5f : 1f, warning ? duration : .9f);

            if (!warning)
            {
                int flash = Take(star, FxMode.Sprite, Color.white, .08f, ImpactOrder + 2);
                Place(flash, focus, diameter * .65f);
                _sequence.Insert(0f, _renderers[flash].transform.DOScale(_renderers[flash].transform.localScale * 1.8f, .16f).SetEase(Ease.OutExpo));
                TweenProgress(flash, 0f, 1f, .4f);
                // 能量从主将射向锁定落点，三条线有界；它只是轨迹，不扩大地面命中集合。
                Vector3 origin = Center(info.Origin, info.OriginSize);
                PatternBeam(origin, Center(info.A, 1), tint, .7f);

                if (info.B != info.A)
                {
                    PatternBeam(origin, Center(info.B, 1), tint, .65f);
                }
                if (info.C != info.A && info.C != info.B)
                {
                    PatternBeam(origin, Center(info.C, 1), tint, .6f);
                }
                if (special)
                {
                    Shockwave(focus, diameter * 1.5f, tint, .9f);
                }
            }

            return duration;
        }
    }
}
