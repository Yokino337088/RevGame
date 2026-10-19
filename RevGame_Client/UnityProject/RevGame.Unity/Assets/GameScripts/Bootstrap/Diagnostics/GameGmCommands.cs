// ============================================================
// GameGmCommands.cs —— 开发环境的受控验收入口。
// 命令只调用流程/服务公开动作，拒绝抛 RevGMUsageException，机器能通过 Success 判断失败。
// 不提供任意执行脚本、任意改属性或跨项目操作；正式包不注册这些命令。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
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
            "刷新手牌",
            "选奖励",
            "跳过",
            "战斗事件",
            "载入验收场景",
            "推进"
        };
        public static void Register()
        {
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
            RevGM.Register("RevGame/选奖励", "选择0~2号奖励", args => BattleResult(Logic().ChooseReward(args.Int(0, 0))), RevGMArg.Int("选项"));
            RevGM.Register("RevGame/跳过", "第10回合未落牌时跳过换攻击强化", args => BattleResult(Logic().SkipRound()));
            RevGM.Register("RevGame/战斗事件", "战斗中触发0~7号事件，间隔7秒且每回合最多12次", args =>
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

