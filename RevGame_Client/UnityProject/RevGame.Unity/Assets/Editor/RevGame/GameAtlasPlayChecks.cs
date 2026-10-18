using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Generated;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    /// <summary>
    /// 实际AB图集验收：既查图集中的Sprite，也查场景Renderer实际绑定的贴图。
    /// 不能只数.spriteatlasv2文件就宣布成功；源贴图被剥离、UV和重入寿命必须一起验证。
    /// </summary>
    public static class GameAtlasPlayChecks
    {
        public static string LastResult { get; private set; } = "未运行";

        /// <summary>与其它Play夹具串行运行，因为它会真实进入/退出战斗。所有临时租约都在finally配对释放。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            int sprites = 0;
            var report = new List<string>();
            try
            {
                Require(RevResBootstrap.UseABInEditor, "必须真实AB模式运行");
                foreach (string name in GameSpriteAtlases.Names)
                {
                    var completion = new RevTaskCompletionSource<RevResHandle>();
                    RevResManager.LoadAsync(RevResPath.Atlases, name, typeof(SpriteAtlas), h => completion.SetResult(h), RevResGroup.Scene);
                    RevResHandle handle = await completion.Task;
                    try
                    {
                        Require(handle.IsLoaded, name + "加载失败：" + handle.ErrorReason);
                        var atlas = (SpriteAtlas)handle.Content;
                        Require(atlas.spriteCount > 0, name + "不能是空图集");
                        var frames = new Sprite[atlas.spriteCount];
                        atlas.GetSprites(frames);
                        try
                        {
                            Require(frames.All(s => s != null && s.packed && s.texture != null && !s.texture.isReadable), name + "每帧必须使用不可读图集纹理");
                            var pages = frames.Select(s => s.texture).Distinct().ToArray();
                            report.Add(name + "：" + frames.Length + "张Sprite，" + pages.Length + "页，" + pages.Sum(p => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(p)) + "字节");
                            sprites += frames.Length;
                        }
                        finally
                        {
                            // GetSprites产生Sprite克隆；测试用完也必须销毁，正式动画直接用GUID引用，没有这笔克隆开销。
                            foreach (Sprite frame in frames) { if (frame != null) { UnityEngine.Object.Destroy(frame); } }
                        }
                    }
                    finally { RevResManager.Release(RevResPath.Atlases, name); }
                }
                var flow = RevServices.GetRequired<GameFlow>();
                int baselineHeld = -1;
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    if (flow.Screen != GameScreen.Home) { flow.ReturnHome(); }
                    Require(flow.OpenLevels() && flow.EnterBattle(1), "正常进入战斗");
                    await RevTask.Delay(1800);
                    var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                    battle.LoadDevelopmentScenario("events");
                    RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                    await RevTask.Delay(800);
                    SpriteRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>().Where(r => r.enabled && r.sprite != null).ToArray();
                    Require(renderers.Length > 90, "真实棋盘和棋子加载完成");
                    // Unity内置UISprite（血条白片）是引擎生成资源，无源PNG可入AB；其余项目图片必须packed。
                    Require(renderers.All(r => r.sprite.name == "UISprite" || r.sprite.packed), "实际场景不能偷偷回退散图");
                    Require(UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>().Where(i => i.sprite != null).All(i => i.sprite.name == "UISprite" || i.sprite.packed), "UI实际绑定图集");
                    report.Add("重入" + cycle + "：Renderer=" + renderers.Length + "，贴图=" + renderers.Select(r => r.sprite.texture).Distinct().Count() + "，批次=" + UnityEditor.UnityStats.batches);
                    flow.ReturnHome();
                    await RevTask.Delay(600);
                    Require(RevResManager.GetRefCount(RevResPath.Atlases, "PieceFrames") == 0 && RevResManager.GetRefCount(RevResPath.Atlases, "CombatFrames") == 0 && RevResManager.GetRefCount(RevResPath.Atlases, "BoardStatus") == 0, "返回大厅释放战斗图集租约");
                    int held = RevServices.GetRequired<GameSpriteAtlasService>().HeldCount;
                    if (baselineHeld < 0) { baselineHeld = held; }
                    Require(held == baselineHeld, "重复进入/退出不能累积服务租约");
                }
                LastResult = "通过：" + GameSpriteAtlases.Names.Length + "张实际AB图集、" + sprites + "张Sprite、3次战斗重入与引用释放\n" + string.Join("\n", report);
            }
            catch (Exception error) { LastResult = "失败：" + error; RevLog.Exception(error, "图集Play验收失败", "RevGame"); }
            File.WriteAllText("Temp/AtlasPlayChecks.txt", LastResult);
        }

        private static void Require(bool success, string reason)
        {
            if (!success) { throw new InvalidOperationException(reason); }
        }
    }
}
