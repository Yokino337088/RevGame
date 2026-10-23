#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;

namespace RevGame.Animation
{
    /// <summary>
    /// 一份可由 CLI 发送的业务测试计划。这里只描述“玩家要做什么”，不携带任意脚本或私有字段修改。
    /// 由 GameAutomationRunner 持有一次运行，结束后保留报告；正式发行构建不编译这些测试类型。
    /// </summary>
    [Serializable]
    public sealed class GameAutomationPlan
    {
        /// <summary>显示在报告中的用例名，不参与游戏规则。</summary>
        public string name = "业务流程";
        /// <summary>整份计划的真实时间上限，单位秒；不受游戏暂停/倍速影响，允许范围 1～3600。</summary>
        public float timeoutSeconds = 1800f;
        /// <summary>显式选择的测试强化；不填写则完全沿用普通游戏数值。报告会保留它，不能据此判断正式难度。</summary>
        public GameAutomationBoost boost;
        /// <summary>仅编辑器取证：请求Unity在正常帧末写PNG到Library；不暂停、不手工推进PlayerLoop。</summary>
        public bool captureScreenshots;
        /// <summary>依次执行的步骤，最多 256 项；失败即停止，不会继续点击后面的界面。</summary>
        public GameAutomationStep[] steps;
    }

    /// <summary>四项均为额外整数百分比：0为原值，250为原值×3.5；只作用于开发态我方棋子。</summary>
    [Serializable]
    public sealed class GameAutomationBoost
    {
        /// <summary>我方最终普通攻击伤害的额外百分比，对后落手牌、援军和升级后的棋子同样有效。</summary>
        public int attackBonusPercent;
        /// <summary>我方攻击进度积累速度的额外百分比；不提高移动频率，也不加快整个游戏时钟。</summary>
        public int attackSpeedBonusPercent;
        /// <summary>防御等效加成：正伤害除以(1+百分比/100)，最少1；盾挡后的0伤害保持0。</summary>
        public int defenseBonusPercent;
        /// <summary>我方最大生命额外百分比；调整时保持当前生命比例，死亡棋子不复活，后续新棋同样继承。</summary>
        public int healthBonusPercent;

        // 这是调试输入的技术边界，不是正式玩法数值；防止整数溢出与一次模拟累积过量攻击动作。
        public const int MaxBonusPercent = 10000;

        public static void Validate(GameAutomationBoost value)
        {
            if (value == null) { return; }
            if (!Valid(value.attackBonusPercent) || !Valid(value.attackSpeedBonusPercent)
                || !Valid(value.defenseBonusPercent) || !Valid(value.healthBonusPercent))
            {
                throw new Revolution.RevGMUsageException("四项额外加成均需为0～" + MaxBonusPercent + "%的整数");
            }
        }

        private static bool Valid(int value)
        {
            return value >= 0 && value <= MaxBonusPercent;
        }

        /// <summary>租约持有自己的快照；GM的后续编辑不能悄悄改写旧报告或另一个测试。</summary>
        public GameAutomationBoost Copy()
        {
            return new GameAutomationBoost { attackBonusPercent = attackBonusPercent, attackSpeedBonusPercent = attackSpeedBonusPercent,
                defenseBonusPercent = defenseBonusPercent, healthBonusPercent = healthBonusPercent };
        }
    }

