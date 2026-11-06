#if UNITY_EDITOR
// 本次开发态表现夹具保存输入而非伪造输出：临时改变已有配置字段，再调用正式剧情/布局，收尾恢复。
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    /// <summary>仅内存隔离Play开放，验证用户修改立绘与场景偏移后的真实落屏，禁止写资产/生产进度。</summary>
    internal static class StoryPresentationPreview
    {
        /// <summary>当前夹具持有原始输入，退出时逐项还原；不修改对白、表情文件或材质资产。</summary>
        private static StoryBook.Episode _episode;
        private static Sprite _episodePortrait, _linePortrait;
        private static int _expression;
        private static LobbySceneView _lobby;
        private static Vector3 _offset;
        private static bool _lobbyPreview;

        /// <summary>fixture取none/episode/line/default，分别验证表情回退、篇章覆盖、逐句覆盖及默认图。</summary>
        public static object Portrait(string fixture)
        {
            GameFlow flow = RequireIsolated();
            if (flow.IsStoryPlaying || flow.IsTransitioning)
            {
                throw new InvalidOperationException("请先结束剧情与过场");
            }
            RestorePortrait();
            _episode = flow.StoryContent.Find("opening");
            _episodePortrait = _episode.Portrait;
            _linePortrait = _episode.Lines[0].Portrait;
            _expression = _episode.Lines[0].Expression;
            _episode.Portrait = null;
            _episode.Lines[0].Portrait = null;
            if (fixture == "episode" || fixture == "line")
            {
                _episode.Portrait = flow.StoryContent.ExpressionSprite(23, out _);
            }
            if (fixture == "line")
            {
                _episode.Lines[0].Portrait = flow.StoryContent.ExpressionSprite(11, out _);
            }
            if (fixture == "default")
            {
                _episode.Lines[0].Expression = 0;
            }
            bool accepted = flow.PreviewStoryForTest("opening");
            return new Dictionary<string, object> { ["accepted"] = accepted };
        }
        /// <summary>固定Shader时钟用于前后帧像素比较；offsetY可观察真实相机适配后位移是否保留。</summary>
        public static object Lobby(float offsetY, float time)
        {
            GameFlow flow = RequireIsolated();
            if (flow.Screen != GameScreen.Home || flow.IsStoryPlaying || flow.IsTransitioning)
            {
                throw new InvalidOperationException("构图预览需要稳定大厅且没有剧情");
            }
            _lobby = UnityEngine.Object.FindObjectOfType<LobbySceneView>();
            if (!_lobbyPreview)
            {
                _offset = (Vector3)((Dictionary<string, object>)_lobby.Snapshot())["placementOffset"];
                _lobbyPreview = true;
            }
            _lobby.ApplyPreview(new Vector3(_offset.x, offsetY, _offset.z), time);
            return _lobby.Snapshot();
        }
        /// <summary>测试收尾先结束正式剧情再还原输入。Bootstrap注销时也调用，Play停止不会保留夹具。</summary>
        public static object Cleanup()
        {
            // 只结束本夹具启动的剧情。Bootstrap正常注销时不能把玩家尚未读完的开场标记为完成。
            if (_episode != null)
            {
                RevServices.Get<GameFlow>()?.SkipStory();
            }
            RestorePortrait();
            if (_lobbyPreview && _lobby != null)
            {
                _lobby.ApplyPreview(_offset, -1);
            }
            _lobbyPreview = false;
            _lobby = null;
            return new Dictionary<string, object> { ["restored"] = true };
        }
        /// <summary>仅恢复本次保存的三个输入字段，不覆盖其它篇章或策划数据。</summary>
        private static void RestorePortrait()
        {
            if (_episode == null)
            {
                return;
            }
            _episode.Portrait = _episodePortrait;
            _episode.Lines[0].Portrait = _linePortrait;
            _episode.Lines[0].Expression = _expression;
            _episode = null;
        }
        /// <summary>隔离是可变预览的必要前提，正式存档运行直接拒绝。</summary>
        private static GameFlow RequireIsolated()
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                throw new InvalidOperationException("表现预览只允许内存隔离测试");
            }
            return RevServices.GetRequired<GameFlow>();
        }
    }
}
#endif
