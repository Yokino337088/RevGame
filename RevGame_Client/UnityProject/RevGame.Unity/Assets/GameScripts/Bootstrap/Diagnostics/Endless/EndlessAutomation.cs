#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;
namespace RevGame.Endless
{
    /// <summary>仅开发环境的窄入口。动作调用真实玩家服务，查询结构化只读；测试新局强制隔离保存。</summary>
    public static class EndlessAutomation
    {
        private static readonly object Owner = new object();
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        private static EndlessService Service
        {
            get
            {
                return RevServices.GetRequired<EndlessService>();
            }
        }
        public static void Register()
        {
            RevGM.Register("无尽/进入测试局", "从大厅经正式流程进入固定种子隔离无尽局", args =>
            {
                if (!RevServices.GetRequired<GameFlow>().EnterEndless(0, 0, 1101, false, true))
                {
                    throw new RevGMUsageException(RevServices.GetRequired<GameFlow>().LastError);
                }
                return "已接受；需等待Screen=Endless且IsTransitioning=false";
            }
            );
            RevGM.Register("无尽/继续测试局", "从大厅恢复隔离测试存档，不访问玩家存档", args =>
            {
                if (!RevServices.GetRequired<GameFlow>().EnterEndless(0, 0, 1101, true, true))
                {
                    throw new RevGMUsageException(RevServices.GetRequired<GameFlow>().LastError);
                }
                return "已接受恢复请求";
            }
            );
            RevRuntimeAutomation.Register("endless.state", "只读无尽流程、模型、资源和计时摘要", Empty, _ => State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("endless.start", "玩家整备完成后开始迎战", Empty, _ => Accepted(Service.StartWave()), owner: Owner);
            RevRuntimeAutomation.Register("endless.pause", "玩家暂停或恢复", Empty, _ => Accepted(Service.Pause()), owner: Owner);
            RevRuntimeAutomation.Register("endless.rearrange", "玩家本分钟慢速改阵", Empty, _ => Accepted(Service.Rearrange()), owner: Owner);
            RevRuntimeAutomation.Register("endless.save-exit", "完整保存隔离局后经正式流程返回大厅", Empty, _ =>
            {
                return Accepted(Service.Isolated && Service.Save() && RevServices.GetRequired<GameFlow>().ReturnHome());
            }
            , owner: Owner);
            RevRuntimeAutomation.Register("endless.finish", "经正式结算入口结束本局，重复调用幂等", Empty, _ =>
            {
                Service.Finish();
                return State();
            }
            , owner: Owner);
            RevRuntimeAutomation.Register("endless.home", "正式返回大厅并收回无尽域", Empty, _ => Accepted(RevServices.GetRequired<GameFlow>().ReturnHome()), owner: Owner);
            const string indexSchema = "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5}},\"required\":[\"index\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.card", "选择实际手牌", indexSchema, json => Accepted(Service.SelectCard(JsonUtility.FromJson<IndexArgs>(json).index)), owner: Owner);
            RevRuntimeAutomation.Register("endless.choose", "选择当前升级项", indexSchema, json => Accepted(Service.Choose(JsonUtility.FromJson<IndexArgs>(json).index)), owner: Owner);
            RevRuntimeAutomation.Register("endless.evolve", "满足配方后真实消耗核心进化", indexSchema, json => Accepted(Service.Evolve(JsonUtility.FromJson<IndexArgs>(json).index)), owner: Owner);
            const string cellSchema = "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8}},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.cell", "正式棋盘点击入口，非法占格不扣牌", cellSchema, json =>
            {
                var p = JsonUtility.FromJson<CellArgs>(json);
                return Accepted(Service.ClickCell(p.x, p.y));
            }
            , owner: Owner);
            RevRuntimeAutomation.Register("endless.rules", "隔离实例验证占格、暂停、存档随机源、上限和重复结算", Empty, _ => EndlessChecks.Run(), owner: Owner);
            RevRuntimeAutomation.Register("endless.boss-rules", "隔离实例跑完六章领主：招式归属、专属技周期、献祭潮掉血与预警一致性", Empty, _ => EndlessService.CheckBossSkills(), owner: Owner);
            const string chapterSchema = "{\"type\":\"object\",\"properties\":{\"chapter\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6}},\"required\":[\"chapter\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.boss-spawn", "隔离局立刻召唤指定章节的领主，正式局拒绝", chapterSchema, json => Accepted(Service.DebugSpawnBoss(JsonUtility.FromJson<ChapterArgs>(json).chapter)), owner: Owner);
            RevRuntimeAutomation.Register("endless.music", "只读BGM轮播状态与内存中实际存在的曲目", Empty, _ => UnityEngine.Object.FindObjectOfType<EndlessArena>()?.MusicSnapshot(), readOnly: true, owner: Owner);
            const string seekSchema = "{\"type\":\"object\",\"properties\":{\"remaining\":{\"type\":\"number\",\"minimum\":0,\"maximum\":600}},\"required\":[\"remaining\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.music-seek", "把当前曲目跳到结尾前若干秒，只用于验证切歌，不改变任何玩法", seekSchema, json =>
            {
                UnityEngine.Object.FindObjectOfType<EndlessArena>()?.DebugSeekMusic(JsonUtility.FromJson<SeekArgs>(json).remaining);
                return Accepted(true);
            }
            , owner: Owner);
            RevRuntimeAutomation.Register("endless.extended-rules", "独立规则夹具：身份转换、Boss破阵、六配方、有限巡回、损坏存档及画质一致性", Empty, _ => EndlessService.CheckExtendedRules(), owner: Owner);
        }
        public static void Unregister()
        {
            RevRuntimeAutomation.UnregisterAllOf(Owner);
            RevGM.Unregister("无尽/进入测试局");
            RevGM.Unregister("无尽/继续测试局");
        }
        private static object Accepted(bool value)
        {
            return new Dictionary<string, object>
            {
                ["accepted"] = value, ["reason"] = value ? "" : Service.LastError
            };
        }
        public static object State()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var s = Service;
            var r = s.Run;
            return new Dictionary<string, object>
            {
                ["screen"] = flow.Screen.ToString(), ["transitioning"] = flow.IsTransitioning,
                ["error"] = flow.LastError, ["phase"] = r == null ? "None" : r.phase.ToString(),
                ["seconds"] = r?.seconds ?? 0, ["heart"] = r?.heart ?? 0, ["score"] = r?.score ?? 0,
                ["players"] = s.PlayerCount(), ["hand"] = r?.hand.Count ?? 0, ["horde"] = r == null ? 0 : s.HordeCount(),
                ["level"] = r?.level ?? 0, ["kills"] = r?.kills ?? 0, ["circuit"] = r?.circuit ?? 0,
                ["isolated"] = s.Isolated, ["settled"] = r?.settled ?? false, ["random"] = r?.random ?? 0,
                ["tickCount"] = s.TickCount, ["tickTotalMs"] = s.TickMilliseconds, ["tickMaxMs"] = s.MaxTickMilliseconds,
                ["units"] = s.Units.Count, ["logicCount"] = RevLogic.Count, ["pool"] = RevPool.DumpStats(),
                ["hasEnemies"] = r != null && (s.HordeCount() > 0 || s.Occupied(GameSide.Enemy) > 0),
                ["hasKills"] = r != null && r.kills + r.invaderKills > 0,
                ["seed"] = r?.seed ?? 0,
                ["bossChapter"] = s.BossUnit()?.BossChapter ?? 0, ["bossHp"] = s.BossUnit()?.Piece.Hp ?? 0, ["bossSkill"] = s.BossUnit() == null ? "" : ((BossSkillKind)s.BossUnit().ChapterSkill).ToString(),
                ["bossCasts"] = s.BossUnit()?.SkillCasts ?? 0, ["bossCastSerial"] = s.BossCastSerial, ["bossBanner"] = s.BossBanner,
                ["performance"] = RevUI.Get<EndlessHudPanel>()?.PerformanceSnapshot()
            };
        }
        [Serializable] private sealed class IndexArgs
        {
            public int index;
        }
        [Serializable] private sealed class ChapterArgs
        {
            public int chapter;
        }
        [Serializable] private sealed class SeekArgs
        {
            public float remaining;
        }
        [Serializable] private sealed class CellArgs
        {
            public int x, y;
        }
    }
}
#endif
