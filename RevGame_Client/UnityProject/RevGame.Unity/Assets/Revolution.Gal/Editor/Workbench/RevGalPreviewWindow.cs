using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>同运行时解释器的编辑态试读。信号由策划模拟确认，本地档案不参与。</summary>
    internal sealed class RevGalPreviewWindow : EditorWindow
    {
        private RevGalStory _story;
        private string _entry;
        private RevGalSession _session;
        private RevGalSave _bookmark;
        private string _error;
        private Vector2 _scroll;

        public static void Open(RevGalStory story, string entry)
        {
            var window = GetWindow<RevGalPreviewWindow>();
            window.titleContent = new GUIContent("Gal 试读");
            window.minSize = new Vector2(375, 300);
            window._story = story;
            window._entry = entry;
            window.Restart();
            window.Show();
        }

        private void OnDisable()
        {
            _session?.Dispose();
            _session = null;
        }

        private void Restart()
        {
            Run(() =>
            {
                _session?.Dispose();
                _session = null;
                _bookmark = null;
                _session = RevGalPlayback.Preview(_story, _entry);
                _scroll = Vector2.zero;
            });
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("重来", EditorStyles.toolbarButton)) { Restart(); }
                using (new EditorGUI.DisabledScope(_session == null))
                {
                    if (GUILayout.Button("回退", EditorStyles.toolbarButton)) { Run(() => _session.Rollback()); }
                    if (GUILayout.Button("记住此页", EditorStyles.toolbarButton)) { Run(() => _bookmark = _session.Capture()); }
                    if (GUILayout.Button("恢复试读", EditorStyles.toolbarButton)) { Run(() => _session.Restore(_bookmark)); }
                }
            }
            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }
            if (_session == null)
            {
                return;
            }
            var frame = _session.Frame;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField(frame.Title ?? frame.Id, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(RevGalEditorLabels.Kind(frame.Kind) + " · " + frame.Id, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(frame.Speaker))
            {
                EditorGUILayout.LabelField(frame.Speaker, EditorStyles.boldLabel);
            }
            EditorGUILayout.LabelField(frame.Text, EditorStyles.wordWrappedLabel);
            if (frame.Kind == RevGalNodeKind.Line && GUILayout.Button("继续"))
            {
                Run(() => _session.Advance());
            }
            foreach (var choice in frame.Choices)
            {
                using (new EditorGUI.DisabledScope(!choice.Enabled))
                {
                    if (GUILayout.Button(choice.Text)) { Run(() => _session.Choose(choice.Id)); }
                }
            }
            if (frame.Signal != null)
            {
                EditorGUILayout.HelpBox("宿主信号：" + frame.Signal.Name + " · " + frame.Signal.Argument, MessageType.Info);
                if (GUILayout.Button("模拟宿主完成")) { Run(() => _session.CompleteSignal(frame.Token)); }
            }
            var vars = _session.Variables();
            if (vars.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("变量", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(string.Join("    ", vars.Select(x => x.Id + " = " + x.Value)), EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void Run(Action action)
        {
            _error = null;
            try
            {
                action();
            }
            catch (Exception error)
            {
                _error = error.Message;
            }
            Repaint();
        }
    }
}
