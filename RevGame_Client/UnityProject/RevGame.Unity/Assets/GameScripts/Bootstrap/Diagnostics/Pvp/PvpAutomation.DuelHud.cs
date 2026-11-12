#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Pvp.Duel;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    // 决斗 HUD 的开发验收入口，使用正式 UI 射线、触点和已确认快照，供 RevMCP 读取。
    public static partial class PvpAutomation
    {
        // 测试阶段的只读结果，退出场景后仍能读取失败原因。
        private static string _hudCheckStage = "";
        // 本轮未通过的具体断言。
        private static string _hudCheckError = "";
        // 只有权威快照同时出现离地和横向移动才会置位。
        private static bool _hudMovedAndJumped;
        // 独立的技能触点是否在摇杆持续持有时完成权威开招。
        private static bool _hudHeldWhileCasting;

        // 随原决斗诊断一起注册。两个查询无副作用，适合 RevMCP 有界轮询。
        private static void RegisterDuelHudAutomation()
        {
            RevRuntimeAutomation.Register("pvp.duel-hud-input-check", "经真实触点验证横向摇杆与跳跃同时操作", Empty,
                _ => StartDuelHudCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-hud-input-state", "读取摇杆与多触点验收结果", Empty,
                _ => new Dictionary<string, object> { ["stage"] = _hudCheckStage, ["error"] = _hudCheckError,
                    ["movedAndJumped"] = _hudMovedAndJumped, ["heldWhileCasting"] = _hudHeldWhileCasting },
                readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-hud-layout", "读取正式圆键的屏幕几何与图集状态", Empty,
                _ => InspectDuelHud(), readOnly: true, owner: Owner);
            RegisterDuelArenaChecks();
        }

        // 仅控制测试夹具创建的可见对局，避免将诊断输入发给正常联机房间。
        private static object StartDuelHudCheck()
        {
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            if (view == null || view.HudPanel == null || _lanGameCheck == null || !_lanGameCheck.OwnsVisibleDuel(Service) ||
                !Service.DuelBothReady || _hudCheckStage == "running")
            {
                return Result(false, "请先进入隔离的可见决斗。");
            }
            _hudCheckStage = "running";
            _hudCheckError = "";
            _hudMovedAndJumped = false;
            _hudHeldWhileCasting = false;
            RunDuelHudCheck(view).Forget();
            return Result(true, "");
        }

        // 分别用移动触点和动作触点验证真实双手操作，最后释放所有指针。
        private static async RevTask RunDuelHudCheck(PvpDuelView view)
        {
            string duel = Service.DuelId;
            GameObject stick = view.HudPanel.MoveStick.gameObject;
            var move = new PointerEventData(EventSystem.current) { pointerId = 21 };
            var other = new PointerEventData(EventSystem.current) { pointerId = 22 };
            try
            {
                PressDuelHudButton(stick, move, .08f);
                if (view.HudPanel.MoveStick.Direction != 0)
                {
                    throw new InvalidOperationException("圆心死区内触点发出了移动意图。");
                }
                DragDuelStick(stick, move, .72f);
                PressDuelHudButton(stick, other, -.72f);
                ExecuteEvents.Execute(stick, other, ExecuteEvents.pointerUpHandler);
                if (view.HudPanel.MoveStick.Direction != 1)
                {
                    throw new InvalidOperationException("其他触点抢占或清除了摇杆方向。");
                }
                int start = Service.DuelSnapshot.FirstX;
                ClickDuelHudButton(view.HudPanel.Jump.gameObject, other);
                await WaitForDuelControl(duel, () => Service.DuelSnapshot.FirstX > start && Service.DuelSnapshot.FirstY > 0, "权威同时走位和跳跃");
                _hudMovedAndJumped = true;

                // 向圆外拖动，验证真实拖拽捕获能反向并持续工作。
                int turn = Service.DuelSnapshot.FirstX;
                DragDuelStick(stick, move, -1.25f);
                await WaitForDuelControl(duel, () => Service.DuelSnapshot.FirstX < turn, "摇杆圆外反向");

                // 两席相距超过技能攻击范围，验证开招触点而不提前改变后续连招用例的对手血量。
                await WaitForDuelControl(duel, () => Service.DuelSnapshot.FirstY == 0 &&
                    Service.DuelSnapshot.FirstEnergy >= DuelConfig.SkillCost &&
                    Service.DuelSnapshot.SecondX - Service.DuelSnapshot.FirstX > 2500, "双手技能试放前置");
                ClickDuelHudButton(view.HudPanel.Skill.gameObject, other);
                await WaitForDuelControl(duel, () => Service.DuelSnapshot.FirstAction == 2 &&
                    view.HudPanel.MoveStick.Direction == -1, "技能与摇杆由各自触点控制");
                _hudHeldWhileCasting = true;
                ExecuteEvents.Execute(stick, move, ExecuteEvents.pointerUpHandler);
                int released = Service.DuelSnapshot.Frame;
                await WaitForDuelControl(duel, () => Service.DuelSnapshot.Frame >= released + 3 &&
                    Service.DuelSnapshot.FirstAction == 0 && Service.DuelSnapshot.FirstX == Service.DuelPreviousSnapshot.FirstX, "摇杆松手归中");
                _hudCheckStage = "passed";
            }
            catch (Exception error)
            {
                _hudCheckError = error.Message;
                _hudCheckStage = "failed";
            }
            finally
            {
                if (stick != null)
                {
                    ExecuteEvents.Execute(stick, move, ExecuteEvents.pointerUpHandler);
                }
            }
        }

        // 模拟同一触点被 EventSystem 持有后的拖动，和正常拖拽一样允许坐标越出底座。
        private static void DragDuelStick(GameObject stick, PointerEventData pointer, float horizontal)
        {
            var rect = (RectTransform)stick.transform;
            Camera camera = pointer.pressEventCamera;
            Vector2 local = rect.rect.center + Vector2.right * (horizontal * rect.rect.width * .5f);
            pointer.position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
            ExecuteEvents.Execute(stick, pointer, ExecuteEvents.dragHandler);
        }

        // 用实际 Canvas 落屏坐标检验圆度、间距和安全区，确认主要按钮使用已打包图集。
        private static object InspectDuelHud()
        {
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            if (view == null || view.HudPanel == null)
            {
                return new Dictionary<string, object> { ["ready"] = false };
            }
            Component[] controls = { view.HudPanel.MoveStick, view.HudPanel.Jump, view.HudPanel.Jab, view.HudPanel.Skill, view.HudPanel.SkillTwo, view.HudPanel.Ultimate };
            var centers = new Vector2[controls.Length];
            var radii = new float[controls.Length];
            var corners = new Vector3[4];
            Vector2 screen = new Vector2(UnityEngine.Device.Screen.width, UnityEngine.Device.Screen.height);
            Rect normalized = GameBattleLayout.SafeViewport();
            var safe = new Rect(Vector2.Scale(normalized.position, screen), Vector2.Scale(normalized.size, screen));
            bool inside = true;
            bool separated = true;
            bool packed = true;
            float roundError = 0;
            for (int i = 0; i < controls.Length; i++)
            {
                var control = (RectTransform)controls[i].transform;
                control.GetWorldCorners(corners);
                Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
                centers[i] = (min + max) * .5f;
                radii[i] = Mathf.Max(max.x - min.x, max.y - min.y) * .5f;
                roundError = Mathf.Max(roundError, Mathf.Abs((max.x - min.x) - (max.y - min.y)));
                inside &= min.x >= safe.xMin - 1 && min.y >= safe.yMin - 1 && max.x <= safe.xMax + 1 && max.y <= safe.yMax + 1;
                packed &= control.GetComponent<Image>().sprite.packed;
                for (int j = 0; j < i; j++)
                {
                    separated &= Vector2.Distance(centers[i], centers[j]) >= radii[i] + radii[j];
                }
            }
            return new Dictionary<string, object> { ["ready"] = true, ["controlCount"] = controls.Length,
                ["round"] = roundError <= 1, ["insideSafeArea"] = inside, ["separated"] = separated,
                ["packed"] = packed, ["primaryLargest"] = radii[2] > Mathf.Max(radii[3], Mathf.Max(radii[4], radii[5])),
                ["playerNamesVisible"] = view.HudPanel.FirstName.cachedTextGenerator.characterCountVisible > 2 &&
                    view.HudPanel.SecondName.cachedTextGenerator.characterCountVisible > 2,
                ["width"] = screen.x, ["height"] = screen.y };
        }
    }
}
#endif
