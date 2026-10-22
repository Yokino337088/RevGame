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
        /// <summary>依次执行的步骤，最多 256 项；失败即停止，不会继续点击后面的界面。</summary>
        public GameAutomationStep[] steps;
    }

    /// <summary>测试夹具配置，三项均为“额外百分比”，0关闭，500表示原值的6倍；只允许0～500。</summary>
    [Serializable]
    public sealed class GameAutomationBoost
    {
        /// <summary>我方最终普通攻击伤害的额外百分比，对后落手牌、援军和升级后的棋子同样有效。</summary>
        public int attackBonusPercent;
        /// <summary>我方攻击进度积累速度的额外百分比；不提高移动频率，也不加快整个游戏时钟。</summary>
        public int attackSpeedBonusPercent;
        /// <summary>防御等效加成：正伤害除以(1+百分比/100)，最少1；盾挡后的0伤害保持0。</summary>
        public int defenseBonusPercent;
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
        /// <summary>额外只读条件：ready、roundWon、chapterWon、homeClean；不填写则仅检查上面的字段。</summary>
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
