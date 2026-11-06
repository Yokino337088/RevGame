#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 自动化只把固定事实送入正式评价器，所有视图和冷却由相同生产入口验证；可变入口限定隔离存档。
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Endless;
using RevGame.Flow;
using RevGame.View;
using Revolution;
using UnityEngine;
namespace RevGame.Bootstrap
{
    /// <summary>鸦鸦互动只读查询、窗口夹具和清理，集中Bootstrap成对注册，不直接SetActive立绘。</summary>
    public static class YayaReactionAutomation
    {
        /// <summary>本模块所有运行期注册共用所有者，Bootstrap销毁时按Owner完整注销。</summary>
        private static readonly object Owner = new object();
        /// <summary>查询无输入对象，禁止额外路径或隐藏参数进入协议。</summary>
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        /// <summary>发现真实当前域的观察者，无运行中的互动Logic则明确失败，不返回虚假状态。</summary>
        public static YayaReactionLogic Logic()
        {
            return (YayaReactionLogic)RevLogic.Get<CampaignYayaReactionLogic>() ?? RevLogic.Get<EndlessYayaReactionLogic>() ?? throw new InvalidOperationException("当前不在六章或无尽互动域");
        }
        /// <summary>Bootstrap统一调用一次；变更入口自身检查隔离与阶段，不凭工具名称默认有授权。</summary>
        public static void Register()
        {
            RevRuntimeAutomation.Register("yaya.reaction-state", "只读实际评价、弹出、气泡和资源租约", Empty, _ => State(), readOnly: true, owner: Owner);
            const string schema = "{\"type\":\"object\",\"properties\":{\"kills\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"losses\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10},\"taken\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000},\"dealt\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000},\"health\":{\"type\":\"number\",\"minimum\":1,\"maximum\":10000},\"advance\":{\"type\":\"number\",\"minimum\":0,\"maximum\":60}},\"required\":[\"kills\",\"losses\",\"taken\",\"dealt\",\"health\",\"advance\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("yaya.evaluate-window", "隔离局提交表现事实窗口，复用正式分类和控频；不改棋局", schema, json =>
            {
                var args = JsonUtility.FromJson<WindowArgs>(json);
                bool accepted = Logic().EvaluateForTest(args.kills, args.losses, args.taken, args.dealt, args.health, args.advance);
                return new Dictionary<string, object> { ["accepted"] = accepted };
            }, owner: Owner);
            RevRuntimeAutomation.Register("yaya.reaction-cleanup", "结束临时评价预览、归池释放，恢复自然采样", Empty, _ =>
            {
                var logic = (YayaReactionLogic)RevLogic.Get<CampaignYayaReactionLogic>() ?? RevLogic.Get<EndlessYayaReactionLogic>();
                logic?.CleanupPreview();
                return new Dictionary<string, object> { ["cleaned"] = true };
            }, owner: Owner);
        }
        /// <summary>同时返回业务观察者与场景实际状态，区别请求接受与图片可画。</summary>
        public static object State()
        {
            var presenter = UnityEngine.Object.FindObjectOfType<YayaReactionPresenter>();
            var logic = (YayaReactionLogic)RevLogic.Get<CampaignYayaReactionLogic>() ?? RevLogic.Get<EndlessYayaReactionLogic>();
            return new Dictionary<string, object> { ["logic"] = logic?.Snapshot(), ["presentation"] = presenter?.Snapshot() };
        }
        /// <summary>停止测试能力但不改玩法，域清理独立释放观察者与场景资源。</summary>
        public static void Unregister()
        {
            RevRuntimeAutomation.UnregisterAllOf(Owner);
        }
        /// <summary>窗口输入只有数值事实，不允许任意资源路径或直接改判定结果。</summary>
        [Serializable]
        private sealed class WindowArgs
        {
            /// <summary>窗口的击杀/死亡次数及实际伤害点，参数范围由实时schema限制。</summary>
            public int kills, losses, taken, dealt;
            /// <summary>窗口起始总生命点及额外推进的测试观察秒数，不修改战斗真实时间。</summary>
            public float health, advance;
        }
    }
    public static partial class GameGmCommands
    {
        /// <summary>与真实运行期契约共用入口，GM只用于隔离测试与查询，不给玩家改存档。</summary>
        private static void RegisterYayaReactionCommands()
        {
            RegisterCommand("鸦鸦/互动状态", "只读当前评价、是否弹出、气泡和图片租约", args =>
            {
                var presenter = UnityEngine.Object.FindObjectOfType<YayaReactionPresenter>();
                if (presenter == null)
                {
                    return "当前场景没有局内鸦鸦；请进入六章或无尽";
                }
                var state = (Dictionary<string, object>)presenter.Snapshot();
                return "visible=" + state["visible"] + ",band=" + state["band"] + ",expression=" + state["expression"] + ",error=" + state["error"];
            });
            RegisterCommand("鸦鸦/测试评价", "仅隔离局输入1夸奖/2鞭策/3嘲讽事实窗口，经正式控频与资源播放", args =>
            {
                int band = args.Int(0, 1);
                if (band < 1 || band > 3)
                {
                    throw new RevGMUsageException("档位要求1～3");
                }
                int kills = band == 1 ? (int)YayaReactionConfig.Value(RevServices.GetRequired<GameFlow>().Screen == GameScreen.Endless ? "EndlessGoodKills" : "CampaignGoodKills") : 0;
                bool accepted = YayaReactionAutomation.Logic().EvaluateForTest(kills, band == 3 ? 1 : 0, 0, 100, 1000, YayaReactionConfig.Value("InitialDelay"));
                return "accepted=" + accepted + "；请查询实际visible，冷却可能拒绝";
            }, RevGMArg.Int("档位", 1));
        }
    }
}
#endif
