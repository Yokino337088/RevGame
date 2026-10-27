// 所有RevGame GM的唯一文件与注册入口。Bootstrap只调用RegisterAll/UnregisterAll。
// 玩家操作复用正式业务/真实指针，诊断造局明确标注；正式发布不编译这些入口。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Animation;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using System.Linq;
using System.Text;
using System;
using UnityEngine;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        public static void RegisterAll()
        {
            // 同一宿主重复装配幂等；失败时回滚本文件已注册的命令，不污染其他模块。
            if (Registered.Count > 0) { return; }
            try
            {
            GameCombatGmCommands.RegisterGroup();
            RevGame.Animation.GameAttributeGmCommands.RegisterGroup();
            RevGame.Animation.GameAutomationCommands.RegisterGroup();
            RegisterPlayerActions();
            RegisterBossChapterCommands();
            RegisterCommand("RevGame/声音设置", "打开玩家声音设置面板，accepted表示已接受异步加载", args =>
            {
                if (!RevServices.GetRequired<GameFlow>().OpenAudioSettings()) { throw new RevGMUsageException(RevServices.GetRequired<GameFlow>().LastError); }
                return "accepted";
            });
            RegisterCommand("RevGame/BGM音量", "设置玩家BGM音量百分比0～100；保存并立即生效", args =>
            {
                int percent = args.Int(0, 60);
                if (percent < 0 || percent > 100) { throw new RevGMUsageException("音量范围0～100"); }
                var settings = RevServices.GetRequired<GameAudioSettings>();
                settings.SetVolume(percent / 100f);
                settings.Save();
                return "volume=" + percent;
            });
            RegisterCommand("RevGame/声音查询", "读取玩家BGM音量、静音与设置面板状态", args =>
            {
                var settings = RevServices.GetRequired<GameAudioSettings>();
                return "volume=" + Mathf.RoundToInt(settings.Volume * 100f) + ",muted=" + settings.Muted
                    + ",panel=" + RevServices.GetRequired<GameFlow>().IsAudioSettingsVisible;
            });
            RegisterCommand("RevGame/播放初始剧情", "重播开场剧情；保留首次进度，不自动进入教学，accepted仅表示请求已接受", args =>
            {
                var flow = RevServices.GetRequired<GameFlow>();
                if (!flow.ReplayOpeningStory()) { throw new RevGMUsageException(flow.LastError); }
                return "accepted";
            });
            RegisterCommand("RevGame/解锁六章关卡", "解锁本次运行全部六章；不发通关奖励，退出游戏后不保存", args =>
            {
                int unlocked = ((BattleService)RevServices.GetRequired<IBattleService>()).UnlockAllChaptersForDevelopment();
                // 已打开的选关界面不会自动观察服务字段，主动刷新才能立即让章节按钮可点击。
                if (RevUI.IsOpen<LevelSelectPanel>()) { RevUI.Get<LevelSelectPanel>().RefreshView(); }
                return "unlocked=" + unlocked;
            });
            RegisterCommand("RevGame/新手教学", "重播真实棋盘教学：替换当前战役，保留首次进度；返回accepted后等待剧情/场景就绪", args =>
            {
                var flow = RevServices.GetRequired<GameFlow>();
                if (!flow.ReplayTutorial()) { throw new RevGMUsageException(flow.LastError); }
                return "accepted";
            });

            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/敌方AI", "只读重量级状态机当前状态、状态时长和最近切换原因", args =>
            {
                Logic();
                return ((BattleService)RevServices.GetRequired<IBattleService>()).EnemyAIStatus();
            });
            // 仅开发验收：让 Boss 立刻施放指定技能，用来逐个检查特效与伤害。
            // 仍会检查“这一招此刻有没有合法目标”，打不到人时返回失败而不是空放；正式战斗由 Boss 自己挑技能。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss技能", "Boss 回合战斗中立刻施放技能：0横扫 1狙击 2轰炸 3弹幕 4瞬移 5激光 6汲取 7召唤 8狂暴 9流星 10十字 11冰环 12雷链 13毒沼 14火墙 15冰刺 16引力 17烙印", args =>
            {
                Logic();
                int index = args.Int(0, 0);
                if (index < 0 || index >= System.Enum.GetValues(typeof(Core.BossSkillKind)).Length)
                {
                    throw new RevGMUsageException("技能编号超出范围");
                }
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (!battle.IsBossRound || battle.Phase != Core.GamePhase.Battle)
                {
                    throw new RevGMUsageException("只有 Boss 回合的战斗阶段才能施放 Boss 技能");
                }
                var kind = (Core.BossSkillKind)index;
                if (!battle.ForceBossSkill(kind))
                {
                    throw new RevGMUsageException(kind + " 当前没有合法目标");
                }
                // 返回固定字符串，自动化脚本才能用 assert 精确比较（expected 是精确相等，不是包含）。
                return "ok";
            }, RevGMArg.Int("技能", 0));
            // 自动化专用：幂等地把游戏推进到“Boss 回合已开战”。
            // 流程：主界面 → 选关 → 进入战斗域（异步，返回 loading）→ 已到战斗屏后加载 boss 夹具并开战（返回 ready）。
            // 场景脚本用 until 轮询本条，直到返回 ready，避免用 wait 猜加载时间。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss准备", "自动化：进入战斗域并直接开始 Boss 回合；loading=加载中，ready=已开战", args =>
            {
                int chapter = args.Int(0, 1);
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                // 过场没结束前绝不能开战：此时准备面板还没打开，提前切到战斗会让
                // GamePresentation.WaitUntilReadyAsync 永远等不到 PreparePanel，过场最终超时。
                if (flow.IsTransitioning)
                {
                    return "loading";
                }
                if (flow.Screen != GameScreen.Battle)
                {
                    if (flow.Screen == GameScreen.Home && !flow.OpenLevels())
                    {
                        throw new RevGMUsageException(flow.LastError);
                    }
                    if (!flow.EnterBattle(chapter))
                    {
                        throw new RevGMUsageException(flow.LastError);
                    }
                    return "loading";
                }
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (battle.Phase == Core.GamePhase.Battle && battle.IsBossRound)
                {
                    return "ready";
                }
                if (battle.Phase == Core.GamePhase.Prepare && battle.IsBossRound)
                {
                    // 夹具已加载：补满准备 3 秒发放增益，然后开战。
                    battle.Tick(Balance.PrepDelaySeconds);
                    if (!battle.StartBattle())
                    {
                        throw new RevGMUsageException(battle.LastError);
                    }
                    return "ready";
                }
                // 首次进入：加载固定的 boss 夹具（第 11 回合、含一颗我方大棋），再发增益、开战。
                if (!battle.LoadDevelopmentScenario("boss"))
                {
                    throw new RevGMUsageException(battle.LastError);
                }
                battle.Tick(Balance.PrepDelaySeconds);
                if (!battle.StartBattle())
                {
                    throw new RevGMUsageException(battle.LastError);
                }
                return "ready";
            }, RevGMArg.Int("章节", 1));
            // 自动化查询：把 Boss 状态折叠成稳定字符串，供 until/assert 精确比较。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss查询", "自动化：Boss 状态 idle / casting / dead / stopped / none", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                BossAIState? state = battle.IsBossRound ? battle.BossState : null;
                if (!state.HasValue)
                {
                    return "none";
                }
                if (state.Value == BossAIState.Idle)
                {
                    return "idle";
                }
                if (state.Value == BossAIState.Dead)
                {
                    return "dead";
                }
                if (state.Value == BossAIState.Stopped)
                {
                    return "stopped";
                }
                return "casting";
            });
            // 自动化准备：把我方一颗棋子挪到 Boss 身边，保证每个技能都有合法目标。
            // 只移动真实棋子（走 BoardModel.Move 与真实占格预检），不扣血、不改属性。
            // 自动化专用：清掉 Boss 护卫，给贴身技能腾位置。只用于验收，不改变规则。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss清卫", "自动化：移除 Boss 以外的敌方棋子，给贴身技能腾出落点", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (!battle.IsBossRound)
                {
                    throw new RevGMUsageException("当前不是 Boss 回合");
                }
                return "ok:" + battle.ClearBossGuards();
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss靠近", "自动化：把我方一颗存活棋子移到离 Boss 指定距离的空格（1=贴身，2=隔一格）", args =>
            {
                int distance = args.Int(0, 1);
                if (distance < 1 || distance > 4)
                {
                    throw new RevGMUsageException("距离只允许 1~4");
                }
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                Piece boss = battle.EnemyPieces.FirstOrDefault(p => p.IsAlive && p.IsKing);
                // 优先取最小的一颗：1×1 棋子才可能贴到 Boss 身边，2×2 大棋在贴身格常常放不下。
                Piece player = battle.PlayerPieces.Where(p => p.IsAlive).OrderBy(p => p.Size).FirstOrDefault();
                if (boss == null || player == null)
                {
                    throw new RevGMUsageException("缺少 Boss 或我方棋子");
                }
                // 按扫描顺序找第一个“与 Boss 身体间隔恰好为 distance”的可放置格。
                // 为什么需要距离：横扫要求贴脸（间隔 1），狙击/轰炸要求至少隔一格（间隔 2），
                // 只用一个固定距离会让另一类技能永远没有合法目标。
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var cell = new BoardPos(x, y);
                        int gap = System.Math.Max(
                            System.Math.Max(0, System.Math.Max(x - (boss.Pos.X + boss.Size - 1), boss.Pos.X - x)),
                            System.Math.Max(0, System.Math.Max(y - (boss.Pos.Y + boss.Size - 1), boss.Pos.Y - y)));
                        if (gap != distance || !cell.IsInside(Consts.BoardSize))
                        {
                            continue;
                        }
                        if (!battle.Board.CanFit(cell, player.Size, GameSide.Player, player, respectEnemyEyes: false))
                        {
                            continue;
                        }
                        if (battle.Board.Move(player, cell, respectEnemyEyes: false))
                        {
                            return "ok";
                        }
                    }
                }
                throw new RevGMUsageException("Boss 周围 " + distance + " 格外没有可放置的空格");
            }, RevGMArg.Int("距离", 1));
            // 自动化续航：把双方都回满血，保证连续验收 9 个技能时不会因为一方提前阵亡而中断。
            // 只调用真实的 Heal 与血条事件，不修改最大生命、攻击或任何其它数值。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss续航", "自动化：回满 Boss 与我方全部棋子，保证多技能连续验收", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                foreach (Piece piece in battle.PlayerPieces.Concat(battle.EnemyPieces))
                {
                    if (piece.IsAlive && piece.Heal(piece.MaxHp) > 0)
                    {
                        RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                    }
                }
                return "ok";
            });
            // 仅开发验收：我方全队免伤，避免 Boss 技能把棋子打死后后续技能没有目标。
            // 开关状态保存在 BattleService，不修改棋子的血量或属性；正式包不会编译这条命令。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/我方无敌", "仅开发验收：1=我方全队免伤，0=恢复正常受伤", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                bool on = args.Int(0, 1) != 0;
                return battle.SetPlayerInvincible(on) ? "ok:on" : "ok:off";
            }, RevGMArg.Int("开关", 1));
            // 仅开发验收：把 Boss 压到 80% 血，专门满足汲取技能的施放条件。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/Boss压血", "仅开发验收：把 Boss 血量压到 80%，用于验证汲取", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (!battle.PressBossHp(0.8f))
                {
                    throw new RevGMUsageException("当前没有存活的 Boss");
                }
                return "ok";
            });
            // 仅开发验收：把当前 Game 画面存成 PNG，供测试报告引用。
            // 证据目录由调用者传入；默认写进程临时目录，不从资源目录反推工作区。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/截图", "仅开发验收：保存当前 Game 画面为 PNG（名称不带扩展名）", args =>
            {
                string name = args.Str(0, string.Empty);
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new RevGMUsageException("截图名称不合法");
                }
                string dir = System.IO.Path.GetFullPath(args.Str(1, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevGameEvidence")));
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, name + ".png");
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                return "ok:" + path;
            }, RevGMArg.Str("名称", "shot"), RevGMArg.Str("目录", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevGameEvidence")));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/快照", "读取流程、作用域、手牌和棋盘", args =>
            {
                string snapshot = RevServices.GetRequired<GameFlow>().Snapshot();
                BattleLogic logic = RevLogic.Get<BattleLogic>();
                return snapshot + (logic == null ? "" : "\n" + logic.Snapshot());
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/打开选关", "主界面进入选关", args => FlowResult(RevServices.GetRequired<GameFlow>().OpenLevels()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/进入战斗", "进入已解锁章节", args => FlowResult(RevServices.GetRequired<GameFlow>().EnterBattle(args.Int(0, 1))), RevGMArg.Int("章节", 1));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/返回主界面", "退出当前域并返回主界面", args => FlowResult(RevServices.GetRequired<GameFlow>().ReturnHome()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/过场故障", "仅开发验收：下一次过场在before-scene或after-scene失败，检查错误面板与回退", args =>
            {
                if (!RevServices.GetRequired<GameFlow>().ArmDevelopmentFailure(args.Str(0, "before-scene")))
                {
                    throw new RevGMUsageException("正在过场或阶段名无效");
                }
                return "下一次过场将主动失败，返回按钮仍走真实场景恢复流程";
            }, RevGMArg.Str("阶段", "before-scene"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/开始战斗", "与准备按钮一致", args => BattleResult(Logic().BeginBattle()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/下一回合", "与结算继续按钮一致", args => BattleResult(Logic().ContinueToNextRound()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/落一张盾战", "只消费手中实际持有的盾战牌", args => AutoPlace(true));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/落首张牌", "把第一张手牌落在己方合法空位", args => AutoPlace(false));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/落子", "按手牌编号和格子落子；越界不会扣牌", args =>
            {
                Logic();
                var pos = new BoardPos(args.Int(1, 0), args.Int(2, 0));
                return BattleResult(RevServices.GetRequired<IBattleService>().TryPlayCard(args.Int(0, 0), pos));
            }, RevGMArg.Int("牌编号"), RevGMArg.Int("X"), RevGMArg.Int("Y"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/刷新手牌", "准备期重抽剩余手牌，不补额度", args => BattleResult(Logic().RefreshHand()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/点棋盘", "准备或战斗时点己方棋子，再点空格换位；与触屏同一入口", args =>
            {
                return BattleResult(Logic().ClickBoard(new BoardPos(args.Int(0, 0), args.Int(1, 0))));
            }, RevGMArg.Int("X"), RevGMArg.Int("Y"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/攻击范围", "长按棋子的受控等价入口：0=第一颗我方棋子，或指定棋子编号", args =>
            {
                Logic();
                BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                if (board == null)
                {
                    throw new RevGMUsageException("当前没有棋盘视图");
                }

                int id = args.Int(0, 0);
                if (id == 0)
                {
                    System.Collections.Generic.IReadOnlyList<Piece> players = RevServices.GetRequired<IBattleService>().PlayerPieces;
                    if (players.Count == 0)
                    {
                        throw new RevGMUsageException("我方棋盘上没有棋子");
                    }
                    id = players[0].Id;
                }

                if (!board.ShowAttackRange(id))
                {
                    throw new RevGMUsageException("棋子 #" + id + " 无法显示攻击范围（不存在/已死亡/敌方被锁死/射界为空）");
                }
                return "棋子 #" + id + " 攻击范围已显示，共 " + board.AttackRangeCellCount + " 格";
            }, RevGMArg.Int("棋子编号", 0));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/收起攻击范围", "与再次点击屏幕任意位置一致", args =>
            {
                BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                if (board == null || !board.AttackRangeVisible)
                {
                    throw new RevGMUsageException("当前没有显示中的攻击范围");
                }

                int count = board.AttackRangeCellCount;
                board.HideAttackRange();
                return "攻击范围已收起，本次显示过 " + count + " 格";
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/选奖励", "选择0~2号奖励", args => BattleResult(Logic().ChooseReward(args.Int(0, 0))), RevGMArg.Int("选项"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/跳过", "第10回合未落牌时跳过换攻击强化", args => BattleResult(Logic().SkipRound()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/战斗事件", "战斗中按BattleEventKind编号触发事件，含攻击/防御/攻速/生命；受导表冷却与次数约束", args =>
            {
                Logic();
                int id = args.Int(0, 0);
                if (!System.Enum.IsDefined(typeof(BattleEventKind), id))
                {
                    throw new RevGMUsageException("事件编号不在BattleEventKind配置范围内");
                }

                return BattleResult(RevServices.GetRequired<IBattleService>().TriggerBattleEvent((BattleEventKind)id));
            }, RevGMArg.Int("事件编号"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/载入验收场景", "预定义场景；enemy-one-eye/ enemy-two-eyes观察真实敌阵", args =>
            {
                Logic();
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                return BattleResult(battle.LoadDevelopmentScenario(args.Str(0, "merge")));
            }, RevGMArg.Str("场景", "merge"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/推进", "固定步长推进1~100步，不能修改胜负结果", args =>
            {
                Logic();
                int count = args.Int(0, 1);
                if (count < 1 || count > 100)
                {
                    throw new RevGMUsageException("推进步数必须是1~100");
                }

                IBattleService battle = RevServices.GetRequired<IBattleService>();
                if (battle.Phase != GamePhase.Battle)
                {
                    throw new RevGMUsageException("当前不在战斗阶段");
                }

                for (int i = 0; i < count && battle.Phase == GamePhase.Battle; i++)
                {
                    battle.Tick(Balance.BattleTickSeconds);
                }

                return "推进完成，阶段=" + battle.Phase;
            }, RevGMArg.Int("步数", 1));
            // 真实指针输入 + 结构化查询：玩家路径与 AI 决策入口，见同目录 GameGmCommands.Input.cs。
            RegisterInputCommands();
            }
            catch { UnregisterAll(); throw; }
        }

        private static BattleLogic Logic()
        {
            BattleLogic logic = RevLogic.Get<BattleLogic>();
            if (logic == null)
            {
                throw new RevGMUsageException("请先从选关进入战斗");
            }

            return logic;
        }

        private static string FlowResult(bool success)
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (!success)
            {
                throw new RevGMUsageException(flow.LastError);
            }

            return flow.Snapshot();
        }

        private static string BattleResult(bool success)
        {
            if (!success)
            {
                throw new RevGMUsageException(RevServices.GetRequired<IBattleService>().LastError);
            }

            return "成功";
        }

        private static string AutoPlace(bool shieldOnly)
        {
            Logic();
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            foreach (var card in battle.Hand)
            {
                if (shieldOnly && card.Kind != PieceKind.Shield)
                {
                    continue;
                }

                battle.SelectCard(card.Id);
                for (int y = 0; y < Consts.BoardSize / 2; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var pos = new BoardPos(x, y);
                        if (battle.CanPlaySelectedAt(pos))
                        {
                            return BattleResult(battle.TryPlayCard(card.Id, pos));
                        }
                    }
                }
            }

            throw new RevGMUsageException("没有符合条件的手牌或合法落点");
        }

        /// <summary>唯一注销入口；跟随注册实际成功的名称清理，新增Boss/输入命令不再漏记手写列表。</summary>
        public static void UnregisterAll()
        {
            Pointer.Release(false);
            foreach (string name in Registered) { RevGM.Unregister(name); }
            Registered.Clear();
        }

        private static readonly System.Collections.Generic.HashSet<string> Registered = new System.Collections.Generic.HashSet<string>();

        /// <summary>本文件所有命令统一经过此处；重复名立即失败，参数契约仍交给框架验证。</summary>
        internal static void RegisterCommand(string name, string description, System.Func<RevGMArgs, string> handler, params RevGMArg[] args)
        {
            if (Registered.Contains(name)) { throw new System.InvalidOperationException("重复GM：" + name); }
            RevGM.Register(name, description, handler, args);
            Registered.Add(name);
        }

        /// <summary>真实玩家按钮覆盖：使用与点击GM相同的EventSystem链，保留遮罩、翻牌、禁用与阶段拒绝。</summary>
        private static string ClickPlayerButton(string path)
        {
            EnsurePointerIdle();
            var button = Pointer.FindButton(path);
            if (button == null || !Pointer.PressButton(button) || !Pointer.Release(true)) { throw new RevGMUsageException(Pointer.LastError); }
            return "ok:" + path;
        }

        private static void RegisterPlayerActions()
        {
            RegisterCommand("RevGame/选择眼石", "与眼石按钮一致；再调用点格子完成落眼石", args => ClickPlayerButton("PreparePanel/btnEye"));
            RegisterCommand("RevGame/返回选关首页", "与选关页返回按钮一致", args => ClickPlayerButton("LevelSelectPanel/btnHome"));
            RegisterCommand("RevGame/故障返回", "与加载失败返回按钮一致", args => ClickPlayerButton("LoadingPanel/btnReturn"));
            RegisterCommand("RevGame/玩家对战", "与首页PvP按钮一致，目前只显示尚未开放提示", args => ClickPlayerButton("HomePanel/btnPvP"));
            RegisterCommand("RevGame/退出游戏", "与首页退出按钮一致；结束本次Play或Player", args => ClickPlayerButton("HomePanel/btnQuit"));
            RegisterCommand("RevGame/玩家操作目录", "只读：当前版本全部人工动作与GM映射", args =>
                "首页/选关/回家=打开选关,进入战斗,返回主界面；六张手牌=选牌,落子,刷新手牌；眼石=选择眼石,点格子；移动/换位=点棋盘；长按=攻击范围,收起攻击范围；开战/跳过/继续/三选一=开始战斗,跳过,下一回合,选奖励；PvP/退出/加载失败=玩家对战,退出游戏,故障返回；真实按钮=点击 面板名/节点名；状态=流程状态,手牌,棋子,落点,奖励");
            RegisterCommand("RevGame/流程状态", "只读JSON：请求accepted后等待transitioning=false并核对screen/blocked/error", args =>
            {
                var flow = RevServices.GetRequired<GameFlow>();
                return UnityEngine.JsonUtility.ToJson(new FlowSnapshot { screen = flow.Screen.ToString(), transitioning = flow.IsTransitioning, blocked = flow.IsGameplayBlocked, error = flow.LastError });
            });
        }
        [System.Serializable] private sealed class FlowSnapshot
        {
            public string screen, error;
            public bool transitioning, blocked;
        }
    }
}

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        /// <summary>
        /// 复用的指针适配器：它要在"按下"和"松开"之间保存手势状态，
        /// 所以不能每次点击都 new 一个，否则上一次没松开的按压会丢。
        /// </summary>
        private static readonly GamePointerInput Pointer = new GamePointerInput();

        /// <summary>注册输入层与查询层命令。由 RegisterAll 调用，统一注册账本负责注销。</summary>
        private static void RegisterInputCommands()
        {
            // ---------------- 输入层：真实指针 ----------------
            // 注意：这里的"点击"不是调用 Button.onClick.Invoke，而是把 EventSystem 的整条链路跑一遍，
            // 所以加载遮罩挡住、按钮置灰不可交互、面板没打开、按钮没接线，都会如实失败。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/点击", "模拟玩家真实点击按钮（EventSystem 射线；被遮挡或不可交互会失败）：目标=面板类型名/按钮节点名", args =>
            {
                string target = args.Str(0, string.Empty);
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new RevGMUsageException("用法：RevGame/点击 HomePanel/btnPlay");
                }

                EnsurePointerIdle();
                UnityEngine.UI.Button button = Pointer.FindButton(target);
                if (button == null)
                {
                    throw new RevGMUsageException(Pointer.LastError);
                }
                if (!Pointer.PressButton(button))
                {
                    throw new RevGMUsageException(Pointer.LastError);
                }
                // 同一次调用里立刻松开：Button 的 onClick 在 pointerClick 阶段触发，
                // 不需要真的跨帧等待；跨帧等待会让 GM 结果和业务状态产生时序歧义。
                if (!Pointer.Release(true))
                {
                    throw new RevGMUsageException("松开时目标已不可点击：" + Pointer.LastError);
                }
                return "ok:" + target;
            }, RevGMArg.Str("目标"));

            // 棋盘格同样走 Physics2DRaycaster；被 UI 板子挡住时必须失败，不能绕过遮挡直接落子。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/点格子", "模拟玩家真实点击棋盘格（受 UI 遮挡影响）：X Y", args =>
            {
                var pos = new BoardPos(args.Int(0, 0), args.Int(1, 0));
                if (!pos.IsInside(Consts.BoardSize))
                {
                    throw new RevGMUsageException("格子越界：" + pos.X + "," + pos.Y + "（合法范围 0~" + (Consts.BoardSize - 1) + "）");
                }

                EnsurePointerIdle();
                BoardCellView cell = FindCell(pos);
                if (cell == null)
                {
                    throw new RevGMUsageException("棋盘格视图还没创建：" + pos.X + "," + pos.Y + "（等棋盘加载完再点）");
                }
                if (!Pointer.PressCell(cell))
                {
                    throw new RevGMUsageException(Pointer.LastError);
                }
                if (!Pointer.Release(true))
                {
                    throw new RevGMUsageException("松开时格子已不可点击：" + Pointer.LastError);
                }
                return "ok:" + pos.X + "," + pos.Y;
            }, RevGMArg.Int("X"), RevGMArg.Int("Y"));

            // ---------------- 查询层：只读结构化状态 ----------------
            // 只列业务面板白名单，不做反射扫描：既避免读出框架内部面板，也让结果稳定可断言。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/面板", "只读：当前打开的业务面板名，逗号分隔；都没开返回 none", args =>
            {
                var open = new StringBuilder();
                AppendPanel(open, "HomePanel", RevUI.Get<HomePanel>());
                AppendPanel(open, "LevelSelectPanel", RevUI.Get<LevelSelectPanel>());
                AppendPanel(open, "PreparePanel", RevUI.Get<PreparePanel>());
                AppendPanel(open, "BattleHudPanel", RevUI.Get<BattleHudPanel>());
                AppendPanel(open, "SettlePanel", RevUI.Get<SettlePanel>());
                AppendPanel(open, "RewardPanel", RevUI.Get<RewardPanel>());
                return open.Length == 0 ? "none" : open.ToString();
            });

            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/战斗状态", "只读：章=..,回=..,阶段=..,Boss回合=..,已用=../总时长,剩余落子=..,胜方=..", args =>
            {
                IBattleService battle = RequireBattle();
                string winner = battle.LastWinner.HasValue ? battle.LastWinner.Value.ToString() : "none";
                return "章=" + battle.Chapter
                    + ",回=" + battle.Round
                    + ",阶段=" + battle.Phase
                    + ",Boss回合=" + (battle.IsBossRound ? "1" : "0")
                    + ",已用=" + battle.BattleElapsed.ToString("0.00") + "/" + battle.BattleDuration.ToString("0.00")
                    + ",剩余落子=" + battle.CardsRemaining
                    + ",胜方=" + winner;
            });

            // 手牌是 AI 出牌的唯一依据：编号 + 外形 + 星级，编号就是落子/选牌要传的参数。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/手牌", "只读：额度=..,选中=..,牌=编号:外形:星级,...；没有手牌返回 额度=..,选中=..,牌=none", args =>
            {
                IBattleService battle = RequireBattle();
                var cards = new StringBuilder();
                for (int i = 0; i < battle.Hand.Count; i++)
                {
                    HandCard card = battle.Hand[i];
                    if (cards.Length > 0)
                    {
                        cards.Append(',');
                    }
                    cards.Append(card.Id).Append(':').Append(card.Kind).Append(':').Append(card.Star);
                }
                return "额度=" + battle.CardsRemaining + ",选中=" + battle.SelectedCardId + ",牌=" + (cards.Length == 0 ? "none" : cards.ToString());
            });

            // 棋子快照：P/E 区分阵营，#编号用于攻击范围与其它 GM，@x,y 是左下角格，size 是占格边长。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/棋子", "只读：P#编号/外形/星级@x,y/边长/血-上限，E 开头为敌方；王/主将带 king 标记", args =>
            {
                IBattleService battle = RequireBattle();
                var text = new StringBuilder();
                AppendPieces(text, 'P', battle.PlayerPieces);
                AppendPieces(text, 'E', battle.EnemyPieces);
                return text.Length == 0 ? "none" : text.ToString();
            });

            // 选牌只改选择状态，不落子；这样"先看落点再决定"才成立（对应玩家点一下手牌看高亮）。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/选牌", "选中一张手牌（不改棋盘、不扣额度）；传 0 取消选择", args =>
            {
                IBattleService battle = RequireBattle();
                int id = args.Int(0, 0);
                if (!battle.SelectCard(id))
                {
                    throw new RevGMUsageException(battle.LastError);
                }
                return id == 0 ? "ok:取消选择" : "ok:选中 #" + id;
            }, RevGMArg.Int("牌编号", 0));

            // 落点供 AI 决策：只做预检（CanPlaySelectedAt），不占格不扣牌，可以反复调用。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/落点", "只读：当前所选牌的全部合法落点，格式 数量:x,y;x,y；未选牌直接失败", args =>
            {
                IBattleService battle = RequireBattle();
                if (battle.SelectedCardId == 0)
                {
                    throw new RevGMUsageException("还没有选牌：先执行 RevGame/选牌 <牌编号>");
                }

                var cells = new StringBuilder();
                int count = 0;
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        if (!battle.CanPlaySelectedAt(new BoardPos(x, y)))
                        {
                            continue;
                        }
                        if (count > 0)
                        {
                            cells.Append(';');
                        }
                        cells.Append(x).Append(',').Append(y);
                        count++;
                    }
                }
                return count + ":" + (count == 0 ? "none" : cells.ToString());
            });

            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/奖励", "只读：结算三选一候选，格式 #序号 类型 标题（带外形时附外形）；没有待选返回 none", args =>
            {
                IBattleService battle = RequireBattle();
                if (battle.Rewards.Count == 0)
                {
                    return "none";
                }

                var text = new StringBuilder();
                for (int i = 0; i < battle.Rewards.Count; i++)
                {
                    RewardDef reward = battle.Rewards[i];
                    if (i > 0)
                    {
                        text.Append(';');
                    }
                    text.Append('#').Append(i).Append(' ').Append(reward.Kind).Append(' ').Append(reward.Title);
                    if (reward.Kind == RewardKind.NewPiece)
                    {
                        text.Append('(').Append(reward.PieceKind).Append(')');
                    }
                    else if (reward.Kind == RewardKind.TeamBoost)
                    {
                        text.Append('(').Append(reward.Boost).Append(')');
                    }
                }
                return text.ToString();
            });
        }

        /// <summary>
        /// 上一次调用若在"按下后、松开前"失败，手势会一直挂在适配器里；
        /// 下一次点击前先静默松开（不补点击），避免把失败状态带到下一步。
        /// </summary>
        private static void EnsurePointerIdle()
        {
            if (Pointer.IsPressed)
            {
                Pointer.Release(false);
            }
        }

        /// <summary>按坐标找棋盘格视图。81 格线性查找足够快，也避免依赖 BoardView 的私有字典。</summary>
        private static BoardCellView FindCell(BoardPos pos)
        {
            BoardCellView[] cells = UnityEngine.Object.FindObjectsOfType<BoardCellView>();
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] != null && cells[i].Position.X == pos.X && cells[i].Position.Y == pos.Y)
                {
                    return cells[i];
                }
            }
            return null;
        }

        /// <summary>取战斗服务；不在战斗域时给出人话原因，而不是抛 NullReference。</summary>
        private static IBattleService RequireBattle()
        {
            IBattleService battle = RevServices.Get<IBattleService>();
            if (battle == null)
            {
                throw new RevGMUsageException("当前不在战斗域：先从选关进入战斗");
            }
            return battle;
        }

        /// <summary>把"面板已打开"的判定收敛成一处：实例存在且整条层级处于激活状态。</summary>
        private static void AppendPanel(StringBuilder text, string name, RevUIPanel panel)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy)
            {
                return;
            }
            if (text.Length > 0)
            {
                text.Append(',');
            }
            text.Append(name);
        }

        /// <summary>按固定格式追加一侧棋子：前缀#编号/外形/星级@x,y/边长/当前血-上限。</summary>
        private static void AppendPieces(StringBuilder text, char side, System.Collections.Generic.IReadOnlyList<Piece> pieces)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (piece == null)
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append(';');
                }
                text.Append(side).Append('#').Append(piece.Id)
                    .Append('/').Append(piece.Def.Kind)
                    .Append('/').Append(piece.Def.Star)
                    .Append('@').Append(piece.Pos.X).Append(',').Append(piece.Pos.Y)
                    .Append('/').Append(piece.Size)
                    .Append('/').Append(piece.Hp).Append('-').Append(piece.MaxHp);
                if (piece.IsKing || piece.IsBoss)
                {
                    text.Append("/king");
                }
                if (!piece.IsAlive)
                {
                    text.Append("/dead");
                }
            }
        }
    }
}

