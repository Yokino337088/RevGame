# 域所有权和自动回收

业务状态在进入时保存 `RevScope.Enter` 返回的句柄，真正离开时关闭该句柄。挂起的父域保留状态，重开同名域会建立新的所有权。场景加载和业务域切换可以各有阶段，由流程决定何时提交。

## 默认归属

| 入口 | 默认行为 |
| --- | --- |
| `RevResManager.Load`、`LoadHandle`、`LoadAsync`、`AddRef` | 有默认域时按缓存代次和域登记数量。退域归还未提前归还的引用并断开本域的待办回调 |
| `RevPool.Get`、`GetAsync` | 以实际域和 prefab 建池。退域销毁空闲、借出和延迟归还对象，回收池自身的 prefab 引用 |
| `RevUI` | 面板和空闲缓存归实际登记域，退域会先停止转场并收回资源 |
| `RevTimer`、`RevEvent`、`RevMono`、`RevInput` | 省略所有者时归当前域。退出取消计时器、订阅、更新回调和输入锁 |
| `RevServices.Register` | 省略所有者时自动随域注销。容器创建的实例会在注销时释放 |
| `RevSound` | 音效播放和待播状态随域收尾，只在仍被使用或显式预加载时保留音频。BGM 跟随播放器的跨场景生命周期 |

域外的请求沿用显式持有。同一个 prefab 或 AB 可以被多个域使用，释放一方的引用后其余消费者继续工作。`RevResGroup` 与兼容字段 `Reclaim` 用于资源分类，退域按真实所有权回收。

## 业务写法

```csharp
RevScopeHandle battle = RevScope.Enter("Battle");
RevTimer.Every(1f, Refresh);
RevInput.Block();
RevResManager.LoadAsync<Sprite>("UI/Icon", "Hero", ApplyIcon);
// 退出前需要提前放手时，在取得引用的域内调用 Release。
RevScope.Exit(battle);
```

- 由 Bootstrap、全局服务或跨域过场决定寿命的资源使用具名 `LoadOwnedAsync` 租约。保留原租约并在宿主结束时 `Dispose()`。历史匿名手动引用显式选 `RevResLifetime.Manual`，归还时同样选择 Manual。`RevResManager.Get` 只查看当前缓存。
- `Release` 按调用时有效域归还同路径的一份引用，没有匹配域时只处理普通手动引用。具名租约、其它域和独立预加载的引用受到保护。需要精确交接的对象用显式租约。`RevResScope.Track` 接收明确取得的手动引用。
- 输入、事件和服务可以传宿主作为 `owner` 或使用 `RevScope.GlobalOwner`。显式宿主需要随域结束时用 `Attach`。GameObject 池可以用同一个标记显式声明跨域共享。`TakeOverOwner = false` 适合由宿主明确管理的外部流程。
- 项目自有可释放对象交给 `scope.Own(value)`。提前关闭时可先 `Disown`。跨帧业务工作读取 `scope.Cancellation`，作用域关闭时令牌先失效。默认资源回调在原域仍开放时交付，交付时继承原域的默认所有者。跨帧等待由业务在继续执行前检查令牌。

## 预加载与物理回收

下一场景的就绪资源用 `handoff.Resources.PreloadAsync<T>`。交接独立持有引用，资源同时进入原有的来源与首屏记录。交付前由目标面板、池和场景对象取得自己的持有。到 `CleaningUp` 时统一归还预热，失败或取消在结案时也会归还。实体预建应等目标域打开再执行。面向整个会话的 `RevResPreloader` 和显式音频预加载拥有自己的引用。

退域先关入口和任务，再清理 UI、回调、池、资源及服务。逻辑引用同步归还。归零的 AB 后续按每帧最多 8 项、约 1 ms 预算检查并回收，已经开始的单次 AB 操作持续到结束。共享、常驻、重新取得的和正在读包的资源会重新检查。场景交接会在遮挡期间合并 Unity 原生资源整理，局部退出产生的原生整理可在正常空闲维护执行。业务切场调用域和交接门面即可。

## 验证

跨帧请求用 [RevScopedOperations](automatic-delivery.md#作用域请求和-ui) 按实际句柄收尾，切场可用同一专题的交接执行器。长生命周期工作由业务明确提供跨域宿主。

- 通过正式入口运行真实 AB 冷启动与多轮进出，读 `scope.state` 核对域内引用、等待回调、池数和最近各步耗时。交接资源快照会列出具名租约、域和独立预加载的持有。
- 特别覆盖相同路径多域共享、同名重入、独立预加载和旧域同时读取、取消或失败、输入锁及待播音效。核对实际 AssetBundle 和有效画面。引用归零、AB 卸载与 Unity 原生对象内存回落是不同采样时点。
- 同场景、资产、画质和输入比较冷开、重进、退场帧与 Unity 已分配内存。关注清理阶段耗时和有效帧样本数。按已缓存的路径复测主线程托管分配。
