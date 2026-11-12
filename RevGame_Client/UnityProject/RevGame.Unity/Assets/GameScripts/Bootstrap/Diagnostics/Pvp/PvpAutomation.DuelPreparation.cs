#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    // 仅开发态控制隔离白方；可见席的点选和锁角仍使用正常 RevUI 按钮。
    public static partial class PvpAutomation
    {
        [Serializable]
        private sealed class DuelGuestArgs
        {
            // 指定要预选或确认的白方角色；加载阶段仅使用 releaseLoad。
            public int role;
            public bool confirm;
            public bool releaseLoad;
        }

        // 复用真实 UTP 房间与 GameFlow，不直接调用场景激活或修改权威状态。
        private static void RegisterDuelPreparationAutomation()
        {
            RevRuntimeAutomation.Register("pvp.duel-prep-lan", "空棋盘建立可见 UTP 决斗，保留玩家真实选角与白方 37% 加载等待", Empty,
                _ => BeginLanGameCheck(duel: true, duelTestEntry: true, visibleDuel: true, manualPreparation: true), owner: Owner);
            const string schema = "{\"type\":\"object\",\"properties\":{\"role\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2},\"confirm\":{\"type\":\"boolean\"},\"releaseLoad\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.duel-prep-peer", "隔离白方经 UTP 预选、确认或完成加载，不改可见端状态", schema, json =>
            {
                var args = JsonUtility.FromJson<DuelGuestArgs>(json);
                bool accepted = args != null && _lanGameCheck?.ApplyGuestPreparation(Service, args.role, args.confirm, args.releaseLoad) == true;
                return Result(accepted, accepted ? "" : "当前不是可操作的隔离准备阶段");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-prep-state", "只读两席权威选角、加载百分比、可见面板与活动场景", Empty,
                _ => ObserveDuelPreparation(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-prep-cancel", "幂等撤销本次准备，供验收失败时释放预加载队列", Empty, _ =>
            {
                Service.AbortDuelPreparation();
                return Result(true, "");
            }, owner: Owner);
        }

        // 场景名和面板是现场读回。阶段读权威副本，避免把 UI 动画当作业务通过。
        private static object ObserveDuelPreparation()
        {
            var service = Service;
            var state = service.DuelPrep;
            var select = RevUI.Get<DuelSelectPanel>();
            var loading = RevUI.Get<DuelLoadingPanel>();
            var panel = (DuelPrepPanelBase)loading ?? select;
            var background = panel == null ? null : panel.transform.Find("screenBackground")?.GetComponent<Image>();
            return new Dictionary<string, object>
            {
                ["phase"] = state?.phase.ToString() ?? "",
                ["seconds"] = service.DuelPickSeconds,
                ["black"] = service.DuelRoleFor(1).ToString(),
                ["white"] = service.DuelRoleFor(2).ToString(),
                ["locked"] = state?.locked ?? 0,
                ["blackLoad"] = state?.blackLoad ?? 0,
                ["whiteLoad"] = state?.whiteLoad ?? 0,
                ["canPick"] = service.DuelCanPick,
                ["guestCanPick"] = _lanGameCheck?.OwnsVisibleDuel(service) == true && _lanGameCheck.GuestCanPick,
                ["selectPanel"] = select != null,
                ["loadingPanel"] = loading != null,
                ["boardPanelVisible"] = RevUI.Get<PvpPanel>()?.gameObject.activeInHierarchy == true,
                ["backgroundReady"] = background != null && background.sprite != null && background.sprite.packed &&
                    background.rectTransform.rect.width > 1 && background.rectTransform.rect.height > 1,
                ["riftShader"] = background?.material.shader.isSupported == true ? background.material.shader.name : "",
                ["scene"] = RevScene.CurrentName,
                ["sceneLoading"] = RevScene.IsLoading,
                ["transitioning"] = Flow.IsTransitioning,
                ["ready"] = service.DuelBothReady,
                ["frame"] = service.DuelSnapshot.Frame,
                ["error"] = service.DuelError
            };
        }
    }
}
#endif
