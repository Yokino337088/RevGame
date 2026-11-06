// 配置修改用真实SO/Scene输入，验证只触发实际解析与布局；使用临时SO和finally恢复，不写生产剧情。
using System;
using RevGame.Data;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>编辑态配置契约检查：优先级、未知表情拒绝、复制配置与再制作保持立绘。</summary>
    public static class GameLobbyConfigurationChecks
    {
        /// <summary>纯配置用现有Sprite做夹具，临时对象永不入库，不修改正式对白。</summary>
        public static string CheckPortraits()
        {
            StoryBook source = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
            StoryBook copy = UnityEngine.Object.Instantiate(source);
            try
            {
                StoryBook.Episode episode = copy.Episodes[0];
                StoryBook.Line line = episode.Lines[0];
                line.Expression = 0;
                Require(copy.ResolvePortrait(episode, line, out _) == copy.Guide, "默认立绘");
                line.Expression = 1;
                Require(copy.ResolvePortrait(episode, line, out _) == copy.ExpressionSprite(1, out _), "表情映射");
                episode.Portrait = copy.ExpressionSprite(23, out _);
                Require(copy.ResolvePortrait(episode, line, out _) == episode.Portrait, "篇章覆盖");
                line.Portrait = copy.ExpressionSprite(11, out _);
                Require(copy.ResolvePortrait(episode, line, out _) == line.Portrait, "逐句覆盖最高优先级");
                line.Expression = 999;
                Require(copy.ResolvePortrait(episode, line, out _) == line.Portrait, "逐句覆盖不要求备用表情存在");
                line.Portrait = null;
                episode.Portrait = null;
                bool rejected = false;
                try
                {
                    copy.ResolvePortrait(episode, line, out _);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                Require(rejected, "没有覆盖时未知表情明确拒绝");
                source.Validate();
                return "7项立绘配置/优先级/拒绝检查通过；临时SO已释放，正式剧情未修改";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }
        /// <summary>用临时选择实际重跑资源配方，验证制作不会覆盖用户立绘与场景位移；finally还原并保存。</summary>
        public static string CheckPreservation()
        {
            StoryBook book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
            Sprite guide = book.Guide;
            Sprite mapped = book.GuideFrames[0];
            Sprite episodePortrait = book.Episodes[0].Portrait;
            Sprite linePortrait = book.Episodes[0].Lines[0].Portrait;
            GameObject placement = GameObject.Find("LobbyScene/YayaPlacement");
            if (placement == null)
            {
                throw new InvalidOperationException("请先在StartScene配置大厅节点");
            }
            Vector3 offset = placement.transform.localPosition;
            try
            {
                // 模拟美术选择新立绘、篇章/逐句覆盖与场景位移，保存后重跑真实配方，不修改对白内容。
                book.Guide = book.ExpressionSprite(12, out _);
                book.GuideFrames[0] = book.ExpressionSprite(13, out _);
                book.Episodes[0].Portrait = book.ExpressionSprite(23, out _);
                book.Episodes[0].Lines[0].Portrait = book.ExpressionSprite(11, out _);
                placement.transform.localPosition = new Vector3(.12f, -.52f, 0);
                EditorUtility.SetDirty(book);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(placement.scene);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(placement.scene);
                AssetDatabase.SaveAssets();
                GameYayaStoryBuild.Configure();
                book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
                Require(book.Guide == book.ExpressionSprite(12, out _), "制作保留默认立绘");
                Require(book.GuideFrames[0] == book.ExpressionSprite(13, out _), "制作保留表情映射");
                Require(book.Episodes[0].Portrait == book.ExpressionSprite(23, out _), "制作保留篇章覆盖");
                Require(book.Episodes[0].Lines[0].Portrait == book.ExpressionSprite(11, out _), "制作保留逐句覆盖");
                Require(GameObject.Find("LobbyScene/YayaPlacement").transform.localPosition == new Vector3(.12f, -.52f, 0), "制作保留场景位移");
                return "5项真实保存/重制作保持检查通过；原立绘和场景位移已恢复";
            }
            finally
            {
                book.Guide = guide;
                book.GuideFrames[0] = mapped;
                book.Episodes[0].Portrait = episodePortrait;
                book.Episodes[0].Lines[0].Portrait = linePortrait;
                GameObject restored = GameObject.Find("LobbyScene/YayaPlacement");
                restored.transform.localPosition = offset;
                EditorUtility.SetDirty(book);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(restored.scene);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(restored.scene);
                AssetDatabase.SaveAssets();
            }
        }
        /// <summary>失败信息直接返回Editor自动化调用，不能只打印日志后假装通过。</summary>
        private static void Require(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("立绘配置检查失败：" + name);
            }
        }
    }
}
