#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    /// <summary>剧情/教程自动化契约。查询无副作用，动作复用玩家入口；Bootstrap成对注册与注销。</summary>
    public static class StoryAutomationCommands
    {
        private static readonly object Owner = new object();
        private static System.IDisposable _scoreIsolation;
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        public static void Register()
        {
            // 教学已在真实战斗中运行，测试会产生积分；租用服务已有隔离区，退出时释放，不碰生产存档。
            if (RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                _scoreIsolation = ((BattleService)RevServices.GetRequired<IBattleService>()).BeginScorePreview();
            }
            RevRuntimeAutomation.Register("board.magic.state", "只读棋盘格面、落点合法性、眼睛帧和章节租约", Empty, _ =>
            {
                var board = Object.FindObjectOfType<RevGame.View.BoardView>();
                return board == null ? null : board.MagicSnapshot();
            }, readOnly: true, owner: Owner);
#if UNITY_EDITOR
            RevRuntimeAutomation.Register("board.magic.eye-fixture", "仅隔离Play：装配双方真实眼位，供眨眼表现验收", Empty, _ =>
            {
                var flow = RevServices.GetRequired<GameFlow>();

                if (!RevServices.GetRequired<StoryProgress>().IsIsolated || flow.IsGameplayBlocked || flow.Screen != RevGame.Core.GameScreen.Battle)
                {
                    throw new System.InvalidOperationException("需要隔离进度、无剧情过场的战斗界面");
                }
                return Accepted(((BattleService)RevServices.GetRequired<IBattleService>()).LoadEyeScenario());
            }, owner: Owner);
#endif
            RevRuntimeAutomation.Register("story.state", "查询剧情、教学与隔离进度", Empty, _ => State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("story.next", "点击剧情下一句", Empty, _ => new Dictionary<string, object> { ["accepted"] = RevServices.GetRequired<GameFlow>().AdvanceStory() }, owner: Owner);
            RevRuntimeAutomation.Register("story.skip", "与右上角跳过按钮共用正式剧情收尾", Empty, _ => new Dictionary<string, object> { ["accepted"] = RevServices.GetRequired<GameFlow>().SkipStory() }, owner: Owner);
            RevRuntimeAutomation.Register("story.yaya-state", "只读鸦鸦立绘/表情、全屏背景、层级与文本布局", Empty, _ => RevGame.UI.StoryPanel.YayaSnapshot(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("lobby.yaya-state", "只读StartScene实际场景对象、背景遮罩、待机与HomePanel无旧背景", Empty, _ => LobbySnapshot(), readOnly: true, owner: Owner);
#if UNITY_EDITOR
            const string portraitSchema = "{\"type\":\"object\",\"properties\":{\"fixture\":{\"type\":\"string\",\"enum\":[\"none\",\"episode\",\"line\",\"default\"]}},\"required\":[\"fixture\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("story.presentation-preview", "隔离Play修改立绘配置并调用正式播放，cleanup恢复", portraitSchema, json => StoryPresentationPreview.Portrait(JsonUtility.FromJson<PresentationArgs>(json).fixture), owner: Owner);
            const string lobbySchema = "{\"type\":\"object\",\"properties\":{\"offsetY\":{\"type\":\"number\",\"minimum\":-1,\"maximum\":1},\"time\":{\"type\":\"number\",\"minimum\":-1,\"maximum\":60}},\"required\":[\"offsetY\",\"time\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("lobby.presentation-preview", "隔离Play调整构图输入和Shader时钟，不修改资产", lobbySchema, json =>
            {
                PresentationArgs args = JsonUtility.FromJson<PresentationArgs>(json);
                return StoryPresentationPreview.Lobby(args.offsetY, args.time);
            }, owner: Owner);
            RevRuntimeAutomation.Register("story.presentation-cleanup", "还原本次立绘、偏移与Shader时钟预览", Empty, _ => StoryPresentationPreview.Cleanup(), owner: Owner);
#endif
            RevRuntimeAutomation.Register("tutorial.card", "选择练习手牌", Empty, _ => Accepted(RevLogic.Get<TutorialLogic>()?.SelectCard() == true), owner: Owner);
            RevRuntimeAutomation.Register("tutorial.cell", "点击练习棋盘的格子", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8}},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}", json =>
            {
                var cell = JsonUtility.FromJson<CellArgs>(json);
                return Accepted(RevLogic.Get<TutorialLogic>()?.ClickCell(cell.x, cell.y) == true);
            }, owner: Owner);
            RevRuntimeAutomation.Register("tutorial.finish", "完成实操后进入第一章", Empty, _ => Accepted(RevServices.GetRequired<GameFlow>().CompleteTutorial()), owner: Owner);
            RevRuntimeAutomation.Register("story.preview", "仅隔离会话预览文案，不更改战役", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"],\"additionalProperties\":false}", json =>
                Accepted(RevServices.GetRequired<GameFlow>().PreviewStoryForTest(JsonUtility.FromJson<PreviewArgs>(json).id)), owner: Owner);
        }
        public static void Unregister()
        {
#if UNITY_EDITOR
            StoryPresentationPreview.Cleanup();
#endif
            RevRuntimeAutomation.UnregisterAllOf(Owner);
            _scoreIsolation?.Dispose();
            _scoreIsolation = null;
        }
