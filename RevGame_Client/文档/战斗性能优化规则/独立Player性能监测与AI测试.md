# 独立Player性能监测与AI测试

2026-10-31。正式性能结论以目标设备独立Player为准，Editor用于接线和热点诊断；Mono、IL2CPP、Development与非开发包分别报告。

## 游戏内面板

内部测试包按F8打开/关闭。右上角Fold折叠、Close关闭；可用启动参数`-revPerformance`在首页就绪后自动打开。面板由RevUI异步加载真实`UI/Panel/Diagnostics/PerformanceMonitorPanel`资源，使用独立`ui_diagnostics`包。

显示最近窗口平均FPS、P50/P95/P99/最大帧间隔、GPU帧时、Draw Calls、Batches、SetPass、三角形/顶点、动态/静态/实例化批次、GC分配与内存、主/渲染线程、双方棋子数量、分辨率/设备/构建类型/焦点状态。缺失指标为N/A，不能当零成本。Release缺少部分详细合批指标属于引擎边界。

面板每0.5秒读取框架同一采样器，关闭后不再格式化文字。固定性能窗口应隐藏面板；截图与GIF在窗口外记录。面板不会修改VSync、帧率上限、分辨率、难度或反馈预算。

## 构建顺序

1. 修改界面/代码后运行项目字体工具`py -3.13 tools/font-subset.py`；缺字门禁失败必须修复，不能绕过。
2. 首次通过RevMCP `rev_discover`发现能力；调用`game.performance.configure`制作Prefab并更新项目资源映射。
3. 使用项目`game.background.build(target=StandaloneWindows64)`完成依赖校验、PC AB构建和发布到StreamingAssets。单独`ab.build`受copyToStreamingAssets配置影响，不能假定已经发布。
4. Player构建前确认StreamingAssets/PC/ui_diagnostics真实存在。必要时`game.performance.player-preflight`移除错误的Editor DontSave字体Shader引用，保存并报告当前设置。
5. `build.player`指定目标、完整exe路径、backend=il2cpp、development=false、automation=true、frameTiming=true。输出专用空目录，保留失败报告，不覆盖旧包。没有通过BuildReport不能称打包成功。

通用CLI例子：

```powershell
py -3.13 RevGame_Client/UnityProject/Revolution.RevCLI/rev.py --project RevGame_Client/UnityProject/RevGame.Unity --mode live --timeout 3600 build player --target StandaloneWindows64 --path D:/Builds/RevGame/run01/RevGame.exe --backend il2cpp --automation --frame-timing
```

本机Unity 2022.3.15f1c1首次IL2CPP构建识别VS2026为UNKNOWN；这不等于没有安装C++编译器，但意味着该Unity构建器当前不能使用它。需要兼容工具链后重验，不能用Mono成功代替IL2CPP验收。

## Player与AI连接

launch使用新的会话目录与独立端口，参数数组包含`-revTestSession`，确保业务初始化前隔离剧情/积分；不修改生产存档。默认跳过开场教学仅限隔离会话。测试包非开发时项目GM/夹具使用REV_AUTOMATION条件，正常发布不注册这些入口。

```powershell
py -3.13 RevGame_Client/UnityProject/Revolution.RevCLI/rev.py player launch --executable D:/Builds/RevGame/run01/RevGame.exe --directory D:/Evidence/RevGame/session01 --arguments '["-revTestSession","-revPerformance"]'
py -3.13 RevGame_Client/UnityProject/Revolution.RevMCP/rev_mcp.py --runtime-endpoint D:/Evidence/RevGame/session01/runtime.json serve
```

过程必须在对话中展示：rev_discover → rev_call(runtime.identity/story.state/完整GM) → rev_run(带断言与finally)。身份要匹配本次buildId/pid/sessionId，runtime.json含token，不能提交或粘贴到报告。

## 项目压力夹具

- `game.performance.prepare`参数chapter=1..6、pieces=48或81；仅隔离会话允许。接受后查询`game.performance.state`直到ready=true。每秒补血维持负载，81棋围困免疫是压力夹具条件，不代表正式难度。
- 通用采样用`runtime.performance.begin`（seconds、warmup）和只读`runtime.performance.state`。核对唯一runId、实际时长、frames、errors、取消状态；再核对项目真实棋数/攻击次数，不能用空盘高FPS通过。
- finally先取消采样，再`game.performance.cleanup`，等state.cleaned=true、logic=0、pieceRefs=0；隐藏面板，释放Recorder。完成业务收尾后再`player.stop`退出本次进程。
- 面板动作`game.performance.panel(visible=true/false)`只接受请求，等`game.performance.panel-state`确认visible/opening，不能把加载请求当显示成功。

报告保留第一次失败、完整环境与构建类型。原始机器证据放`.codex/work/player-2026-10-31/`；不提交安装包、runtime.json、Profiler录制，不生成ZIP。框架升级经`tools/sync-framework.ps1`增量同步；RevResPath、RevSoundPath、ResMap及meta受保护。新增面板对应的映射只由本项目资源生成器更新。
