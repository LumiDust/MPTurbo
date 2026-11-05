# 参与开发

TurboTick 仅面向 RimWorld 1.7。先阅读 [README](README.md) 和 [路线图](docs/ROADMAP.md)，按独立客户端＋专用服务器的架构提交改动。

## 本地验证

安装 .NET 10 SDK 与 PowerShell 7，在仓库根目录执行：

```powershell
pwsh scripts/package.ps1
```

脚本完成客户端构建、主测试、网络生命周期验证和两个发布包的生成。游戏引用来自 NuGet；游戏内验证需要本机安装 RimWorld 1.7。

需要离线引用时：

```powershell
dotnet build Source/TurboTick.csproj -c Release /p:RimWorldDir="<ref/net10.0>"
```

## Pull request

- 每次改动聚焦一个问题，说明前后行为及验证方式。
- 网络改动覆盖超时、断线、重连、并发及损坏数据，避免旧会话影响新房间。
- 仿真相关设置经过服务端确认；后台网络任务通过队列回到游戏线程。
- 用户可见字符串同步更新中英文语言文件。
- 发布版本以 `Directory.Build.props` 为准，同时更新 `About/About.xml` 和协议版本声明。
- 代码构建、自动测试与游戏内实测分别报告，不以一项替代另一项。

## 问题反馈

使用 GitHub issue 模板，附完整游戏版本、TurboTick 版本、复现步骤，以及相关的客户端/服务器日志。分享日志前移除个人路径、账号信息和凭据。

## Steam 发布

流程见 [创意工坊发布指南](docs/STEAM_WORKSHOP.md)。本地保存并备份 Steam 返回的条目 ID，沿用同一 ID 更新；该 ID 文件不提交到 Git。
