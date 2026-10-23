// ============================================================
// GameGmCommands.Input.cs —— 模拟真实玩家输入 + 供 AI 决策的结构化查询。
//
// 【为什么单独拆一个 partial 文件】
// 原有一批 GM 都是"直接调业务方法"（落子 / 开战 / 选奖励），做回归够用，
// 但它不是玩家路径：按钮被遮罩盖住、按钮禁用、面板根本没打开，调方法全都照样成功。
// AI 自动化真正要验的是"玩家点得动吗、点完业务对不对"，所以这里补两层：
//   1) 输入层：一律走 GamePointerInput —— EventSystem 真实射线 → 按下 → 松开 → 点击；
//   2) 查询层：只读地把棋盘、手牌、落点、奖励折叠成稳定字符串，供 assert 精确比较。
// 两条都不改任何玩法规则，正式包不注册这些命令。
//
// 【为什么返回稳定字符串】
// 场景脚本的 expected 是"精确相等"，不是包含匹配；带时间戳或随机数的输出没法断言。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using RevGame.Animation;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        /// <summary>
        /// 复用的指针适配器：它要在"按下"和"松开"之间保存手势状态，
        /// 所以不能每次点击都 new 一个，否则上一次没松开的按压会丢。
        /// </summary>
        private static readonly GamePointerInput Pointer = new GamePointerInput();

        /// <summary>注册输入层与查询层命令。由 Register() 调用，注销时共用同一份 Names 清单。</summary>
        private static void RegisterInputCommands()
        {
            // ---------------- 输入层：真实指针 ----------------
            // 注意：这里的"点击"不是调用 Button.onClick.Invoke，而是把 EventSystem 的整条链路跑一遍，
            // 所以加载遮罩挡住、按钮置灰不可交互、面板没打开、按钮没接线，都会如实失败。
            RevGM.Register("RevGame/点击", "模拟玩家真实点击按钮（EventSystem 射线；被遮挡或不可交互会失败）：目标=面板类型名/按钮节点名", args =>
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
            RevGM.Register("RevGame/点格子", "模拟玩家真实点击棋盘格（受 UI 遮挡影响）：X Y", args =>
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
            RevGM.Register("RevGame/面板", "只读：当前打开的业务面板名，逗号分隔；都没开返回 none", args =>
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

            RevGM.Register("RevGame/战斗状态", "只读：章=..,回=..,阶段=..,Boss回合=..,已用=../总时长,剩余落子=..,胜方=..", args =>
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
            RevGM.Register("RevGame/手牌", "只读：额度=..,选中=..,牌=编号:外形:星级,...；没有手牌返回 额度=..,选中=..,牌=none", args =>
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
            RevGM.Register("RevGame/棋子", "只读：P#编号/外形/星级@x,y/边长/血-上限，E 开头为敌方；王/主将带 king 标记", args =>
            {
                IBattleService battle = RequireBattle();
                var text = new StringBuilder();
                AppendPieces(text, 'P', battle.PlayerPieces);
                AppendPieces(text, 'E', battle.EnemyPieces);
                return text.Length == 0 ? "none" : text.ToString();
            });

            // 选牌只改选择状态，不落子；这样"先看落点再决定"才成立（对应玩家点一下手牌看高亮）。
            RevGM.Register("RevGame/选牌", "选中一张手牌（不改棋盘、不扣额度）；传 0 取消选择", args =>
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
            RevGM.Register("RevGame/落点", "只读：当前所选牌的全部合法落点，格式 数量:x,y;x,y；未选牌直接失败", args =>
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

            RevGM.Register("RevGame/奖励", "只读：结算三选一候选，格式 #序号 类型 标题（带外形时附外形）；没有待选返回 none", args =>
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
#endif
