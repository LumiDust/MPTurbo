# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**0.5.1 · RimWorld 1.7 · 网络协议 v4**

独立的共享殖民地联机 Mod，支持多人共同操作同一世界，包含游戏客户端、独立网络服务器和 headless 常驻模拟宿主。游戏端依赖 [Harmony 3](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)；不依赖其他联机 Mod。

## 下载与安装

从 [GitHub Releases](https://github.com/LumiDust/TurboTick/releases/latest) 获取配套附件，或订阅 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013)。

| Release 附件 | 用途 |
|---|---|
| `TurboTick-0.5.1-mod-1.7.zip` | 玩家客户端；解压后把完整 `TurboTick` 目录放入游戏 `Mods` |
| `TurboTick-0.5.1-server.zip` | 独立 .NET 网络服务器；无需安装游戏，运行需 .NET 10 Runtime |
| `TurboTick-0.5.1-headless-installer.zip` | 常驻模拟部署；包含匹配的 TurboTick、SteamCMD 安装脚本、部署说明和 systemd 模板 |

启用 Harmony 和 TurboTick 后重启游戏。headless 附件通过另行安装的 SteamCMD 下载游戏；所有附件均不内嵌 Harmony 或原版游戏文件。

## 主持与加入

1. 启动一台专用服务器，所有玩家连接同一地址，例如 `SERVER_HOST:30572`。
2. 首次开局时，主持者进入「多人游戏 → 主持游戏」，填写地址和端口，连接并申请当前局的房主身份。
3. 房主选择已有存档、新建殖民地或使用当前单人殖民地，再将世界发布到原连接的专服。
4. 其他玩家通过「加入游戏」连接，自动下载快照并进入。专服已有世界时直接加入该世界。

暂停、速度使用原版按钮和快捷键，由专服确认后同步。可折叠玩家面板显示身份、延迟、地图和同步状态；同一地图能看到其他玩家的彩色指针与名字。

已接入标记、建造与取消、小人征召与指令、禁用、工作优先级、区域、配方等操作同步，以及快照续传、检查点与状态校验。交易、远行队默认限制房主操作，运输舱同步为实验开关。

## 选择服务器形态

| 形态 | 模拟与检查点 | 部署要求 |
|---|---|---|
| **headless 常驻模拟** | 服务端与玩家共同执行锁步模拟，服务端保存检查点；全部玩家离开后仍可推进世界 | SteamCMD 下载的匹配游戏、Harmony、TurboTick 及相同模组组合 |
| **独立 .NET 网络服务器 / Docker** | 协调时间线、转发操作、保存玩家上传的检查点，游戏模拟由玩家执行 | .NET 10 Runtime，或已含运行时的 Docker 镜像；无需游戏安装 |

headless 使用 `RimWorld -headless -turbotick-server`。它仍是 **lockstep**：客户端也执行完整模拟，并非 thin client。新玩家加入时，常驻模拟建立共同检查点，所有已连接模拟副本一起重载后继续，期间会有一次加载停顿。隐藏的常驻模拟身份不占房主名额，也不计入在线玩家。

完整的 SteamCMD 登录、Harmony 获取、启动、systemd、管理及恢复步骤见 [部署指南](https://github.com/LumiDust/TurboTick/blob/main/deploy/README.md)。原有独立网络服可直接运行：

```sh
dotnet TurboTick.Server.dll --host 0.0.0.0 --port 30572 --data Saved
```

或使用 Docker：

```sh
docker run -d --name turbotick-server --restart unless-stopped \
  -p 30572:30572 -v turbotick-data:/data \
  ghcr.io/lumidust/turbotick-server:0.5.1
```

[Compose 配置](https://github.com/LumiDust/TurboTick/blob/main/docker-compose.yml) 同样部署独立网络服，不包含 headless 游戏进程。容器更新时保留数据卷，并让镜像标签与客户端版本一致。

## 版本、权限与连接

- 客户端与专服配套使用 **0.5.1 / 协议 v4**。还会校验完整游戏 build 和模组指纹，包括程序集、XML、加载顺序及配置；仅显示“1.7”或使用同名 Mod 并不足以匹配。重建 DLL 后也应把同一份包同步给服务端和所有玩家。
- 独立网络服首次有效连接锁定兼容配置；headless 以已安装的游戏/DLC/模组为准，恢复持久化世界时继续校验。更换模组需同步部署并重启；归档重开不会自动安装或热载入另一套模组。
- 房主、倍率控制者、检查点所有者分别由服务器管理。管理使用服务器配置的**共享密码**，与首位加入者或创建存档者无关。设置 `TURBOTICK_ADMIN_PASSWORD_FILE` 后，在大厅「管理服务器」独立认证，可查看状态、归档旧局并重开空房间；未配置密码时管理功能关闭。
- 客户端不保存管理密码。SteamCMD 登录仅用于安装和更新；玩家名、SteamID 字符串不授予管理权限。
- 默认连接端口为 **30572/TCP**。公网直连需要可达地址、对应端口映射与防火墙放行；当前游戏和管理 TCP 通道未内置 TLS。需要链路加密时，可通过 VPN 或隧道承载连接。

## 已验证范围

0.5.1 已在 **RimWorld 1.7.5000 rev1275** 上进行 Windows 原生客户端与 Linux headless 的实机验收，使用 Core、Harmony、TurboTick 的匹配组合。tick **120–21780** 范围内的 **334 个同 tick 共同采样**，状态哈希与 RNG 全部一致；同时验证了晚加入、暂停恢复、全部外部玩家退出后继续推进，以及检查点停机与重启恢复。

这是特定 build、模组组合与测试时段的结果。第三方 Mod、任意操作组合及长期运行仍需分别验证。

## 源码导航与构建

| 目录 | 内容 |
|---|---|
| [Source](https://github.com/LumiDust/TurboTick/tree/main/Source) | 游戏接入、Harmony 补丁、多人界面与 headless 运行角色 |
| [Networking](https://github.com/LumiDust/TurboTick/tree/main/Networking) | TCP 协议、客户端、专服、快照与管理认证 |
| [Simulation](https://github.com/LumiDust/TurboTick/tree/main/Simulation) | 锁步时间线、共享操作和确定性状态 |
| [Server](https://github.com/LumiDust/TurboTick/tree/main/Server) | 独立 .NET 网络服务器入口 |
| [Tests](https://github.com/LumiDust/TurboTick/tree/main/Tests) / [Server/Verification](https://github.com/LumiDust/TurboTick/tree/main/Server/Verification) | 模拟回归与真实 TCP 集成验证 |
| [scripts](https://github.com/LumiDust/TurboTick/tree/main/scripts) / [scripts/steamcmd](https://github.com/LumiDust/TurboTick/tree/main/scripts/steamcmd) | 构建打包、Mod 部署、SteamCMD 安装器及其离线测试 |
| [deploy](https://github.com/LumiDust/TurboTick/tree/main/deploy) | 公开部署说明与 systemd 模板 |
| [About](https://github.com/LumiDust/TurboTick/tree/main/About) / [Languages](https://github.com/LumiDust/TurboTick/tree/main/Languages) | Mod 元信息、图片和本地化资源 |

源码构建需要 **.NET 10 SDK** 和 **PowerShell 7**。默认通过 NuGet 获取 1.7 引用程序集；也可追加 `-p:RimWorldDir="GAME_ASSEMBLIES_DIR"`，指向含四个游戏引用程序集的目录。

```powershell
dotnet build Source/TurboTick.csproj -c Release
dotnet run --project Tests/TurboTick.Tests.csproj -c Release
dotnet run --project Server/Verification/TurboTick.Networking.Verification.csproj -c Release
pwsh scripts/prepare-workshop.ps1 -PackageDirectory . -OutputDirectory "GAME_DIR/Mods/TurboTick"
```

替换 DLL 前退出游戏，部署后重启。部署脚本保留目标目录已有的工坊发布 ID。完整构建、回归和三类 Release 附件由下列命令生成，输出到 `artifacts`：

```powershell
pwsh scripts/package.ps1
```

SteamCMD 安装器的离线验证入口为 [test-install-server.ps1](https://github.com/LumiDust/TurboTick/blob/main/scripts/steamcmd/test-install-server.ps1)；其通过表示安装器控制流和生成物符合契约，游戏运行需单独实测。仓库跟踪源码、必要测试、运行配置及 Mod 资源；本地研究记录、存档、日志、备份和构建产物不纳入发布源码。

## 致谢

感谢 [Multiplayer](https://github.com/rwmt/Multiplayer) 与 [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together) 的设计参考。TurboTick 不依赖它们的运行时。

[MIT License](LICENSE)