namespace RevGame.Bootstrap
{
    internal static class GameCombatGmCommands
    {
        private static BattleService Battle => (BattleService)RevServices.GetRequired<IBattleService>();
        internal static void RegisterGroup()
        {
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/玩家AI", "查询真实重量级状态机与包围/破眼/驻眼原因", args => Battle.PlayerAIStatus());
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/战斗节奏", "查询本轮攻击/防御/攻速/生命增益及波动/炸弹计数", args => Battle.CombatPacingStatus());
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/区域波动", "触发真实随机区域预警，不能重叠施放", args => Result(Battle.TriggerRegionPulse()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/能量巨大", "触发真实自动炸弹，等待蓄力后才对敌军扣血", args => Result(Battle.TriggerEnergyBomb()));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/战斗增益", "本轮全队属性：Attack/Defense/Haste/Vitality；第二参数为额外百分比", args =>
            {
                if (args.Count != 2 || !Enum.TryParse(args.Str(0), out BattleEventKind kind)) { throw new RevGMUsageException("用法：RevGame/战斗增益 Attack|Defense|Haste|Vitality 百分比"); }
                return Result(Battle.AddCombatBuff(kind, args.Float(1) / 100f));
            }, RevGMArg.Str("属性", "Attack"), RevGMArg.Float("额外百分比", 30f));
        }
        private static string Result(bool success)
        {
            if (!success) { throw new RevGMUsageException(Battle.LastError); }
            return "ok";
        }

    }
}

