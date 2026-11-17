using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>在 Prefab Mode 为运行时动态槽预览实际美术。临时图形随编辑阶段销毁，保存时先移除。</summary>
    [InitializeOnLoad]
    public sealed partial class GameUiPrefabPreview : IDisposable
    {
        private const string Story = "Assets/GameRes/UI/Panel/Story/StoryPanel.prefab";
        private const string HandCard = "Assets/GameRes/UI/Item/Card/CardItem.prefab";
        private const string Prepare = "Assets/GameRes/UI/Panel/PreparePanel.prefab";
        private const string Reward = "Assets/GameRes/UI/Panel/RewardPanel.prefab";
        private static GameUiPrefabPreview _stagePreview;

        private readonly List<GameObject> _overlays = new List<GameObject>();
        private readonly List<(Image image, Sprite sprite)> _overrides = new List<(Image, Sprite)>();
        private readonly GameObject _root;
        private RawImage _backdrop;

        static GameUiPrefabPreview()
        {
            PrefabStage.prefabStageOpened += OnStageOpened;
            PrefabStage.prefabStageClosing += OnStageClosing;
            PrefabStage.prefabSaving += RemoveFromStage;
            PrefabStage.prefabSaved += OnStageSaved;
            AssemblyReloadEvents.beforeAssemblyReload += DetachStage;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            // 编译重载时用户可能已经在改这份 Prefab。
            EditorApplication.delayCall += RefreshStage;
        }

        private GameUiPrefabPreview(GameObject root)
        {
            _root = root;
        }

        private static void OnStageOpened(PrefabStage stage)
        {
            if (HasPreview(stage.assetPath))
            {
                EditorApplication.delayCall += RefreshStage;
            }
        }

        private static void OnStageClosing(PrefabStage stage)
        {
            RemoveFromStage(stage.prefabContentsRoot);
        }

        private static void RemoveFromStage(GameObject root)
        {
            if (_stagePreview != null && _stagePreview._root == root)
            {
                DetachStage();
            }
        }

        private static void OnStageSaved(GameObject root)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot == root && HasPreview(stage.assetPath))
            {
                EditorApplication.delayCall += RefreshStage;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                DetachStage();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += RefreshStage;
            }
        }

        private static void RefreshStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (EditorApplication.isPlayingOrWillChangePlaymode || stage == null || !HasPreview(stage.assetPath) || _stagePreview != null)
            {
                return;
            }
            _stagePreview = Attach(stage.assetPath, stage.prefabContentsRoot);
            if (_stagePreview != null)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }

        private static void DetachStage()
        {
            EditorApplication.delayCall -= RefreshStage;
            _stagePreview?.Dispose();
            _stagePreview = null;
        }

        /// <summary>同一份排版可挂到实际 Prefab Stage 或离屏验收实例。原控件的序列化属性保持原样。</summary>
        public static GameUiPrefabPreview Attach(string path, GameObject root)
        {
            if (!HasPreview(path))
            {
                return null;
            }

            var preview = new GameUiPrefabPreview(root);
            try
            {
                switch (path)
                {
                    case Story:
                        preview.ShowOpening();
                        break;
                    case HandCard:
                    case Prepare:
                        foreach (var card in root.GetComponentsInChildren<CardItem>(true))
                        {
                            preview.ShowCard(card);
                        }
                        break;
                    case Reward:
                        preview.ShowRewards();
                        break;
                }
                return preview;
            }
            catch
            {
                preview.Dispose();
                throw;
            }
        }

        private static bool HasPreview(string path)
        {
            return path is Story or HandCard or Prepare or Reward;
        }

        private void ShowOpening()
        {
            var backgroundSlot = _root.transform.Find("storyBackdrop")?.GetComponent<RawImage>();
            Transform view = _root.transform.Find("storyView");
            var book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
            var opening = book?.Find("opening");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameRes/Lobby/MagicCottage.png");
            if (backgroundSlot != null && backgroundSlot.texture == null && texture != null)
            {
                _backdrop = AddOverlay<RawImage>(backgroundSlot.rectTransform);
                _backdrop.texture = texture;
                _backdrop.color = Color.white;
            }
            if (view == null || opening?.Lines == null || opening.Lines.Length == 0)
            {
                return;
            }

            var line = opening.Lines[0];
            ShowSprite(view.Find("guide")?.GetComponent<Image>(), book.ResolvePortrait(opening, line, out _));
            Transform card = view.Find("DialogueCard");
            ShowIfEmpty(card, "title", opening.Title);
            ShowIfEmpty(card, "speaker", line.Speaker);
            ShowIfEmpty(card, "dialogue", line.Text);
            ShowIfEmpty(card, "counter", "1 / " + opening.Lines.Length + "    点击任意处 · 下一句");
        }

        private void ShowCard(CardItem item)
        {
            var card = new SerializedObject(item);
            var art = card.FindProperty("artConfig").objectReferenceValue as PieceArtConfig;
            var frames = art?.Find(PieceKind.Swordman, StarTier.One, GameSide.Player)?.idleFrames;
            if (frames != null && frames.Length > 0)
            {
                ShowSprite(card.FindProperty("portrait").objectReferenceValue as Image, frames[0]);
            }
        }

        private void ShowRewards()
        {
            var icons = new SerializedObject(_root.GetComponent<RewardPanel>()).FindProperty("rewardIcons");
            for (int i = 0; i < Mathf.Min(3, icons.arraySize); i++)
            {
                var icon = _root.transform.Find("choice" + i + "/icon")?.GetComponent<Image>();
                ShowSprite(icon, icons.GetArrayElementAtIndex(i).objectReferenceValue as Sprite);
            }
        }

        private void ShowSprite(Image image, Sprite sprite)
        {
            if (image != null && image.overrideSprite == null && sprite != null)
            {
                image.overrideSprite = sprite;
                _overrides.Add((image, sprite));
            }
        }

        private T AddOverlay<T>(RectTransform parent) where T : Graphic
        {
            // 作为原控件的子级跟随锚点移动。隐藏节点不参与编辑器选中、输入或资产保存。
            var overlay = new GameObject("界面版式预览", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
            _overlays.Add(overlay);
            var rect = (RectTransform)overlay.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var graphic = overlay.AddComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private void ShowIfEmpty(Transform card, string node, string value)
        {
            var source = card?.Find(node)?.GetComponent<Text>();
            if (source == null || !string.IsNullOrEmpty(source.text))
            {
                return;
            }
            Text example = AddOverlay<Text>(source.rectTransform);
            EditorUtility.CopySerialized(source, example);
            example.text = value;
            example.raycastTarget = false;
        }

        /// <summary>读取当前打开的 Prefab Stage 与临时纹理绑定，供编辑态回归确认资产没有被写回。</summary>
        public static object InspectStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var preview = _stagePreview;
            return new StageState
            {
                prefab = stage?.assetPath,
                dirty = stage != null && stage.scene.isDirty,
                backdrop = preview?._backdrop?.texture?.name,
                spriteOverrides = preview?._overrides.Count ?? 0,
                overlays = preview?._overlays.Count ?? 0
            };
        }

        [Serializable]
        private sealed class StageState
        {
            public string prefab, backdrop;
            public bool dirty;
            public int overlays, spriteOverrides;
        }

        public void Dispose()
        {
            foreach (var (image, sprite) in _overrides)
            {
                if (image != null && image.overrideSprite == sprite)
                {
                    image.overrideSprite = null;
                }
            }
            _overrides.Clear();
            foreach (GameObject overlay in _overlays)
            {
                if (overlay != null)
                {
                    Object.DestroyImmediate(overlay);
                }
            }
            _overlays.Clear();
        }
    }
}
