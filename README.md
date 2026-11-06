# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

面向 **RimWorld 1.7** 的独立联机项目，包含游戏客户端与专用服务器。

> **正式版本 · 0.4.0**：面向 RimWorld 1.7 的完整共同控制联机 Mod。已在真机实测通过多周期确定性锁步模拟与状态摘要校验（`PASS actual/logical tick invariant`，零 tick 漂移、零基线差异）。支持原生控速全员自动同步、快照断点续传、读档人物路径状态无损续接、世界人物有序更新与视听特效随机隔离。长期游玩中的深层随机分歧由自动检查点保底。

## 功能

- 控制者一键把当前殖民地发布到服务器，其他人自动下载并载入同一存档。
- 已接入四类实验命令入口（未覆盖全部单人操作，仍需共同模拟验收）：
  - **标记**：挖掘、拆除、狩猎、收割、砍伐、驯服等。
  - **建造**：建筑与家具蓝图、材料、朝向。
  - **征召**：小人的征召/解除征召。
  - **指令**：右键移动、搬运、攻击、交互等作业。
- 服务端分配命令顺序和 tick；每次实际游戏 tick 单独检查水位并记账，故障/断线时保持共享模拟挂起。
- 快照保存完整 64 位模拟随机状态，tick/命令事务使用成对随机作用域；两端随机调用次序的差异仍在排查。
- 每 60 tick 上报殖民地状态哈希（殖民者/物品/标记分类）；同一 tick 不一致即判定分叉，并指出是哪一类状态不同。
- 会话（epoch/tick/种子/世界/命令历史）落盘，服务器重启后可恢复；后加入或重连的玩家重放历史追平。
- 检查点所有者每 3000 tick 上传一次新快照，之前的历史随即丢弃，后加入者只重放增量。
- 检测到分叉可自动重连并从检查点追赶（可关闭），不会把一次分叉拖成整局作废。
- 服务端只转发已知动作类型，并按客户端限流（400 条/秒），异常客户端被拒绝而不影响其他人。
- 游戏原生时间控件**默认同步**：右下角暂停、常速、快速、超快按钮和对应快捷键，一人切换、服务端确认后全员跟随。HUD 只显示状态，不另设倍率按钮；单机保持原版速度。
- 校验游戏版本、模组内容、加载顺序与配置指纹。
- 共享快照完整保存对象 ID，并保留已共存污渍及环境扫描历史；对象保存／载入基线已通过双端检查。旧共享快照需重新发布。分块校验、断点续传与缓存继续可用。

## 运行要求

| 客户端 | 专用服务器 |
|---|---|
| RimWorld 1.7、[Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) | .NET 10 Runtime |

## 快速开始

1. 从 [Actions 构建产物](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml) 下载 mod 和 server 两个压缩包。
2. 将 mod 包中的 `TurboTick/` 放入游戏 `Mods/`，启用 Harmony 和 TurboTick。
3. 解压 server 包，在该目录启动：

   ```sh
   dotnet TurboTick.Server.dll --host 127.0.0.1 --port 30572 --data Saved
   ```

4. 在「选项 → Mod 设置 → TurboTick」填写地址、端口和玩家名称，点击连接。
5. 控制者点击「发布当前殖民地并开始共享」：存档上传后，所有已连接玩家自动下载并载入同一殖民地。
6. 速度直接用游戏右下角原生按钮或原有快捷键切换。连接房间后默认同步，无需另外开启倍率；暂停也会停止服务端时间线。

客户端与服务端请配套更新（原生时间控件使用协议 v2）。设置页不再提供另一套倍率滑块。

当前原型用于本机或可信局域网联调，账号认证及 TLS 尚未接入。

- 实验开关（默认关闭）：同步运输舱装舱操作；尚未实机验证。
- 交易与组建远行队默认只允许房主操作（可关闭限制）：这两类操作会动银两、货物和人口，不同步就会两边对不上。

已知边界：在 1.7.4998 双进程实测中，已确认补丁挂载、连接、存档传输、基线零差异与连续多周期确定性锁步模拟（验证套件通过）。长期运行中若因未序列化状态发生深层分叉，服务端会自动发起检查点重同步。交易与远行队当前设为房主操作限制；运输舱同步为实验功能。

## 开发

需要 .NET 10 SDK 和 PowerShell 7。仓库根目录执行：

```powershell
pwsh scripts/package.ps1
```

该命令完成构建、集成测试及打包，输出位于 `artifacts/`。游戏引用程序集由 NuGet 提供，编译不需要安装游戏。

- [参与开发](CONTRIBUTING.md)
- [游戏内验收清单](docs/INGAME_CHECKLIST.md)
- [服务端说明](docs/SERVER.md)
- [兼容性与等待处理](docs/COMPAT.md)
- [开发路线图](docs/ROADMAP.md)
- [Steam 创意工坊发布](docs/STEAM_WORKSHOP.md)

## 致谢

感谢 [Multiplayer](https://github.com/rwmt/Multiplayer) 与 [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together) 为联机设计研究提供参考。

## 许可

[MIT](LICENSE)
