using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Pvp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>九路围棋固定资产配方。通过Unity API保留GUID，复用已有Sprite与字体，禁止白膜按钮或直接改YAML。</summary>
    public static partial class GamePvpBuild
    {
        private static Font _font;
        private static readonly Color Cream = new Color(1, .95f, .80f);
        private static readonly Color Ink = new Color(.08f, .16f, .19f);
        public static string Build()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请先退出Play再制作PvP资源");
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("场景有未保存改动，已保留现场");
                }
            }
            Directory.CreateDirectory("Assets/GameRes/Pvp");
            Directory.CreateDirectory("Assets/GameRes/UI/Panel/Pvp");
            AssetDatabase.Refresh();
            _font = GameAssetBuilder.Font();
            BuildBoard();
            BuildPanel();
            BuildMenus();
            BuildAgentAssets();
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                cameraObject.tag = "MainCamera";
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 6.4f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.84f, .73f, .54f);
                camera.transform.position = new Vector3(4, 4, -10);
                EditorSceneManager.SaveScene(scene, "Assets/Scenes/PvpScene.unity");
                var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
                if (!scenes.Exists(s => s.path == "Assets/Scenes/PvpScene.unity"))
                {
                    scenes.Add(new EditorBuildSettingsScene("Assets/Scenes/PvpScene.unity", true));
                }
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            AssetDatabase.SaveAssets();
            return "九路围棋场景、黑白棋盘Shader与简洁联机面板已制作；请更新字库并构建真实AB";
        }
        private static Material Material(string name, Shader shader)
        {
            if (shader == null)
            {
                throw new InvalidOperationException("围棋Shader未找到：" + name);
            }
            string path = "Assets/GameRes/Pvp/" + name + ".mat";
            Material result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (result == null)
            {
                result = new Material(shader);
                AssetDatabase.CreateAsset(result, path);
            }
            result.shader = shader;
            EditorUtility.SetDirty(result);
            return result;
        }
        private static MeshRenderer Quad(Transform parent, string name, Material material, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }
        private static void BuildBoard()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Endless/EndlessBackground.mat");
            if (existing == null)
            {
                throw new InvalidOperationException("缺少无尽神殿背景材质");
            }
            Material background = Material("PvpTemple", existing.shader);
            background.CopyPropertiesFromMaterial(existing);
            Material board = Material("PvpIvoryBoard", Shader.Find("RevGame/Pvp/IvoryGoBoard"));
            Material endlessGrid = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Endless/EndlessGrid.mat");
            if (endlessGrid == null)
            {
                throw new InvalidOperationException("缺少无尽棋盘配色材质");
            }
            // 直接读取无尽的真实配色，避免旧材质序列化值覆盖Shader新默认值。
            board.SetColor("_Ivory", endlessGrid.GetColor("_Ivory"));
            board.SetColor("_Gold", endlessGrid.GetColor("_Gold"));
            var root = new GameObject("PvpBoardView", typeof(PvpBoardView));
            try
            {
                var data = new SerializedObject(root.GetComponent<PvpBoardView>());
                data.FindProperty("backgroundRenderer").objectReferenceValue = Quad(root.transform, "无尽神殿背景", background,
                    new Vector3(4, 4, 5), new Vector3(23, 12.94f, 1));
                data.FindProperty("boardRenderer").objectReferenceValue = Quad(root.transform, "无尽同款象牙金格棋盘", board,
                    new Vector3(4, 4, 2), new Vector3(9, 9, 1));
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Pvp/PvpBoardView.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        private static RectTransform Rect(string name, Transform parent, float x0, float y0, float x1, float y1)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(x0, y0);
            rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }
        private static Image Plate(RectTransform rect, string spriteName, Color tint, bool raycast = false)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/" + spriteName + ".png");
            if (sprite == null)
            {
                throw new InvalidOperationException("UI素材缺失：" + spriteName);
            }
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = tint;
            image.raycastTarget = raycast;
            return image;
        }
        private static Text Label(string name, Transform parent, string value, float x0, float y0, float x1, float y1, int size = 28, bool dark = false)
        {
            Text text = Rect(name, parent, x0, y0, x1, y1).gameObject.AddComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.color = dark ? Ink : Cream;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }
        private static Button Button(string name, Transform parent, string value, float x0, float y0, float x1, float y1, int size = 26)
        {
            RectTransform rect = Rect(name, parent, x0, y0, x1, y1);
            Image image = Plate(rect, "UI_Tavern_btnClose", Color.white, true);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            Label("label", rect, value, .04f, .05f, .96f, .95f, size);
            return button;
        }
        private static void Input(string name, Transform parent, string value, float x0, float y0, float x1, float y1, int limit)
        {
            RectTransform rect = Rect(name, parent, x0, y0, x1, y1);
            Image image = Plate(rect, "UI_achieveItem_imgBK", Color.white, true);
            Text text = Label("text", rect, value, .04f, .05f, .96f, .95f, 26, true);
            InputField field = rect.gameObject.AddComponent<InputField>();
            field.targetGraphic = image;
            field.textComponent = text;
            field.characterLimit = limit;
            field.text = value;
        }
        private static RectTransform Modal(string name, Transform parent)
        {
            RectTransform root = Rect(name, parent, 0, 0, 1, 1);
            Plate(root, "UI_Tip_imgBK", new Color(.16f, .2f, .22f, .98f), true);
            return root;
        }
        private static void BuildPanel()
        {
            var root = new GameObject("PvpPanel", typeof(RectTransform), typeof(PvpPanel));
            try
            {
                RectTransform rootRect = (RectTransform)root.transform;
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.sizeDelta = Vector2.zero;
                RectTransform top = Rect("top", root.transform, .13f, .86f, .87f, .99f);
                Plate(top, "UI_Tip_imgBK", Color.white);
                Label("title", top, "九路围棋", .30f, .64f, .70f, .97f, 32);
                Label("black", top, "黑先白后", .02f, .08f, .34f, .88f, 29);
                Label("white", top, "同样规则 · 公平落子", .66f, .08f, .98f, .88f, 29);
                Label("turn", top, "选择一种对战方式", .29f, .05f, .71f, .59f, 26);
                Label("preview", root.transform, "", .26f, .82f, .74f, .855f, 23, true);

                RectTransform controls = Rect("controls", root.transform, 0, 0, 1, 1);
                Button("pass", controls, "停一手", .31f, .025f, .43f, .095f, 30);
                Button("resign", controls, "认输", .44f, .025f, .56f, .095f, 30);
                Button("hint", controls, "落子建议", .57f, .025f, .69f, .095f, 27);
                Button("toggleHints", controls, "隐藏辅助", .81f, .025f, .97f, .085f, 25);
                Button("reconnect", controls, "重连对局", .025f, .45f, .20f, .53f, 28);
                Button("back", controls, "返回大厅", .02f, .025f, .15f, .085f, 26);
                // 状态放在所有主页面之上，停服提示在连接页面和对局中都能立即看见。
                Text status = Label("status", root.transform, "", .17f, .105f, .83f, .145f, 24);
                var outline = status.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(.04f, .08f, .10f, .95f);
                outline.effectDistance = new Vector2(1.5f, -1.5f);

                RectTransform scoring = Rect("scoring", root.transform, .72f, .30f, .985f, .70f);
                Plate(scoring, "UI_Tip_imgBK", Color.white, true);
                Label("text", scoring, "确认死子", .05f, .55f, .95f, .94f, 27);
                Button("confirmScore", scoring, "确认计分", .08f, .29f, .92f, .47f);
                Button("resume", scoring, "有分歧，继续下", .08f, .07f, .92f, .25f, 25);
                RectTransform result = Rect("result", root.transform, .72f, .20f, .985f, .76f);
                Plate(result, "UI_Tip_imgBK", Color.white, true);
                Label("text", result, "结算", .06f, .46f, .94f, .95f, 27);
                Button("replayPrevious", result, "上一步", .06f, .30f, .48f, .42f, 25);
                Button("replayNext", result, "下一步", .52f, .30f, .94f, .42f, 25);
                Button("replayExit", result, "返回终局", .06f, .16f, .48f, .28f, 24);
                Button("newGame", result, "再来一局", .52f, .16f, .94f, .28f, 24);

                RectTransform invite = Modal("invite", root.transform);
                Label("text", invite, "棋友邀请", .25f, .45f, .75f, .70f, 36);
                Button("accept", invite, "接受邀请", .28f, .30f, .48f, .40f, 32);
                Button("decline", invite, "暂不接受", .52f, .30f, .72f, .40f, 32);
                RectTransform confirm = Modal("confirm", root.transform);
                Label("text", confirm, "确定吗？", .25f, .45f, .75f, .70f, 36);
                Button("yes", confirm, "确定", .28f, .30f, .48f, .40f, 32);
                Button("no", confirm, "继续对局", .52f, .30f, .72f, .40f, 32);
                invite.gameObject.SetActive(false);
                confirm.gameObject.SetActive(false);
                scoring.gameObject.SetActive(false);
                result.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
