# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

面向 **RimWorld 1.7** 的独立联机项目，包含游戏客户端与专用服务器。

> **开发原型 · 0.3.0**：已实现房间连接、倍率设置同步及快照缓存。共享殖民地载入、人物与建造操作同步仍在开发，游戏内双端验收尚未完成。

## 功能

- 专用服务器管理房间，倍率默认关闭，玩家显式开启后生效。
- 一人修改设置，服务端确认后向全体同步；支持控制者权限。
- 校验游戏版本、模组内容、加载顺序与配置指纹。
- 快照分块校验、断点续传与本地缓存，HUD 显示 TPS 和连接状态。

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
5. 按需启用额外倍率，点击提交。默认允许所有已加入玩家编辑；服务器加 `--controller-only` 可限制为控制者。

当前原型用于本机或可信局域网联调，账号认证及 TLS 尚未接入。快照下载只写缓存，暂不自动载入殖民地。

## 开发

需要 .NET 10 SDK 和 PowerShell 7。仓库根目录执行：

```powershell
pwsh scripts/package.ps1
```

该命令完成构建、集成测试及打包，输出位于 `artifacts/`。游戏引用程序集由 NuGet 提供，编译不需要安装游戏。

- [参与开发](CONTRIBUTING.md)
- [兼容性与等待处理](docs/COMPAT.md)
- [开发路线图](docs/ROADMAP.md)
- [Steam 创意工坊发布](docs/STEAM_WORKSHOP.md)

## 致谢

感谢 [Multiplayer](https://github.com/rwmt/Multiplayer) 与 [RimWorld Together](https://github.com/RimWorld-Together/Rimworld-Together) 为联机设计研究提供参考。

## 许可

[MIT](LICENSE)
