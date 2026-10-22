# Config

留给"换环境才需要改"的部署配置（多机多进程拓扑、Control Center 地址、数据库地址、本地覆盖）。

注意区分两份东西：

| 配置 | 位置 | 说明 |
|---|---|---|
| `Fantasy.config` | `Server/Entity/Fantasy.config` | 框架启服配置，**必须与直接引用 `Fantasy-Net` 包的 `.csproj` 同级**，由框架在引用包后自动生成；改它不要挪位置 |
| 本目录 | `Config/` | 部署环境的副本 / 变体（例如本机、测试服、线上各一份），以及 Control Center 提供拓扑时的说明文件 |

启用 Control Center 后，`Fantasy.config` 只负责连 Control Center（`<controlCenter enabled="true">`），
Machine / Process / World / Scene / Database 的拓扑由 Control Center 下发，此时本目录放的是各环境地址与凭据说明。
