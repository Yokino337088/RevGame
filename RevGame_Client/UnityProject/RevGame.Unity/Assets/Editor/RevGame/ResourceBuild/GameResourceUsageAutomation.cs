using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>项目级资源审计接入现有RevMCP；调用真实AssetDatabase与项目打包入口，不复制框架打包实现。</summary>
    public static class GameResourceUsageAutomation
    {
        [RevAutomationAction("game.battle.stress-before", Environment = "play", Keys = new string[0], Description = "六章48棋真实渲染性能基线，约两分钟，恢复首页与设置")]
        public static object StressBefore(RevAutomationRequest request)
        {
            return Stress("before");
        }
        [RevAutomationAction("game.battle.stress-after", Environment = "play", Keys = new string[0], Description = "六章旧格式性能复测，仅摘要与截图；新测量优先game.perf.begin")]
        // 旧压测兼容桥接，不再导出Profiler数据；新的稳定窗口采样从game.perf.begin启动并按runId核对。
        public static object StressAfter(RevAutomationRequest request)
        {
            return Stress("after");
        }
        private static object Stress(string label)
        {
            Type type = Type.GetType("RevGame.Editor.GameBattleStressChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult");

            if ((string)result.GetValue(null) == "运行中")
            {
                throw new InvalidOperationException("压测正在运行");
            }
            type.GetMethod("Begin").Invoke(null, new object[] { label });
            return new PlayCheckOperation(result);
        }
        [RevAutomationAction("game.score.check", Environment = "play", Keys = new string[0], Description = "真实AB积分飞星、橙色暴击、滚筒与密集渲染回收验收")]
        public static object ScoreCheck(RevAutomationRequest request)
        {
            if (!Revolution.RevResBootstrap.UseABInEditor)
            {
                throw new InvalidOperationException("需要冷启动真实AB模式");
            }
            Type type = Type.GetType("RevGame.Editor.GameScoreChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult");

            if ((string)result.GetValue(null) == "运行中")
            {
                throw new InvalidOperationException("积分验收正在运行");
            }
            type.GetMethod("Begin").Invoke(null, null);
            return new PlayCheckOperation(result);
        }
        [RevAutomationAction("game.score.rules", Environment = "edit", Keys = new string[0], Description = "验证真实计分、永久累计隔离、10回合与60局初始布局")]
        public static object ScoreRules(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameScoreChecks, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("RunRules").Invoke(null, null) };
            }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [RevAutomationAction("game.score.configure", Environment = "edit", Keys = new string[0], Description = "装配左侧积分滚筒与共享Shader飞星预制体")]
        public static object ScoreConfigure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameScoreBuilder, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
        }
        [RevAutomationAction("game.ui.preview-recover", Environment = "edit", Keys = new string[0], Description = "恢复非Play图集预览并读取HUD实际Sprite纹理")]
        public static object PreviewRecover(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameEditorAtlasPreview, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("Recover").Invoke(null, null) };
        }
        [RevAutomationAction("game.ui.preview-check", Environment = "edit", Keys = new string[0], Description = "独立场景绘制真实UGUI并保存白图排查像素证据")]
        public static object PreviewCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameUiPreviewChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("Capture").Invoke(null, null) };
        }
        /// <summary>只允许两个固定标签，前后使用同一输入，不开放任意代码执行。</summary>
        [RevAutomationAction("game.score.perf-before", Environment = "edit", Keys = new string[0], Description = "固定输入记录计分升级前规则性能")]
        public static object ScorePerfBefore(RevAutomationRequest request)
        {
            return FixedPerformance("before");
        }
        [RevAutomationAction("game.score.perf-after", Environment = "edit", Keys = new string[0], Description = "固定输入记录计分升级后规则性能")]
        public static object ScorePerfAfter(RevAutomationRequest request)
        {
            return FixedPerformance("after");
        }
        private static object FixedPerformance(string label)
        {
            Type type = Type.GetType("RevGame.Editor.GameBattlePerformanceChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("RunFixed").Invoke(null, new object[] { label }) };
        }

        /// <summary>通过固定编辑器入口测普通阵容的近战走位，回传查询计时和安全断言。</summary>
        [RevAutomationAction("game.perf.enemy-move", Environment = "edit", Keys = new string[0], Description = "固定普通阵容，测敌方近战实际走位查询并核对棋盘不变")]
        public static object EnemyMove(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameBattlePerformanceChecks, Assembly-CSharp-Editor", true);
            try
            {
                return type.GetMethod("EnemyMove").Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }

        /// <summary>只读返回逐资源的使用者、源文件大小和当前包名，允许配置前后对照。</summary>
        [RevAutomationAction("game.resources.audit", Environment = "edit", Keys = new string[0], Description = "只读审计Art/Atlases的真实业务引用与AB标记")]
        public static object Audit(RevAutomationRequest request)
        {
            return GameResourceUsage.Audit();
        }
        /// <summary>统一入口重新计算引用、重建图集内容、清除闲置资源标记并生成ResMap/路径常量；不删除源素材。</summary>
        [RevAutomationAction("game.resources.configure", Keys = new string[0], Description = "按实际引用配置项目AB并排除闲置图片/图集")]
        public static object Configure(RevAutomationRequest request)
        {
            return new Result { text = GameResourceBuild.Configure() };
        }
        /// <summary>严格断言去留与标记一致，失败抛出具体资产路径，不能以普通命令成功代替通过。</summary>
        [RevAutomationAction("game.resources.validate", Environment = "edit", Keys = new string[0], Description = "只读断言未引用Art/图集不进AB，且所有使用图像仍有包标记")]
        public static object Validate(RevAutomationRequest request)
        {
            return new Result { text = GameResourceUsage.Validate() + "；" + GameSpriteAtlasBuild.Validate() };
        }
        /// <summary>
        /// 复用项目已有的冷启动/三次场景往返夹具，不预先加载全部图集来掩盖白图问题。
        /// 因Unity程序集引用方向限制，只桥接这一固定类型的方法，不接受外部传入任意反射目标。
        /// </summary>
        [RevAutomationAction("game.resources.play-check", Environment = "play", Keys = new string[0], Description = "真实AB冷启动UI、三次战斗往返及图集绑定回归")]
        public static object PlayCheck(RevAutomationRequest request)
        {
            if (!Revolution.RevResBootstrap.UseABInEditor)
            {
                throw new InvalidOperationException("此验收必须先冷启动真实AB模式");
            }
            Type type = Type.GetType("RevGame.Editor.GameSceneTransitionChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult", BindingFlags.Public | BindingFlags.Static);

            if ((string)result.GetValue(null) == "运行中")
            {
                throw new InvalidOperationException("场景验收正在运行，不能并发启动");
            }
            // false仅跳过会主动模拟失败的场景，正常资源断言保持原样，不修改业务数据或放宽图集检查。
            type.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { false });
            return new PlayCheckOperation(result);
        }
        /// <summary>沿用固定的跨程序集桥执行完整场景交接夹具，结果仅通过当前工单返回。</summary>
        [RevAutomationAction("game.scene-handoff.check", Environment = "play", Keys = new string[0],
            Description = "验证真实大厅与六章战场交接、请求去重、故障恢复和退场回收")]
        public static object SceneHandoffCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameSceneTransitionChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult", BindingFlags.Public | BindingFlags.Static);
            // 只调用固定夹具入口。夹具发布 LastResult 后，既有操作桥将业务和清理的断言转为工单结果。
            type.GetMethod("BeginHandoffCheck", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            return new PlayCheckOperation(result);
        }

        /// <summary>执行一次真实 AB 往返，返回可继续查询入场与退场资源的请求 ID。</summary>
        [RevAutomationAction("game.scene-handoff.resource-check", Environment = "play", Keys = new string[0],
            Description = "真实 AB 往返第一章，验证资源来源、首屏门禁、持有与释放")]
        public static object SceneResourceCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameSceneTransitionChecks, Assembly-CSharp-Editor", true);
            type.GetMethod("BeginResourceTraceCheck", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            return new PlayCheckOperation(type.GetProperty("LastResult"), type.GetProperty("LastBattleHandoffId"));
        }

        /// <summary>复用正式场景夹具对比冷进与两次重入，返回全部入场和退场交接 ID。</summary>
        [RevAutomationAction("game.scene-handoff.performance-check", Environment = "play", Keys = new string[0],
            Description = "真实 AB 下对比第一章冷进与两次重入的分段耗时、帧间隔和内存峰值")]
        public static object ScenePerformanceCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameSceneTransitionChecks, Assembly-CSharp-Editor", true);
            type.GetMethod("BeginHandoffPerformanceCheck", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            return new PlayCheckOperation(type.GetProperty("LastResult"), requestIds: type.GetProperty("LastPerformanceRequestIds"));
        }

        /// <summary>桥每帧等待现有夹具结束；只有实际业务断言通过才成功，失败时保留原始原因。</summary>
        private sealed class PlayCheckOperation : RevAutomationOperation
        {
            /// <summary>固定夹具公布最终结果的静态属性。</summary>
            private readonly PropertyInfo _result;
            /// <summary>资源夹具额外公布的入场 ID，旧夹具可省略。</summary>
            private readonly PropertyInfo _requestId;
            /// <summary>性能夹具公布的多轮请求 ID，其他夹具可省略。</summary>
            private readonly PropertyInfo _requestIds;

            /// <summary>保存固定静态属性，夹具结案后才读取结果和可选的交接 ID。</summary>
            public PlayCheckOperation(PropertyInfo result, PropertyInfo requestId = null, PropertyInfo requestIds = null)
            {
                _result = result;
                _requestId = requestId;
                _requestIds = requestIds;
            }
            /// <summary>等待夹具完成业务与清理，再发布结果以及可选的交接 ID。</summary>
            public override bool Tick()
            {
                string text = (string)_result.GetValue(null);

                if (text == "运行中")
                {
                    return false;
                }
                Success = text.StartsWith("通过", StringComparison.Ordinal);
                Message = text;
                Result = _requestId == null && _requestIds == null
                    ? (object)new Result { text = text }
                    : new SceneResourceResult
                    {
                        text = text,
                        requestId = (string)_requestId?.GetValue(null),
                        requestIds = (string[])_requestIds?.GetValue(null)
                    };
                return true;
            }
        }

        /// <summary>交接回归的内存回执，允许下一步 RevMCP 读取单轮资源或多轮性能快照。</summary>
        [Serializable]
        private sealed class SceneResourceResult
        {
            /// <summary>回归观察和断言摘要。</summary>
            public string text;
            /// <summary>用于查询前后资源快照的第一章交接 ID。</summary>
            public string requestId;
            /// <summary>用于查询多轮性能的交接 ID，每轮依次是进场、退场。</summary>
            public string[] requestIds;
        }
        /// <summary>固定入口只驱动已有密集反馈夹具；异步完成后由真实断言决定成功。</summary>
        [RevAutomationAction("game.feedback.check", Environment = "play", Keys = new string[0], Description = "密集弹道、即时伤害、图集与跨回合清理验收")]
        public static object FeedbackCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult", BindingFlags.Public | BindingFlags.Static);

            if ((string)result.GetValue(null) == "运行中")
            {
                throw new InvalidOperationException("反馈验收正在运行");
            }
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
        [Serializable]
        private sealed class Result
        {
            public string text;
        }
        /// <summary>固定规则夹具入口，不允许外部传反射目标。</summary>
        [RevAutomationAction("game.impact.rules", Environment = "edit", Keys = new string[0], Description = "验收敌方攻速、后排追击、封气与安全整队")]
        public static object ImpactRules(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameCombatImpactChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("RunRules").Invoke(null, null) };
        }
        /// <summary>敌方节奏改动后复用原有全规则和战术回放，覆盖合成、守眼与跨回合契约。</summary>
        [RevAutomationAction("game.impact.regression", Environment = "edit", Keys = new string[0], Description = "运行原有规则与战术回放回归")]
        public static object ImpactRegression(RevAutomationRequest request)
        {
            string result = string.Empty;
            foreach (string name in new[] { "GameRuleChecks", "GameTacticsChecks" })
            {
                Type type = Type.GetType("RevGame.Editor." + name + ", Assembly-CSharp-Editor", true);
                try
                {
                    result += (string)type.GetMethod("Run").Invoke(null, null) + "\n";
                }
                catch (TargetInvocationException error) { throw new InvalidOperationException(name + "：" + (error.InnerException ?? error).Message, error.InnerException ?? error); }
            }
            return new Result { text = result };
        }
        /// <summary>真实Play记录整队、近远程特效与区域波动连续帧，完成后返回大厅。</summary>
        [RevAutomationAction("game.impact.check", Environment = "play", Keys = new string[0], Description = "实际战斗冲击视觉与回收验收")]
        public static object ImpactCheck(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameCombatImpactChecks, Assembly-CSharp-Editor", true);
            PropertyInfo result = type.GetProperty("LastResult");

            if ((string)result.GetValue(null) == "运行中")
            {
                throw new InvalidOperationException("冲击验收正在运行");
            }
            type.GetMethod("Begin").Invoke(null, null);
            return new PlayCheckOperation(result);
        }
    }
}
