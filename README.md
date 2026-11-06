# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**0.5.2 容器预览 · RimWorld 1.7 · 网络协议 v4**

独立的共享殖民地联机 Mod，支持多人共同操作同一世界，由**客户端 Mod**和 **headless 常驻模拟专服**组成。游戏端依赖 [Harmony 3](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)；不依赖其他联机 Mod。

## 下载与安装

从 [0.5.2 预览 Release](https://github.com/LumiDust/TurboTick/releases/tag/v0.5.2) 下载同一版本的配套附件（[稳定版 latest](https://github.com/LumiDust/TurboTick/releases/latest) 保持独立）；玩家也可订阅 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013)。固定世界建议保留 Release 原包，避免客户端与服务端在不同时间更新。

| Release 附件 | 用途 |
|---|---|
| `TurboTick-0.5.2-mod-1.7.zip` | 玩家客户端；解压后把完整 `TurboTick` 目录放入游戏 `Mods` |
| `TurboTick-0.5.2-headless-installer.zip` | 专服部署；包含同字节的 TurboTick Mod、SteamCMD 安装器、Compose 配置、容器入口、环境示例、部署说明和 systemd 模板 |

玩家启用 Harmony、TurboTick 和与专服匹配的其他模组后重启游戏。部署包及镜像均不内嵌原版游戏、Harmony 或 Steam 凭据；游戏和官方 Harmony 工坊包由安装阶段获取，也可使用已有的匹配安装。

0.5.2 起专服统一采用 headless：Linux 原生/systemd、Windows 原生，或下述 Linux Docker 镜像。旧独立 .NET 网络服已退役，`turbotick-server` 镜像停止新发布；**≤0.5.1 的历史 Release 附件保留用于回退**。历史网络服世界的迁移方法见[部署指南](https://github.com/LumiDust/TurboTick/blob/main/deploy/README.md#旧独立网络服迁移)。

## 部署专服

| 方式 | 选择与要求 |
|---|---|
| **Docker Compose** | 镜像 `ghcr.io/lumidust/turbotick-headless:0.5.2`，仅 **linux/amd64**；宿主持久化游戏、profile、state 和安装账号缓存 |
| **Linux 原生 / systemd** | SteamCMD、匹配的 Linux 游戏、原生运行依赖、Harmony 和 TurboTick；由 systemd 托管游戏进程 |
| **Windows 原生** | PowerShell 7、SteamCMD、匹配的 Windows 游戏、Harmony 和 TurboTick |

Docker 镜像包含 Ubuntu 运行依赖、SteamCMD bootstrap、容器入口及与本次 Release 相同字节的 TurboTick Mod。首次在交互终端显式运行安装器，完成游戏与 Harmony 安装后，再启动日常服务：

```sh
docker compose --profile setup run --rm installer
docker compose up -d headless
```

这两行须在完成目录权限、环境文件和版本配置后执行。[详细部署指南](https://github.com/LumiDust/TurboTick/blob/main/deploy/README.md)包含从空机器准备 Docker/Compose、Steam Guard 登录、代理、卷权限到健康检查的完整步骤。默认 `headless` 服务只运行已有安装，启动时不自动更新游戏、不登录 Steam；`installer` 只在显式选择 `setup` profile 时运行。ARM/arm64 不在此镜像发布范围内，旧镜像的架构支持不沿用。

## 主持与加入

1. 启动专服，确认健康状态；所有玩家连接同一地址，例如 `SERVER_HOST:30572`。
2. 首次开局时，主持者进入「多人游戏 → 主持游戏」，填写地址和端口，连接并申请当前局的房主身份。
3. 房主选择已有存档、新建殖民地或使用当前单人殖民地，再将世界发布到原连接的专服。
4. 其他玩家通过「加入游戏」连接，自动下载快照并进入。专服已有世界时直接加入该世界。

暂停、速度使用原版按钮和快捷键，由专服确认后同步。可折叠玩家面板显示身份、延迟、地图和同步状态；同一地图能看到其他玩家的彩色指针与名字。

已接入标记、建造与取消、小人征召与指令、禁用、工作优先级、区域、配方等操作同步，以及快照续传、检查点与状态校验。交易、远行队默认限制房主操作，运输舱同步为实验开关。

专服通过 `RimWorld -headless -turbotick-server` 执行完整模拟和检查点，全部玩家离开后仍可推进世界。它仍是 **lockstep**：每个客户端也执行完整模拟。新玩家加入时，常驻模拟建立共同检查点，所有已连接模拟副本一起重载后继续，期间会有一次加载停顿。隐藏的常驻模拟身份不占房主名额，也不计入在线玩家。

## 版本、权限与连接

- 客户端与专服使用 **0.5.2 / 协议 v4**。使用 `:0.5.2` 镜像时，所有玩家都部署该 Release 的 `TurboTick-0.5.2-mod-1.7.zip`；自行重新编译的 DLL 也应统一分发同一份完整包。
- 握手校验**精确 GameBuild、DLC、模组内容、加载顺序和配置**，包括程序集和 XML。仅“都是 1.7”、版本显示相同或模组同名仍可能得到 `ModHashMismatch`。
- 已持久化世界继续使用原有兼容指纹。换新镜像不会自动迁移旧 world 指纹；更新前保留原镜像版本、游戏及完整模组包、profile/state 备份，按[升级与回滚流程](https://github.com/LumiDust/TurboTick/blob/main/deploy/README.md#备份版本固定升级与回滚)处理。
- 房主、倍率控制者、检查点所有者分别由服务器管理。管理采用运行时配置的**共享密码文件**：设置 `TURBOTICK_ADMIN_PASSWORD_FILE` 后，在大厅「管理服务器」独立认证，可查看状态、归档旧局并重开空房间；未配置密码时管理功能关闭。客户端不保存管理密码。
- SteamCMD 登录只用于安装和更新。玩家名、SteamID 字符串、创建存档和 SteamCMD 缓存都不授予管理权限。
- 默认端口为 **30572/TCP**。公网直连需要可达地址、端口映射及防火墙放行；当前游戏和管理 TCP 通道未内置 TLS，需要链路加密时可通过 VPN 或隧道承载连接。

## 已验证范围

基础 headless 模拟已在 **RimWorld 1.7.5000 rev1275** 上进行 Windows 原生客户端与 Linux headless 的实机验收，使用 Core、Harmony、TurboTick 的匹配组合。tick **120–21780** 范围内的 **334 个同 tick 共同采样**，状态哈希与 RNG 全部一致；同时验证了晚加入、暂停恢复、全部外部玩家退出后继续推进，以及检查点停机与重启恢复。

0.5.2 容器入口的离线契约、实际游戏启动、健康检查与 SIGTERM 流程已验证；新增随机存档的双端长跑仍出现锁步分叉，故本版按预览发布，故障时挂起时间线，未替换既有正式服务。上述 334 个匹配采样属于此前特定存档的结果，不代表其他存档长期一致。每套部署应先在独立 profile/state 副本中验证连接、发布、晚加入和停机恢复。

## 源码导航与构建

| 目录 | 内容 |
|---|---|
| [Source](https://github.com/LumiDust/TurboTick/tree/main/Source) | 游戏接入、Harmony 补丁、多人界面与 headless 运行角色 |
| [Networking](https://github.com/LumiDust/TurboTick/tree/main/Networking) | TCP 协议、客户端、专服、快照与管理认证 |
| [Simulation](https://github.com/LumiDust/TurboTick/tree/main/Simulation) | 锁步时间线、共享操作和确定性状态 |
| [Tests](https://github.com/LumiDust/TurboTick/tree/main/Tests) | 模拟回归、打包检查及真实 TCP 集成验证 |
| [scripts](https://github.com/LumiDust/TurboTick/tree/main/scripts) / [scripts/steamcmd](https://github.com/LumiDust/TurboTick/tree/main/scripts/steamcmd) | 构建打包、Mod 部署、SteamCMD 安装器及离线验证 |
| [deploy](https://github.com/LumiDust/TurboTick/tree/main/deploy) | 公开部署说明、容器运行入口和 systemd 模板 |
| [About](https://github.com/LumiDust/TurboTick/tree/main/About) / [Languages](https://github.com/LumiDust/TurboTick/tree/main/Languages) | Mod 元信息、图片和本地化资源 |

源码构建需要 **.NET 10 SDK** 和 **PowerShell 7**。默认通过 NuGet 获取 1.7 引用程序集；也可追加 `-p:RimWorldDir="GAME_ASSEMBLIES_DIR"`，指向含四个游戏引用程序集的目录。

```powershell
dotnet build Source/TurboTick.csproj -c Release
dotnet run --project Tests/TurboTick.Tests.csproj -c Release
dotnet run --project Tests/Networking/TurboTick.Networking.Tests.csproj -c Release
pwsh scripts/prepare-workshop.ps1 -PackageDirectory . -OutputDirectory "GAME_DIR/Mods/TurboTick"
```

替换 DLL 前退出游戏，部署后重启。部署脚本保留目标目录已有的工坊发布 ID。完整构建、回归和两类 Release 附件由下列命令生成，输出到 `artifacts`：

```powershell
pwsh scripts/package.ps1
```

SteamCMD 安装器的离线验证入口为 [test-install-server.ps1](https://github.com/LumiDust/TurboTick/blob/main/scripts/steamcmd/test-install-server.ps1)；其通过表示安装器控制流和生成物符合契约，实际 Steam 下载和游戏运行另行验证。镜像构建也与 Release 使用同一份已打包 Mod；构建上下文不应包含游戏、Harmony、账号缓存、密码、存档或日志。仓库跟踪源码、必要测试、运行配置及 Mod 资源。

## 致谢

感谢 [Multiplayer](https://github.com/rwmt/Multiplayer) 与 [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together) 的设计参考。TurboTick 不依赖它们的运行时。

[MIT License](LICENSE)
