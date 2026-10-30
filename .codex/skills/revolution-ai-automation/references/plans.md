# 跨接口测试计划

RevMCP 计划字段是 name/steps/finally。每步 target/action/arguments，可选 id/confirm/expect/until。与 Automation 的 steps/action/payload 格式不同，不混用。

2026-10-29 起，计划和步骤支持可选 `intent`（最多 512 字符）。用人话说明本计划/本步要验证什么，例如“模拟玩家点击后核对计数增加”。它只写活动日志，不能改变业务参数/权限；步骤 intent 优先于计划。finally 没提供 intent 时按清理契约解释，不冒充 Agent 自述。

```json
{
  "name": "按钮调用真实业务",
  "steps": [
    {"target":"game","action":"demo.reset"},
    {"target":"game","action":"ui.dump","arguments":{"query":"AutomationCanvas/Increment"},"id":"ui","expect":[{"path":"/data/total","equals":1}]},
    {"target":"game","action":"ui.click","arguments":{"id":{"$ref":"ui#/data/items/0/id"}}},
    {"target":"game","action":"demo.state","until":{"path":"/data/count","equals":1,"timeoutSeconds":5,"intervalSeconds":0.2}},
    {"target":"game","action":"runtime.screenshot"}
  ],
  "finally":[{"target":"game","action":"demo.reset","expect":[{"path":"/data/count","equals":0}]}]
}
```

这是 RevAutomationDemo 的真实能力，仅在示例组件运行时存在。测试其他玩法必须重新发现并换成项目实际契约。

JSON Pointer `/data/count` 按完整响应取字段；断言支持 equals 或 exists，不能两者同时提供。`{"$ref":"前一步ID#/data/..."}` 保持原始 JSON 类型，只能引用之前步骤。所有可预检字段先验证，动态引用在调用前再次验证。finally 引用可能因前置失败不存在，应尽量让复位命令不依赖成功步骤。

`until` 是单步只读轮询，超时失败；不会重试 mutation。用于登录/加载时，应查询实际就绪状态，不用固定 sleep 猜时机。整个流程仍要检查业务状态和错误计数。每次调用的单独工单期限由 --timeout 控制，条件期限可能在一个已提交工单到期后才能观察到。

GM 的结果文本在 `/data/message`；结构化能力在 `/data/<字段>`。截图返回本机路径及 MCP image；保存的报告省略重复 base64，不丢截图文件路径。报告路径来自 reportPath，原 Editor 工单在 jobDirectory。
