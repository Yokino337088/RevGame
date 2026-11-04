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
        /// <summary>保存前保护未保存场景，保留StartScene GUID与相机；只替换本配方拥有的LobbyScene根。</summary>
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
                foreach (GameObject old in scene.GetRootGameObjects().Where(g => g.name == "LobbyScene").ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(old);
                }
                Camera camera = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Camera>(true)).First(c => c.CompareTag("MainCamera"));
                var root = new GameObject("LobbyScene");
                var view = root.AddComponent<LobbySceneView>();
                GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
                backdrop.name = "MagicCottage";
                backdrop.transform.SetParent(root.transform, false);
                UnityEngine.Object.DestroyImmediate(backdrop.GetComponent<Collider>());
                MeshRenderer renderer = backdrop.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = null;
                renderer.enabled = false;
                renderer.sortingOrder = -100;
                var yaya = new GameObject("Yaya", typeof(SpriteRenderer), typeof(Animator));
                yaya.transform.SetParent(root.transform, false);
                var sprite = yaya.GetComponent<SpriteRenderer>();
                sprite.enabled = false;
                sprite.sortingOrder = 10;
                yaya.GetComponent<Animator>().enabled = false;
                var fields = new SerializedObject(view);
                fields.FindProperty("lobbyCamera").objectReferenceValue = camera;
                fields.FindProperty("cottageRenderer").objectReferenceValue = renderer;
                fields.FindProperty("yayaRenderer").objectReferenceValue = sprite;
                fields.FindProperty("yayaAnimator").objectReferenceValue = yaya.GetComponent<Animator>();
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
