// ============================================================
// GameGmCommands.cs —— 开发环境的受控验收入口。
// 命令只调用流程/服务公开动作，拒绝抛 RevGMUsageException，机器能通过 Success 判断失败。
// 不提供任意执行脚本、任意改属性或跨项目操作；正式包不注册这些命令。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Linq;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;

namespace RevGame.Bootstrap
{
    public static class GameGmCommands
    {
        private static readonly string[] Names =
        {
            "快照",
            "打开选关",
            "进入战斗",
            "返回主界面",
            "过场故障",
            "开始战斗",
            "下一回合",
            "落一张盾战",
            "落首张牌",
            "落子",
            "点棋盘",
            "攻击范围",
            "收起攻击范围",
            "刷新手牌",
            "选奖励",
            "跳过",
            "战斗事件",
            "载入验收场景",
            "推进"
        };
        public static void Register()
        {
            RevGM.Register("RevGame/敌方AI", "只读重量级状态机当前状态、状态时长和最近切换原因", args =>
            {
                Logic();
                return ((BattleService)RevServices.GetRequired<IBattleService>()).EnemyAIStatus();
            });
            // 仅开发验收：让 Boss 立刻施放指定技能，用来逐个检查特效与伤害。
            // 仍会检查“这一招此刻有没有合法目标”，打不到人时返回失败而不是空放；正式战斗由 Boss 自己挑技能。
            RevGM.Register("RevGame/Boss技能", "Boss 回合战斗中立刻施放技能：0横扫 1狙击 2轰炸 3弹幕 4瞬移 5激光 6汲取 7召唤 8狂暴", args =>
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
            RevGM.Register("RevGame/Boss准备", "自动化：进入战斗域并直接开始 Boss 回合；loading=加载中，ready=已开战", args =>
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
            RevGM.Register("RevGame/Boss查询", "自动化：Boss 状态 idle / casting / dead / stopped / none", args =>
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
            RevGM.Register("RevGame/Boss清卫", "自动化：移除 Boss 以外的敌方棋子，给贴身技能腾出落点", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (!battle.IsBossRound)
                {
                    throw new RevGMUsageException("当前不是 Boss 回合");
                }
                return "ok:" + battle.ClearBossGuards();
            });
            RevGM.Register("RevGame/Boss靠近", "自动化：把我方一颗存活棋子移到离 Boss 指定距离的空格（1=贴身，2=隔一格）", args =>
            {
                int distance = args.Int(1, 1);
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
            RevGM.Register("RevGame/Boss续航", "自动化：回满 Boss 与我方全部棋子，保证多技能连续验收", args =>
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
            RevGM.Register("RevGame/我方无敌", "仅开发验收：1=我方全队免伤，0=恢复正常受伤", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                bool on = args.Int(0, 1) != 0;
                return battle.SetPlayerInvincible(on) ? "ok:on" : "ok:off";
            }, RevGMArg.Int("开关", 1));
            // 仅开发验收：把 Boss 压到 80% 血，专门满足汲取技能的施放条件。
            RevGM.Register("RevGame/Boss压血", "仅开发验收：把 Boss 血量压到 80%，用于验证汲取", args =>
            {
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                if (!battle.PressBossHp(0.8f))
                {
                    throw new RevGMUsageException("当前没有存活的 Boss");
                }
                return "ok";
            });
            // 仅开发验收：把当前 Game 画面存成 PNG，供测试报告引用。
            // 保存目录固定在进度同步附件下，文件名由测试场景指定。
            RevGM.Register("RevGame/截图", "仅开发验收：保存当前 Game 画面为 PNG（名称不带扩展名）", args =>
            {
                string name = args.Str(0, string.Empty);
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new RevGMUsageException("截图名称不合法");
                }
                // TODO(联调后删除)：仅用于本轮多宽高比取证的归档目录，正式业务不需要截图命令。
                string dir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, "../../../文档/进度同步/BossAI自动化测试结果-2026-10-22-附件"));
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, name + ".png");
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                return "ok:" + path;
            }, RevGMArg.Str("名称", "shot"));
            RevGM.Register("RevGame/快照", "读取流程、作用域、手牌和棋盘", args =>
            {
                string snapshot = RevServices.GetRequired<GameFlow>().Snapshot();
                BattleLogic logic = RevLogic.Get<BattleLogic>();
                return snapshot + (logic == null ? "" : "\n" + logic.Snapshot());
            });
            RevGM.Register("RevGame/打开选关", "主界面进入选关", args => FlowResult(RevServices.GetRequired<GameFlow>().OpenLevels()));
            RevGM.Register("RevGame/进入战斗", "进入已解锁章节", args => FlowResult(RevServices.GetRequired<GameFlow>().EnterBattle(args.Int(0, 1))), RevGMArg.Int("章节", 1));
            RevGM.Register("RevGame/返回主界面", "退出当前域并返回主界面", args => FlowResult(RevServices.GetRequired<GameFlow>().ReturnHome()));
            RevGM.Register("RevGame/过场故障", "仅开发验收：下一次过场在before-scene或after-scene失败，检查错误面板与回退", args =>
            {
                if (!RevServices.GetRequired<GameFlow>().ArmDevelopmentFailure(args.Str(0, "before-scene")))
                {
                    throw new RevGMUsageException("正在过场或阶段名无效");
                }
                return "下一次过场将主动失败，返回按钮仍走真实场景恢复流程";
            }, RevGMArg.Str("阶段", "before-scene"));
            RevGM.Register("RevGame/开始战斗", "与准备按钮一致", args => BattleResult(Logic().BeginBattle()));
            RevGM.Register("RevGame/下一回合", "与结算继续按钮一致", args => BattleResult(Logic().ContinueToNextRound()));
            RevGM.Register("RevGame/落一张盾战", "只消费手中实际持有的盾战牌", args => AutoPlace(true));
            RevGM.Register("RevGame/落首张牌", "把第一张手牌落在己方合法空位", args => AutoPlace(false));
            RevGM.Register("RevGame/落子", "按手牌编号和格子落子；越界不会扣牌", args =>
            {
                Logic();
                var pos = new BoardPos(args.Int(1, 0), args.Int(2, 0));
                return BattleResult(RevServices.GetRequired<IBattleService>().TryPlayCard(args.Int(0, 0), pos));
            }, RevGMArg.Int("牌编号"), RevGMArg.Int("X"), RevGMArg.Int("Y"));
            RevGM.Register("RevGame/刷新手牌", "准备期重抽剩余手牌，不补额度", args => BattleResult(Logic().RefreshHand()));
            RevGM.Register("RevGame/点棋盘", "准备或战斗时点己方棋子，再点空格换位；与触屏同一入口", args =>
            {
                return BattleResult(Logic().ClickBoard(new BoardPos(args.Int(0, 0), args.Int(1, 0))));
            }, RevGMArg.Int("X"), RevGMArg.Int("Y"));
            RevGM.Register("RevGame/攻击范围", "长按棋子的受控等价入口：0=第一颗我方棋子，或指定棋子编号", args =>
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
            RevGM.Register("RevGame/收起攻击范围", "与再次点击屏幕任意位置一致", args =>
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
            RevGM.Register("RevGame/选奖励", "选择0~2号奖励", args => BattleResult(Logic().ChooseReward(args.Int(0, 0))), RevGMArg.Int("选项"));
            RevGM.Register("RevGame/跳过", "第10回合未落牌时跳过换攻击强化", args => BattleResult(Logic().SkipRound()));
            RevGM.Register("RevGame/战斗事件", "战斗中触发0~7号事件，受配置表里的事件间隔与每回合上限约束", args =>
            {
                Logic();
                int id = args.Int(0, 0);
                if (id < 0 || id > 7)
                {
                    throw new RevGMUsageException("事件编号必须是0~7");
                }

                return BattleResult(RevServices.GetRequired<IBattleService>().TriggerBattleEvent((BattleEventKind)id));
            }, RevGMArg.Int("事件编号"));
            RevGM.Register("RevGame/载入验收场景", "预定义场景；enemy-one-eye/ enemy-two-eyes观察真实敌阵", args =>
            {
                Logic();
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                return BattleResult(battle.LoadDevelopmentScenario(args.Str(0, "merge")));
            }, RevGMArg.Str("场景", "merge"));
            RevGM.Register("RevGame/推进", "固定步长推进1~100步，不能修改胜负结果", args =>
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

        public static void Unregister()
        {
            foreach (string name in Names)
            {
                RevGM.Unregister("RevGame/" + name);
            }
        }
    }
}
#endif