#if UNITY_EDITOR
        /// <summary>开发态预览的输入DTO，参数范围由实时schema限制，不允许传任意资源/路径。</summary>
        [System.Serializable]
        private sealed class PresentationArgs
        {
            public string fixture;
            public float offsetY;
            public float time;
        }
#endif
        private static object Accepted(bool value)
        {
            return new Dictionary<string, object> { ["accepted"] = value };
        }
        [System.Serializable]
        private sealed class CellArgs
        {
            public int x; public int y;
        }
        [System.Serializable]
        private sealed class PreviewArgs
        {
            public string id;
        }
        private static int TotalHp(IReadOnlyList<RevGame.Pieces.Piece> pieces)
        {
            int total = 0;
            foreach (var piece in pieces)
            {
                total += piece.Hp;
            }
            return total;
        }
        /// <summary>只读实际RectTransform边界，检查提示是否侵占可见按钮或状态面板，不修改布局。</summary>
        private static object TutorialLayout()
        {
            var panel = RevUI.Get<RevGame.UI.TutorialPanel>();

            if (panel == null)
            {
                return null;
            }
            var hint = panel.transform.Find("Hint") as RectTransform;
            var focus = panel.transform.Find("focus").GetComponent<UnityEngine.UI.Image>();
            var hintGlow = hint.Find("hintGlow")?.GetComponent<UnityEngine.UI.Image>();
            var corners = new Vector3[4];
            hint.GetWorldCorners(corners);
            Rect box = Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            var overlaps = new List<string>();
            foreach (var canvas in new Component[] { RevUI.Get<RevGame.UI.PreparePanel>(), RevUI.Get<RevGame.UI.BattleHudPanel>() })
            {
                if (canvas == null)
                {
                    continue;
                }
                foreach (var rect in canvas.GetComponentsInChildren<RectTransform>())
                {
                    if (!rect.gameObject.activeInHierarchy || (rect.GetComponent<UnityEngine.UI.Button>() == null && rect.name != "infoPlate"))
                    {
                        continue;
                    }
                    rect.GetWorldCorners(corners);

                    if (box.Overlaps(Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y)))
                    {
                        overlaps.Add(rect.name);
                    }
                }
            }
            bool textFits = true;
            foreach (var label in hint.GetComponentsInChildren<UnityEngine.UI.Text>())
            {
                // 使用真实字体排版后的首选高度，避免矩形不重叠却把标题/正文裁没了。
                var settings = label.GetGenerationSettings(label.rectTransform.rect.size);
                settings.fontSize = label.resizeTextForBestFit ? label.cachedTextGenerator.fontSizeUsedForBestFit : label.fontSize;
                float required = label.cachedTextGeneratorForLayout.GetPreferredHeight(label.text, settings) / label.pixelsPerUnit;

                if (required > label.rectTransform.rect.height + 1f)
                {
                    textFits = false;
                }
            }
            return new Dictionary<string, object>
            {
                ["textFits"] = textFits,
                ["overlapCount"] = overlaps.Count,
                ["overlaps"] = overlaps,
                ["width"] = hint.rect.width,
                ["height"] = hint.rect.height,
                ["shader"] = focus.material.shader.name,
                ["hintHighlight"] = hintGlow != null && hintGlow.enabled && hintGlow.material.shader.name == "RevGame/UI/SlimeTutorialHighlight",
                ["focusWidth"] = focus.material.GetFloat("_Width"),
                ["pulseDepth"] = focus.material.GetFloat("_PulseDepth"),
                ["fill"] = focus.material.GetFloat("_Fill"),
                ["fontSize"] = hint.Find("instruction").GetComponent<UnityEngine.UI.Text>().fontSize,
                ["supported"] = focus.material.shader.isSupported,
                ["raycast"] = focus.raycastTarget,
                ["focusVisible"] = focus.enabled
            };
        }
        private static object State()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var progress = RevServices.GetRequired<StoryProgress>();
            var battle = RevServices.GetRequired<IBattleService>();
            var boardView = Object.FindObjectOfType<RevGame.View.BoardView>();
            var lesson = RevLogic.Get<TutorialLogic>();
            var panel = RevUI.Get<RevGame.UI.StoryPanel>();
            var view = panel == null ? null : panel.GetComponentInChildren<RevGame.UI.StoryView>();
            // 正式Player固定走AB；编辑器开关只在编辑器定义，开发包不能直接引用。
            bool abMode = true;
