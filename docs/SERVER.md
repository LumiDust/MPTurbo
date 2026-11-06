# TurboTick Server

TurboTick 的独立专用服务器，运行需要 **.NET 10 Runtime**，不需要安装游戏。当前为 0.4.0 版本，提供房间握手、原生速度同步、快照分发、动作中继和锁步状态哈希校验。

## 启动方式

### 方式 A：Docker 容器运行（推荐）

官方预构建镜像托管在 GitHub Container Registry（GHCR）：

```sh
docker run -d \
  --name turbotick-server \
  --restart unless-stopped \
  -p 30572:30572 \
  -v turbotick-data:/data \
  ghcr.io/lumidust/turbotick-server:latest
```

或使用仓库自带的 `docker-compose.yml`：

```sh
docker compose up -d
```

#### 容器环境变量

| 变量名 | 默认值 | 说明 |
|---|---|---|
| `TURBOTICK_HOST` | `0.0.0.0` | 监听地址 |
| `TURBOTICK_PORT` | `30572` | 监听端口 |
| `TURBOTICK_DATA` | `/data` | 持久化数据目录（建议挂载数据卷） |
| `TURBOTICK_CONTROLLER_ONLY` | `false` | 设为 `true` 仅首位加入玩家可控制倍率 |
| `TURBOTICK_GAME_VERSION` | 空 | 锁定特定游戏 build |
| `TURBOTICK_MOD_HASH` | 空 | 锁定特定模组配置指纹 |

### 方式 B：原生 .NET 10 运行

在解压目录执行：

```sh
dotnet TurboTick.Server.dll --host 0.0.0.0 --port 30572 --data Saved
```

用 `Ctrl+C` 正常退出。完整参数：

```sh
dotnet TurboTick.Server.dll --help
```

- `--controller-only`：仅服务器分配的控制者可编辑倍率。
- `--game-version 1.7.xxxx`：固定完整游戏 build。
- `--mod-hash <指纹>`：固定模组配置指纹。
- `--import <文件>`：导入文件供快照缓存/续传测试，当前不自动载入游戏。

默认 `1.7` 和 `development` 会在首位有效玩家加入时锁定完整版本与指纹，并保存在数据目录。其他玩家需精确匹配。默认仅监听本机；跨机器联调需显式设置监听地址。当前尚无账号认证或 TLS，仅用于可信环境。

## 数据

`Saved/server-state.json` 保存设置修订与兼容性锁，`Saved/Snapshots/` 保存内容寻址快照。修改 modpack 或版本前先备份数据；不要将不同游戏状态混入同一房间。

[项目与客户端安装](https://github.com/LumiDust/TurboTick) · [问题反馈](https://github.com/LumiDust/TurboTick/issues)
