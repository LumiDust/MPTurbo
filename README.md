# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**0.5.0 · RimWorld 1.7 · 网络协议 v4**

独立的共享殖民地联机 Mod，包含游戏客户端和专用服务器。客户端需要 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)，专服运行需要 .NET 10；Docker 镜像已包含运行时。

## 主持与加入

1. 从 [Releases](https://github.com/LumiDust/TurboTick/releases/latest) 获取客户端，或订阅 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013)。手动安装时把 `TurboTick` 目录放入游戏 `Mods`，启用后重启游戏。
2. 启动专用服务器。房主和其他玩家都连接这台专服。
3. 房主打开主菜单「多人游戏 → 主持游戏」，填写专服地址、端口和玩家名，连接并取得房主身份。
4. 房主选择已有存档、新建殖民地或当前单人殖民地，完成后发布到该专服。
5. 其他玩家通过「加入游戏」连接同一地址，自动下载存档并进入。专服已有运行中的世界时会继续该世界。

游戏内提供可折叠玩家面板，显示身份、延迟、地图与同步状态。同一地图显示其他玩家的彩色指针和名字。暂停与速度使用原版按钮和快捷键，由专服确认后同步。

已接入标记、建造与取消、小人征召与指令、禁用、工作优先级、区域和配方等操作同步，以及快照续传、检查点与状态校验。交易与远行队默认限制房主操作，运输舱同步为实验开关。第三方 Mod 和长期游玩仍需按实际模组组合验证。

## 专用服务器

Docker：

```sh
docker run -d --name turbotick-server --restart unless-stopped \
  -p 30572:30572 -v turbotick-data:/data \
  ghcr.io/lumidust/turbotick-server:0.5.0
```

将 Compose 的版本标签更新为与客户端一致后，更新容器并保留数据卷：

```sh
docker compose pull
docker compose up -d
```

也可从 Releases 下载服务器，解压后运行：

```sh
dotnet TurboTick.Server.dll --host 0.0.0.0 --port 30572 --data Saved
```

容器环境变量：`TURBOTICK_HOST`、`TURBOTICK_PORT`、`TURBOTICK_DATA`、`TURBOTICK_CONTROLLER_ONLY`。原生启动参数可用 `--help` 查看。首次有效连接会锁定完整游戏版本和模组指纹；所有玩家需使用相同版本、模组、顺序和配置。

房主、时间控制权限与检查点所有者分别由专服管理。房主离开不关闭专服，运行局可转交房主。客户端与专服需配套更新到 **0.5.0 / 协议 v4**。账号认证和 TLS 尚未接入，适合可信网络环境。

## 构建与本地部署

需要 .NET 10 SDK 和 PowerShell 7。引用程序集默认通过 NuGet 获取，也可用 `-p:RimWorldDir="<游戏目录>"` 指定本机安装。

```powershell
dotnet build Source/TurboTick.csproj -c Release
pwsh scripts/prepare-workshop.ps1 -PackageDirectory . -OutputDirectory "<游戏目录>/Mods/TurboTick"
```

同步脚本保留已有的创意工坊发布 ID。更换 DLL 前退出游戏，替换后重启。`scripts/package.ps1` 用于 CI 构建、自动化测试和生成发布附件。仓库只跟踪源码、必要测试、运行配置及 Mod 资源；构建产物、存档、备份和本地验证资料均忽略。

## 致谢

感谢 [Multiplayer](https://github.com/rwmt/Multiplayer) 与 [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together) 的设计参考。TurboTick 不依赖它们的运行时。

[MIT License](LICENSE)
