// ============================================================
// RevAutomationAnimation —— AnimationClip 与 AnimatorController 制作。
// 【为什么】这两类资产是 Unity 的二进制式序列化结构，手改 YAML 容易写坏；状态机的状态、过渡又不是
//          普通序列化字段，asset.set 够不到。这里全部走 UnityEditor.Animations 公开 API。
// 【怎么用】clip.create → clip.curve → animator.create → animator.parameter/state/transition → animator.inspect 读回，
//          最后把 .controller 用 component.set 赋给 Animator.m_Controller。
// 【边界】只做基础 Layer 的常用结构，不做 BlendTree、IK、Avatar Mask；Timeline 需要可选包，这里不依赖。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    public static class RevAutomationAnimation
    {
        [Serializable] private sealed class ClipOptions { public string path, targetPath = "", componentType, property; public float frameRate = 30; public bool loop; }
        [Serializable] private sealed class AnimatorOptions
        {
            public string path, name, parameterType, clip, from, to;
            public int layer; public bool isDefault, hasExitTime; public float exitTime = .9f, duration = .1f;
        }

        [Serializable] public sealed class CurveInfo { public string path, property, type; public int keys; public float start, end; }
        [Serializable] public sealed class ClipInfo { public string path; public float length, frameRate; public bool loop; public CurveInfo[] curves; }
        [Serializable] public sealed class TransitionInfo { public string from, to; public bool hasExitTime; public float exitTime, duration; public string[] conditions; }
        [Serializable] public sealed class StateInfo { public string name, motion; public bool isDefault; }
        [Serializable] public sealed class ControllerInfo { public string path; public string[] parameters; public StateInfo[] states; public TransitionInfo[] transitions; }

        private static T Load<T>(string path, string extension) where T : Object
        {
            RevAutomationEditorModel.AssetPath(path, false);
            if (path == null || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 " + extension + " 资产：" + path); }
            return AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new ArgumentException("资产不存在：" + path);
        }

        private static void Save(Object asset) { EditorUtility.SetDirty(asset); AssetDatabase.SaveAssets(); }

        private static ClipInfo Describe(AnimationClip clip, string path)
        {
            return new ClipInfo
            {
                path = path, length = clip.length, frameRate = clip.frameRate, loop = AnimationUtility.GetAnimationClipSettings(clip).loopTime,
                curves = AnimationUtility.GetCurveBindings(clip).Select(binding =>
                {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    return new CurveInfo { path = binding.path, property = binding.propertyName, type = binding.type.Name, keys = curve.length,
                        start = curve.length > 0 ? curve[0].time : 0, end = curve.length > 0 ? curve[curve.length - 1].time : 0 };
                }).ToArray()
            };
        }

        [RevAutomationAction("clip.create", Keys = new[] { "path", "frameRate", "loop" }, Description = "创建空的 .anim 动画片段，可设帧率与循环；不覆盖已有资产")]
        public static object CreateClip(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<ClipOptions>(request.payloadJson ?? "{}");
            if (p.path == null || !p.path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("path 需要 .anim 后缀"); }
            RevAutomationEditorModel.RequireNewAsset(p.path);
            if (!(p.frameRate > 0) || p.frameRate > 1000) { throw new ArgumentException("frameRate 为 0..1000"); }
            var clip = new AnimationClip { frameRate = p.frameRate };
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = p.loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, p.path);
            AssetDatabase.SaveAssets();
            return Describe(clip, p.path);
        }

        [RevAutomationAction("clip.inspect", Environment = "either", Keys = new[] { "path" }, Description = "读取动画片段的时长、帧率、循环与全部浮点曲线绑定")]
        public static object InspectClip(RevAutomationRequest request)
        {
            string path = JsonUtility.FromJson<ClipOptions>(request.payloadJson ?? "{}").path;
            return Describe(Load<AnimationClip>(path, ".anim"), path);
        }

        [RevAutomationAction("clip.curve", Keys = new[] { "path", "targetPath", "componentType", "property", "keys" },
            Description = "新增或替换一条浮点曲线：targetPath 为子节点相对路径（根为空），property 如 m_LocalPosition.x，keys 为 [[时间,值],...]；时间必须严格递增；支持 Undo")]
        public static object SetCurve(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            var p = JsonUtility.FromJson<ClipOptions>(request.payloadJson ?? "{}");
            if (string.IsNullOrEmpty(p.property)) { throw new ArgumentException("需要 property"); }
            Type type = RevAutomationEditorModel.FindType(p.componentType, typeof(Component));
            if (!values.TryGetValue("keys", out object rawKeys) || !(rawKeys is List<object> rows) || rows.Count == 0 || rows.Count > 100000) { throw new ArgumentException("keys 需要 1..100000 个 [时间,值]"); }
            var keys = new Keyframe[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is List<object> pair) || pair.Count != 2 || !(pair[0] is double time) || !(pair[1] is double value)
                    || double.IsNaN(time) || double.IsInfinity(time) || double.IsNaN(value) || double.IsInfinity(value) || time < 0)
                { throw new ArgumentException("keys[" + i + "] 必须是 [非负时间,有限数值]"); }
                if (i > 0 && time <= keys[i - 1].time) { throw new ArgumentException("keys 的时间必须严格递增"); }
                keys[i] = new Keyframe((float)time, (float)value);
            }
            AnimationClip clip = Load<AnimationClip>(p.path, ".anim");
            RevAutomationEditorModel.AssetPath(p.path, true);
            Undo.RecordObject(clip, "Revolution Automation Curve");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(p.targetPath ?? "", type, p.property), new AnimationCurve(keys));
            Save(clip);
            return Describe(clip, p.path);
        }

        private static ControllerInfo Describe(AnimatorController controller, string path, int layer)
        {
            AnimatorStateMachine machine = Machine(controller, layer);
            var transitions = machine.anyStateTransitions.Select(t => Transition("Any", t)).ToList();
            foreach (ChildAnimatorState child in machine.states) { transitions.AddRange(child.state.transitions.Select(t => Transition(child.state.name, t))); }
            return new ControllerInfo
            {
                path = path, parameters = controller.parameters.Select(x => x.name + ":" + x.type).ToArray(), transitions = transitions.ToArray(),
                states = machine.states.Select(x => new StateInfo { name = x.state.name, motion = x.state.motion == null ? null : x.state.motion.name, isDefault = machine.defaultState == x.state }).ToArray()
            };
        }

        private static TransitionInfo Transition(string from, AnimatorStateTransition t) => new TransitionInfo
        {
            from = from, to = t.isExit ? "Exit" : t.destinationState == null ? null : t.destinationState.name, hasExitTime = t.hasExitTime, exitTime = t.exitTime, duration = t.duration,
            conditions = t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold).ToArray()
        };

        private static AnimatorStateMachine Machine(AnimatorController controller, int layer)
        {
            if (layer < 0 || layer >= controller.layers.Length) { throw new ArgumentException("layer 超出范围"); }
            return controller.layers[layer].stateMachine;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name)
        {
            var match = machine.states.Where(x => x.state.name == name).ToArray();
            if (match.Length != 1) { throw new ArgumentException("状态不存在或重名：" + name); }
            return match[0].state;
        }

        [RevAutomationAction("animator.create", Keys = new[] { "path" }, Description = "创建 .controller 动画控制器（含默认 Base Layer），不覆盖已有资产")]
        public static object CreateController(RevAutomationRequest request)
        {
            string path = JsonUtility.FromJson<AnimatorOptions>(request.payloadJson ?? "{}").path;
            if (path == null || !path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("path 需要 .controller 后缀"); }
            RevAutomationEditorModel.RequireNewAsset(path);
            return Describe(AnimatorController.CreateAnimatorControllerAtPath(path), path, 0);
        }

        [RevAutomationAction("animator.inspect", Environment = "either", Keys = new[] { "path", "layer" }, Description = "读取控制器的参数、指定 Layer 的状态与过渡结构")]
        public static object InspectController(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AnimatorOptions>(request.payloadJson ?? "{}");
            return Describe(Load<AnimatorController>(p.path, ".controller"), p.path, p.layer);
        }

        [RevAutomationAction("animator.parameter", Keys = new[] { "path", "name", "parameterType" }, Description = "添加控制器参数，parameterType 为 Float/Int/Bool/Trigger，重名拒绝")]
        public static object AddParameter(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AnimatorOptions>(request.payloadJson ?? "{}");
            if (string.IsNullOrWhiteSpace(p.name)) { throw new ArgumentException("需要 name"); }
            if (!new[] { "Float", "Int", "Bool", "Trigger" }.Contains(p.parameterType)) { throw new ArgumentException("parameterType 必须为 Float/Int/Bool/Trigger"); }
            AnimatorController controller = Load<AnimatorController>(p.path, ".controller");
            RevAutomationEditorModel.AssetPath(p.path, true);
            if (controller.parameters.Any(x => x.name == p.name)) { throw new ArgumentException("参数已存在：" + p.name); }
            controller.AddParameter(p.name, (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), p.parameterType));
            Save(controller);
            return Describe(controller, p.path, 0);
        }

        [RevAutomationAction("animator.state", Keys = new[] { "path", "name", "clip", "layer", "isDefault" }, Description = "向 Layer 添加状态，可绑定 .anim 片段并设为默认状态，重名拒绝")]
        public static object AddState(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AnimatorOptions>(request.payloadJson ?? "{}");
            if (string.IsNullOrWhiteSpace(p.name)) { throw new ArgumentException("需要 name"); }
            AnimatorController controller = Load<AnimatorController>(p.path, ".controller");
            RevAutomationEditorModel.AssetPath(p.path, true);
            AnimatorStateMachine machine = Machine(controller, p.layer);
            if (machine.states.Any(x => x.state.name == p.name)) { throw new ArgumentException("状态已存在：" + p.name); }
            AnimationClip clip = string.IsNullOrEmpty(p.clip) ? null : Load<AnimationClip>(p.clip, ".anim");
            AnimatorState state = machine.AddState(p.name);
            state.motion = clip;
            if (p.isDefault) { machine.defaultState = state; }
            Save(controller);
            return Describe(controller, p.path, p.layer);
        }

        [RevAutomationAction("animator.transition", Keys = new[] { "path", "from", "to", "layer", "hasExitTime", "exitTime", "duration", "conditions" },
            Description = "添加过渡：from 可为状态名或 Any，to 可为状态名或 Exit；conditions 为 [{parameter,mode,threshold}]，mode 为 If/IfNot/Greater/Less/Equals/NotEqual。无条件且无 exitTime 的过渡会被拒绝，否则它永远不触发")]
        public static object AddTransition(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            var p = JsonUtility.FromJson<AnimatorOptions>(request.payloadJson ?? "{}");
            var conditions = new List<(string parameter, AnimatorConditionMode mode, float threshold)>();
            AnimatorController controller = Load<AnimatorController>(p.path, ".controller");
            RevAutomationEditorModel.AssetPath(p.path, true);
            if (values.TryGetValue("conditions", out object raw))
            {
                foreach (object item in raw as List<object> ?? throw new ArgumentException("conditions 必须是数组"))
                {
                    var entry = item as Dictionary<string, object> ?? throw new ArgumentException("condition 必须是对象");
                    string parameter = entry.TryGetValue("parameter", out object name) ? name as string : null;
                    string mode = entry.TryGetValue("mode", out object rawMode) ? rawMode as string : null;
                    if (!controller.parameters.Any(x => x.name == parameter)) { throw new ArgumentException("参数不存在：" + parameter); }
                    if (mode == null || !Enum.TryParse(mode, out AnimatorConditionMode parsed) || !Enum.IsDefined(typeof(AnimatorConditionMode), parsed)) { throw new ArgumentException("mode 不合法：" + mode); }
                    conditions.Add((parameter, parsed, entry.TryGetValue("threshold", out object threshold) && threshold is double number ? (float)number : 0));
                }
            }
            if (conditions.Count == 0 && !p.hasExitTime) { throw new ArgumentException("没有条件也没有 hasExitTime 的过渡永远不会触发"); }
            if (string.IsNullOrEmpty(p.from) || string.IsNullOrEmpty(p.to)) { throw new ArgumentException("需要 from 和 to"); }
            AnimatorStateMachine machine = Machine(controller, p.layer);
            if (p.from == "Any" && p.to == "Exit") { throw new ArgumentException("Any 不能直接过渡到 Exit"); }
            AnimatorStateTransition transition = p.from == "Any" ? machine.AddAnyStateTransition(State(machine, p.to))
                : p.to == "Exit" ? State(machine, p.from).AddExitTransition() : State(machine, p.from).AddTransition(State(machine, p.to));
            transition.hasExitTime = p.hasExitTime; transition.exitTime = p.exitTime; transition.duration = p.duration;
            foreach (var condition in conditions) { transition.AddCondition(condition.mode, condition.threshold, condition.parameter); }
            Save(controller);
            return Describe(controller, p.path, p.layer);
        }
    }
}