#if UNITY_EDITOR
            abMode = RevResBootstrap.UseABInEditor;
#endif
            return new Dictionary<string, object>
            {
                ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                ["tutorialLayout"] = TutorialLayout(),
                ["unlockedChapter"] = battle.UnlockedChapter,
                ["chapter"] = battle.Chapter,
                ["tutorialActive"] = flow.IsTutorialActive,
                ["tutorialOpen"] = RevUI.IsOpen<RevGame.UI.TutorialPanel>(),
                ["scoreIsolated"] = _scoreIsolation != null,
                ["preparationGranted"] = battle.PreparationGranted,
                ["battleElapsed"] = battle.BattleElapsed,
                ["enemyHp"] = TotalHp(battle.EnemyPieces),
                ["playerHp"] = TotalHp(battle.PlayerPieces),
                ["sameBoard"] = lesson != null && ReferenceEquals(lesson.Board, battle.Board),
                ["handCount"] = battle.Hand.Count,
                ["phase"] = battle.Phase.ToString(),
                ["playerCount"] = battle.PlayerPieces.Count,
                ["enemyCount"] = battle.EnemyPieces.Count,
                ["renderedCells"] = boardView == null ? 0 : boardView.CellCount,
                ["renderedPieces"] = boardView == null ? 0 : boardView.PieceCount,
                ["prepareOpen"] = RevUI.IsOpen<RevGame.UI.PreparePanel>(),
                ["hudOpen"] = RevUI.IsOpen<RevGame.UI.BattleHudPanel>(),
                ["battleHud"] = RevUI.Get<RevGame.UI.BattleHudPanel>()?.InfoSnapshot(),
                ["guideFrame"] = view == null ? -1 : view.GuideFrame,
                ["guideFrameCount"] = flow.StoryContent == null ? 0 : flow.StoryContent.GuideFrames.Length,
                ["screen"] = flow.Screen.ToString(),
                ["transitioning"] = flow.IsTransitioning,
                ["blocked"] = flow.IsGameplayBlocked,
                ["playing"] = flow.IsStoryPlaying,
                ["episode"] = flow.StoryId,
                ["line"] = flow.StoryLine,
                ["error"] = flow.LastError,
                ["openingComplete"] = progress.OpeningComplete,
                ["tutorialComplete"] = progress.TutorialComplete,
                ["isolated"] = progress.IsIsolated,
                ["step"] = lesson == null ? "none" : lesson.Step.ToString(),
                ["liberties"] = lesson == null ? -1 : lesson.EnemyLiberties,
                ["locked"] = lesson != null && lesson.EnemyLocked
                ,
                ["typing"] = view != null && view.IsTyping,
                ["soundStarted"] = view != null && view.SoundStarted,
                ["visibleCharacters"] = view == null ? 0 : view.VisibleCharacters,
                ["guidePacked"] = flow.StoryContent != null && flow.StoryContent.Guide != null && flow.StoryContent.Guide.packed,
                ["abMode"] = abMode
            };
        }
        /// <summary>读取场景Renderer与UI边界，验证没有把大厅背景/角色放回HomePanel。</summary>
        private static object LobbySnapshot()
        {
            var lobby = Object.FindObjectOfType<RevGame.View.LobbySceneView>();
            var home = RevUI.Get<RevGame.UI.HomePanel>();
            return new Dictionary<string, object>
            {
                ["lobby"] = lobby?.Snapshot(),
                ["noUIBackdrop"] = home != null && home.transform.Find("screenBackground") == null && home.transform.Find("LobbyBackground") == null,
                ["noUICharacter"] = home != null && home.transform.Find("Yaya") == null
            };
        }
    }
}
#endif
