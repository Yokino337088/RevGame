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
            const string stressSchema = "{\"type\":\"object\",\"properties\":{\"enemies\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1024}},\"required\":[\"enemies\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.stress", "隔离压力局沿正式出生入口创建指定敌数", stressSchema, json => Accepted(Service.DebugStress(JsonUtility.FromJson<StressArgs>(json).enemies)), owner: Owner);
            RevRuntimeAutomation.Register("endless.thrust-preview", "隔离横向四格目标真实刺客突刺与美术预览", Empty, _ => Service.PreviewThrust(), owner: Owner);
            RevRuntimeAutomation.Register("endless.card-rules", "单卡随机/指定升级与非法手牌原子性检查", Empty, _ => EndlessService.CheckCardRules(), owner: Owner);
            RevRuntimeAutomation.Register("endless.upgrade-preview", "隔离局建立两枚同名主力，验真实箭头吸附与松手升级", Empty, _ => Service.PreviewUpgradeDrag(), owner: Owner);
            RevRuntimeAutomation.Register("endless.durability-rules", "隔离检查Boss生命防御倍率与真实减伤", Empty, _ => Service.CheckBossDurability(), owner: Owner);
            const string firePreviewSchema = "{\"type\":\"object\",\"properties\":{\"endless\":{\"type\":\"boolean\"}},\"required\":[\"endless\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.fire-preview", "隔离视觉夹具重播火法范围，不提交伤害；核对两个模式参数与排序", firePreviewSchema, json => UnityEngine.Object.FindObjectOfType<EndlessArena>().PreviewFire(JsonUtility.FromJson<FirePreviewArgs>(json).endless), owner: Owner);
            RevRuntimeAutomation.Register("endless.fire-state", "只读当前火焰租约真实透明度、排序与Shader", Empty, _ => UnityEngine.Object.FindObjectOfType<EndlessArena>()?.FireVisualSnapshot(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("endless.audio-rules", "固定10秒1000请求验证生产声音控频门禁，玩家密集帮手低频", Empty, _ => RevGame.View.GameAudioRateChecks.Run(), owner: Owner);
            const string captureSchema = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"upgrade\",\"thrust\",\"fire-endless\",\"fire-campaign\"]}},\"required\":[\"mode\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.visual-capture", "隔离连拍真实升级拖曳/突刺/火焰16帧，原始证据写忽略目录", captureSchema, json => EndlessVisualCapture.Begin(JsonUtility.FromJson<CaptureArgs>(json).mode), owner: Owner);
            RevRuntimeAutomation.Register("endless.visual-capture-state", "只读连拍完成、错误和清理结果", Empty, _ => EndlessVisualCapture.State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("campaign.fire-check", "隔离大厅正式进入六章，核对火法0.70透明度与排序7并连拍，完成归池返回", Empty, _ => RevGame.View.GameCampaignFireCheck.Begin(), owner: Owner);
            RevRuntimeAutomation.Register("campaign.fire-check-state", "只读六章火焰视觉参数、连拍和归池返回结果", Empty, _ => RevGame.View.GameCampaignFireCheck.State(), readOnly: true, owner: Owner);
            const string dragSchema = "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3},\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8}},\"required\":[\"index\",\"x\",\"y\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.drag-begin", "隔离局通过EventSystem开始真实手牌拖曳", dragSchema, json =>
            {
                var p = JsonUtility.FromJson<DragArgs>(json);
                return EndlessDragInput.Begin(p.index, p.x, p.y);
            }, owner: Owner);
            RevRuntimeAutomation.Register("endless.drag-move", "移动已按下的指针，手牌固定箭头弹性跟随", dragSchema, json =>
            {
                var p = JsonUtility.FromJson<DragArgs>(json);
                return EndlessDragInput.Move(p.x, p.y);
            }, owner: Owner);
            RevRuntimeAutomation.Register("endless.drag-end", "真实松手事件，只提交一次打牌", Empty, _ => EndlessDragInput.End(), owner: Owner);
            RevRuntimeAutomation.Register("endless.drag-state", "只读箭头/手势和手牌固定状态", Empty, _ => EndlessDragInput.State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("endless.drag-cancel", "取消测试手势，非法屏外松开保留手牌", Empty, _ =>
            {
                EndlessDragInput.Cancel();
                return EndlessDragInput.State();
            }, owner: Owner);
            RevRuntimeAutomation.Register("endless.ecs-rules", "隔离验证80格部署、一格升星、盘外Boss总预算与存档", Empty, _ => EndlessService.CheckEcsRules(), owner: Owner);
            RevRuntimeAutomation.Register("endless.ecs-benchmark", "固定240/1024实体全扫描与空间桶查询CPU对照", Empty, _ => EndlessService.BenchmarkEcs(), owner: Owner);
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
            const string formationSchema = "{\"type\":\"object\",\"properties\":{\"formation\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2}},\"required\":[\"formation\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("endless.enter", "从大厅经正式流程进入指定棋阵的固定种子隔离测试局（0弩雨 1炎环 2守眼）", formationSchema, json =>
            {
                return Accepted(RevServices.GetRequired<GameFlow>().EnterEndless(JsonUtility.FromJson<FormationArgs>(json).formation, 0, 1101, false, true));
            }
            , owner: Owner);
            RevRuntimeAutomation.Register("endless.guard-rules", "隔离实例验证盾战3×3防御区与盾击、格挡回执、刺客贯穿射程、远程攻速与一步连发、不停顿补牌、智能驻守与手动保持、选牌后点棋可移动", Empty, _ => EndlessService.CheckGuardRules(), owner: Owner);
            RevRuntimeAutomation.Register("endless.boss-reward", "隔离局立刻走一次击杀领主的真实奖励（随机增益+大横幅），正式局拒绝", Empty, _ => Accepted(Service.DebugBossReward()), owner: Owner);
            RevRuntimeAutomation.Register("endless.snowball-rules", "隔离实例验证波次血攻成长、一步连刷与上限、10波领主来袭横幅、领主奖励随机累加与彩色横幅、增益接入伤害攻速范围生命", Empty, _ => EndlessService.CheckSnowballRules(), owner: Owner);
            RevRuntimeAutomation.Register("endless.feedback", "只读：六章同款战斗反馈的在播/累计次数、绿色落子提示格数、外围敌人出生口分布", Empty, _ => Feedback(), readOnly: true, owner: Owner);
        }
        /// <summary>
        /// 表现验收用的只读快照。arena 部分来自 EndlessArena.FeedbackSnapshot；
        /// lanes 部分直接数服务里的外围敌人：左右各占几只、各铺了几行、有没有不在横线上或从上下门来的。
        /// </summary>
        private static object Feedback()
        {
            EndlessRun run = Service.Run;
            int left = 0, right = 0, otherGates = 0, offRow = 0;
            var leftRows = new HashSet<int>();
            var rightRows = new HashSet<int>();
            if (run != null)
            {
                for (int i = 0; i < Service.Horde.Length; i++)
                {
                    EndlessHorde h = Service.Horde[i];
                    if (!h.active)
                    {
                        continue;
                    }
                    int row = Mathf.RoundToInt(h.y);
                    if (Mathf.Abs(h.y - row) > .01f)
                    {
                        offRow++;
                    }
                    if (h.gate == 0)
                    {
                        left++;
                        leftRows.Add(row);
                    }
                    else if (h.gate == 1)
                    {
                        right++;
                        rightRows.Add(row);
                    }
                    else
                    {
                        otherGates++;
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["arena"] = UnityEngine.Object.FindObjectOfType<EndlessArena>()?.FeedbackSnapshot(),
                ["hordeLeft"] = left, ["hordeRight"] = right, ["hordeOtherGates"] = otherGates, ["hordeOffRow"] = offRow,
                ["leftRows"] = leftRows.Count, ["rightRows"] = rightRows.Count,
                ["comboCasts"] = Service.ComboCasts, ["guardZones"] = GuardZones(), ["handLimit"] = EndlessConfig.Count("HandLimit"), ["banner"] = Service.Banner, ["bannerSerial"] = Service.BannerSerial, ["majorBanner"] = Service.MajorBanner, ["majorBannerSerial"] = Service.MajorBannerSerial, ["wave"] = Service.WaveNumber, ["boons"] = Service.BoonCount,
                ["phase"] = run == null ? "None" : run.phase.ToString(),
                ["selectedCard"] = run?.selectedCard ?? -1, ["selectedUnit"] = run?.selectedUnit ?? -1
            };
        }
        public static void Unregister()
        {
            EndlessDragInput.Cancel();
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
                ["wave"] = s.WaveNumber, ["boons"] = s.BoonCount, ["bossKills"] = r?.bossKills ?? 0, ["cardPlays"] = s.CardPlaySerial, ["cardAction"] = s.LastCardAction, ["cardTarget"] = s.LastCardTarget,
                ["selectedCard"] = r?.selectedCard ?? -1, ["selectedUnit"] = r?.selectedUnit ?? -1,
                ["isolated"] = s.Isolated, ["settled"] = r?.settled ?? false, ["random"] = r?.random ?? 0,
                ["tickCount"] = s.TickCount, ["tickTotalMs"] = s.TickMilliseconds, ["tickMaxMs"] = s.MaxTickMilliseconds,
                ["units"] = s.Units.Count, ["logicCount"] = RevLogic.Count, ["pool"] = RevPool.DumpStats(),
                ["hasEnemies"] = r != null && (s.HordeCount() > 0 || s.Occupied(GameSide.Enemy) > 0),
                ["hasKills"] = r != null && r.kills + r.invaderKills > 0,
                ["seed"] = r?.seed ?? 0,
                ["heartReady"] = UnityEngine.Object.FindObjectOfType<EndlessArena>()?.HeartReady ?? false,
                ["bossOutside"] = s.BossUnit()?.Outside ?? false, ["ecsCandidates"] = s.World.CandidateChecks,
                ["ecsBuilds"] = s.World.SpatialBuilds, ["enemyCapacity"] = s.Horde.Length,
                ["densityScale"] = r == null ? 1 : s.WaveDensityScale(), ["speedScale"] = r == null ? 1 : s.WaveSpeedScale(),
                ["bossChapter"] = s.BossUnit()?.BossChapter ?? 0, ["bossHp"] = s.BossUnit()?.Piece.Hp ?? 0, ["bossSkill"] = s.BossUnit() == null ? "" : ((BossSkillKind)s.BossUnit().ChapterSkill).ToString(),
                ["bossCasts"] = s.BossUnit()?.SkillCasts ?? 0, ["bossCastSerial"] = s.BossCastSerial, ["bossBanner"] = s.BossBanner,
                ["performance"] = RevUI.Get<EndlessHudPanel>()?.PerformanceSnapshot()
            };
        }
        /// <summary>近战智能驻守的分配情况：分到区的近战数、覆盖了几个不同的区、手动保持中的近战数。</summary>
        private static object GuardZones()
        {
            int assigned = 0, held = 0;
            var zones = new HashSet<int>();
            foreach (EndlessUnit u in Service.Units)
            {
                if (u.Piece.Side != GameSide.Player || !EndlessService.IsMelee(u.Piece.Def.Kind) || u.Stone)
                {
                    continue;
                }
                if (u.Zone >= 0)
                {
                    assigned++;
                    zones.Add(u.Zone);
                }
                if (u.HoldUntil > (Service.Run?.seconds ?? 0))
                {
                    held++;
                }
            }
            return new Dictionary<string, object>
            {
                ["assigned"] = assigned, ["distinctZones"] = zones.Count, ["held"] = held
            };
        }
        /// <summary>视觉夹具的模式参数，仅控制显示透明度，不允许通过它改变伤害。</summary>
        [Serializable]
        private sealed class FirePreviewArgs
        {
            public bool endless;
        }
        /// <summary>只允许固定表现白名单，不接收路径、代码或任意反射目标。</summary>
        [Serializable]
        private sealed class CaptureArgs
        {
            public string mode;
        }
        [Serializable] private sealed class DragArgs
        {
            public int index, x, y;
        }
        [Serializable] private sealed class StressArgs
        {
            public int enemies;
        }
        [Serializable] private sealed class FormationArgs
        {
            public int formation;
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
