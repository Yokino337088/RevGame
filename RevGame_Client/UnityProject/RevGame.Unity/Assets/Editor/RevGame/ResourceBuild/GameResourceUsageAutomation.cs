using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>项目级资源审计接入现有RevMCP；调用真实AssetDatabase与项目打包入口，不复制框架打包实现。</summary>
    public static class GameResourceUsageAutomation
    {
        [RevAutomationAction("game.score.check", Environment = "play", Keys = new string[0], Description = "真实AB积分飞星、橙色暴击、滚筒与密集渲染回收验收")]
        public static object ScoreCheck(RevAutomationRequest request)
        {
            if (!Revolution.RevResBootstrap.UseABInEditor) { throw new InvalidOperationException("需要冷启动真实AB模式"); }
            Type type = Type.GetType("RevGame.Editor.GameScoreChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult");
            if ((string)result.GetValue(null) == "运行中") { throw new InvalidOperationException("积分验收正在运行"); }
            type.GetMethod("Begin").Invoke(null, null);
            return new PlayCheckOperation(result);
        }
        [RevAutomationAction("game.score.rules", Environment = "edit", Keys = new string[0], Description = "验证真实计分、永久累计隔离、10回合与60局初始布局")]
        public static object ScoreRules(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameScoreChecks, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("RunRules").Invoke(null, null) }; }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [RevAutomationAction("game.score.configure", Environment = "edit", Keys = new string[0], Description = "装配左侧积分滚筒与共享Shader飞星预制体")]
        public static object ScoreConfigure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameScoreBuilder, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
        }
        /// <summary>只允许两个固定标签，前后使用同一输入，不开放任意代码执行。</summary>
        [RevAutomationAction("game.score.perf-before", Environment = "edit", Keys = new string[0], Description = "固定输入记录计分升级前规则性能")]
        public static object ScorePerfBefore(RevAutomationRequest request) { return FixedPerformance("before"); }
        [RevAutomationAction("game.score.perf-after", Environment = "edit", Keys = new string[0], Description = "固定输入记录计分升级后规则性能")]
        public static object ScorePerfAfter(RevAutomationRequest request) { return FixedPerformance("after"); }
        private static object FixedPerformance(string label)
        {
            Type type = Type.GetType("RevGame.Editor.GameBattlePerformanceChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("RunFixed").Invoke(null, new object[] { label }) };
        }
        /// <summary>只读返回逐资源的使用者、源文件大小和当前包名，允许配置前后对照。</summary>
        [RevAutomationAction("game.resources.audit", Environment = "edit", Keys = new string[0], Description = "只读审计Art/Atlases的真实业务引用与AB标记")]
        public static object Audit(RevAutomationRequest request) { return GameResourceUsage.Audit(); }

        /// <summary>统一入口重新计算引用、重建图集内容、清除闲置资源标记并生成ResMap/路径常量；不删除源素材。</summary>
        [RevAutomationAction("game.resources.configure", Keys = new string[0], Description = "按实际引用配置项目AB并排除闲置图片/图集")]
        public static object Configure(RevAutomationRequest request) { return new Result { text = GameResourceBuild.Configure() }; }

        /// <summary>严格断言去留与标记一致，失败抛出具体资产路径，不能以普通命令成功代替通过。</summary>
        [RevAutomationAction("game.resources.validate", Environment = "edit", Keys = new string[0], Description = "只读断言未引用Art/图集不进AB，且所有使用图像仍有包标记")]
        public static object Validate(RevAutomationRequest request) { return new Result { text = GameResourceUsage.Validate() + "；" + GameSpriteAtlasBuild.Validate() }; }

        /// <summary>
        /// 复用项目已有的冷启动/三次场景往返夹具，不预先加载全部图集来掩盖白图问题。
        /// 因Unity程序集引用方向限制，只桥接这一固定类型的方法，不接受外部传入任意反射目标。
        /// </summary>
        [RevAutomationAction("game.resources.play-check", Environment = "play", Keys = new string[0], Description = "真实AB冷启动UI、三次战斗往返及图集绑定回归")]
        public static object PlayCheck(RevAutomationRequest request)
        {
            if (!Revolution.RevResBootstrap.UseABInEditor) { throw new InvalidOperationException("此验收必须先冷启动真实AB模式"); }
            Type type = Type.GetType("RevGame.Editor.GameSceneTransitionChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult", BindingFlags.Public | BindingFlags.Static);
            if ((string)result.GetValue(null) == "运行中") { throw new InvalidOperationException("场景验收正在运行，不能并发启动"); }
            // false仅跳过会主动模拟失败的场景，正常资源断言保持原样，不修改业务数据或放宽图集检查。
            type.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { false });
            return new PlayCheckOperation(result);
        }

        /// <summary>桥每帧等待现有夹具结束；只有实际业务断言通过才成功，失败时保留原始原因。</summary>
        private sealed class PlayCheckOperation : RevAutomationOperation
        {
            private readonly PropertyInfo _result;
            public PlayCheckOperation(PropertyInfo result) { _result = result; }
            public override bool Tick()
            {
                string text = (string)_result.GetValue(null);
                if (text == "运行中") { return false; }
                Success = text.StartsWith("通过", StringComparison.Ordinal);
                Message = text;
                Result = new Result { text = text };
                return true;
            }
        }

        /// <summary>固定入口只驱动已有密集反馈夹具；异步完成后由真实断言决定成功。</summary>
        [RevAutomationAction("game.feedback.check", Environment = "play", Keys = new string[0], Description = "密集弹道、即时伤害、图集与跨回合清理验收")]
        public static object FeedbackCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult", BindingFlags.Public | BindingFlags.Static);
            if ((string)result.GetValue(null) == "运行中") { throw new InvalidOperationException("反馈验收正在运行"); }
            type.GetMethod("Begin").Invoke(null, null);
            return new PlayCheckOperation(result);
        }

        /// <summary>只装配项目弹道资产，不重建或保存用户场景。</summary>
        [RevAutomationAction("game.feedback.configure", Keys = new string[0], Description = "导入远程原画并装配五职业共享特效材质")]
        public static object FeedbackConfigure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameFeelBuilder, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("ApplyProjectileStyle").Invoke(null, null) };
        }

        [Serializable] private sealed class Result { public string text; }

        /// <summary>固定规则夹具入口，不允许外部传反射目标。</summary>
        [RevAutomationAction("game.impact.rules", Environment = "edit", Keys = new string[0], Description = "验收敌方攻速、后排追击、封气与安全整队")]
        public static object ImpactRules(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameCombatImpactChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("RunRules").Invoke(null,null) };
        }

        /// <summary>敌方节奏改动后复用原有全规则和战术回放，覆盖合成、守眼与跨回合契约。</summary>
        [RevAutomationAction("game.impact.regression", Environment = "edit", Keys = new string[0], Description = "运行原有规则与战术回放回归")]
        public static object ImpactRegression(RevAutomationRequest request)
        {
            string result = string.Empty;
            foreach (string name in new[] { "GameRuleChecks", "GameTacticsChecks" })
            {
                Type type = Type.GetType("RevGame.Editor."+name+", Assembly-CSharp-Editor",true);
                try { result += (string)type.GetMethod("Run").Invoke(null,null) + "\n"; }
                catch (TargetInvocationException error) { throw new InvalidOperationException(name + "：" + (error.InnerException ?? error).Message,error.InnerException ?? error); }
            }
            return new Result { text=result };
        }

        /// <summary>真实Play记录整队、近远程特效与区域波动连续帧，完成后返回大厅。</summary>
        [RevAutomationAction("game.impact.check", Environment = "play", Keys = new string[0], Description = "实际战斗冲击视觉与回收验收")]
        public static object ImpactCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameCombatImpactChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult");
            if ((string)result.GetValue(null) == "运行中") { throw new InvalidOperationException("冲击验收正在运行"); }
            type.GetMethod("Begin").Invoke(null,null);
            return new PlayCheckOperation(result);
        }
    }
}