    /// <summary>单步契约。支持 wait、assert、click、deployHand、gm；未识别的操作必须拒绝。</summary>
    [Serializable]
    public sealed class GameAutomationStep
    {
        /// <summary>报告里的步骤说明，建议用中文描述预期业务结果。</summary>
        public string name;
        /// <summary>操作类型；wait 等到条件成立，assert 立即核对，其余类型执行一次真实动作。</summary>
        public string op;
        /// <summary>点击目标，格式为 Panel类型名/按钮节点名，例如 HomePanel/btnPlay；不允许任意场景路径。</summary>
        public string target;
        /// <summary>可选的期望屏幕，使用 GameScreen 枚举名称；留空表示不检查。</summary>
        public string screen;
        /// <summary>可选的期望战斗阶段，使用 GamePhase 枚举名称；留空表示不检查。</summary>
        public string phase;
        /// <summary>期望章节，0 表示不检查；章号从 1 开始。</summary>
        public int chapter;
        /// <summary>期望回合，0表示不检查；Boss也必须按真实回合号核对。</summary>
        public int round;
        /// <summary>期望手牌数；-1 表示不检查，0 表示确实应当没有手牌。</summary>
        public int hand = -1;
        /// <summary>额外只读条件：ready、roundWon、roundWonNoHand、chapterWon、homeClean；不填写则仅检查上面的字段。</summary>
        public string condition;
        /// <summary>deployHand 要打出的实际张数，1～6；空间不足时明确失败，绝不删除旧棋来腾位置。</summary>
        public int count = 1;
        /// <summary>本步骤的真实时间上限（秒）；这是测试容错期限，不是策划战斗数值。</summary>
        public float timeoutSeconds = 120f;
        /// <summary>gm 操作的白名单命令；测试报告会将 GM 与模拟输入分开计数。</summary>
        public string command;
        /// <summary>gm的可选精确返回值；为空时仅要求命令成功，不能用它覆盖业务状态断言。</summary>
        public string expected;
    }

    /// <summary>一次执行的可序列化证据。只在步骤完成/失败时记录，不在每帧拼接整个棋盘。</summary>
    [Serializable]
    public sealed class GameAutomationReport
    {
        /// <summary>计划显示名，随原始计划一起归档。</summary>
        public string name;
        /// <summary>开始时间，UTC的ISO8601格式；与真实耗时一起定位对应框架作业。</summary>
        public string startedUtc;
        /// <summary>执行状态：idle/running/passed/failed/cancelled；取消不能当通过。</summary>
        public string state = "idle";
        /// <summary>首个终止原因，成功为空；后续清理不能覆盖原始失败。</summary>
        public string error = "";
        /// <summary>已经通过后置条件的步骤数，不包含正在等待或仅按下未松开的步骤。</summary>
        public int completedSteps;
        /// <summary>计划步骤总数，完整通过必须等于completedSteps。</summary>
        public int totalSteps;
        /// <summary>成功松开并提交的EventSystem点击次数，不代表操作系统物理鼠标次数。</summary>
        public int pointerClicks;
        /// <summary>已同时验证手牌、额度、棋种及占格的实际落牌数。</summary>
        public int cardsPlayed;
        /// <summary>计划内业务GM次数；开始/查询/清理等工具命令另由框架记录。</summary>
        public int gmActions;
        /// <summary>本次观察到的我方出手事件数，不凭特效数量反推。</summary>
        public int playerAttacks;
        /// <summary>本次观察到的敌方出手事件数；高强化时可以来不及出手。</summary>
        public int enemyAttacks;
        /// <summary>正伤害受击事件数，护盾抵消为零的不计入。</summary>
        public int damageEvents;
        /// <summary>实际观察到的Boss技能施放次数；0不能当作九技能已验收。</summary>
        public int bossCasts;
        /// <summary>运行期Error/Exception/Assert数量；Warning另行保留，不混为零错误。</summary>
        public int unityErrors;
        /// <summary>从开始到当前/终止的真实秒数，不受游戏暂停或TimeScale影响。</summary>
        public float elapsedSeconds;
        /// <summary>本轮实际使用的测试配置；报告必须展示，避免把加速夹具当成正式平衡结果。</summary>
        public GameAutomationBoost boost;
        /// <summary>按实际 BattleEnded 事件写入的胜负，不由“进入下一回合”反推，也不主动制造结算。</summary>
        public List<GameAutomationRound> rounds = new List<GameAutomationRound>();
        /// <summary>终止清理后核对战斗服务已无测试强化；运行中为false，不能把未清理作业当最终通过。</summary>
        public bool boostReleased;
        /// <summary>按输入和步骤完成顺序追加；只在低频动作处生成记录。</summary>
        public List<GameAutomationEvidence> evidence = new List<GameAutomationEvidence>();
    }