namespace RevGame.Animation
{
    /// <summary>开发属性命令的唯一注册点；只保存本次Play中的临时强化，不写入存档或正式配置。</summary>
    internal static class GameAttributeGmCommands
    {
        /// <summary>由Bootstrap的自动化装配成对注册/注销；面板、官方Automation和业务计划共用这些命令。</summary>
        internal static void RegisterGroup()
        {
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/属性设置", "依次设置额外攻击/攻速/等效防御/最大生命百分比；250=原值×3.5，重复设置不叠加", args =>
            {
                if (args.Count != 4) { throw new RevGMUsageException("用法：RevGame/属性设置 攻击% 攻速% 防御% 生命%"); }
                return Apply(new GameAutomationBoost { attackBonusPercent = args.Int(0), attackSpeedBonusPercent = args.Int(1),
                    defenseBonusPercent = args.Int(2), healthBonusPercent = args.Int(3) });
            }, RevGMArg.Int("攻击%", 0), RevGMArg.Int("攻速%", 0), RevGMArg.Int("防御%", 0), RevGMArg.Int("生命%", 0));
            RegisterAdd("RevGame/加攻击", 0);
            RegisterAdd("RevGame/加攻速", 1);
            RegisterAdd("RevGame/加防御", 2);
            RegisterAdd("RevGame/加血量", 3);
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/属性查询", "返回四项开发额外加成JSON；0为不额外增加，未修改正常准备增益", args =>
            {
                if (args.Count != 0) { throw new RevGMUsageException("属性查询不接受参数"); }
                return Snapshot();
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/属性清除", "清除四项开发强化，生命按比例恢复原上限，不复活死棋", args =>
            {
                if (args.Count != 0) { throw new RevGMUsageException("属性清除不接受参数"); }
                return Apply(null);
            });
        }

