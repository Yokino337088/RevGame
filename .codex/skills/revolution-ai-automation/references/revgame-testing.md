# RevGame：自动化测试规范与工程样本

## 选择实际示例

业务自动化先参考 [RevGame GitHub工程](https://github.com/Yokino337088/RevGame)，阅读目标相近的动作接线与断言，再适配目标项目实际注册能力。基线提交 `ed8e3627963d3eabc21b9ef21806e9445cb1f8fb`（2026-10-28）；main可能继续变动，不用历史通过率替代本次运行。

下面路径相对 `RevGame_Client/UnityProject/RevGame.Unity/Assets/`：

| 要测什么 | 实际样本 | 应参考的内容 |
|---|---|---|
| 玩家操作输入 | `GameScripts/Automation/Input/GamePointerInput.cs` | EventSystem射线、按下/松开、遮挡与失效检查，不直接Invoke按钮 |
| 开发态GM | `GameScripts/Bootstrap/Diagnostics/GameGmCommands.cs` | 唯一注册账本、限定构建、正式Logic/Service动作、明确拒绝 |
| 运行时能力/存档隔离 | `GameScripts/Bootstrap/Diagnostics/StoryAutomationCommands.cs` | 实际结构化状态、进度与积分隔离，避免嵌套已持有的预览租约 |
| Editor动作桥 | `Editor/RevGame/ResourceBuild/GameHelperAutomation.cs` | 显式动作ID、Environment/Keys、程序集边界；只调用固定项目检查 |
| 规则正反例 | `GameScripts/Services/Battle/Diagnostics/BattleService.HelperChecks.cs` | 真实服务固定输入、上限不扣牌、吞噬原子性、目标、积分与失误失败 |
| 实际UI与动态画面 | `Editor/RevGame/GameHelperPlayChecks.cs` | 隔离Play、实际指针、帧/材质/可见几何、六章连拍与退出回收 |
| 长时间战术回放 | `Editor/RevGame/Balance/GameBalanceCampaignChecks.cs`、`GameScripts/Automation/Balance/GameBalanceTactics.cs` | 固定种子、真实手牌、正常策略和换位、记录实际命中/死亡/胜负 |
| 冷AB与重进场景 | `Editor/RevGame/Scenes/GameSceneTransitionChecks.cs` | 首次图集绑定、过场交付与反复出入，不能先预热全图集 |
| 独立控制台 | 仓库 `RevGame_Client/Tests/CombatAttackRegression/` | 链接实际源码/解析器/FSM，隔离Unity宿主；不是图形/设备验收 |

直接源码入口：[Play检查](https://github.com/Yokino337088/RevGame/blob/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/GameHelperPlayChecks.cs)、[玩家指针](https://github.com/Yokino337088/RevGame/blob/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Automation/Input/GamePointerInput.cs)、[最新版本报告](https://github.com/Yokino337088/RevGame/blob/main/RevGame_Client/文档/进度同步/助阵帮手与棋盘管理-2026-10-28/助阵帮手与棋盘管理测试记录_2026-10-28.html)。

测试源码也是待审查参考：它包含项目专用路径、固定高生命视觉夹具、实际反射接线和历史缺陷，不复制成无条件通用标准。

## 验收契约

| 功能 | 正常后置 | 失败后置 | 生命周期/视觉 |
|---|---|---|---|
| 真实落牌 | 牌数/额度各减一、对应棋种占格 | 超限/非法点不扣牌、不占格 | 按钮/落点可点击、视图实际显示 |
| 手动移除 | 占格/名单/选择撤销，不给击杀分 | 敌棋/结算不允许移除 | Tween回池；最后玩家棋移除后真实判负 |
| Boss出生/吞噬 | 完整大身体与整队计划成功才提交 | 规划失败保留旧名单/阶段 | 出生结束UI还原，45空格总数不代表3×3空间成立 |
| 帮手积分 | 实际命中入账、开火原子扣款 | 余额不足不发射；返分不足自给自足 | 积分飞星不二次计分，目标死亡/退出后回调失效 |
| 远程共鸣 | 普攻伤害与攻击进度实际变化 | 不影响近战/敌方/环境；重开清零 | HUD值与实际加成一致，旧版本数据失效 |
| Shader/序列帧 | 真实当前帧贴图与局部UV | 白图/材质未就绪明确失败 | 四帧实播、菱形/光束连拍可见，退出无残留 |

## RevMCP实际计划示例

以下是在RevGame隔离Play已经完成测试、准备收尾时使用的契约。`story.state` 是项目能力，并非其它游戏自动存在；先discover确认目标与schema。

```json
{
  "name": "RevGame隔离测试收尾",
  "steps": [
    {
      "target": "editor",
      "action": "runtime.call",
      "arguments": {"runtimeAction": "story.state", "argumentsJson": "{}"},
      "expect": [
        {"path": "/data/screen", "equals": "Home"},
        {"path": "/data/transitioning", "equals": false},
        {"path": "/data/isolated", "equals": true}
      ]
    }
  ],
  "finally": [
    {"target": "editor", "action": "editor.stop", "arguments": {}},
    {"target": "editor", "action": "game.story.test-disarm", "arguments": {}}
  ]
}
```

- `path` 使用完整响应的JSON Pointer，不能写 `data.screen`。
- 接受进入/返回请求后仍等transitioning=false并核对Screen。样本先断言已回Home，再finally停Play和解除隔离。
- `game.helper.state`、`game.balance.state`示例返回 `data.text` 中的JSON字符串，不是原生data.status；调用者要按真实响应解析。未声明readOnly时不能硬塞until轮询，缺接口应补稳定只读状态或有界外部查询。
- 同一实例一个变更作业。轮询不与另一个任务stop/play、场景预览、资产变更争用；IO sharing violation、Reload、退出Play必须记录，不把工具错误从unityErrors中删除。

## 战术与性能数据如何表述

固定种子/启发式布阵可以验证可重复战斗，不能称LLM每帧实时决策。六章60回合是RevGame配置，不是其它项目固定测试规模。正常难度不启用攻击/血量GM；视觉高生命/强制技能只是独立表现夹具，报告披露并与胜率分开。

一轮实际60局58胜2负可以报告完成覆盖与失败位置，不能报告“全通过”。三种子服务180局全胜不证明真实UI、帧率或Android设备。历史278条断言包含重复逐帧检查，不等于278个独立场景。报告引用原始版本、平台、输入、采样单位、阈值和清理结果，不能把已过期数字推广为框架性能承诺。

## 证据策略

RevGame约定：文档目录只保留HTML/摘要与必要截图、GIF；原始工单/JSON/CSV/Profiler放忽略的 `.codex/work/`，不生成ZIP，`.codex/`不提交。对其它项目，用户和AGENTS证据/提交规则优先；不因Skill默认归档要求而违背它。

截图来自真实运行，不补画、不拼接。只有控制台或只读工具检查时不伪造截图/GIF，明确未执行视觉。重跑保留首次失败，最新HTML只对最终版本作结论，图片/链接检查后再交付。