    /// <summary>每一回合的真实交战证据；伤害、攻击计数均来自游戏既有事件。</summary>
    [Serializable]
    public sealed class GameAutomationRound
    {
        /// <summary>BattleEnter时的真实章节。</summary>
        public int chapter;
        /// <summary>BattleEnter时的真实回合，逐一核对以防漏跑第10轮。</summary>
        public int round;
        /// <summary>BattleEnded传来的胜方；测试不自行计算或改写。</summary>
        public string winner;
        /// <summary>服务记录的累计模拟秒数，与CLI真实耗时不是同一个单位口径。</summary>
        public float battleSeconds;
        /// <summary>该回合观察到的我方攻击次数。</summary>
        public int playerAttacks;
        /// <summary>该回合观察到的敌方攻击次数。</summary>
        public int enemyAttacks;
        /// <summary>该回合正伤害事件次数。</summary>
        public int damageEvents;
        /// <summary>开战时敌方实际名单数；0敌人自动结算不能作为通过证据。</summary>
        public int enemiesAtStart;
        /// <summary>开战时Boss主将的真实编号，0表示不存在；结算必须匹配同一颗棋。</summary>
        public int kingId;
        /// <summary>结算时确有主将且已死亡/锁死。围棋擒王允许未出手就合法取胜，不能误判为测试失败。</summary>
        public bool kingCaptured;
        /// <summary>本轮准备阶段由测试指针实际验证的落牌数；用于核对末四轮确实没有出牌。</summary>
        public int cardsPlayed;
        /// <summary>开战与结算瞬间剩余手牌数；零出牌轮开战为6，原业务结算统一清牌后为0。</summary>
        public int handAtStart, handAtEnd;
        /// <summary>开战与结算时双方仍存活的棋子数；援军和复活可能改变数量，不直接当作死亡计数。</summary>
        public int playersAtStart, playersAtEnd, enemiesAtEnd;
        /// <summary>双方存活棋子的当前生命合计，单位HP；回血/增援会影响变化，实际受伤另按事件累计。</summary>
        public int playerHpStart, playerHpEnd, enemyHpStart, enemyHpEnd;
        /// <summary>开战时存活棋子的最大生命合计，用于区分已有伤势与本轮受伤。</summary>
        public int playerMaxHpStart, enemyMaxHpStart;
        /// <summary>本轮双方实际承受的正伤害合计，单位HP，来自BattleHit而非伤害公式推算。</summary>
        public int playerDamageTaken, enemyDamageTaken;
        /// <summary>有攻击者来源的实际受伤合计；环境/辅助无来源伤害单独扣除，避免都算作双方对攻。</summary>
        public int playerDamageTakenFromAttacker, enemyDamageTakenFromAttacker;
        /// <summary>本轮真实Boss技能施放数；不能把普通攻击当作技能覆盖。</summary>
        public int bossCasts;
        /// <summary>本轮实际启用的原有轻松模式，以及已抽到的准备增益名称；测试不会改写它们。</summary>
        public bool victoryAssistEnabled;
        public string[] preparationBuffs;
    }

    /// <summary>单条审计记录：动作、输入来源、执行时的章/回合/阶段与真实时间。</summary>
    [Serializable]
    public sealed class GameAutomationEvidence
    {
        /// <summary>所属步骤显示名；工具诊断也以单独步骤名保留。</summary>
        public string step;
        /// <summary>动作来源或断言结果，明确区分输入、GM和只读证据。</summary>
        public string detail;
        /// <summary>记录瞬间的游戏屏幕。</summary>
        public string screen;
        /// <summary>记录瞬间的战斗阶段。</summary>
        public string phase;
        /// <summary>记录瞬间的章节号。</summary>
        public int chapter;
        /// <summary>记录瞬间的回合号。</summary>
        public int round;
        /// <summary>相对计划开始的真实秒数，用于和截图/作业时间对照。</summary>
        public float atSeconds;
    }
}
#endif
