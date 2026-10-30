// ============================================================
// GameResourcePlayChecks.cs —— 实际AB加载、全屏覆盖和被围换帧的Play验收。
// 只读AssetDatabase取得应当存在的资产类型，再用RevResManager加载；不把编辑器直读冒充AB验证。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameResourcePlayChecks
    {
        public static string LastResult { get; private set; } = "未运行";

        /// <summary>只有AB模式的真实Play才执行；每次取用都释放自己的租约，避免验收改变正式资源寿命。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中")
            {
                return;
            }
            LastResult = "运行中";
            int count = 0;
            PieceView piece = null;
            GameObject container = null;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            try
            {
                Require(RevResBootstrap.UseABInEditor, "本验收必须开启AB模式");
                // 背景现在属于GameScene，不能继续假设首页里也存在GameBackground。
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "进入真实战斗场景再验背景与动画");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                container = new GameObject("资源与动画验收");
                foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets/GameRes" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);

                    if (AssetDatabase.IsValidFolder(path))
                    {
                        continue;
                    }
                    // GameRes现在同时保留“已打包资源”和“未启用素材”。只验收有AB标记的资源，
                    // 不能强求已经按真实引用剔除的备用图片仍然能从AB加载；去留正确性由GameResourceUsage独立校验。
                    if (string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(path)))
                    {
                        continue;
                    }
                    string root = Path.GetDirectoryName(path).Replace('\\', '/').Substring("Assets/GameRes/".Length) + "/";
                    string name = Path.GetFileNameWithoutExtension(path);
                    var source = new RevTaskCompletionSource<RevResHandle>();
                    RevResGroup group = root.StartsWith("UI/Panel/") ? RevResGroup.UI : RevResGroup.Battle;

                    if (root.StartsWith("Material/"))
                    {
                        group = RevResGroup.Scene;
                    }
                    if (root.StartsWith("Data/") || root.StartsWith("Config/"))
                    {
                        group = RevResGroup.Config;
                    }
                    if (root.StartsWith("Audio/"))
                    {
                        group = RevResGroup.Sound;
                    }
                    // 入图集后AB会剥离源Texture2D像素，只保留Sprite元数据和图集页面。
                    // 因此图片必须按Sprite加载；用Texture2D测试会强求本就应该被剥离的散图副本。
                    TextureImporter textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
                    // 章节2~6是Shader的非Sprite采样纹理；它们不受SpriteAtlas剥离规则约束，须按Texture2D验收。
                    Type contentType = textureImporter != null && textureImporter.textureType == TextureImporterType.Sprite
                        ? typeof(Sprite) : AssetDatabase.GetMainAssetTypeAtPath(path);
                    RevResManager.LoadAsync(root, name, contentType, h => source.SetResult(h), group);
                    RevResHandle handle = await source.Task;
                    try
                    {
                        string bundle = AssetDatabase.GetImplicitAssetBundleName(path);
                        Require(handle.IsLoaded && AssetBundle.GetAllLoadedAssetBundles().Any(b => b.name == bundle), path + "：" + handle.ErrorReason);
                        Material material = handle.Content as Material;

                        if (material != null)
                        {
                            Require(material.shader != null && material.shader.isSupported, "AB材质Shader可用：" + path);
                        }
                        count++;
                    }
                    finally { RevResManager.Release(root, name); }
                }
                Require(RevDataTableManager.IsLoaded<BattleTextTable>() && RevDataTableManager.IsLoaded<BattleTuningTable>(), "游戏启动的两张表已从AB读取");
                Camera camera = Camera.main;
                SpriteRenderer bg = GameObject.Find("GameBackground").GetComponent<SpriteRenderer>();
                Require(bg.sharedMaterial.shader.name == "RevGame/Background/Chapter01_Tutorial", "真实第一章背景使用独立迷幻材质");
                // 自动铺满默认关闭（尊重场景里手调的背景尺寸），验收这里显式打开，测完还原原值。
                GamePresentation presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
                FieldInfo fitField = typeof(GamePresentation).GetField("fitBackgroundToCamera", BindingFlags.NonPublic | BindingFlags.Instance);
                bool originalFit = (bool)fitField.GetValue(presentation);
                fitField.SetValue(presentation, true);
                float originalAspect = camera.aspect;
                try
                {
                    foreach (float aspect in new[] { 16f / 9f, 19.5f / 9f, 4f / 3f })
                    {
                        camera.aspect = aspect;
                        typeof(GamePresentation).GetMethod("FitCamera", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(presentation, null);
                        Bounds bounds = bg.bounds;
                        foreach (Vector3 corner in new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) })
                        {
                            Vector3 world = camera.ViewportToWorldPoint(new Vector3(corner.x, corner.y, bg.transform.position.z - camera.transform.position.z));
                            Require(world.x > bounds.min.x && world.x < bounds.max.x && world.y > bounds.min.y && world.y < bounds.max.y, "背景覆盖全部视口角点：" + aspect);
                        }
                    }
                }
                finally
                {
                    camera.aspect = originalAspect;
                    fitField.SetValue(presentation, originalFit);
                    typeof(GamePresentation).GetMethod("FitCamera", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(presentation, null);
                }
                var loaded = new RevTaskCompletionSource<PieceView>();
                RevPool.GetAsync<PieceView>(v => loaded.SetResult(v), container.transform);
                piece = await loaded.Task;
                Require(piece != null, "移动目录后的棋子池实际加载成功");
                piece.Bind(99001, PieceKind.Archer, StarTier.One, GameSide.Player);
                piece.SetLocked(true);
                var renderer = (SpriteRenderer)typeof(PieceView).GetField("lockedRenderer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(piece);
                var names = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < 12; i++)
                {
                    // 模拟HUD高频刷新同一个状态，确保刷新不会把动画反复重置到第一帧。
                    piece.SetLocked(true);
                    names.Add(renderer.sprite.name);
                    await RevTask.Delay(50);
                }
                Require(names.Count == 3 && names.Contains("Encircled_001") && names.Contains("Encircled_002") && names.Contains("Encircled_003"), "实际轮播用户指定的三张图（迁移保留GUID）");
                piece.SetLocked(false);
                Require(!renderer.enabled, "脱离围困立即隐藏标识");
                var body = (SpriteRenderer)typeof(PieceView).GetField("bodyRenderer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(piece);
                Material shared = body.sharedMaterial;
                Require(shared.shader.name == "RevGame/Pieces/ArcanePiece" && shared.shader.isSupported, "池中棋子实际使用魔幻Shader");
                piece.Bind(99002, PieceKind.HeavySword, StarTier.Three, GameSide.Enemy);
                piece.SetBodySize(3, true, true);
                names.Clear();
                for (int i = 0; i < 15; i++)
                {
                    names.Add(body.sprite.name);
                    await RevTask.Delay(50);
                }
                Require(names.Count >= 3, "Boss套魔幻材质后仍实际播放三帧身体动画");
                for (int i = 0; i < 5; i++)
                {
                    RevPool.Return(piece, 0);
                    var again = new RevTaskCompletionSource<PieceView>();
                    RevPool.GetAsync<PieceView>(v => again.SetResult(v), container.transform);
                    piece = await again.Task;
                    body = (SpriteRenderer)typeof(PieceView).GetField("bodyRenderer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(piece);
                    Require(body.sharedMaterial == shared, "反复出入池不生成独立材质");
                }
                LastResult = "通过：" + count + "个资源实际AB加载、三种比例背景覆盖、被围三帧、Boss三帧与魔幻共享材质五次回池";
            }
            catch (Exception error) { LastResult = "失败：" + error; RevLog.Exception(error, "资源Play验收失败", "RevGame"); }
            finally
            {
                if (piece != null && piece.gameObject.activeInHierarchy)
                {
                    RevPool.Return(piece, 0);
                }
                UnityEngine.Object.Destroy(container);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
            }
        }
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
