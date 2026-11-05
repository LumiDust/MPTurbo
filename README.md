# TurboTick：RimWorld 1.7 独立联机项目

目标是 **独立的专用服务器＋共享殖民地联机 mod**。Multiplayer 和 RimWorld Together 仅作研究参考；本项目既不订阅它们，也不引用它们的 DLL、同步 API、运行时类名或服务器程序。

> 当前版本 **0.3.0 · 协议原型**。已实现专用服务器、真实 TCP 客户端、权威倍率设置同步、兼容性握手及快照分块缓存。**共同载入同一殖民地、人物/建造命令复制、确定性 tick 与掉线状态追赶尚未完成**。此版本不是完整可玩的联机替代品。

仓库沿用历史地址 [LumiDust/MPTurbo](https://github.com/LumiDust/MPTurbo)，实际项目、程序集与包 ID 已使用 TurboTick。早期把项目做成附属倍速插件的代码已移除。

## 当前能力

| 能力 | 本阶段状态 |
|---|---|
| 自有专用服务器（.NET 10） | 已实现；运行不需要游戏或另外两个联机 mod |
| RimWorld 1.7 客户端连接页 | 已编译；地址、端口、名称、连接状态、重连/离开 |
| 一人改倍率，全员收到 | 已用两个真实 TCP 客户端验证 |
| 启用开关＋倍率值＋上限 | 同一服务端修订内广播；默认关闭，显式开启后生效 |
| 谁可以修改 | 默认每位已加入玩家；`--controller-only` 由服务器分配控制者并强制权限 |
| 兼容性 | 完整 1.7 build、协议/客户端版本、模组顺序/代码/Def/Patch/配置指纹精确检查 |
| 已完成快照缓存命中 | 长度＋完整 SHA256 验证后直接使用本地文件，零块下载 |
| 中断下载续传 | 256 KiB 分块；逐块校验 `.part`，只重新获取缺失或损坏块 |
| 真实 TPS | 已完成 tick 的增量除以墙钟时间；暂停不显示虚假的高 TPS |
| 共同操作殖民地 | 开发中，见 [ROADMAP.md](docs/ROADMAP.md) |

## 编译与测试

只面向 **RimWorld 1.7 / Waylith / .NET 10 / Harmony 3**。

```powershell
# .NET 10 SDK；运行目录为仓库根
# 游戏引用由 NuGet Krafs.Rimworld.Ref 提供，无需游戏本体参与编译。
dotnet build Source/TurboTick.csproj -c Release
dotnet run --project Tests/TurboTick.Tests.csproj -c Release

# 补充的网络生命周期、坏块续传与事件回调竞态验证
dotnet run --project Server/Verification/TurboTick.Networking.Verification.csproj -c Release

# 离线引用程序集路径
# dotnet build Source/TurboTick.csproj -c Release /p:RimWorldDir="<ref/net10.0>"

# 构建、全部测试、生成 mod 与 server 两个 zip
pwsh scripts/package.ps1
```

源码测试不装游戏也能运行；游戏内 HUD、载入顺序、真实操作手感仍需 RimWorld 1.7 实机验收。CI 构建成功不等于游戏内运行已经验收。

## 启动独立服务器

```powershell
dotnet run --project Server/TurboTick.Server.csproj -c Release -- --host 127.0.0.1 --port 30572 --data Saved

# 仅控制者可改倍率
# ... --controller-only

# 固定房间兼容性；避免第一次连接决定房间身份
# ... --game-version 1.7.4998 --mod-hash <兼容指纹>

# 导入本地文件供客户端缓存/续传测试；当前不会自动把文件作为游戏存档载入
# ... --import "<snapshot-file>"
```

默认只监听本机。`--host 0.0.0.0` 可用于可信局域网或受控隧道联调；当前协议尚未加入账号认证和 TLS，公网发行前需补齐。

默认配置 `--game-version 1.7 --mod-hash development` 会把**第一位成功加入者的完整游戏 build 和模组指纹锁定并持久化**，后续玩家精确匹配。锁定信息在服务器日志中显示。要换 modpack，应显式建立新的数据目录或按迁移流程更新房间状态，避免与旧快照混用。

## 游戏端使用

1. 将 mod 包中的 `TurboTick/` 放入游戏 `Mods/`，启用 Harmony 及本 mod。客户端唯一额外 mod 依赖为 `brrainz.harmony`。
2. 打开「选项 → Mod 设置 → TurboTick · 独立联机」。填写服务器地址、端口、玩家名称。
3. 点击「连接／重连」。客户端先在后台生成兼容指纹，再完成握手；TCP 建连本身不算成功。
4. 额外倍率默认关闭。编辑启用开关、倍率、上限后点击「提交给服务器／全体同步」；HUD 的按钮同样只发请求，收到权威修订才应用。
5. 断线/握手失败保持等待，防止回退到某台机器自己的倍率。显式点击「退出房间」后恢复本地模式。
6. 「下载／续传服务器快照到缓存」只下载并校验文件，**不替换当前存档**。缓存位于游戏存档数据目录下 `TurboTickCache/`。

倍率以当前原版速率为基数，例如原版 3x、额外 2x→总计 6x，并受服务器上限约束。暂停及原版威胁强制常速保留。任何与仿真有关的设置都不再走每端本地覆盖；自动单端降速已经移除。

## 数据与依赖

```text
Source/        RimWorld 1.7 接入、设置页、主线程队列、指纹采集、HUD
Networking/    自有 TCP 协议、客户端、权威房间、快照内容寻址与下载
Simulation/    速率权威状态与 TPS 测量（不引用游戏）
Server/        独立控制台程序
Tests/         真实多客户端与打包验收测试
1.7/Assemblies/
    TurboTick.dll
    TurboTick.Networking.dll
    TurboTick.Simulation.dll
```

服务器持久化 `server-state.json`（兼容锁＋倍率修订）和 `Snapshots/`（整文件 SHA256、分块 SHA256、不可变对象、latest 指针）。服务器只接受本地管理员导入快照，暂未开放任意玩家上传。

兼容指纹导出在游戏配置目录 `TurboTick/compatibility-manifest.json`。目前采用保守策略：代码、Def/Patch、mod 元信息与顺序、其他 mod 配置均检查；纯贴图/声音/语言资源不参与。该规则有意偏严格；允许更多 mod 的确定性兼容仍需实际验证，不宣称“兼容一切”。

详细范围与验收：[COMPAT.md](docs/COMPAT.md) · 后续共享殖民地实现：[ROADMAP.md](docs/ROADMAP.md)。

MIT。参考项目的原始代码和二进制均未打包到本项目。
