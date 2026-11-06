// 保存场景对象而不是把大厅画面塞进UI；固定配方保护场景现场与GUID，重复执行只替换自己拥有的根。
using System;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>固定StartScene场景制作入口；背景面片、鸦鸦Sprite/Animator直接保存场景，实际内容由Rev资源门面加载。</summary>
    public static class GameLobbySceneBuild
    {
        /// <summary>保存前保护未保存场景，保留已有节点、构图字段和人工位移，重复制作不能重置策划配置。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("大厅场景制作要求编辑态");
            }
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Any(s => s.isLoaded && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(s.path).isDirty))
            {
                throw new InvalidOperationException("存在未保存场景，已保留现场");
            }
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity", OpenSceneMode.Single);
                Camera camera = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Camera>(true)).First(c => c.CompareTag("MainCamera"));
                GameObject root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == "LobbyScene") ?? new GameObject("LobbyScene");
                LobbySceneView view = root.GetComponent<LobbySceneView>() ?? root.AddComponent<LobbySceneView>();
                Transform backdropNode = root.transform.Find("MagicCottage");
                GameObject backdrop;
                if (backdropNode == null)
                {
                    backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    backdrop.name = "MagicCottage";
                    backdrop.transform.SetParent(root.transform, false);
                    UnityEngine.Object.DestroyImmediate(backdrop.GetComponent<Collider>());
                }
                else
                {
                    backdrop = backdropNode.gameObject;
                }
                MeshRenderer renderer = backdrop.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = null;
                renderer.enabled = false;
                renderer.sortingOrder = -100;
                Transform placement = root.transform.Find("YayaPlacement");
                if (placement == null)
                {
                    placement = new GameObject("YayaPlacement").transform;
                    placement.SetParent(root.transform, false);
                    // 本次默认向下移0.35世界单位。只在新建人工位移节点时设置一次，之后由用户在Scene调整。
                    placement.localPosition = new Vector3(0, -.35f, 0);
                }
                Transform previous = placement.Find("Yaya") ?? root.transform.Find("Yaya");
                GameObject yaya = previous == null ? new GameObject("Yaya", typeof(SpriteRenderer), typeof(Animator)) : previous.gameObject;
                yaya.transform.SetParent(placement, false);
                var sprite = yaya.GetComponent<SpriteRenderer>();
                sprite.enabled = false;
                sprite.sortingOrder = 10;
                yaya.GetComponent<Animator>().enabled = false;
                var fields = new SerializedObject(view);
                fields.FindProperty("lobbyCamera").objectReferenceValue = camera;
                fields.FindProperty("cottageRenderer").objectReferenceValue = renderer;
                fields.FindProperty("yayaRenderer").objectReferenceValue = sprite;
                fields.FindProperty("yayaAnimator").objectReferenceValue = yaya.GetComponent<Animator>();
                fields.FindProperty("yayaPlacement").objectReferenceValue = placement;
                // 编辑态静帧让美术能直接用Scene移动节点查看构图；正式Play仍经Rev资源门面加载动画/材质。
                fields.FindProperty("editorBackdrop").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Lobby/MagicCottageMaterial.mat");
                fields.FindProperty("editorPortrait").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Characters/Yaya/Idle24/frame_000.png");
                fields.ApplyModifiedPropertiesWithoutUndo();
                GamePresentation presentation = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<GamePresentation>(true)).Single();
                var presentationFields = new SerializedObject(presentation);
                presentationFields.FindProperty("lobbyScene").objectReferenceValue = view;
                presentationFields.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                return "StartScene直接放置小屋MeshRenderer与鸦鸦SpriteRenderer/Animator；HomePanel只保留交互";
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }
    }
}
