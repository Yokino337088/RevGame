# Client/Unity

留给"服务端产出、客户端使用"的东西，目前为空。

- 客户端工程在 `RevGame_Client/UnityProject/RevGame.Unity`；
- Fantasy 客户端包已通过 UPM git 依赖安装（`Packages/manifest.json` → `com.fantasy.unity`，tag `2026.1.2003`）；
- 改完 `Tools/NetworkProtocol` 下的 `.proto` 后，用 `Tools/ProtocolExportTool/Run.bat` 导出，
  生成的协议代码放到本目录存档，再同步进 Unity 工程；
- 协议、消息号（OpCode.Cache）、路由（RouteType）与漫游类型（RoamingType）必须服务端与客户端成对一致，
  不一致时先查这三份配置，不要先怀疑网络。
