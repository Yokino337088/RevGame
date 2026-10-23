using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>项目级资源审计接入现有RevMCP；调用真实AssetDatabase与项目打包入口，不复制框架打包实现。</summary>
    public static class GameResourceUsageAutomation
    {
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

        [Serializable] private sealed class Result { public string text; }
    }
}