        /// <summary>attribute仅为本类内部路由：0攻击、1攻速、2等效防御、3最大生命；单位都是额外百分比。</summary>
        private static void RegisterAdd(string command, int attribute)
        {
            RevGame.Bootstrap.GameGmCommands.RegisterCommand(command, "累计增加该项额外整数百分比；负数可调低，最终需在0～10000%；AI重试建议用属性设置", args =>
            {
                if (args.Count != 1) { throw new RevGMUsageException("请提供一个整数百分比"); }
                int amount = args.Int(0);
                GameAutomationBoost next = Battle.CopyAutomationBoost();
                // 用long先做加法，防止int溢出后反而绕过范围校验；校验成功前绝不写回原配置。
                int current = attribute == 0 ? next.attackBonusPercent : attribute == 1 ? next.attackSpeedBonusPercent
                    : attribute == 2 ? next.defenseBonusPercent : next.healthBonusPercent;
                long sum = (long)current + amount;
                if (sum < 0 || sum > GameAutomationBoost.MaxBonusPercent) { throw new RevGMUsageException("累计加成超出0～10000%"); }
                if (attribute == 0) { next.attackBonusPercent = (int)sum; }
                else if (attribute == 1) { next.attackSpeedBonusPercent = (int)sum; }
                else if (attribute == 2) { next.defenseBonusPercent = (int)sum; }
                else { next.healthBonusPercent = (int)sum; }
                return Apply(next);
            }, RevGMArg.Int("增加百分比", 0));
        }

