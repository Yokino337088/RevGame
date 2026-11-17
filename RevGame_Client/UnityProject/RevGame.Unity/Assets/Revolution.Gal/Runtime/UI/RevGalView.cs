using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution.Gal
{
    public enum RevGalInput
    {
        Advance, Auto, Skip, Rollback, History, Save, Load, Close, OpenSettings, CloseSettings
    }

    /// <summary>挂在 Panel 的 View 子节点。它只渲染帧、控制打字机和报告 UGUI 输入。</summary>
    public sealed class RevGalView : MonoBehaviour
    {
        /// <summary>Prefab 保存部位和组件的关联，运行时直接访问引用。</summary>
        [Serializable]
        private struct ThemeTarget
        {
            public RevGalThemePart part;
            public RectTransform rect;
        }

        [SerializeField] private RevGalPanelTheme theme;
        [SerializeField] private ThemeTarget[] themeTargets;
        [SerializeField] private Image background;
        [SerializeField] private Image left;
        [SerializeField] private Image center;
        [SerializeField] private Image right;
        [SerializeField] private Text title;
        [SerializeField] private Text speaker;
        [SerializeField] private Text dialogue;
        [SerializeField] private ScrollRect dialogueScroll;
        [SerializeField] private Text notice;
        [SerializeField] private Text mode;
        [SerializeField] private Button advance;
        [SerializeField] private Button auto;
        [SerializeField] private Button skip;
        [SerializeField] private Button rollback;
        [SerializeField] private Button history;
        [SerializeField] private Button save;
        [SerializeField] private Button load;
        [SerializeField] private Button close;
        [SerializeField] private CanvasGroup storyInput;
        [SerializeField] private GameObject settingsOverlay;
        [SerializeField] private Button openSettings;
        [SerializeField] private Button closeSettings;
        [SerializeField] private Text settingsMode;
        [SerializeField] private Text settingsNotice;
        [SerializeField] private RectTransform choiceRoot;
        [SerializeField] private GameObject choiceOverlay;
        [SerializeField] private Button choiceTemplate;
        [SerializeField] private GameObject historyOverlay;
        [SerializeField] private ScrollRect historyScroll;
        [SerializeField] private Text historyText;
        [SerializeField] private Text historyPage;
        [SerializeField] private Button olderHistory;
        [SerializeField] private Button newerHistory;
        [SerializeField] private Button closeHistory;

        private readonly List<Button> _choiceButtons = new List<Button>();
        private readonly Dictionary<string, Sprite> _atlasSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private RevGalFrame _frame;
        private string _text = "";
        private int[] _offsets = Array.Empty<int>();
        private float _speed;
        private float _elapsed;
        private int _written;
        private const int HistoryPageSize = 24;
        private IReadOnlyList<string> _history = Array.Empty<string>();
        private int _historyIndex;
        private bool _returnToSettings;

        public event Action<RevGalInput> InputRequested;
        public event Action<string> ChoiceRequested;
        public event Action<int> Revealed;

        public bool TextComplete => _written >= _offsets.Length;
        public bool HistoryOpen => historyOverlay.activeSelf;
        public bool SettingsOpen => settingsOverlay != null && settingsOverlay.activeSelf;
        public RevGalPanelTheme Theme => theme;
        public string VisibleDialogue => dialogue.text;
        internal bool ModalOpen => SettingsOpen || HistoryOpen;
        internal bool BackgroundVisible => background.enabled && background.sprite != null;

        private void Awake()
        {
            ApplyTheme();
            Bind(advance, RevGalInput.Advance);
            Bind(auto, RevGalInput.Auto);
            Bind(skip, RevGalInput.Skip);
            Bind(rollback, RevGalInput.Rollback);
            Bind(history, RevGalInput.History);
            Bind(save, RevGalInput.Save);
            Bind(load, RevGalInput.Load);
            Bind(close, RevGalInput.Close);
            Bind(openSettings, RevGalInput.OpenSettings);
            Bind(closeSettings, RevGalInput.CloseSettings);
            closeHistory.onClick.AddListener(CloseHistory);
            olderHistory.onClick.AddListener(() => PresentHistory(_historyIndex - 1));
            newerHistory.onClick.AddListener(() => PresentHistory(_historyIndex + 1));
        }

        /// <summary>主题在面板打开前应用一次，克隆的图集 Sprite 随该 View 回收。</summary>
        private void ApplyTheme()
        {
            if (theme == null)
            {
                return;
            }
            if (theme.Font != null)
            {
                foreach (var label in GetComponentsInChildren<Text>(true))
                {
                    label.font = theme.Font;
                }
            }
            foreach (var target in themeTargets ?? Array.Empty<ThemeTarget>())
            {
                var style = theme.Parts.FirstOrDefault(part => part != null && part.Part == target.part);
                if (style == null || target.rect == null)
                {
                    continue;
                }
                Sprite sprite = RevGalPartStyle.UsesThemeSprite(target.part) ? ResolveSprite(style) : null;
                style.ApplyTo(target.rect, sprite);
            }
        }

        private Sprite ResolveSprite(RevGalPartStyle style)
        {
            if (style.Sprite != null || string.IsNullOrWhiteSpace(style.AtlasSprite))
            {
                return style.Sprite;
            }
            if (!_atlasSprites.TryGetValue(style.AtlasSprite, out var sprite))
            {
                sprite = theme.Atlas == null ? null : theme.Atlas.GetSprite(style.AtlasSprite);
                _atlasSprites.Add(style.AtlasSprite, sprite);
                if (sprite == null)
                {
                    RevLog.Warn($"Gal 主题「{theme.name}」找不到图集切片「{style.AtlasSprite}」", "RevGal");
                }
            }
            return sprite;
        }

        private void OnDestroy()
        {
            foreach (var sprite in _atlasSprites.Values)
            {
                if (sprite != null)
                {
                    Destroy(sprite);
                }
            }
            _atlasSprites.Clear();
        }

        private void Bind(Button button, RevGalInput action)
        {
            // 旧版生成的 Prefab 保留原有按钮和绑定，设置弹层由新版生成器装配。
            if (button != null)
            {
                button.onClick.AddListener(() => InputRequested?.Invoke(action));
            }
        }

        internal void Present(string storyTitle, RevGalFrame frame, float speed, bool canRollback, bool canStore, bool canLoad)
        {
            _frame = frame;
            title.text = string.IsNullOrEmpty(storyTitle) ? frame.Title ?? "" : storyTitle;
            speaker.text = frame.Speaker;
            speaker.gameObject.SetActive(!string.IsNullOrEmpty(speaker.text));
            _text = frame.Text;
            _offsets = StringInfo.ParseCombiningCharacters(_text);
            _written = 0;
            _elapsed = 0;
            _speed = speed;
            dialogue.text = "";
            dialogueScroll.StopMovement();
            dialogueScroll.verticalNormalizedPosition = 1f;
            advance.interactable = frame.Kind == RevGalNodeKind.Line;
            rollback.interactable = canRollback;
            save.interactable = canStore;
            load.interactable = canLoad;
            auto.interactable = skip.interactable = frame.Kind == RevGalNodeKind.Line;

            for (int index = 0; index < frame.Choices.Count; index++)
            {
                RevGalChoice choice = frame.Choices[index];
                if (index == _choiceButtons.Count)
                {
                    var button = Instantiate(choiceTemplate, choiceRoot, false);
                    button.name = "Choice_" + (index + 1);
                    _choiceButtons.Add(button);
                }
                var target = _choiceButtons[index];
                target.GetComponentInChildren<Text>(true).text = choice.Text;
                target.onClick.RemoveAllListeners();
                target.onClick.AddListener(() => ChoiceRequested?.Invoke(choice.Id));
                target.interactable = choice.Enabled;
                target.gameObject.SetActive(true);
            }
            for (int index = frame.Choices.Count; index < _choiceButtons.Count; index++)
            {
                _choiceButtons[index].gameObject.SetActive(false);
            }
            choiceOverlay.SetActive(frame.Choices.Count != 0);
            if (frame.Kind != RevGalNodeKind.Line || speed <= 0 || _offsets.Length == 0)
            {
                Reveal();
            }
        }

        internal void Reveal()
        {
            if (_frame == null)
            {
                return;
            }
            _written = _offsets.Length;
            dialogue.text = _text;
            Revealed?.Invoke(_frame.Token);
        }

        private void Update()
        {
            if (_frame == null || TextComplete || ModalOpen)
            {
                return;
            }
            _elapsed += Time.unscaledDeltaTime;
            int count = Mathf.Min(_offsets.Length, Mathf.FloorToInt(_elapsed * _speed));
            if (count <= _written)
            {
                return;
            }
            if (count >= _offsets.Length)
            {
                Reveal();
            }
            else
            {
                _written = count;
                dialogue.text = _text.Substring(0, _offsets[count]);
            }
        }

        internal void ToggleHistory(IReadOnlyList<string> lines)
        {
            if (HistoryOpen)
            {
                CloseHistory();
                return;
            }
            _returnToSettings = SettingsOpen;
            historyOverlay.SetActive(true);
            SetSettingsOpen(false);
            _history = lines;
            PresentHistory((lines.Count - 1) / HistoryPageSize);
        }

        /// <summary>弹层独占剧情区输入，包含鼠标、触摸和 uGUI 的按钮提交。</summary>
        internal void SetSettingsOpen(bool open)
        {
            if (settingsOverlay != null)
            {
                settingsOverlay.SetActive(open);
            }
            if (storyInput != null)
            {
                storyInput.interactable = storyInput.blocksRaycasts = !ModalOpen;
            }
        }

        private void PresentHistory(int index)
        {
            int total = Mathf.Max(1, (_history.Count + HistoryPageSize - 1) / HistoryPageSize);
            _historyIndex = Mathf.Clamp(index, 0, total - 1);
            historyText.text = _history.Count == 0 ? "还没有对话记录"
                : string.Join("\n\n", _history.Skip(_historyIndex * HistoryPageSize).Take(HistoryPageSize));
            historyPage.text = (_historyIndex + 1) + " / " + total;
            olderHistory.interactable = _historyIndex > 0;
            newerHistory.interactable = _historyIndex + 1 < total;
            Canvas.ForceUpdateCanvases();
            historyScroll.StopMovement();
            historyScroll.verticalNormalizedPosition = _historyIndex == total - 1 ? 0f : 1f;
        }

        internal void CloseHistory()
        {
            historyOverlay.SetActive(false);
            _history = Array.Empty<string>();
            SetSettingsOpen(_returnToSettings);
            _returnToSettings = false;
        }

        internal void ShowNotice(string message)
        {
            notice.text = message ?? "";
            if (settingsNotice != null)
            {
                settingsNotice.text = notice.text;
            }
        }

        internal void ShowMode(string label)
        {
            mode.text = label ?? "";
            if (settingsMode != null)
            {
                settingsMode.text = string.IsNullOrEmpty(label) ? "手动播放" : label;
            }
        }

        internal void ShowBackground(Sprite value) => SetImage(background, value);
        internal void ShowLeft(Sprite value) => SetImage(left, value);
        internal void ShowCenter(Sprite value) => SetImage(center, value);
        internal void ShowRight(Sprite value) => SetImage(right, value);

        private static void SetImage(Image image, Sprite sprite)
        {
            image.sprite = sprite;
            image.enabled = sprite != null;
            if (sprite != null)
            {
                image.CrossFadeAlpha(0f, 0f, true);
                image.CrossFadeAlpha(1f, .16f, true);
            }
        }

        internal void ResetView()
        {
            _frame = null;
            dialogue.text = "";
            _returnToSettings = false;
            CloseHistory();
            ShowMode(null);
            ShowNotice(null);
        }
    }
}
