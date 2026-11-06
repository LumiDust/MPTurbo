# TurboTick 服务器部署

**0.5.1 · RimWorld 1.7 · 网络协议 v4**。本文使用 `ACCOUNT`、`SERVER_HOST`、`BUILD` 等占位值；执行前替换成实际账号、可达地址和已核对的游戏 build。Linux 示例根目录为 `/srv/turbotick`，Windows 为 `C:\Servers\TurboTick`。

## 两种运行形态

- **headless 常驻模拟**：SteamCMD 下载游戏，加载 Harmony、TurboTick 和匹配模组，运行 `RimWorld -headless -turbotick-server`。服务端执行完整模拟和检查点，玩家离线后仍可推进世界。
- **独立 .NET 网络服务器 / Docker**：无需游戏安装，协调锁步时间线并存储玩家上传的检查点，模拟仍由玩家执行。现有容器镜像属于此形态。

两者使用同一锁步协议。headless 下玩家也执行本地模拟，并非 thin client；新玩家加入会触发共同检查点，所有已连接模拟副本一起重载，期间有一次加载停顿。

## 1. 准备安装器与 SteamCMD

从 [Releases](https://github.com/LumiDust/TurboTick/releases/latest) 下载 `TurboTick-0.5.1-headless-installer.zip`，解压到 `/srv/turbotick/packages` 或 `C:\Servers\TurboTick\packages`。附件中包含：

- `TurboTick/`：与本版本客户端匹配的完整 Mod。
- [Linux 安装器](../scripts/steamcmd/install-server.sh) 和 [PowerShell 7 安装器](../scripts/steamcmd/install-server.ps1)。
- 本部署说明及 [systemd 模板](turbotick-headless.service)。

单独安装 [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD)，例如 `/opt/steamcmd/steamcmd.sh` 或 `C:\Servers\SteamCMD\steamcmd.exe`。安装器不安装 SteamCMD、系统运行库，也不自动启动服务器；Linux 需 Bash 与基本系统工具，Windows 使用 PowerShell 7。所下载游戏的原生运行依赖以该 build 为准。

选择独立的版本目录 `/srv/turbotick/rimworld/1.7-BUILD` 或 `C:\Servers\TurboTick\rimworld\1.7-BUILD`。`client-headless` profile 使用 AppId **294100**、默认分支 **unstable**；分支会更新，目录名不证明实际版本。以下载后的 `Version.txt` 为准，所有参与端使用相同完整 build。

### Steam Guard 与缓存

首次登录或缓存过期时，在交互终端执行：

```bash
cd /srv/turbotick/packages
bash scripts/steamcmd/install-server.sh \
  --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT --login-only
```

```powershell
Set-Location C:\Servers\TurboTick\packages
./scripts/steamcmd/install-server.ps1 `
  -SteamCmd 'C:\Servers\SteamCMD\steamcmd.exe' -User ACCOUNT -LoginOnly
```

SteamCMD 原生处理密码、Steam 手机确认、邮件验证码或令牌码提示，具体流程取决于账号和 SteamCMD 版本；安装器不承诺二维码登录。登录成功输出及退出码均通过检查后，才报告认证已确认。

复用缓存时，沿用建立缓存时的 **SteamCMD 可执行文件或包装脚本、操作系统账号和 HOME/用户目录**，仅传 `ACCOUNT`，保持 `STEAM_PASS`、`STEAM_GUARD` 未设置。缓存由 SteamCMD 自行管理，没有统一固定路径；无需复制桌面 Steam 的凭据文件。SteamCMD 登录只用于下载更新，不赋予 TurboTick 管理权限，也不要求 SteamCMD 常驻在线。

可选的 `--proxy http://PROXY_HOST:PROXY_PORT` / `-Proxy 'http://PROXY_HOST:PROXY_PORT'` 只设置 SteamCMD 子进程代理环境，实际下载传输是否采用由 SteamCMD 决定。既有自动化也可使用 `STEAM_PASS`、`STEAM_GUARD`；它们会进入原生进程参数，预检输出脱敏，安装元数据及启动器不保存这些值。Guard 码参数排在登录之前。

## 2. 预检与下载游戏

以下命令在刚才的 `packages` 目录中执行。将 `1.7.BUILD` 替换成要校验的版本，例如实测 build 的 `1.7.5000`；如需约束 revision，可传版本文件中的完整首行。

```bash
bash scripts/steamcmd/install-server.sh --profile client-headless \
  --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT \
  --dir /srv/turbotick/rimworld/1.7-BUILD --branch unstable \
  --expected-version 1.7.BUILD \
  --mod-source /srv/turbotick/packages/TurboTick --mod-name TurboTick \
  --launch-arg -headless --launch-arg -turbotick-server --preflight
```

```powershell
./scripts/steamcmd/install-server.ps1 -Profile client-headless `
  -SteamCmd 'C:\Servers\SteamCMD\steamcmd.exe' -User ACCOUNT `
  -Dir 'C:\Servers\TurboTick\rimworld\1.7-BUILD' -Branch unstable `
  -ExpectedVersion '1.7.BUILD' `
  -ModSource 'C:\Servers\TurboTick\packages\TurboTick' -ModName TurboTick `
  -LaunchArgs @('-headless', '-turbotick-server') -Preflight
```

预检不认证、不下载、不写文件。确认计划后，移除 `--preflight` / `-Preflight` 执行安装；`--dry-run` / `-DryRun` 与预检相同。`--branch public` / `-Branch public` 选择默认分支，但仍需实际版本匹配。

安装器要求 SteamCMD 退出成功并输出指定 AppId 的成功标志，然后核对可执行文件、版本及以下客户端 layout：`Version.txt`、`Assembly-CSharp.dll`、`RimWorld.dll`、`Waylith.dll`、`UnityStubs.dll`、`Assets.zip`、`Data/Core`。任何检查失败都会停止后续准备，调用方以本次退出码判断结果；SteamCMD 已写入的部分文件和旧启动器仍可能留在目录中。

| 其他模式 | 行为 |
|---|---|
| `--check-only` / `-CheckOnly` | 仅检查已有 layout 和版本，无 SteamCMD、认证或文件写入 |
| `--skip-download` / `-SkipDownload` | 检查已有文件后准备 Mod、初始配置和启动器，无 SteamCMD 或认证 |
| `--no-validate` / `-NoValidate` | 仅略过 SteamCMD 的 `validate`，本地 layout/版本检查仍执行 |

准备成功后生成 `start-server.sh`，或 `start-server.ps1` 与 `launch-settings.json`。客户端 profile 的启动器固定使用安装目录下的 `Saved` 作为 `-savedatafolder`，不会自动启动进程。默认参数仅有 `-headless`；上例已显式追加 `-turbotick-server`。

每个 `--launch-arg` 是一个字面 argv 项，PowerShell 使用 `-LaunchArgs` 字符串数组；显式参数会替换默认数组，不做 shell 拆词或表达式求值。旧 `LAUNCH_ARGS` 环境字符串已停用。

## 3. 安装 Harmony 并启用匹配模组

Harmony 单独通过 SteamCMD 工坊下载：

```bash
/opt/steamcmd/steamcmd.sh \
  +force_install_dir /srv/turbotick/rimworld/1.7-BUILD \
  +login ACCOUNT +workshop_download_item 294100 2009463077 validate +quit
```

```powershell
& 'C:\Servers\SteamCMD\steamcmd.exe' `
  +force_install_dir 'C:\Servers\TurboTick\rimworld\1.7-BUILD' `
  +login ACCOUNT +workshop_download_item 294100 2009463077 validate +quit
```

以 SteamCMD 输出的实际工坊下载目录为准，把 **完整 Harmony Mod 根目录**复制到游戏 `Mods/Harmony`，使最终存在 `Mods/Harmony/About/About.xml`。TurboTick 对应 `Mods/TurboTick/About/About.xml`。Harmony 不嵌入 TurboTick 的 `Assemblies`，安装器附件也不分发 Harmony 或原版游戏文件。

安装器默认生成 Core-only 的 `Saved/Config/ModsConfig.xml`。在其中启用模组；本次基础组合的顺序为：

```xml
<activeMods>
  <li>ludeon.rimworld</li>
  <li>brrainz.harmony</li>
  <li>lumidust.turbotick</li>
</activeMods>
```

使用 DLC 或其他模组时，服务端与所有客户端的游戏 build、模组内容、加载顺序和配置均需一致。指纹包含程序集和 XML；同版本源码重新构建的 DLL 也可能产生不同指纹，应统一分发同一份包。

也可在安装时传 `--mods-config PATH` / `-ModsConfig PATH` 作为初始配置。已有的 `Saved/Config/ModsConfig.xml` 和存档始终保留，传新初始配置不会覆盖旧配置。Mod 复制为覆盖式更新，旧文件不会自动清理；更新应先停服，在独立目录准备完整包后替换。

## 4. 启动 headless 世界

下列示例使用独立 `profile`，便于与 systemd 保持一致。先将上一步已编辑的 `Saved/Config/ModsConfig.xml` 复制到该 profile；使用第三方模组配置时，一并部署与客户端一致的相关 `Mod_*.xml`。每个实例使用自己的 profile 和 state。

Linux：

```bash
mkdir -p /srv/turbotick/profile/Config /srv/turbotick/state
cp /srv/turbotick/rimworld/1.7-BUILD/Saved/Config/ModsConfig.xml \
  /srv/turbotick/profile/Config/ModsConfig.xml
cd /srv/turbotick/rimworld/1.7-BUILD
TURBOTICK_HOST=0.0.0.0 TURBOTICK_PORT=30572 \
TURBOTICK_DATA=/srv/turbotick/state \
./RimWorld -headless -turbotick-server -savedatafolder=/srv/turbotick/profile
```

Windows / PowerShell 7：

```powershell
New-Item -ItemType Directory -Force -Path `
  'C:\Servers\TurboTick\profile\Config', 'C:\Servers\TurboTick\state' | Out-Null
Copy-Item 'C:\Servers\TurboTick\rimworld\1.7-BUILD\Saved\Config\ModsConfig.xml' `
  'C:\Servers\TurboTick\profile\Config\ModsConfig.xml'
Set-Location 'C:\Servers\TurboTick\rimworld\1.7-BUILD'
$env:TURBOTICK_HOST = '0.0.0.0'
$env:TURBOTICK_PORT = '30572'
$env:TURBOTICK_DATA = 'C:\Servers\TurboTick\state'
& ./RimWorld.exe -headless -turbotick-server `
  '-savedatafolder=C:\Servers\TurboTick\profile'
```

可执行文件名以安装器检测结果为准。若直接使用生成的启动器，则 profile 位于游戏目录的 `Saved`，不要同时混用另一套配置。仅有 `-headless` 不启用 TurboTick 服务端角色，普通客户端不会创建监听器。

空大厅等待玩家在「多人游戏 → 主持游戏」中发布已有存档或新殖民地，其他玩家连接 `SERVER_HOST:30572` 加入。也可将存档放到所选 profile 的 `Saves/WORLD.rws`，设置 `TURBOTICK_SAVE=WORLD`，仅在空房间首次启动时导入；参数只接受无扩展名的存档名称。

| 环境变量 | 默认 / 用途 |
|---|---|
| `TURBOTICK_HOST` | `0.0.0.0`；headless 使用回环或通配监听地址，以供内部常驻模拟接入 |
| `TURBOTICK_PORT` | `30572`；游戏及管理连接使用同一 TCP 端口 |
| `TURBOTICK_DATA` | `<profile>/TurboTickServer`；持久化协议状态、快照及运行状态 |
| `TURBOTICK_SAVE` | 空；仅在空房间导入 profile 中的指定存档 |
| `TURBOTICK_CONTROLLER_ONLY` | `false`；启用后仅服务器分配的控制者可调整仿真设置 |
| `TURBOTICK_ADMIN_PASSWORD_FILE` | 空；可选共享管理密码文件，详见下节 |

host、port、data、save、controller-only 也可用 `-turbotick-NAME=VALUE` 设置。加载未完成、常驻模拟故障或内部连接断开时，服务器停止授予新 tick；时间线最多领先常驻模拟 6 tick。

公网直连需将 `SERVER_HOST:30572` 路由或映射到监听端口，并放行 **30572/TCP**。当前游戏和管理 TCP 通道未内置 TLS；链路加密可由 VPN 或隧道提供。SteamCMD 的登录认证和缓存不替代 TurboTick 的连接或管理配置。

## 5. 共享密码管理

管理独立于当前房主身份，采用服务器配置的共享密码，不由首次加入者注册或永久占有。配置 `TURBOTICK_ADMIN_PASSWORD_FILE` 指向运行账号可读的密码文件；文件尾部换行会移除，空文件或读取失败会使启动失败。也支持 `TURBOTICK_ADMIN_PASSWORD`，文件配置优先。

在多人大厅选择「管理服务器」，输入同一服务器地址与管理密码，认证后查看状态，确认后归档旧局并重开空房间。客户端不保存密码；未配置密码时管理功能关闭。玩家名、SteamID 字符串、SteamCMD 登录及创建存档均不授予管理权限。

归档重开沿用服务器已安装的游戏/DLC/模组，不会自动切换模组包。需要另一套组合时，停服、部署匹配的完整包，并使用对应持久化世界或独立空 state/profile。

## 6. Linux systemd 常驻

[模板](turbotick-headless.service) 使用通用的 `/srv/turbotick` 路径及可选环境文件。创建 `turbotick` 系统用户，赋予其游戏读取/执行权限、profile/state 写权限；SteamCMD 可由独立安装账号维护。

应用本页目录时，将模板的 `/srv/turbotick/game` 统一替换为实际 `/srv/turbotick/rimworld/1.7-BUILD`，`headless-profile` 改为 `profile`，`headless-state` 改为 `state`。核对可执行文件名以及 `WorkingDirectory`、`HOME`、`XDG_CONFIG_HOME`、`TURBOTICK_DATA` 和 `-savedatafolder`，确保它们指向已准备的目录。

可选的 `/srv/turbotick/headless.env` 可设置管理密码文件、初始导入存档等，例如：

```ini
TURBOTICK_ADMIN_PASSWORD_FILE=/srv/turbotick/secrets/admin-password
```

不要把 SteamCMD 账号密码或 Guard 码放进运行环境文件。准备好路径和权限后，从附件根目录安装模板：

```bash
sudo cp deploy/turbotick-headless.service /etc/systemd/system/turbotick-headless.service
sudo systemctl daemon-reload
sudo systemctl enable --now turbotick-headless.service
systemctl status turbotick-headless.service
journalctl -u turbotick-headless.service -f
```

每 3000 个逻辑 tick 自动提交检查点，每 10 秒保存协议状态。SIGTERM/Ctrl+C 会在主线程等待上传、生成并提交最终检查点后退出；模板保留至少 90 秒停止时间。使用 `systemctl stop` 停服，再更新游戏或 DLL。

## 7. 检查、恢复与更新

运行状态写入 `TURBOTICK_DATA` 中的 `headless-status.json`，包含 phase、连接状态、服务器/已执行 tick、checkpointTick 和故障。`server-state.json` 与 `Snapshots/` 用于恢复，需连同匹配 profile、游戏 build 和模组包保存。

恢复时使用同一 state/profile，且不加测试建图参数；服务器加载已持久化世界并继续。游戏或模组指纹不符时启动停止并保留旧数据，应恢复对应包，或归档旧房间后使用新组合。两个进程不共用 state/profile；同一监听端口也只运行一个宿主。

回到附件的 `packages` 根目录，可离线检查现有游戏：

```bash
bash scripts/steamcmd/install-server.sh \
  --dir /srv/turbotick/rimworld/1.7-BUILD --expected-version 1.7.BUILD --check-only
```

```powershell
./scripts/steamcmd/install-server.ps1 `
  -Dir 'C:\Servers\TurboTick\rimworld\1.7-BUILD' -ExpectedVersion '1.7.BUILD' -CheckOnly
```

更新前正常停止服务并保留恢复数据，在独立版本目录准备新游戏及同一份客户端/服务端 Mod 包，核对指纹和配置后再切换。测试实例使用不同端口（例如 `30573`）和独立 state/profile，避免与正式世界混用。

## 8. 独立 .NET 网络服务器与 Docker

从 `TurboTick-0.5.1-server.zip` 解压，在 .NET 10 Runtime 下运行：

```sh
dotnet TurboTick.Server.dll --host 0.0.0.0 --port 30572 --data Saved
dotnet TurboTick.Server.dll --help
```

Docker 镜像已包含该运行时：

```sh
docker run -d --name turbotick-server --restart unless-stopped \
  -p 30572:30572 -v turbotick-data:/data \
  ghcr.io/lumidust/turbotick-server:0.5.1
```

也可使用 [Compose 配置](https://github.com/LumiDust/TurboTick/blob/main/docker-compose.yml)：

```sh
docker compose pull
docker compose up -d
```

保留数据卷，让版本标签与客户端一致。常用环境变量为 `TURBOTICK_HOST`、`TURBOTICK_PORT`、`TURBOTICK_DATA`、`TURBOTICK_CONTROLLER_ONLY`；共享密码管理与上述相同。首次有效连接锁定完整游戏 build 和模组指纹，已有世界沿用持久化的兼容配置。

此网络服不会因下载了游戏就自动变成常驻模拟宿主。切换 headless 时应分别保留两种形态的恢复数据，停止原进程后再启用同端口的新宿主。

## 9. 验证范围与未来 profile

0.5.1 在 **1.7.5000 rev1275** 的 Windows 原生客户端与 Linux headless 上，用 Core/Harmony/TurboTick 匹配组合完成实机验证：tick **120–21780** 内 **334 个同 tick 共同采样**的状态哈希和 RNG 全部一致；覆盖晚加入、暂停恢复、外部玩家全部退出后继续推进、正常检查点停机和重启恢复。该结果限定于此 build、组合与测试时段，长期运行及第三方 Mod 需另外验证。

源码中的 [安装器离线测试](https://github.com/LumiDust/TurboTick/blob/main/scripts/steamcmd/test-install-server.ps1) 验证参数、失败处理、文件布局、已有配置保留、字面 argv 和未来 profile 的控制流，不连接 Steam，也不代表实际下载或游戏验收。在源码仓库根目录运行：

```powershell
./scripts/steamcmd/test-install-server.ps1 -Bash 'C:\Program Files\Git\bin\bash.exe'
```

`official-server` 是未来应用的显式适配入口，尚未内置已核实的官方服务端 AppId、layout 或启动行为。须同时提供 AppId、必需路径、可执行文件与 argv，或显式选择空 argv；仅替换 AppId 不保证兼容。以下示例从附件或源码根目录执行。

```bash
bash scripts/steamcmd/install-server.sh --profile official-server \
  --app-id SERVER_APP_ID --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT \
  --dir /srv/turbotick/official/BUILD --branch public \
  --required-path server.pak --required-path runtime \
  --executable bin/OfficialServer \
  --launch-arg --data=/srv/turbotick/official-data --preflight
```

`SERVER_APP_ID` 要替换成已核实的数字，示例 layout 和参数也按实际包更换。该 profile 不假定客户端 DLL、`Mods/Saved` 布局，不自动追加 `-savedatafolder`，也不套用客户端 Mod/ModsConfig 复制行为。官方包的分支、运行依赖、数据路径和 TurboTick 接入方式仍需针对真实文件与运行行为逐项验证。
