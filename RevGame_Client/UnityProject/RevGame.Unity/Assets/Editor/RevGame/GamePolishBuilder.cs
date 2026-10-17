// ============================================================
// GamePolishBuilder.cs —— 通过 MCP 执行的音频导入和表现预制体装配。
// 外部原音频是完整歌曲，保留在 ArtRes；运行时选一首放入逻辑资源根。短音效由正弦波生成。
// 这些文件操作只在编辑器制作资产时执行，游戏运行时一律用 RevSound / RevPool。
// ============================================================
using System.IO;
using RevGame.Data;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GamePolishBuilder
    {
        public static string Build(string sourceAudioDirectory)
        {
            Folder("Assets/ArtRes/音频");
            Folder("Assets/GameRes/Audio/Bgm");
            Folder("Assets/GameRes/Audio/Sfx");
            foreach (string source in Directory.GetFiles(sourceAudioDirectory, "*.mp3"))
            {
                string target = "Assets/ArtRes/音频/" + Path.GetFileName(source);
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                }

                AssetDatabase.ImportAsset(target);
            }

            string bgm = "Assets/GameRes/Audio/Bgm/普通朋友.mp3";
            if (!File.Exists(bgm))
            {
                File.Copy(Path.Combine(sourceAudioDirectory, "普通朋友.mp3"), bgm);
            }

            AssetDatabase.ImportAsset(bgm);
            Tone("落子", 420, 0.09f);
            Tone("受击", 155, 0.08f);
            Tone("升星", 740, 0.35f);
            Tone("胜利", 880, 0.5f);
            Tone("失败", 220, 0.4f);
            BuildEffects();
            BuildHud();
            PolishReadability();
            var host = GameObject.Find("GamePresentation");
            if (host.GetComponent<GameAudio>() == null)
            {
                host.AddComponent<GameAudio>();
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            return "8首音乐已导入；1首运行时BGM、5个短音效、破碎/升星/飘字、Boss血条和事件提示已保存";
        }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static void Tone(string name, float frequency, float duration)
        {
            const int rate = 22050;
            int count = Mathf.RoundToInt(rate * duration);
            string path = "Assets/GameRes/Audio/Sfx/" + name + ".wav";
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + count * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(rate);
                writer.Write(rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(count * 2);
                for (int i = 0; i < count; i++)
                {
                    float t = (float)i / count;
                    float envelope = Mathf.Sin(Mathf.PI * t) * (1f - t);
                    float sample = Mathf.Sin(2f * Mathf.PI * frequency * i / rate) * envelope * 0.35f;
                    writer.Write((short)(sample * short.MaxValue));
                }

                File.WriteAllBytes(path, stream.ToArray());
            }

            AssetDatabase.ImportAsset(path);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
        }

        private static void BuildEffects()
        {
            Folder("Assets/GameRes/Effects");
            var root = new GameObject("BattleEffect");
            var renderer = root.AddComponent<SpriteRenderer>();
            renderer.sortingOrder = 30;
            var textGo = new GameObject("damage");
            textGo.transform.SetParent(root.transform, false);
            var text = textGo.AddComponent<TextMesh>();
            text.font = GameAssetBuilder.Font();
            text.fontSize = 64;
            text.characterSize = 0.055f;
            text.anchor = TextAnchor.MiddleCenter;
            var textRenderer = textGo.GetComponent<MeshRenderer>();
            textRenderer.sharedMaterial = text.font.material;
            textRenderer.sortingOrder = 40;
            var effect = root.AddComponent<BattleEffectView>();
            GameAssetBuilder.SetReference(effect, "spriteRenderer", renderer);
            GameAssetBuilder.SetReference(effect, "damageText", text);
            var so = new SerializedObject(effect);
            Frames(so, "deathFrames", "特效_提子破碎_", 6);
            Frames(so, "mergeFrames", "特效_三合一升星_", 8);
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Effects/BattleEffect.prefab");
            Object.DestroyImmediate(root);
        }

        private static void Frames(SerializedObject so, string field, string prefix, int count)
        {
            SerializedProperty frames = so.FindProperty(field);
            frames.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                frames.GetArrayElementAtIndex(i).objectReferenceValue = GameAssetBuilder.Sprite("特效/" + prefix + (i + 1).ToString("00") + ".png");
            }
        }

        private static void BuildHud()
        {
            const string path = "Assets/GameRes/UI/BattleHudPanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.transform.Find("bossFrame") == null)
                {
                    Sprite frameSprite = GameAssetBuilder.Sprite("界面/新版/UI_Game_imgMobilityBK.png");
                    Image frame = GameAssetBuilder.Picture("bossFrame", root.transform, frameSprite, new Vector2(700, 58), new Vector2(0, -145));
                    GameAssetBuilder.Anchor(frame.rectTransform, new Vector2(0.5f, 1));
                    Image fill = GameAssetBuilder.Picture("bossBar", root.transform, frame.sprite, new Vector2(660, 25), new Vector2(0, -145));
                    GameAssetBuilder.Anchor(fill.rectTransform, new Vector2(0.5f, 1));
                    fill.color = new Color(0.7f, 0.12f, 0.08f);
                    fill.type = Image.Type.Filled;
                    fill.fillMethod = Image.FillMethod.Horizontal;
                    Text title = GameAssetBuilder.Label("bossText", root.transform, "主将", new Vector2(650, 45), new Vector2(0, -145), 25);
                    GameAssetBuilder.Anchor(title.rectTransform, new Vector2(0.5f, 1));
                    title.color = Color.white;
                    GameObject notice = GameAssetBuilder.Rect("notice", root.transform, new Vector2(650, 150), new Vector2(0, 160));
                    var image = notice.AddComponent<Image>();
                    image.sprite = GameAssetBuilder.Sprite("界面/新版/UI_Tip_imgBK.png");
                    image.raycastTarget = false;
                    GameAssetBuilder.Picture("eventIcon", notice.transform, null, new Vector2(90, 90), new Vector2(-225, 0));
                    GameAssetBuilder.Label("eventText", notice.transform, "", new Vector2(430, 90), new Vector2(45, 0), 40);
                }

                var panel = root.GetComponent<BattleHudPanel>();
                var so = new SerializedObject(panel);
                var icons = so.FindProperty("eventIcons");
                icons.arraySize = 8;
                for (int i = 0; i < 8; i++)
                {
                    icons.GetArrayElementAtIndex(i).objectReferenceValue = GameAssetBuilder.Sprite("界面/新版/UI_Setting_Checkmark.png");
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>根据实际 Play 画面修正可读性。尺寸按 Sprite 的真实世界宽度换算，不能把缩放当作宽度。</summary>
        public static void PolishReadability()
        {
            const string piecePath = "Assets/GameRes/Pieces/棋子.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(piecePath);
            try
            {
                // 当前 Boss 大棋图片只是旗帜占位，改用已有的敌方重剑立绘作为身体，王冠单独标识主将。
                var art = AssetDatabase.LoadAssetAtPath<PieceArtConfig>("Assets/GameRes/棋子美术配置.asset");
                Sprite bossBody = art.Find(Core.PieceKind.HeavySword, Core.StarTier.Three, Core.GameSide.Enemy).idleFrames[0];
                GameAssetBuilder.SetReference(root.GetComponent<PieceView>(), "bossSmallSprite", bossBody);
                GameAssetBuilder.SetReference(root.GetComponent<PieceView>(), "bossLargeSprite", bossBody);
                var crown = root.transform.Find("王冠").GetComponent<SpriteRenderer>();
                crown.transform.localScale = Vector3.one * 0.25f;
                crown.transform.localPosition = new Vector3(0.5f, 0.45f, 0);
                crown.color = new Color(0.95f, 0.65f, 0.1f);
                var halo = root.transform.Find("大棋光环").GetComponent<SpriteRenderer>();
                halo.color = new Color(0.9f, 0.65f, 0.1f, 0.3f);
                foreach (string name in new[]
                {
                    "血条底图",
                    "血条"
                }

                )
                {
                    var bar = root.transform.Find(name).GetComponent<SpriteRenderer>();
                    float width = name == "血条" ? 0.78f : 0.84f;
                    float height = name == "血条" ? 0.07f : 0.11f;
                    bar.transform.localScale = new Vector3(width / bar.sprite.bounds.size.x, height / bar.sprite.bounds.size.y, 1f);
                    bar.transform.localPosition = new Vector3(0f, 0.53f, 0f);
                    bar.color = name == "血条" ? new Color(0.22f, 0.65f, 0.32f) : new Color(0.18f, 0.12f, 0.08f);
                }

                PrefabUtility.SaveAsPrefabAsset(root, piecePath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            foreach (string panel in new[]
            {
                "HomePanel",
                "LevelSelectPanel",
                "CardItem",
                "PreparePanel",
                "BattleHudPanel",
                "SettlePanel",
                "RewardPanel"
            }

            )
            {
                string path = "Assets/GameRes/UI/" + panel + ".prefab";
                root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (Text text in root.GetComponentsInChildren<Text>(true))
                    {
                        text.fontStyle = FontStyle.Bold;
                        if (text.name == "stats")
                        {
                            text.fontSize = 20;
                        }
                    }

                    if (panel == "BattleHudPanel")
                    {
                        var icon = root.transform.Find("phaseIcon").GetComponent<Image>();
                        icon.color = new Color(0.35f, 0.24f, 0.12f);
                        var frame = root.transform.Find("bossFrame").GetComponent<Image>();
                        frame.rectTransform.anchoredPosition = new Vector2(0, -132);
                        frame.rectTransform.sizeDelta = new Vector2(700, 38);
                        if (frame.transform.Find("backdrop") == null)
                        {
                            Image backdrop = GameAssetBuilder.Picture("backdrop", frame.transform, null, new Vector2(700, 38), Vector2.zero);
                            backdrop.color = new Color(0.2f, 0.1f, 0.08f);
                        }

                        var bar = root.transform.Find("bossBar").GetComponent<Image>();
                        // Filled Image 必须有 Sprite；空 Sprite 只会画普通四边形，fillAmount=0 仍是满条。
                        bar.sprite = AssetDatabase.LoadAssetAtPath<GameObject>(piecePath).transform.Find("血条").GetComponent<SpriteRenderer>().sprite;
                        bar.preserveAspect = false;
                        bar.rectTransform.anchoredPosition = new Vector2(0, -132);
                        var title = root.transform.Find("bossText").GetComponent<Text>();
                        title.rectTransform.anchoredPosition = new Vector2(0, -132);
                        title.fontSize = 23;
                    }

                    if (panel == "PreparePanel")
                    {
                        // 提示原来压在棋盘最后一行，改放右侧按钮上方，手牌仍位于屏幕下边。
                        var hint = (RectTransform)root.transform.Find("hintText");
                        GameAssetBuilder.Anchor(hint, new Vector2(1f, 0f));
                        hint.anchoredPosition = new Vector2(-180f, 285f);
                        hint.sizeDelta = new Vector2(300f, 120f);
                    }

                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            AssetDatabase.SaveAssets();
            // ★ 最后统一应用新版美术，防止可读性修整把填充图或颜色恢复成旧占位样式。
            GameArtMigration.ApplyAll();
        }
    }
}
