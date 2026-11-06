# RevGame TCP 接收兼容补丁

来源：Fantasy.Unity 2026.1.2003，Git 解析版本 e9004b15c3；从项目已解析的同版本包内嵌，保留原始源码与 meta。

唯一运行时代码修改：TCPClientNetwork.ReceiveSocketAsync 中 `UNITY_2021` 条件扩展为 `UNITY_2021 || UNITY_2022`，复用上游已有 ArraySegment/ReceiveFromAsync 路径。服务器协议和版本不变，不修改 Library/PackageCache。

原因：2026-11-06 在 Unity 2022.3.15f1c1 Editor 中对固定输入 `13-27-00-18-FE-80-31-42` 做真实回环 TCP 对照，Memory ReceiveAsync 返回 count=8，但目标数组全零；ArraySegment ReceiveAsync 返回相同长度且字节完全一致。项目动作 `game.pvp.socket-check` / `game.pvp.socket-check-state` 可复现。

范围：此对照证明当前编辑器的 Memory 接收异常，不代表全部 Unity 2022 平台均有问题，也不替代 Fantasy 真实握手、对局与移动端验收。升级上游时先重跑对照与真实网络回归，再决定是否移除此兼容分支。
