#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 开发态画面夹具：只借真实资源池重播范围表现，不改伤害/射程；视觉证据与规则断言分别报告。
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>开发态范围特效预览职责，复用正式ShowPattern与池租约，用结构化回执提供实际视觉参数。</summary>
    public sealed partial class EndlessArena
    {
        /// <summary>本次单独视觉租约的模式；只用于读取检查，生产逻辑不依赖此字段。</summary>
        private bool _previewFireEndless;
        /// <summary>
        /// 隔离画面动作：将同一火法范围快照按目标模式播放，供连拍核对透明度与排序。
        /// 这是展示夹具，不伪造真实伤害；实际共享池和ShowPattern入口与六章/无尽一致。
        /// </summary>
        public object PreviewFire(bool endless)
        {
            EndlessService service = RevServices.GetRequired<EndlessService>();
            if (!service.Isolated || !_active)
            {
                throw new InvalidOperationException("火焰画面夹具需要隔离无尽竞技场");
            }
            _previewFireEndless = endless;
            int generation = _fxGeneration;
            GamePoolRequests.GetAsync<BattleEffectView>(effect =>
            {
                if (!_active || generation != _fxGeneration || effect == null)
                {
                    if (effect != null)
                    {
                        RevPool.Return(effect);
                    }
                    return;
                }
                _activeFx[(int)FxKind.Pattern].Add(effect);
                _patterns.Add(effect);
                effect.ShowPattern(new Vector3(3, 3, 0), new Vector3(3, 3, 0), PieceKind.MageFire, 1, _fxFinished, subtleFire: endless);
            }, _fxRoot);
            return new Dictionary<string, object> { ["mode"] = endless ? "Endless" : "Campaign", ["expectedOpacity"] = endless ? .494f : .7f, ["expectedOrder"] = 7 };
        }
        /// <summary>只读真实在播火焰的参数；没有有效租约明确visible=false，不拿配置声明当已显示。</summary>
        public object FireVisualSnapshot()
        {
            foreach (BattleEffectView effect in _patterns)
            {
                if (effect.IsPatternActive && effect.PatternKind == PieceKind.MageFire)
                {
                    return new Dictionary<string, object>
                    {
                        ["visible"] = true, ["opacity"] = effect.PatternOpacity, ["order"] = effect.PatternOrder,
                        ["border"] = effect.PatternBorder, ["shader"] = effect.UsesElementalShader,
                        ["mode"] = _previewFireEndless ? "Endless" : "Campaign"
                    };
                }
            }
            return new Dictionary<string, object> { ["visible"] = false };
        }
    }
}
#endif
