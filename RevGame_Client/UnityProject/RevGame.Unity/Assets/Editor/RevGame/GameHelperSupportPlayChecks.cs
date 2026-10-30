using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>
    /// 图形Play中的帮手保护回归：使用高生命隔离输入让画面可持续观察，触发仍走真实GM/Service。
    /// 不据此推断正常胜率、Boss时长或手机触觉；结论只记录本轮真正看到的模型、对象和AudioSource。
    /// </summary>
    public static class GameHelperSupportPlayChecks
    {
        [Serializable]
        private sealed class Evidence
        {
            public string status = "idle", stage, error, directory;
            public int chapter, unityErrors, missileFlights, jellyFlights, missileImpacts, jellyImpacts, mushroomSamples;
            public int healingSamples, movingHealingSamples, capturedFrames;
            public bool cleanupComplete;
            public float maxOriginError, maxTargetError;
            public long healingDelta;
            public int hpBefore, hpAfter, injectedDamage;
            public float healingSeconds;
            public List<string> assertions = new List<string>(), audioPlayed = new List<string>();
        }
        private static Evidence _result = new Evidence();
        private static readonly HashSet<string> AudioObserved = new HashSet<string>();
        private static readonly HashSet<long> MissileFlights = new HashSet<long>(), JellyFlights = new HashSet<long>();
        private static readonly HashSet<long> MissileImpacts = new HashSet<long>(), JellyImpacts = new HashSet<long>();
        private static string _folder;
        private static Vector3 _previousHelper, _previousTarget;
        private static bool _hasPreviousHealing;
        private static float _nextCapture;
        private static int _captureRemaining, _captureIndex;
        private static string _captureTitle;

        public static string State()
        {
            return JsonUtility.ToJson(_result);
        }
        public static string Begin(int chapter = 1)
        {
            if (!Application.isPlaying || UnityEditor.EditorApplication.isPaused || chapter < 1 || chapter > 6
                || !RevServices.GetRequired<StoryProgress>().IsIsolated || _result.status == "running")
            {
                throw new InvalidOperationException("帮手保护检查要求未暂停的隔离Play，章节1～6，且无本检查运行中");
            }
            GameFlow flow = RevServices.GetRequired<GameFlow>();

            if (flow.Screen != GameScreen.Home || flow.IsGameplayBlocked)
            {
                throw new InvalidOperationException("请先等隔离首页完全就绪");
            }
            _folder = Path.GetFullPath("../../../.codex/work/boss-helper-2026-10-29/helper-support-ch" + chapter + "-" + DateTime.Now.ToString("HHmmssfff"));
            Directory.CreateDirectory(_folder);
            _result = new Evidence { status = "running", stage = "进入准备", chapter = chapter, directory = _folder };
            AudioObserved.Clear();
            MissileFlights.Clear();
            JellyFlights.Clear();
            MissileImpacts.Clear();
            JellyImpacts.Clear();
            _hasPreviousHealing = false;
            _captureRemaining = 0;
            Run(chapter);
            return State();
        }
        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            _result.assertions.Add(message);
        }
        private static async RevTask Wait(Func<bool> condition, string message, float seconds = 35f)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (!Application.isPlaying || Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException(message);
                }
                GameFlow flow = RevServices.GetRequired<GameFlow>();

                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                await RevTask.Yield();
            }
        }
        private static async RevTask Observe(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (!Application.isPlaying)
                {
                    throw new InvalidOperationException("检查中途退出Play");
                }
                await RevTask.Yield();
            }
        }
        /// <summary>LateUpdate采样在两端Tween和治疗线刷新后执行；不把正常的一帧刷新顺序误报成端点漂移。</summary>
        private static void Sample()
        {
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (source.isPlaying && source.clip != null && source.clip.name.StartsWith("Helper", StringComparison.Ordinal))
                {
                    AudioObserved.Add(source.clip.name);
                }
            }
            foreach (HelperAttackView attack in UnityEngine.Object.FindObjectsOfType<HelperAttackView>())
            {
                HelperStormVisual visual = attack.StormVisual;

                if (!attack.IsStorm || attack.IsBeam || visual == null)
                {
                    continue;
                }
                long id = ((long)attack.GetInstanceID() << 32) | (uint)attack.PlaySerial;

                if (visual.ProjectileVisible && visual.Visible)
                {
                    if (visual.Weapon == HelperWeapon.Missile)
                    {
                        MissileFlights.Add(id);
                        _result.missileFlights++;
                    }
                    if (visual.Weapon == HelperWeapon.Jellyfish)
                    {
                        JellyFlights.Add(id);
                        _result.jellyFlights++;
                    }
                }

                if (visual.IsExplosion && visual.Visible)
                {
                    if (visual.ProjectileVisible)
                    {
                        _result.error = "爆炸阶段仍显示飞行弹体";
                    }
                    if (visual.Weapon == HelperWeapon.Missile && MissileFlights.Contains(id))
                    {
                        MissileImpacts.Add(id);
                        _result.missileImpacts++;
                    }
                    if (visual.Weapon == HelperWeapon.Jellyfish && JellyFlights.Contains(id))
                    {
                        JellyImpacts.Add(id);
                        _result.jellyImpacts++;
                    }
                    if (visual.MushroomOnCamera)
                    {
                        _result.mushroomSamples++;
                    }
                }
            }
            HelperHealingView healing = UnityEngine.Object.FindObjectOfType<HelperHealingView>();
            HelperView helper = UnityEngine.Object.FindObjectOfType<HelperView>();

            if (healing != null && healing.IsConnected && helper != null)
            {
                PieceView target = FindPieceView(healing.TargetId);
                _result.healingSamples++;
                _result.maxOriginError = Math.Max(_result.maxOriginError, healing.OriginError);
                _result.maxTargetError = Math.Max(_result.maxTargetError, healing.TargetError);

                if (target != null)
                {
                    if (_hasPreviousHealing && (Vector3.Distance(helper.transform.position, _previousHelper) > .0001f
                        || Vector3.Distance(target.transform.position, _previousTarget) > .0001f))
                    {
                        _result.movingHealingSamples++;
                    }
                    _previousHelper = helper.transform.position;
                    _previousTarget = target.transform.position;
                    _hasPreviousHealing = true;
                }
            }

            if (_captureRemaining > 0 && Time.realtimeSinceStartup >= _nextCapture)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(_folder, _captureTitle + "-" + (_captureIndex++).ToString("00") + ".png"));
                _captureRemaining--;
                _result.capturedFrames++;
                _nextCapture = Time.realtimeSinceStartup + .12f;
            }
        }

        private static PieceView FindPieceView(int id)
        {
            foreach (PieceView view in UnityEngine.Object.FindObjectsOfType<PieceView>())
            {
                if (view.PieceId == id)
                {
                    return view;
                }
            }
            return null;
        }
        private static void CaptureBurst(string title, int frames = 16)
        {
            _captureTitle = title;
            _captureIndex = 0;
            _captureRemaining = frames;
            _nextCapture = 0;
        }
        private static void Gm(string command, string assertion)
        {
            RevGMResult result = RevGM.Execute(command);
            Check(result.Success, assertion + "：" + result.Message);
        }
        private static int AlivePlayers(BattleService battle)
        {
            int count = 0;
            foreach (Piece player in battle.PlayerPieces)
            {
                if (player.IsAlive && battle.Board.FindPiece(player.Id) == player)
                {
                    count++;
                }
            }
            return count;
        }
        private static async void Run(int chapter)
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            object owner = new object();
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    _result.unityErrors++;
                    _result.error = message;
                }
            };
            Application.logMessageReceived += logger;
            RevMono.AddLateUpdate(Sample, owner: owner);
            try
            {
                Time.timeScale = 1f;
                battle.SetBalanceTestSeed(102900 + chapter);
                battle.UnlockAllChaptersForDevelopment();
                flow.OpenLevels();
                Check(flow.EnterBattle(chapter), "正式GameFlow接受进入章节" + chapter);
                await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "过场未交付准备界面");
                Check(battle.LoadHelperPreview(chapter, false), "创建高生命视觉输入，与正常Boss时长统计分开");
                await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperVisualReady == true, "帮手图集/预制体/材质未就绪");
                while (AlivePlayers(battle) > 3)
                {
                    Gm("RevGame/移除棋子 " + battle.PlayerPieces[battle.PlayerPieces.Count - 1].Id, "准备期通过GM真实撤棋");
                }
                battle.SetHelperPreviewTier(1);
                Gm("RevGame/开始战斗", "GM经正式BattleLogic开始战斗");
                _result.stage = "普通攻击声音";
                await Wait(() => battle.Phase == GamePhase.Battle, "战斗阶段未开始");
                await Wait(() => AudioObserved.Contains("HelperBarrage"), "没有观察到普通帮手攻击AudioSource实播", 8f);
                Check(battle.HelperProtectionTargetId == 0, "三棋阶段没有错误的治疗保护");
                CaptureBurst("normal");
                await Observe(2f);

                _result.stage = "单棋回血和绿色射线";
                Gm("RevGame/移除棋子 " + battle.PlayerPieces[battle.PlayerPieces.Count - 1].Id, "战斗期撤成两棋");
                Check(battle.HelperProtectionTargetId == 0, "两棋阶段不触发单棋保护");
                Gm("RevGame/移除棋子 " + battle.PlayerPieces[battle.PlayerPieces.Count - 1].Id, "战斗期撤成最后一棋");
                Piece last = battle.PlayerPieces[0];
                Check(last.IsAlive && battle.HelperProtectionTargetId == last.Id, "服务实时绑定最后一颗活棋Id");
                _result.injectedDamage = battle.WoundHelperSupportPreview(last.Id, .35f);
                Check(_result.injectedDamage > 0 && last.IsAlive, "通过统一伤害管线产生真实伤口且保留存活");
                _result.hpBefore = last.Hp;
                long healingBefore = battle.HelperProtectionHealing;
                float simulationBefore = battle.BattleElapsed;
                await Wait(() => battle.HelperProtectionHealing > healingBefore && last.Hp > _result.hpBefore, "单棋保护没有自然恢复实际生命", 3f);
                _result.hpAfter = last.Hp;
                _result.healingDelta = battle.HelperProtectionHealing - healingBefore;
                _result.healingSeconds = battle.BattleElapsed - simulationBefore;
                long expectedCeiling = (long)Math.Ceiling(last.MaxHp * (double)Balance.HelperProtectionHealPerSecond * _result.healingSeconds) + 1;
                Check(_result.healingDelta > 0 && _result.healingDelta <= expectedCeiling, "实际治疗为正且不超过30%每模拟秒上限");
                await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperHealingTargetId == last.Id
                    && UnityEngine.Object.FindObjectOfType<BoardView>().HelperHealingVisible, "治疗射线没有绑定真实目标");
                Check(MoveLastPlayer(battle, last), "正式选棋/换位让治疗目标移动");
                CaptureBurst("healing");
                await Observe(2.2f);
                Check(_result.healingSamples > 0 && _result.movingHealingSamples > 0, "真实移动过程中持续出现治疗连线");
                Check(_result.maxOriginError < .03f && _result.maxTargetError < .03f, "治疗两端紧跟帮手和棋子，误差小于0.03世界单位");
                Check(AudioObserved.Contains("HelperHeal"), "观察到治疗音效AudioSource实播");

                _result.stage = "援军撤销保护";
                int beforeCount = AlivePlayers(battle);
                Check(battle.ReinforceHelperSupportPreview(), "正式援军选牌与部署动作成功");
                Check(AlivePlayers(battle) == beforeCount + 1 && battle.HelperProtectionTargetId == 0, "补入第二棋后保护立即撤销");
                long healedAtCancel = battle.HelperProtectionHealing;
                await Wait(() => !UnityEngine.Object.FindObjectOfType<BoardView>().HelperHealingVisible
                    && UnityEngine.Object.FindObjectOfType<HelperHealingView>() == null, "第二棋出现后治疗射线没有回池");
                await Observe(.7f);
                Check(battle.HelperProtectionHealing == healedAtCancel, "两棋阶段没有继续累计保护治疗");
                CaptureBurst("protection-cancelled", 8);

                _result.stage = "导弹水母命中与声音";
                CaptureBurst("storm");
                await Wait(() => MissileImpacts.Count > 0 && JellyImpacts.Count > 0 && _result.mushroomSamples > 0
                    && AudioObserved.Contains("HelperMissile") && AudioObserved.Contains("HelperJellyfish")
                    && AudioObserved.Contains("HelperExplosion") && AudioObserved.Contains("HelperJellyImpact") && AudioObserved.Contains("HelperBeam"),
                    "导弹/水母爆炸、蘑菇云或对应AudioSource未完整出现", 12f);
                Check(MissileFlights.Count > 0 && MissileImpacts.Count > 0, "真实导弹从飞行切入命中爆炸并隐藏弹体");
                Check(JellyFlights.Count > 0 && JellyImpacts.Count > 0, "真实水母从飞行切入命中爆炸并隐藏弹体");
                Check(_result.mushroomSamples > 0, "实际相机内出现导弹蘑菇云");
                Check(string.IsNullOrEmpty(_result.error), "未出现爆炸后弹体残留或Unity异常");
                await Observe(2.2f);

                _result.stage = "退出与重入";
                Check(flow.ReturnHome(), "GameFlow接受返回首页");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "首页交接未完成");
                Check(UnityEngine.Object.FindObjectOfType<HelperView>() == null && UnityEngine.Object.FindObjectOfType<HelperHealingView>() == null
                    && UnityEngine.Object.FindObjectsOfType<HelperAttackView>().Length == 0, "退场回收帮手、治疗与全部攻击池实例");
                Check(battle.HelperProtectionTargetId == 0, "退出后单棋保护Id归零");
                flow.OpenLevels();
                Check(flow.EnterBattle(chapter), "重新进入章节被接受");
                await Wait(() => !flow.IsGameplayBlocked && battle.Phase == GamePhase.Prepare, "重入准备交付失败");
                Check(!UnityEngine.Object.FindObjectOfType<BoardView>().HelperHealingVisible && battle.HelperProtectionTargetId == 0,
                    "重入准备没有上局残留治疗线");
                Check(flow.ReturnHome(), "重入检查后再次返回首页");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "重入后的收尾未完成");
                Check(_result.unityErrors == 0, "本次新增Unity Error/Exception为零");
            }
            catch (Exception error)
            {
                _result.error = error.ToString();

                if (Application.isPlaying)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(_folder, "failure.png"));
                }
            }
            finally
            {
                _captureRemaining = 0;
                Time.timeScale = oldScale;
                try
                {
                    if (Application.isPlaying && flow.Screen != GameScreen.Home)
                    {
                        if (flow.IsTransitioning)
                        {
                            await Wait(() => !flow.IsTransitioning, "清理等待过场超时");
                        }
                        if (flow.Screen != GameScreen.Home)
                        {
                            flow.ReturnHome();
                        }
                        await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "失败后的返回清理未完成");
                    }
                }
                catch (Exception cleanup) { _result.error += "\n收尾失败：" + cleanup; }
                RevMono.RemoveAllOf(owner);
                Application.logMessageReceived -= logger;
                _result.cleanupComplete = Application.isPlaying && flow.Screen == GameScreen.Home && !flow.IsTransitioning;
                _result.status = string.IsNullOrEmpty(_result.error) && _result.cleanupComplete ? "passed" : "failed";
                _result.audioPlayed = new List<string>(AudioObserved);
                _result.audioPlayed.Sort(StringComparer.Ordinal);
                File.WriteAllText(Path.Combine(_folder, "result.json"), JsonUtility.ToJson(_result, true));
            }
        }
        /// <summary>使用玩家同一选棋/空格入口制造可见移动，不直接改Transform或Board坐标。</summary>
        private static bool MoveLastPlayer(BattleService battle, Piece last)
        {
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var target = new BoardPos(x, y);

                    if (target == last.Pos || !battle.Board.CanFit(target, last.Size, GameSide.Player, respectEnemyEyes: false))
                    {
                        continue;
                    }
                    if (battle.SelectedPieceId != last.Id && !battle.HandleBoardClick(last.Pos))
                    {
                        continue;
                    }
                    if (battle.HandleBoardClick(target))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
