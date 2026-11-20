// RevStartupPlan.cs — 统一启动依赖、数据表就绪和失败回收。
// 启动步骤只描述其工作和补偿。校验通过前不执行，失败后按逆序收回本次负责的项。
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>应用或模块宿主持有的主线程启动计划。独立依赖按登记顺序执行。</summary>
    public sealed class RevStartupPlan
    {
        /// <summary>一次可回收的启动声明，工作和补偿由相同宿主提供。</summary>
        private sealed class Step
        {
            /// <summary>计划内唯一键。</summary>
            internal readonly string Id;
            /// <summary>开始前已就绪的步骤键。</summary>
            internal readonly string[] Requires;
            /// <summary>等待本项达到可用状态。</summary>
            internal readonly Func<RevTask> Start;
            /// <summary>回收本项持有，允许在本项只初始化一部分时调用。</summary>
            internal readonly Action Rollback;

            /// <summary>冻结步骤定义，避免执行过程中被外部修改依赖。</summary>
            internal Step(string id, Func<RevTask> start, Action rollback, string[] requires)
            {
                Id = id;
                Start = start;
                Rollback = rollback;
                Requires = (string[])requires.Clone();
            }
        }

        /// <summary>保留声明顺序，依赖相等时由此确定执行顺序。</summary>
        private readonly List<Step> _ordered = new List<Step>();
        /// <summary>预检依赖和查重使用的步骤表。</summary>
        private readonly Dictionary<string, Step> _byId = new Dictionary<string, Step>(StringComparer.Ordinal);
        /// <summary>计划执行期间禁止修改或重复运行。</summary>
        private bool _running;

        /// <summary>声明启动步骤和失败补偿，返回当前计划供继续登记。</summary>
        public RevStartupPlan Add(string id, Func<RevTask> start, Action rollback = null, params string[] requires)
        {
            if (_running)
            {
                throw new InvalidOperationException("启动计划正在执行。");
            }
            if (string.IsNullOrWhiteSpace(id) || start == null || requires == null)
            {
                throw new ArgumentException("启动步骤必须有键、工作和依赖列表。");
            }
            var step = new Step(id, start, rollback, requires);
            _byId.Add(id, step);
            _ordered.Add(step);
            return this;
        }

        /// <summary>使用现有资源系统加载指定表。失败时仅卸载本步骤新建立且仍为当前实例的表。</summary>
        public RevStartupPlan AddTable<T>(params string[] requires) where T : class, RevIDataTable, new()
        {
            T acquired = null;
            return Add(typeof(T).Name, async () =>
            {
                acquired = null;
                bool present = RevDataTableManager.IsLoaded<T>() || RevDataTableManager.HasPendingLoad<T>();
                T loaded = await RevDataTableManager.LoadAsync<T>();
                if (!present)
                {
                    acquired = loaded;
                }
            }, () =>
            {
                if (acquired != null && ReferenceEquals(RevDataTableManager.Get<T>(), acquired))
                {
                    RevDataTableManager.Unload<T>();
                }
                acquired = null;
            }, requires);
        }

        /// <summary>使用现有服务门面登记服务。依赖就绪后按声明决定是否立即初始化。</summary>
        public RevStartupPlan AddService<TService, TImplementation>(object owner, bool eager, params string[] requires)
            where TService : class where TImplementation : class, TService, new()
        {
            bool added = false;
            return Add(typeof(TService).Name, () =>
            {
                added = false;
                if (RevServices.IsRegistered<TService>())
                {
                    throw new InvalidOperationException("启动服务已登记：" + typeof(TService).Name);
                }
                // 立即初始化时若工厂抛异常，仍由补偿检查是否留下登记项。
                added = true;
                RevServices.Register<TService, TImplementation>(owner, eager);
                return RevTask.Completed;
            }, () =>
            {
                if (added)
                {
                    RevServices.Unregister<TService>();
                    added = false;
                }
            }, requires);
        }

        /// <summary>先验证整个依赖图，再运行步骤。失败或中止时按逆序尝试全部已开始步骤的补偿。</summary>
        public async RevTask RunAsync(RevCancellationToken token = null)
        {
            if (_running)
            {
                throw new InvalidOperationException("启动计划正在执行。");
            }
            _running = true;
            var started = new List<Step>(_ordered.Count);
            try
            {
                var sequence = new List<Step>(_ordered.Count);
                var marks = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (Step step in _ordered)
                {
                    Visit(step, marks, sequence);
                }
                foreach (Step step in sequence)
                {
                    token?.ThrowIfCancelled();
                    started.Add(step);
                    await step.Start();
                    token?.ThrowIfCancelled();
                }
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                for (int i = started.Count - 1; i >= 0; --i)
                {
                    try
                    {
                        started[i].Rollback?.Invoke();
                    }
                    catch (Exception cleanupError)
                    {
                        failures.Add(new InvalidOperationException("收回启动步骤失败：" + started[i].Id, cleanupError));
                    }
                }
                if (failures.Count > 1)
                {
                    throw new AggregateException("启动计划未完成且存在清理失败。", failures);
                }
                throw;
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>深度优先生成执行顺序。访问中标记识别闭环，已完成标记避免重复输出。</summary>
        private void Visit(Step step, Dictionary<string, bool> marks, List<Step> sequence)
        {
            if (marks.TryGetValue(step.Id, out bool complete))
            {
                if (!complete)
                {
                    throw new InvalidOperationException("启动依赖闭环：" + step.Id);
                }
                return;
            }
            marks.Add(step.Id, false);
            foreach (string required in step.Requires)
            {
                if (string.IsNullOrWhiteSpace(required) || !_byId.TryGetValue(required, out Step dependency))
                {
                    throw new InvalidOperationException(step.Id + " 缺少启动依赖：" + required);
                }
                Visit(dependency, marks, sequence);
            }
            marks[step.Id] = true;
            sequence.Add(step);
        }
    }
}
