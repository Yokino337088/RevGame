using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>
    /// Agent逐次观察的隔离测试宿主：仅持有时间窗和测量订阅，不替Agent选择落子或目标。
    /// 输入仍由GameGmCommands的正式玩家GM执行；观察期间双方同时停钟，窗口内由BattleLogic正常推进。
    /// </summary>
    public static class GameBalanceAgentSession
    {
        private static readonly object Owner = new object();
        private static Evidence _report;
        private static Round _round;
        private static bool _active, _window;
        private static float _oldScale;
        private static string _folder;
        private static BattleService Battle => (BattleService)RevServices.GetRequired<IBattleService>();
        private static GameFlow Flow => RevServices.GetRequired<GameFlow>();

        /// <summary>只在隔离首页启动，不改存档或血攻；固定种子沿用项目已有测试入口。</summary>
        public static string Begin(int seed)
        {
            if (_active || !Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated
                || Flow.Screen != GameScreen.Home || Flow.IsGameplayBlocked)
            {
                throw new InvalidOperationException("逐次决策测试需要隔离首页，且没有其它运行中的会话");
            }
            EnsureFair();
            Battle.SetBalanceTestSeed(seed);
            _oldScale = Time.timeScale;
            _report = new Evidence { seed = seed, status = "running" };
            _round = null;
            _folder = Path.GetFullPath("../../../.codex/work/balance-revision/agent-" + seed);
            Directory.CreateDirectory(_folder);
            _active = true;
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, OnHit, owner: Owner);
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, OnAttack, owner: Owner);
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelper, owner: Owner);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, OnBoss, owner: Owner);
            Application.logMessageReceived += OnLog;
            EditorApplication.update += Observe;
            Time.timeScale = 0f;
            Save();
            return "started";
        }
        private static void EnsureFair()
        {
            if (Battle.PlayerInvincible || Battle.IsAutomationBoostActive)
            {
                throw new InvalidOperationException("数值测试禁止无敌和开发属性加成");
            }
        }

        /// <summary>接受1～12模拟秒的对等推进；最多4倍同速，真实伤害和AI仍由正式战斗循环产生。</summary>
        public static string Advance(float seconds, float speed)
        {
            if (!_active || _window || seconds < .5f || seconds > 12f || speed < 1f || speed > 4f)
            {
                throw new InvalidOperationException("实测窗口须为0.5～12模拟秒，且上一窗口已结束");
            }
            EnsureFair();
            _window = true;
            AdvanceAsync(seconds, speed);
            return "accepted";
        }
        private static async void AdvanceAsync(float seconds, float speed)
        {
            try
            {
                Time.timeScale = speed;
                float target = Time.time + seconds;
                float deadline = Time.realtimeSinceStartup + 45f;
                while (_active && Application.isPlaying)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        throw new TimeoutException("实测推进窗口超时");
                    }
                    // 演出/过场不在中途冻住；故事仍由Agent的下一句GM决定，不替玩家跳过内容。
                    if (!Flow.IsGameplayBlocked && (Time.time >= target
                        || (Battle.Phase == GamePhase.Settle && RevUI.IsOpen<SettlePanel>())))
                    {
                        break;
                    }
                    if (Flow.IsStoryPlaying && !Flow.IsTransitioning)
                    {
                        break;
                    }
                    await RevTask.Yield();
                }
            }
            catch (Exception error)
            {
                if (_report != null)
                {
                    _report.error = error.ToString();
                    _report.status = "failed";
                }
            }
            finally
            {
                _window = false;

                if (_active && Application.isPlaying)
                {
                    Time.timeScale = 0f;
                    Observe();
                    Save();
                }
            }
        }
        /// <summary>独立只读快照供GM与截图交叉判断；不返回未来随机数、隐藏AI计划或可变模型引用。</summary>
        public static string State()
        {
            if (!_active)
            {
                return "{\"active\":false}";
            }
            EnsureFair();
            var b = Battle;
            var s = new Snapshot
            {
                active = true,
                window = _window,
                chapter = b.Chapter,
                round = b.Round,
                phase = b.Phase.ToString(),
                screen = Flow.Screen.ToString(),
                blocked = Flow.IsGameplayBlocked,
                story = Flow.IsStoryPlaying,
                seconds = b.BattleElapsed,
                cardsRemaining = b.CardsRemaining,
                winner = b.LastWinner?.ToString() ?? "none",
                wallet = b.CombatScore,
                score = b.ChapterScore,
                helperTarget = b.Helper.TargetId,
                helperInterval = b.HelperShotInterval,
                error = _report.error,
                completed = _report.completed,
                helperSpeechFits = UnityEngine.Object.FindObjectOfType<HelperView>()?.SpeechFits == true
            };
            foreach (var card in b.Hand)
            {
                s.cards.Add(card.Id + ":" + card.Kind + ":" + (int)card.Star);
            }
            foreach (Piece p in b.PlayerPieces)
            {
                if (p.IsAlive)
                {
                    s.players.Add(Describe(p));
                }
            }
            foreach (Piece p in b.EnemyPieces)
            {
                if (p.IsAlive)
                {
                    s.enemies.Add(Describe(p));
                }
            }
            foreach (var reward in b.Rewards)
            {
                s.rewards.Add(reward.Kind + ":" + reward.Title);
            }
            return JsonUtility.ToJson(s);
        }
        private static string Describe(Piece p)
        {
            return p.Id + ":" + p.Def.Kind + ":" + (int)p.Def.Star + "@" + p.Pos.X + "," + p.Pos.Y
                + "/" + p.Size + "/" + p.Hp + "/" + p.MaxHp + (p.IsKing ? "/boss" : "")
                + (p.IsEyeGarrison ? "/eye" : "");
        }
        private static void Observe()
        {
            if (!_active)
            {
                return;
            }
            if (!Application.isPlaying)
            {
                End();
                return;
            }
            var b = Battle;

            if (b.Phase == GamePhase.Battle)
            {
                if (_round == null || _round.chapter != b.Chapter || _round.round != b.Round)
                {
                    _round = new Round { chapter = b.Chapter, round = b.Round, boss = b.IsBossRound, minPlayerHpRatio = 1f };
                    foreach (Piece p in b.PlayerPieces)
                    {
                        if (p.IsAlive)
                        {
                            _round.playersStart++;
                            _round.playerHpStart += p.MaxHp;
                        }
                    }
                    foreach (Piece p in b.EnemyPieces)
                    {
                        if (p.IsAlive)
                        {
                            _round.enemyHpStart += p.MaxHp;

                            if (p.IsKing)
                            {
                                _round.bossHpStart = p.MaxHp;
                            }
                        }
                    }
                    _report.rounds.Add(_round);
                }
                int hp = 0, alive = 0;
                foreach (Piece p in b.PlayerPieces)
                {
                    if (p.IsAlive)
                    {
                        hp += p.Hp;
                        alive++;
                    }
                }
                _round.minPlayerHpRatio = Math.Min(_round.minPlayerHpRatio, hp / (float)Math.Max(1, _round.playerHpStart));
                _round.playersEnd = alive;
                _round.playerHpEnd = hp;
                _round.seconds = b.BattleElapsed;
            }
            if (b.Phase == GamePhase.Settle && _round != null && _round.winner == null)
            {
                _round.winner = b.LastWinner?.ToString() ?? "none";
                _round.seconds = b.BattleElapsed;
                _report.completed++;
                Save();
            }
        }

        private static void OnHit(BattleHitInfo hit)
        {
            Observe();

            if (_round == null)
            {
                return;
            }
            if (hit.TargetSide == GameSide.Player)
            {
                _round.damageTaken += hit.Damage;
            }
            else

                if (hit.DamageKind == BattleDamageKind.Helper || hit.DamageKind == BattleDamageKind.HelperBeam)
                {
                    _round.helperDamage += hit.Damage;
                }
                else
                {
                    _round.otherDamage += hit.Damage;
                }
        }
        private static void OnAttack(PieceAttackInfo info)
        {
            Observe();

            if (_round == null)
            {
                return;
            }
            if (info.Side == GameSide.Player)
            {
                _round.playerAttacks++;
            }
            else
            {
                _round.enemyAttacks++;
            }
        }
        private static void OnHelper(HelperShotInfo info)
        {
            if (_round == null)
            {
                return;
            }
            if (info.Beam)
            {
                _round.beamTicks++;
            }
            else
            {
                _round.barrages++;
                _round.projectiles += info.Projectiles;
            }
        }
        private static void OnBoss(BossSkillInfo info)
        {
            if (_round != null)
            {
                _round.bossCasts++;
            }
        }
        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception)
            {
                _report.unityErrors++;
                _report.error = message;
            }
        }

        /// <summary>显式收尾也用于退出Play的兜底；恢复时间和订阅，即使中途失败也保存原始结果。</summary>
        public static string End()
        {
            if (!_active)
            {
                return "inactive";
            }
            _active = false;
            _window = false;
            EditorApplication.update -= Observe;
            Application.logMessageReceived -= OnLog;
            RevEvent.RemoveAllByOwner(Owner);
            Time.timeScale = _oldScale;

            if (_report.status != "failed")
            {
                _report.status = "ended";
            }
            Save();
            return _folder;
        }
        private static void Save()
        {
            File.WriteAllText(Path.Combine(_folder, "result.json"), JsonUtility.ToJson(_report, true));
        }
        [Serializable]
        private sealed class Snapshot
        {
            public bool active, window, blocked, story, helperSpeechFits;
            public int chapter, round, cardsRemaining, helperTarget, completed;
            public long wallet, score;
            public float seconds, helperInterval;
            public string phase, screen, winner, error;
            public List<string> cards = new List<string>(), players = new List<string>(), enemies = new List<string>(), rewards = new List<string>();
        }
        [Serializable]
        private sealed class Evidence
        {
            public string status, error;
            public int seed, completed, unityErrors;
            public List<Round> rounds = new List<Round>();
        }
        [Serializable]
        private sealed class Round
        {
            public int chapter, round, playersStart, playersEnd, playerHpStart, playerHpEnd, enemyHpStart, bossHpStart;
            public int damageTaken, helperDamage, otherDamage, playerAttacks, enemyAttacks, bossCasts, barrages, projectiles, beamTicks;
            public float seconds, minPlayerHpRatio;
            public bool boss;
            public string winner;
        }
    }
}
