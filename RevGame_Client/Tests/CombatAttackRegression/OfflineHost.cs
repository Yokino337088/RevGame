// 工程外宿主没有 Unity 资源系统，只补编译入口，不替代任何战斗或配置解析算法。
using System;

namespace UnityEngine
{
    public static class Time { public static float time => 0f; }
    /// <summary>仅让 BoardPos 的坐标转换方法可以编译；本套攻击断言没有使用引擎或世界坐标。</summary>
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public static class Mathf
    {
        public static int FloorToInt(float value)
        {
            return (int)Math.Floor(value);
        }
    }
}

namespace Revolution
{
    /// <summary>外部规则宿主没有全局服务；测试显式构造BattleService，不注册生产持久化服务。</summary>
    public static class RevServices
    {
        public static T Get<T>() where T : class { return null; }
    }
    // 本轮AI只用同步切换；链接真实RevTask和取消源，但禁止测试误用没有Unity宿主的帧调度。
    public static class RevTaskScheduler
    {
        public static RevTask Delay(int milliseconds) { throw new NotSupportedException("离线同步AI测试不调度延迟"); }
        public static RevTask Yield() { throw new NotSupportedException("离线同步AI测试不调度帧等待"); }
        public static RevTask WhenAll(params RevTask[] tasks) { throw new NotSupportedException("离线同步AI测试不调度并行任务"); }
    }
    /// <summary>业务日志由控制台接收；测试失败依靠异常和非零退出码，不能靠看日志猜测。</summary>
    public static class RevLog
    {
        public static void Error(string message, string tag = null) { throw new InvalidOperationException(message); }
        public static void Info(string message, string tag = null)
        {
        }

        public static void Warn(string message, string tag = null)
        {
            Console.Error.WriteLine("[" + tag + "] " + message);
        }

        public static void Exception(Exception error, string message, string tag = null)
        {
            Console.Error.WriteLine("[" + tag + "] " + message + ": " + error);
        }
    }

    /// <summary>未启动游戏域的外部宿主没有默认域，事件测试均显式传 owner，不替代框架回收逻辑。</summary>
    public static class RevScope
    {
        public static object DefaultNode => null;
    }

    /// <summary>
    /// 生成表类的 Instance 属性需要此类型才能编译，但本宿主绝不走运行时资源加载。
    /// 测试只调用真实 GameConfigFileLoader，经真实 RevDataTable 解析器装配磁盘导表数据；
    /// 如果未来测试误用 Instance，这里立即失败，避免一个空返回值掩盖缺失依赖。
    /// </summary>
    public static class RevDataTableManager
    {
        public static T Get<T>()
        {
            throw new InvalidOperationException("工程外回归禁止使用运行时数据表缓存，请通过 GameConfigFileLoader 装配真实数据。");
        }
    }
}
