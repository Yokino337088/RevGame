#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Pvp.Duel;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine.EventSystems;

namespace RevGame.Pvp
{
    public static partial class PvpAutomation
    {
        // 验收结果留到下一次启动，供 RevMCP 在局内和清理后读取。
        private static Dictionary<string, object> _duelArenaResult = new Dictionary<string, object> { ["stage"] = "idle" };

        // 由决斗 HUD 验收统一登记。读状态不会碰玩家输入。
        private static void RegisterDuelArenaChecks()
        {
            RevRuntimeAutomation.Register("pvp.duel-arena-check", "本地人机通过正式 HUD 验证空挥、边界和技能轨迹", Empty,
                _ => StartDuelArenaCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-arena-state", "读取决斗表现与规则验收状态", Empty,
                _ => _duelArenaResult, readOnly: true, owner: Owner);
        }

        // 只在本地新开的人机局接受测试，确保起始位置和血量可重复。
        private static object StartDuelArenaCheck()
        {
            var hud = RevUI.Get<DuelHudPanel>();
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            var service = Service;
            DuelSnapshot frame = service.DuelSnapshot;
            if (view == null || hud == null || view.HudPanel != hud || !service.DuelBothReady ||
                service.Mode != PvpConnectionMode.Local || service.MySide != GoSide.Black ||
                frame.FirstHealth != DuelConfig.Health || frame.SecondHealth != DuelConfig.Health ||
                _duelArenaResult["stage"].Equals("running"))
            {
                return Result(false, "请在新开的黑方人机决斗进入场地后执行。");
            }
            bool pausedBefore = service.DuelComputerAiPaused;
            if (!service.TrySetDuelComputerAiPaused(true))
            {
                return Result(false, "这局无法暂停测试用电脑。");
            }
            _duelArenaResult = new Dictionary<string, object> { ["stage"] = "running", ["error"] = "" };
            RunDuelArenaCheck(service, view, hud, pausedBefore).Forget();
            return Result(true, "");
        }

        // 一条玩家链依次点击三个普攻、摇杆顶住结界和技能。以权威帧与实际 Sprite、Shader 共同验收。
        private static async RevTask RunDuelArenaCheck(PvpService service, PvpDuelView view, DuelHudPanel hud, bool pausedBefore)
        {
            string id = service.DuelId;
            var stick = hud.MoveStick.gameObject;
            var move = new PointerEventData(EventSystem.current) { pointerId = 44 };
            var press = new PointerEventData(EventSystem.current) { pointerId = 45 };
            try
            {
                DuelSnapshot initial = service.DuelSnapshot;
                bool yaya = service.DuelRoleFor(1) == DuelRole.Yaya;
                int initialGap = initial.SecondX - initial.FirstX;
                if (initialGap <= DuelConfig.ActionRange(yaya, 6) + 300)
                {
                    throw new InvalidOperationException("双方起手间距不足，无法验证三段全部空挥。");
                }
                int previous = -1;
                foreach (int action in new[] { 1, 5, 6 })
                {
                    ClickDuelHudButton(hud.Jab.gameObject, press);
                    await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == action &&
                        service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge > previous, "普攻空挥接段 " + action);
                    int started = service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge;
                    if (previous >= 0 && started - previous > DuelConfig.ActionDuration(action == 5 ? 1 : 5))
                    {
                        throw new InvalidOperationException("普攻等到完整恢复后才起手，没有在取消窗口接段。");
                    }
                    previous = started;
                }
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == 0, "三段完整恢复");
                if (service.DuelSnapshot.SecondHealth != initial.SecondHealth || view.Effects.ImpactsForCheck != 0 ||
                    service.DuelSnapshot.FirstCombo != 0)
                {
                    throw new InvalidOperationException("空挥三段改变了对手血量或真实命中数。");
                }
                _duelArenaResult["whiff"] = "1,5,6";
                _duelArenaResult["whiffHp"] = service.DuelSnapshot.SecondHealth;
                _duelArenaResult["revUi"] = hud.isActiveAndEnabled && hud.FirstPortrait.sprite.packed;

                // 继续按住方向跨过多个逻辑帧，验证一次抵边只触发一次 Shader 扫光。
                int touches = view.CameraRig.LeftTouchesForCheck;
                PressDuelHudButton(stick, move, -.72f);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstX == -DuelConfig.ArenaExtent &&
                    view.CameraRig.LeftTouchesForCheck > touches, "左侧结界响应");
                int boundaryFrame = service.DuelSnapshot.Frame;
                _duelArenaResult["wardVisible"] = view.CameraRig.LeftWard.color.a > .2f &&
                    view.CameraRig.LeftWard.material.shader.isSupported;
                _duelArenaResult["cameraPan"] = view.CameraRig.Fighters.anchoredPosition.x;
                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame >= boundaryFrame + 5, "保持按住边界");
                if (view.CameraRig.LeftTouchesForCheck != touches + 1 || service.DuelSnapshot.FirstX != -DuelConfig.ArenaExtent)
                {
                    throw new InvalidOperationException("持续顶住边界时重复触发或越过了原生边界。");
                }
                _duelArenaResult["edgeTouches"] = view.CameraRig.LeftTouchesForCheck - touches;
                _duelArenaResult["boundaryX"] = service.DuelSnapshot.FirstX;
                _duelArenaResult["backdrop"] = view.CameraRig.Backdrop.sprite.name;

                // 用同一触点走进扩大后的技能一射程，正式 HUD 点击产生权威命中。
                DragDuelStick(stick, move, .76f);
                await WaitForDuelControl(id, () => service.DuelSnapshot.SecondX - service.DuelSnapshot.FirstX <= 3100 &&
                    service.DuelSnapshot.FirstEnergy >= DuelConfig.SkillCost, "进入新版技能射程");
                ExecuteEvents.Execute(stick, move, ExecuteEvents.pointerUpHandler);
                ClickDuelHudButton(hud.Skill.gameObject, press);
                await WaitForDuelControl(id, () => service.DuelSnapshot.SecondHealth < initial.SecondHealth &&
                    view.Effects.ImpactsForCheck > 0, "技能命中与程序短效");
                _duelArenaResult["skillHp"] = service.DuelSnapshot.SecondHealth;
                _duelArenaResult["particles"] = view.Effects.SpawnedParticlesForCheck;
                _duelArenaResult["fadeObserved"] = view.FirstAnimator.AttackFadeSamples > 0;
                _duelArenaResult["stage"] = "passed";
            }
            catch (Exception error)
            {
                _duelArenaResult["error"] = error.Message;
                _duelArenaResult["stage"] = "failed";
            }
            finally
            {
                if (stick != null)
                {
                    ExecuteEvents.Execute(stick, move, ExecuteEvents.pointerUpHandler);
                }
                if (service.DuelId == id)
                {
                    service.TrySetDuelComputerAiPaused(pausedBefore);
                }
            }
        }
    }
}
#endif
