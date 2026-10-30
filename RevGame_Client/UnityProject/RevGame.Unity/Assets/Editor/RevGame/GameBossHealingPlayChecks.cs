// 真实AB图形Play验收多人Boss风暴与治疗；高生命只便于观测，不用于正常难度结论。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Battle;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
namespace RevGame.Editor
{
    public static class GameBossHealingPlayChecks
    {
        [Serializable]
        private sealed class Result
        {
            public string status = "idle", error, folder;
            public int chapter, errors, samples, frames;
            public bool cleaned, sawStorm, sawHealing, sawSwitch;
            public List<string> assertions = new List<string>();
        }
        private static Result _result = new Result();
        private static bool _cancel;
        public static string State()
        {
            return JsonUtility.ToJson(_result);
        }
        public static string Cancel()
        {
            _cancel = true;
            return "requested";
        }
        public static string Begin(int chapter)
        {
            var flow = RevServices.GetRequired<GameFlow>();

            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated || flow.Screen != GameScreen.Home || _result.status == "running")
            {
                throw new InvalidOperationException("需要隔离首页，无运行中的检查");
            }
            _cancel = false;
            _result = new Result { status = "running", chapter = chapter, folder = Path.GetFullPath("../../../.codex/work/boss-healing-2026-10-29/play-ch" + chapter + "-" + DateTime.Now.ToString("HHmmss")) };
            Directory.CreateDirectory(_result.folder);
            Run(chapter);
            return State();
        }
        private static void Check(bool ok, string text)
        {
            if (!ok)
            {
                throw new InvalidOperationException(text);
            }
            _result.assertions.Add(text);
        }
        private static async RevTask Wait(Func<bool> ready, string reason)
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (!ready())
            {
                if (_cancel || !Application.isPlaying || Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException(reason);
                }
                var flow = RevServices.GetRequired<GameFlow>();

                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                await RevTask.Yield();
            }
        }
        private static async void Run(int chapter)
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    _result.errors++;
                    _result.error = message;
                }
            };
            Application.logMessageReceived += logger;
            try
            {
                Time.timeScale = 1f;
                Check(RevGM.Execute("RevGame/进入Boss关卡 " + chapter).Success, "正式进入Boss关卡");
                await Wait(() => !flow.IsGameplayBlocked && !flow.IsTransitioning && battle.Phase == GamePhase.Prepare, "准备交割超时");
                Check(battle.PrepareBossHealingPreview(chapter), "高生命隔离多人输入");
                await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperVisualReady == true && !flow.IsGameplayBlocked && battle.PreparationGranted, "视图与资源未就绪");
                Check(RevGM.Execute("RevGame/开始战斗").Success, "正式开始战斗");
                await Wait(() => battle.HelperStormActive && battle.Helper.State == "Storm", "Boss多人风暴没有自动开启");
                Check(battle.PlayerPieces.Count > 3 && battle.BossRegisteredStates == 14, "多人风暴且机器只注册14状态");
                GameAudio audio = UnityEngine.Object.FindObjectOfType<GameAudio>();
                Check(audio != null && audio.BossAudioChapter == chapter && audio.RegisteredBossAudioCount == 11, "只登记本章十技能和狂暴音效");
                BossSkillKind tested = BossSkillKind.Enrage;
                foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                {
                    if (!BossSkillCatalog.IsExpanded(kind) || !BossSkillCatalog.IsUnlocked(kind, chapter))
                    {
                        continue;
                    }
                    if (battle.ForceBossSkill(kind))
                    {
                        tested = kind;
                        break;
                    }
                }
                Check(tested != BossSkillKind.Enrage, "正式FSM接受本章技能");
                string clipName = GameAudio.GetBossSkillSoundName(tested);
                await Wait(() => Heard(clipName), "本章独立AB音效未实际播放：" + clipName);
                Check(true, "本章分包音效实际AudioSource播放：" + clipName);
                foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                {
                    if (BossSkillCatalog.IsUnlocked(kind, chapter))
                    {
                        continue;
                    }
                    string resource = GameAudio.GetBossSkillSoundName(kind);
                    string soundRoot = "Audio/Sfx/Boss/Chapter" + BossSkillCatalog.ThemeChapter(kind).ToString("00");
                    Check(RevResManager.GetRefCount(soundRoot, resource) == 0, "不持有其它章音频引用：" + kind);
                }
                Check(GameTextConfig.DisplayDamage(7) == 70L && GameTextConfig.DisplayDamage(int.MaxValue) == 21474836470L, "伤害格式七变七十且大伤害乘十不溢出");
                var number = RevPool.Get<BattleEffectView>();
                Check(number != null, "借出真实伤害飘字预制体");
                try
                {
                    number.ShowDamage(Vector3.zero, 7, BattleDamageKind.Normal, null, GameSide.Player);
                    Check(number.TextContent.Contains("70"), "真实TextMesh飘字显示70");
                }
                finally { RevPool.Return(number, 0); }
                int first = battle.PlayerPieces[0].Id, second = battle.PlayerPieces[1].Id;
                Check(battle.WoundBossHealingPreview(first) > 0 && battle.WoundBossHealingPreview(second) > 0, "统一管线制造两个真实远程伤口");
                await Wait(() => UnityEngine.Object.FindObjectOfType<BoardView>()?.HelperHealingVisible == true, "治疗线未出现");
                int initial = battle.HelperProtectionTargetId;
                float until = Time.realtimeSinceStartup + 7f, nextCapture = 0f;
                while (Time.realtimeSinceStartup < until)
                {
                    if (_cancel || !Application.isPlaying)
                    {
                        throw new InvalidOperationException("取消或Play退出");
                    }
                    _result.sawStorm |= battle.HelperStormActive && battle.Helper.State == "Storm";
                    var healing = UnityEngine.Object.FindObjectOfType<HelperHealingView>();

                    if (healing != null && healing.IsConnected)
                    {
                        _result.sawHealing = true;
                        _result.samples++;
                        _result.sawSwitch |= healing.TargetId != initial;
                        var line = healing.GetComponent<LineRenderer>();
                        Check(line.sortingOrder == 7 && line.sharedMaterial.renderQueue < 3010, "治疗线实际队列与排序在棋子下面");
                        Check(healing.OriginError < .03f && healing.TargetError < .03f, "治疗线实时跟随两端");
                    }
                    if (Time.realtimeSinceStartup >= nextCapture && _result.frames < 20)
                    {
                        ScreenCapture.CaptureScreenshot(Path.Combine(_result.folder, "healing-" + _result.frames.ToString("00") + ".png"));
                        _result.frames++;
                        nextCapture = Time.realtimeSinceStartup + .12f;
                    }
                    await RevTask.Yield();
                }
                Check(_result.sawStorm && _result.sawHealing && _result.sawSwitch, "真实多人风暴、治疗、满血换下一目标均出现");
                Check(battle.HelperProtectionHealing > 0, "服务记录实际治疗量");
                Check(flow.ReturnHome(), "正式回首页接受");
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "返回首页超时");
                Check(UnityEngine.Object.FindObjectsOfType<HelperHealingView>().Length == 0 && battle.HelperProtectionTargetId == 0, "治疗线回池与身份归零");
                foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                {
                    string soundRoot = kind == BossSkillKind.Enrage ? "Audio/Sfx/Boss/Common" : "Audio/Sfx/Boss/Chapter" + BossSkillCatalog.ThemeChapter(kind).ToString("00");
                    Check(RevResManager.GetRefCount(soundRoot, GameAudio.GetBossSkillSoundName(kind)) == 0, "退出后所有章节音频引用归零：" + kind);
                }
                Check(_result.errors == 0, "本次新增Unity错误为零");
                _result.status = "passed";
            }
            catch (Exception e) { _result.status = "failed"; _result.error = e.ToString(); }
            finally
            {
                try
                {
                    if (flow.Screen != GameScreen.Home)
                    {
                        flow.ReturnHome();
                        await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "收尾超时");
                    }
                    _result.cleaned = flow.Screen == GameScreen.Home && !flow.IsTransitioning;
                }
                catch (Exception e) { _result.status = "failed"; _result.error += "\n" + e; }
                Time.timeScale = oldScale;
                Application.logMessageReceived -= logger;
                File.WriteAllText(Path.Combine(_result.folder, "result.json"), JsonUtility.ToJson(_result, true));
            }
        }
        /// <summary>只观察真实声源，不以播放请求返回成功替代Clip加载和播放。</summary>
        private static bool Heard(string name)
        {
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (source.isPlaying && source.clip != null && source.clip.name == name)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
