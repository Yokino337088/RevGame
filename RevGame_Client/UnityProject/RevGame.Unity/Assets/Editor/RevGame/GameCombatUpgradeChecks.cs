// 战斗升级的图形Play验收。使用正式流程/GM/服务事件，不能用“动画存在”代替离线规则断言。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameCombatUpgradeChecks
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly List<string> Evidence = new List<string>();
        private static int _assertions;
        public static string LastResult { get; private set; } = "未运行";
        private static string Output => Path.GetFullPath("../../文档/进度同步/战斗升级验收-2026-10-23-附件");
        private static void Require(bool ok, string message)
        {
            if (!ok)
            {
                throw new InvalidOperationException(message);
            }
            _assertions++;
            Evidence.Add(message);
        }
        private static object Field(object owner, string name)
        {
            return owner.GetType().GetField(name, Instance).GetValue(owner);
        }
        private static void Set(object owner, string name, object value)
        {
            owner.GetType().GetField(name, Instance).SetValue(owner, value);
        }
        private static void Gm(string command)
        {
            RevGMResult result = RevGM.Execute("RevGame/" + command);
            Require(result.Success, command + "：" + result.Message);
        }
        /// <summary>仅关闭夹具随机输入并冻结玩家移动以观察预警；双方普通攻击和技能仍由正式规则结算。</summary>
        private static void Isolate(BattleService battle)
        {
            Set(battle, "_eventScheduled", false);
            Set(battle, "_nextRegionAt", float.MaxValue);
            Set(battle, "_nextBombAt", float.MaxValue);
            battle.SetPlayerInvincible(true);
            foreach (Piece piece in battle.PlayerPieces)
            {
                typeof(Piece).GetProperty("NextMoveAt", Instance).SetValue(piece, float.MaxValue);
            }
        }
        /// <summary>十字技能只命中Boss中心横纵轴；用玩家同一换位入口将小棋移到轴线上。</summary>
        private static void AlignCrossTarget(BattleService battle)
        {
            Piece boss = null, player = null;
            foreach (Piece piece in battle.EnemyPieces)
            {
                if (piece.IsAlive && piece.IsKing)
                {
                    boss = piece;
                    break;
                }
            }
            foreach (Piece piece in battle.PlayerPieces)
            {
                if (piece.IsAlive && piece.Size == 1)
                {
                    player = piece;
                    break;
                }
            }
            Require(boss != null && player != null, "十字目标夹具包含Boss与小棋");
            for (int y = 0; y < battle.Board.Size; y++)
            {
                BoardPos pos = new BoardPos(boss.Pos.X + boss.Size / 2, y);

                if (battle.Board.GetPiece(pos) != null)
                {
                    continue;
                }
                Require(battle.HandleBoardClick(player.Pos) && battle.HandleBoardClick(pos), "十字目标通过真实点击入口移至 " + pos);
                return;
            }
            throw new InvalidOperationException("十字轴线上没有合法小棋落点");
        }
        /// <summary>只供Editor验收观察实际AudioSource，不给业务增加框架不存在的在播查询接口。</summary>
        private static bool VoicePlaying(string clipName)
        {
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (source.isPlaying && source.clip != null && source.clip.name == clipName)
                {
                    return true;
                }
            }
            return false;
        }
        [UnityEditor.MenuItem("RevGame/验收/战斗节奏与18技能（Play）")]
        public static async void Begin()
        {
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("需要真实Play");
            }
            if (LastResult == "运行中")
            {
                throw new InvalidOperationException("验收已经在运行");
            }
            LastResult = "运行中";
            Evidence.Clear();
            _assertions = 0;
            Directory.CreateDirectory(Output);
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            int unityErrors = 0, audioFailures = 0, audioDuplicates = 0;
            string failure = null;
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    unityErrors++;
                    Evidence.Add(message);
                }
            };
            Action<string, RevSoundErrorReason> onSoundFailed = (name, reason) =>
            {
                // 同帧同名只播一次是框架的正常防叠音策略，不是缺资源或播放故障。
                // 保留实际抑制数量；其它原因仍全部计为失败，每招另断言实际AudioSource播放。
                if (reason == RevSoundErrorReason.Duplicated)
                {
                    audioDuplicates++;
                    return;
                }
                audioFailures++;
                Evidence.Add("声音失败：" + name + "：" + reason);
            };
            Application.logMessageReceived += onLog;
            RevSound.Failed += onSoundFailed;
            try
            {
                Require(RevResBootstrap.UseABInEditor, "冷启动使用真实AB模式");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "正式流程进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                Require(battle.LoadDevelopmentScenario("boss"), "加载固定Boss夹具");
                battle.Tick(Balance.PrepDelaySeconds);
                Require(battle.StartBattle(), "正式开战");
                Isolate(battle);
                await RevTask.Delay(400);
                var board = UnityEngine.Object.FindObjectOfType<BoardView>();
                Require(board != null, "真实棋盘视图已就绪");
                Gm("战斗增益 Attack 30");
                Gm("战斗增益 Defense 30");
                Gm("战斗增益 Haste 20");
                Gm("战斗增益 Vitality 30");
                Require(battle.CombatPacingStatus().StartsWith("attack=0.3;defense=0.3;haste=0.2;vitality=0.3;"), "四项GM真实查询一致");
                var origins = new Dictionary<BoardCellView, Vector3>();
                foreach (BoardCellView cell in UnityEngine.Object.FindObjectsOfType<BoardCellView>())
                {
                    origins.Add(cell, cell.transform.position);
                }
                Gm("区域波动");
                Require(board.ActiveHazardCellCount == Balance.RegionSize * Balance.RegionSize, "预警覆盖指定区域全部格子");
                await RevTask.Delay(150);
                Capture("01-区域预警");
                Require(VoicePlaying("RegionWave"), "区域声音真实AudioSource正在播放");
                await RevTask.Delay(1440);
                bool moved = false;
                foreach (var pair in origins)
                {
                    moved |= Vector3.Distance(pair.Key.transform.position, pair.Value) > .01f;
                }
                Require(moved, "DOTween逐格起伏真实改变格子显示高度");
                Capture("02-逐格波浪");
                await RevTask.Delay(1200);
                Require(board.ActiveHazardCellCount == 0, "波浪结束没有活跃格子Tween");
                foreach (var pair in origins)
                {
                    Require(Vector3.Distance(pair.Key.transform.position, pair.Value) < .001f, "波浪后格子恢复原位 " + pair.Key.Position);
                }
                Gm("能量巨大");
                await RevTask.Delay(200);
                Capture("03-炸弹落下");
                var active = UnityEngine.Object.FindObjectsOfType<BossSkillFxView>();
                Require(active.Length > 0, "炸弹池化特效已真实加载");
                Sprite sprite = (Sprite)Field(active[0], "energyBomb");
                Require(sprite != null && sprite.packed, "炸弹Sprite来自真实图集");
                var art = (Sprite[])Field(active[0], "extendedSprites");
                Require(art.Length == 9, "九种扩展素材配置齐全");
                foreach (Sprite item in art)
                {
                    Require(item != null && item.packed, "新增Sprite图集绑定 " + (item == null ? "null" : item.name));
                }
                Material material = (Material)Field(active[0], "material");
                Require(material != null && material.shader.isSupported, "真实AB技能Shader支持当前图形设备");
                await RevTask.Delay(1200);
                Capture("04-能量巨大爆炸");
                Require(battle.EnergyBombCount == 1, "实际固定步长已结算一次炸弹");
                Require(VoicePlaying("EnergyHuge"), "能量巨大声音真实AudioSource正在播放");
                bool title = false;
                foreach (Text text in UnityEngine.Object.FindObjectsOfType<Text>())
                {
                    title |= text.text == "能量巨大";
                }
                Require(title, "UI显示能量巨大弹幕");
                var presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
                Require((float)Field(presentation, "_impactAmplitude") > 0, "命中已启动相机震动");
                await RevTask.Delay(700);
                Require((float)Field(presentation, "_impactAmplitude") == 0, "相机震动衰减归零");
                // 每招重新造局，不能复用已受伤/移动过的目标来制造覆盖；原九招与新增九招都走实际状态机。
                foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                {
                    // 此入口保留原18招声音/特效回归；章节专属的状态与视觉由GameBossSkillExpansionPlayChecks逐章验收。
                    if (kind >= BossSkillKind.StarStorm)
                    {
                        continue;
                    }
                    Require(battle.LoadDevelopmentScenario("boss"), "复位技能夹具 " + kind);
                    Gm("Boss清卫");

                    if (kind == BossSkillKind.BlinkBlast)
                    {
                        // 瞬移需要一个合法且能命中玩家的完整身体落点。
                        // 准备期消费实际手牌构造远处目标群，不能绕过选招条件强行报通过。
                        foreach (BoardPos pos in new[] { new BoardPos(0, 0), new BoardPos(1, 0), new BoardPos(1, 1) })
                        {
                            Require(battle.TryPlayCard(battle.Hand[0].Id, pos), "瞬移远端目标落子 " + pos + "：" + battle.LastError);
                        }
                    }

                    battle.Tick(Balance.PrepDelaySeconds);
                    Require(battle.StartBattle(), "技能夹具开战 " + kind);
                    Isolate(battle);

                    if (kind != BossSkillKind.BlinkBlast)
                    {
                        Gm("Boss靠近 1");
                    }
                    if (kind == BossSkillKind.SoulDrain)
                    {
                        Gm("Boss压血");
                    }
                    if (kind == BossSkillKind.CrossLaser)
                    {
                        AlignCrossTarget(battle);
                    }
                    int casts = 0;
                    float firstCastAt = -1f;
                    object owner = new object();
                    RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, info =>
                    {
                        if (info.Kind == kind)
                        {
                            casts++;

                            if (firstCastAt < 0f)
                            {
                                firstCastAt = Time.unscaledTime;
                            }
                        }

                    }, owner: owner);
                    try
                    {
                        Gm("Boss技能 " + (int)kind);
                        await RevTask.Delay(250);
                        Capture("技能-" + ((int)kind).ToString("00") + "-" + kind);
                        bool heard = false, captured = false;
                        for (int i = 0; i < 100 && battle.BossState != BossAIState.Idle; i++)
                        {
                            await RevTask.Delay(50);
                            heard |= VoicePlaying("Boss_" + kind);

                            // 命中最初一帧Shader刚开光，稍后取证才能观察到完整光束，而不是几颗零散粒子。
                            if (casts > 0 && !captured && Time.unscaledTime - firstCastAt >= 0.16f)
                            {
                                Capture("命中-" + ((int)kind).ToString("00") + "-" + kind);
                                captured = true;
                            }
                        }
                        int expected = kind == BossSkillKind.Bombard || kind == BossSkillKind.LaserSpin || kind >= BossSkillKind.MeteorShower ? BossSkillCatalog.Get(kind).Count : 1;
                        Require(casts == expected && battle.BossState == BossAIState.Idle, kind + " 真实命中段数=" + casts + "，收招回到Idle");
                        Require(heard, kind + " 技能声音真实AudioSource曾播放");
                    }
                    finally { RevEvent.RemoveAllByOwner(owner); }
                }
                // 在预警尚未命中时直接退出，检查异步加载/格子Tween/池对象不会在大厅继续出现。
                Isolate(battle);
                Gm("区域波动");
                Gm("能量巨大");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(1600);
                Require(UnityEngine.Object.FindObjectsOfType<BoardCellView>().Length == 0, "退出后无活跃棋盘格子");
                Require(UnityEngine.Object.FindObjectsOfType<BossSkillFxView>().Length == 0, "退出后无Boss/炸弹池对象残留");
                Require(flow.Screen == GameScreen.Home && !flow.IsTransitioning, "清理后回到首页");
                Evidence.Add("框架正常同帧音效去重次数=" + audioDuplicates);
                Require(audioFailures == 0, "本次Play没有声音加载/播放失败");
                Require(unityErrors == 0, "本次Play没有新增Unity Error/Exception");
            }
            catch (Exception error) { failure = error.ToString(); Evidence.Add(failure); }
            finally
            {
                battle.SetPlayerInvincible(false);
                try
                {
                    await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                }
                catch (Exception error) { failure = (failure ?? "") + "\n清理失败：" + error; }
                Application.logMessageReceived -= onLog;
                RevSound.Failed -= onSoundFailed;
                LastResult = failure == null ? "通过 " + _assertions + " 条；18技能/真实AB/区域波浪/炸弹Shader/UI/震屏/退出清理；Unity错误=" + unityErrors : "失败：" + failure;
                File.WriteAllText(Output + "/Play验收.txt", LastResult + "\n" + string.Join("\n", Evidence));
            }
        }
        private static void Capture(string name)
        {
            ScreenCapture.CaptureScreenshot(Output + "/" + name + ".png");
        }
    }
}