        private static BattleService Battle
        {
            get { return (BattleService)RevServices.GetRequired<IBattleService>(); }
        }

        /// <summary>先整体校验再更新；自动化持有租约时沿用该owner，以便失败/取消也能收回本次GM强化。</summary>
        private static string Apply(GameAutomationBoost boost)
        {
            GameAutomationBoost.Validate(boost);
            var runner = RevServices.GetRequired<GameAutomationRunner>();
            if (runner.State == "running") { runner.SetBoostFromGm(boost); }
            else { Battle.SetManualDevelopmentBoost(boost); }
            return Snapshot();
        }

        private static string Snapshot()
        {
            return JsonUtility.ToJson(Battle.CopyAutomationBoost());
        }

        internal static bool IsAttributeCommand(string name)
        {
            return name == "RevGame/属性设置" || name == "RevGame/属性查询" || name == "RevGame/属性清除"
                || name == "RevGame/加攻击" || name == "RevGame/加攻速" || name == "RevGame/加防御" || name == "RevGame/加血量";
        }

        /// <summary>只注销本类拥有的七条命令，不能清空框架里其他业务注册的GM。</summary>

    }
}

namespace RevGame.Animation
{
    /// <summary>
    /// 将项目测试执行器接到现成 RevGM，再由 Revolution.RevCLI CLI/Scenario 调用。
    /// 仅注册六条窄入口，不另建HTTP服务/文件桥，不修改框架快照；注册和注销由Bootstrap成对调用。
    /// </summary>
    internal static class GameAutomationCommands
    {
        internal static void RegisterGroup()
        {

            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化开始", "接收Base64编码的UTF-8测试计划，异步执行真实玩家输入", args =>
            {
                string encoded = args.Str(0, "");
                if (encoded.Length == 0 || encoded.Length > 262144)
                {
                    throw new RevGMUsageException("需要不超过256KB的Base64计划");
                }
                string json;
                try { json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
                catch (FormatException) { throw new RevGMUsageException("计划不是有效的Base64文本"); }
                var plan = JsonUtility.FromJson<GameAutomationPlan>(json);
                RevServices.GetRequired<GameAutomationRunner>().Start(plan);
                return "started";
            }, RevGMArg.Str("计划Base64"));
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化状态", "只读 idle/running/passed/failed/cancelled", args => RevServices.GetRequired<GameAutomationRunner>().State);
            // Scenario.until 在失败时必须立即退出；单纯等待状态变成passed会把真实失败拖成几十分钟超时。
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化检查", "供Scenario轮询：passed/running；失败直接抛出报告原因", args =>
            {
                var runner = RevServices.GetRequired<GameAutomationRunner>();
                if (runner.State == "failed" || runner.State == "cancelled")
                {
                    throw new RevGMUsageException(runner.ReportJson());
                }
                return runner.State;
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化报告", "只读完整JSON报告，含逐步证据和11轮真实交战结果", args => RevServices.GetRequired<GameAutomationRunner>().ReportJson());
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化取消", "幂等取消测试并释放手势、计时器和观察订阅；不修改棋盘", args =>
            {
                RevServices.GetRequired<GameAutomationRunner>().Cancel();
                return "cancelled";
            });
            RevGame.Bootstrap.GameGmCommands.RegisterCommand("RevGame/自动化收尾", "幂等取消并经真实GameFlow返回首页；loading/clean，可在失败后轮询", args =>
            {
                RevServices.GetRequired<GameAutomationRunner>().Cancel();
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsTransitioning)
                {
                    return "loading";
                }
                if (flow.Screen == GameScreen.Home)
                {
                    return "clean";
                }
                if (!flow.ReturnHome())
                {
                    throw new RevGMUsageException(flow.LastError);
                }
                return "loading";
            });
        }


    }
}
#endif
