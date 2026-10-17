// RevITickable.cs —— 可选钩子②：每帧做事
//
// 【什么时候需要它】服务需要按帧推进（冷却计时、表现插值、超时检查……），
//   实现它以后不用自己找 MonoBehaviour 挂 Update，宿主每帧调一次 locator.Tick(dt) 就行。
//
// 【谁会被 Tick】"这个容器自己创建的实例"：根容器 Tick 自己的 Singleton，作用域 Tick 自己的 Scoped ——
//   同一个服务不会既被根又被子作用域 Tick（避免一帧跑两次）。
//
// 【例外处理】OnTick 抛异常会直接抛给宿主（框架不吞异常、也不打日志）：让问题在你写的地方炸出来。

namespace Revolution
{
    /// <summary>
    /// 每帧驱动钩子（可选实现）：宿主每帧调用 <see cref="RevServiceLocator.Tick"/> 时被调用。
    /// </summary>
    public interface RevITickable
    {
        /// <summary>每帧调用一次（按宿主传入的时间步长推进）。</summary>
        void OnTick(float deltaTime);
    }
}
