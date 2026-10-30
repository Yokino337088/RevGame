using System;
using System.Collections.Generic;
using RevGame.Generated;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>
    /// 只负责非Play状态的图集预览。AB图集故意不随Player场景自动打入，运行时由GameSpriteAtlasService绑定；
    /// 退出Play后该服务已释放，Prefab/Scene窗口仍会请求图集，因此编辑器必须有自己独立的绑定入口。
    /// 进入Play前立即卸下此监听，绝不能用AssetDatabase掩盖真实AB漏包或冷启动白图。
    /// </summary>
    [InitializeOnLoad]
    public static class GameEditorAtlasPreview
    {
        private static bool _listening;
        private static readonly HashSet<string> Known = new HashSet<string>(GameSpriteAtlases.Names, StringComparer.Ordinal);

        static GameEditorAtlasPreview()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopListening;

            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                StartListening();
            }
        }

        private static void StartListening()
        {
            if (_listening)
            {
                return;
            }
            SpriteAtlasManager.atlasRequested += BindEditorAtlas;
            _listening = true;
            EditorApplication.delayCall += RefreshVisibleImages;
        }
        private static void StopListening()
        {
            SpriteAtlasManager.atlasRequested -= BindEditorAtlas;
            EditorApplication.delayCall -= RefreshVisibleImages;
            _listening = false;
        }
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                StopListening();
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                StartListening();
            }
        }

        private static void BindEditorAtlas(string tag, Action<SpriteAtlas> register)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !Known.Contains(tag))
            {
                return;
            }
            SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>("Assets/GameRes/Atlases/" + tag + ".spriteatlasv2");

            if (atlas == null)
            {
                Debug.LogError("编辑器预览图集不存在：" + tag);
                return;
            }
            register(atlas);
        }
        /// <summary>只在编辑模式切换/显式修复时更新可见UI，不逐帧扫描，不保存预制体，不修改素材或包设置。</summary>
        private static void RefreshVisibleImages()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }
            foreach (Image image in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (!EditorUtility.IsPersistent(image) && image.gameObject.scene.IsValid())
                {
                    image.SetAllDirty();
                }
            }
            Canvas.ForceUpdateCanvases();
            EditorApplication.QueuePlayerLoopUpdate();
            InternalEditorUtility.RepaintAllViews();
        }
        /// <summary>显式编辑器恢复入口；加载真实HUD后读取Sprite纹理，不能以Image内置白纹理冒充图片恢复。</summary>
        public static string Recover()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("预览恢复只能在编辑态执行");
            }
            StartListening();
            RefreshVisibleImages();
            GameObject root = PrefabUtility.LoadPrefabContents("Assets/GameRes/UI/Panel/BattleHudPanel.prefab");
            try
            {
                var lines = new List<string>();
                foreach (Image image in root.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null)
                    {
                        continue;
                    }
                    Texture2D texture = image.sprite.texture;
                    lines.Add(image.name + ": " + image.sprite.name + " → " + (texture == null ? "缺失纹理" : texture.name + " " + texture.width + "×" + texture.height));
                }
                return string.Join("\n", lines);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
