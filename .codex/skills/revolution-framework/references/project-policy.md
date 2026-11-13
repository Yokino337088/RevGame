# 项目规范检查

把规则写到最接近它实际生效的位置。仓库根 `AGENTS.md` 说明项目级要求与入口。技能按当前任务定位门面、参考实现和验收。Codex 插件的 `SessionStart` 在支持并信任 hooks 的本地会话启动或压缩后恢复简短约定。

项目采用 `.codex/rev-policy.json` 后，Python 检查器可以检查工作区、暂存区或 CI 提交范围。资产检查核对项目目录、`.meta` 配对、变更中的 GUID 与框架快照来源锚点。C# 检查使用 .NET SDK 自带的 Roslyn 读取常用 Unity 条件编译组合，报告项目配置覆盖的分支排版和明确 API 调用。对已有脚本只报告相对于基线新增的诊断。

项目规则由项目维护。材质目录、业务目录、允许的启动入口和框架生成文件以当前仓库配置为准。配置、检查器、Git hook、CI 以及工具限制见 [Revolution Codex 插件说明](https://github.com/Yokino337088/Revolution/blob/main/Plugin/README.md)。

代码审查仍要看生命周期、面板职责、中文注释和业务数值来源。Unity 侧要实际验证 Prefab 绑定、资源依赖、AB、场景转换和业务断言。遇到用户明确批准的规则演进，把真实例外写进可审查的项目配置或检查器并验收。
