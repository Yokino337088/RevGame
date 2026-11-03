#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 六章真实场景的范围显示检查：正式流程进入GameScene，借真实效果池播放ShowPattern，验证排序/透明度并归池。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>仅隔离会话运行一份六章火焰视觉检查。不会修改伤害、存档或战斗属性，完成后正式返回大厅。</summary>
    public static class GameCampaignFireCheck
    {
        /// <summary>开发态作业状态与原始证据目录；只读状态供MCP有界轮询，不作为玩家存档。</summary>
        private static string _status = "idle", _error = "", _directory = "";
        /// <summary>实际Shader参数断言与正式返回大厅结果，二者都成功才可以验收。</summary>
        private static bool _parameters, _cleanup;
        /// <summary>本作业真实截图数，固定8帧覆盖火焰寿命；不写入玩家数据。</summary>
        private static int _frames;
        /// <summary>只允许隔离大厅开始；等待、池租约、相机与清理全部在同一个RevTask流程中。</summary>
        public static object Begin()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || flow.Screen != GameScreen.Home || flow.IsTransitioning || _status == "running")
            {
                throw new InvalidOperationException("六章火焰检查需要已就绪的隔离大厅");
            }
            _status = "running";
            _error = "";
            _frames = 0;
            _parameters = _cleanup = false;
            _directory = Path.GetFullPath("../../../.codex/work/endless-feedback-2026-11-03/campaign-fire-" + DateTime.Now.ToString("HHmmssfff"));
            Directory.CreateDirectory(_directory);
            Run().Forget();
            return State();
        }
        /// <summary>等待真实场景交付后，在同一个池对象上核对参数并连拍；单独视觉夹具不证明技能伤害。</summary>
        private static async RevTask Run()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            BattleEffectView effect = null;
            try
            {
                flow.OpenLevels();
                if (!flow.EnterBattle(1))
                {
                    throw new InvalidOperationException(flow.LastError);
                }
                float deadline = Time.realtimeSinceStartup + 35;
                while (flow.IsTransitioning)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        throw new TimeoutException("六章过场未交付");
                    }
                    await RevTask.Yield();
                }
                if (flow.Screen != GameScreen.Battle || RevScene.CurrentName != "GameScene")
                {
                    throw new InvalidOperationException("没有进入六章真实场景");
                }
                var completion = new RevTaskCompletionSource<BattleEffectView>();
                GamePoolRequests.GetAsync<BattleEffectView>(completion.SetResult);
                effect = await completion.Task;
                if (effect == null)
                {
                    throw new InvalidOperationException("六章范围特效池资源未就绪");
                }
                // 世界中心与普通六章格宽一致；目标位置选已有敌阵，证据可以直接辨认棋子在火焰之上。
                Vector3 center = new Vector3(Consts.BoardOriginOffset + 4.5f*Consts.CellSize, Consts.BoardOriginOffset + 6.5f*Consts.CellSize, 0);
                effect.ShowPattern(center, center, PieceKind.MageFire, 1, _ => { });
                _parameters = Mathf.Approximately(effect.PatternOpacity, .7f) && effect.PatternOrder == 7 && effect.UsesElementalShader;
                if (!_parameters)
                {
                    throw new InvalidOperationException("六章火法透明度0.70或排序7契约失败");
                }
                for (int i = 0; i < 8; i++)
                {
                    effect.AdvancePattern(Time.time);
                    ScreenCapture.CaptureScreenshot(Path.Combine(_directory, "frame_" + i.ToString("00") + ".png"));
                    _frames++;
                    await RevTask.Delay(90);
                }
                _status = "passed";
            }
            catch (Exception error)
            {
                _status = "failed";
                _error = error.Message;
            }
            finally
            {
                // 播放完成后效果会自行回池；只有仍处在本租约时才归还，避免重复Return。
                if (effect != null && effect.IsPatternActive)
                {
                    RevPool.Return(effect);
                }
                flow.ReturnHome();
                float deadline = Time.realtimeSinceStartup + 30;
                while (flow.IsTransitioning && Time.realtimeSinceStartup < deadline)
                {
                    await RevTask.Yield();
                }
                _cleanup = flow.Screen == GameScreen.Home && !flow.IsTransitioning;
                if (!_cleanup)
                {
                    _status = "failed";
                    _error += "；返回大厅清理失败";
                }
            }
        }
        /// <summary>只读实际检查参数、截图与清理；未完成时返回running，调用者必须等清理也结束。</summary>
        public static object State()
        {
            // 运行时桥对匿名属性对象会返回空Json；Dictionary保持字段真实可读，MCP才能进行有界等待与断言。
            return new Dictionary<string, object>
            {
                ["status"] = _cleanup ? _status : (_status == "failed" ? "failed" : "running"),
                ["error"] = _error, ["parameters"] = _parameters, ["cleanup"] = _cleanup, ["frames"] = _frames, ["directory"] = _directory
            };
        }
    }
}
#endif
