# 兼容性与「等待」处理方案

这份文档回答两个问题：**这个 mod 怎么保证不把别人的存档/联机搞坏**，以及**"玩到后面一直等同步"到底能处理到什么程度**。

---

## 1. 兼容性：四个接触面，逐个隔离

| 接触面 | 风险 | 本 mod 的做法 |
|---|---|---|
| **编译期** | 引用 MP 的程序集 → MP 换版本/没装就 `TypeLoadException`，整个 mod 加载失败 | **零引用**。`MPTurbo.csproj` 只引用 `Assembly-CSharp` + `UnityEngine.*` + Harmony；MP 的类型全部用 `GenTypes.GetTypeInAnyAssembly("...")` 反射拿 |
| **补丁期** | 和 MP 抢同一个方法 → 行为叠加或覆盖 | **只补两个方法**：`AsyncTimeComp.TickRateMultiplier`、`AsyncWorldTimeComp.TickRateMultiplier`（Postfix，乘系数）。不碰 `TickManager.TickManagerUpdate`、`TickPatch`、`Scribe`、UI |
| **运行期** | 读到 MP 内部状态，字段改名就崩 | 只读**公开静态字段/属性**（`Multiplayer.session`、`TickPatch.Timer/tickUntil/serverFrozen`、`PlayerInfo.ticksBehind`），全部包在 `try/catch` 里；读失败保持上一次的值 |
| **数据期** | 往存档里塞东西，老存档读不了 / MP 的 `SessionData` 冲突 | 只加一个 `GameComponent` + 一个 float 字段（`mpTurboMultiplier`）。`Scribe_Values.Look` 带默认值，缺字段的旧存档能直接读 |

### 1.1 MP 换版本时的降级矩阵

| 如果…… | 结果 | 玩家看到 |
|---|---|---|
| MP 没装 | 单机模式，倍率照常生效 | 设置页显示「未检测到 Multiplayer（单机模式）」 |
| MP 装了但 `AsyncTimeComp.TickRateMultiplier` 改名/改签名 | 倍率对联机无效（单机仍有效） | 日志一条 `签名变了` warning，HUD 倍率不生效 |
| MP 装了但 `Multiplayer.API.MP` 缺失 | 倍率只在本地生效（**联机会 desync**） | 日志 `注册同步方法失败`，设置页状态行提示 |
| MP 的 `session`/`TickPatch` 字段改名 | HUD 的落后/玩家列表不显示 | HUD 只显示倍率，不显示诊断 |
| Harmony 版本不对 | mod 不加载 | 启动红字 `Could not load assembly` |

> 设计原则：**任何一项失败都只降级那一项功能，绝不抛到顶层**。`Bootstrap.Init` 外面套了 `try/catch`，最坏情况是"装了个没用的 mod"，而不是"游戏起不来"。

### 1.2 与其他 mod 的兼容

| 类别 | 结论 | 原因 |
|---|---|---|
| **Multiplayer** | ✅ 设计目标 | 只补它的两个倍率方法 |
| **RimWorld Together** | ❌ 不适用 | Together 不走 MP 的同步通道；两者本来就互斥 |
| 性能类（RocketMan / Performance Fish / Dubs Analyzer…） | ⚠️ 理论无冲突，但**它们与 MP 本身的冲突与本 mod 无关** | 本 mod 不改寻路/网格/渲染，只改倍率；但性能 mod 常改 `TickManager`/`Rand`，那是 MP 的地盘 |
| UI/HUD 类 | ✅ 无冲突 | 不补任何 UI 方法，HUD 走 `GameComponentOnGUI` |
| 其他"同步类" mod | ⚠️ 看它是否也改倍率 | 两个 mod 同时乘系数 = 乘法叠加，会超速；用 `maxRateCap` 兜底 |
| 存档 mod（RimSaves 等） | ✅ | 只多一个 float 字段 |

### 1.3 工坊里已知的相关 mod（2026-11-05 核验，218 个 ID 全量）

| 工坊 ID | 名称 | 与本 mod 的关系 |
|---|---|---|
| `2902186466` | Multiplayer - Custom Tickrates | **功能重叠**：同样改联机 tick 速率。差异见 README §5.1 |
| `3812768044` | Multiplayer Host Settings Sync | 相邻：它同步"房主设置"，本 mod 只同步"倍率"一个值 |
| `3797749592` / `3830002705` | Desync Fix / Desync Probe | 不同层：它们做哈希级诊断，本 mod 只显示 `ticksBehind`/落后 tick |
| `1629973374` | Multiplayer Compatibility | 互补：它让别的 mod 能在联机下用，本 mod 不碰别人的 mod |
| `3820883853` 等 | 非官方 1.7 移植版 | **不要与本 mod 的 1.6 构建混用**；若移植版协议变了，本 mod 的同步注册可能失效（会降级为本地倍率 → 联机 desync，日志有 warning） |

> 结论：**同时装两个"改倍率"的 mod 会乘法叠加**（例如 Custom Tickrates 3x + MP Turbo 2x = 6x），
> 叠加后的值仍受 `maxRateCap` 限制。要么只装一个，要么把 `maxRateCap` 压低。

### 1.3 联机一致性（最容易被忽略的一条）

**倍率必须全员一致。** 它决定"每个 DoTick 推进多少个游戏 tick"，只要有一个人不同，`mapTicks` 就会分叉 → desync。

因此：

- 联机时**所有人必须装本 mod**（MP 的 mod 列表校验会自动拦住没装的人）；
- 倍率通过 `MP.RegisterSyncMethod` 同步，不做任何"本地覆盖"；
- 「自动降速」也是同步命令（详见 §2.3），不是本地降速。

---

## 2. 「等待」问题：能做什么、不能做什么

先把"等待"拆成四种，它们的解法完全不同：

| # | 现象 | 根因 | 本 mod 能做的 | 需要改 MP/服务器 |
|---|---|---|---|---|
| 1 | 游戏整体变慢、走走停停 | 服务端 tick 闸门按**最慢玩家**走（`serverTimePerTick = max(frameTime)`，落后 90 tick 全体暂停） | 显示"谁在拖后腿"，让你知道该踢谁 / 该降速 | ✅ 必须改服务端节奏（见 `docs/mp-dedicated/`） |
| 2 | 重连要等很久（进度条） | 每次加入都**全量下发整局存档 + 所有地图**，然后主线程重建 | ❌ 做不到 | ✅ 快照缓存 + 增量重连 |
| 3 | 有人加入时全场卡一下 | `CommandType.CreateJoinPoint` 广播给所有人执行 | ❌ 做不到 | ✅ standalone 模式天生没有这个分支 |
| 4 | 网络抖动导致命令延迟 | P2P（房主家宽上行） | ❌ 做不到 | ✅ 换专用服务器 |

### 2.1 诊断（本 mod 已实现）

HUD 每 0.25 秒刷新：

- `落后 N tick`：`TickPatch.tickUntil - TickPatch.Timer`，> 0 说明你还没追上服务器水位；
- `拖后腿：名字 (N)`：从 `session.players[].ticksBehind` 里排序取前二；
- `服务器暂停`：`TickPatch.serverFrozen`（有人在加载/冻结）；
- `追赶中`：本 mod 触发的自动降速正在生效。

这样至少能把"到底是我卡还是他卡"变成可见信息。

### 2.2 缓解：倍率本身就是一种"等待调节"

- 平时开 2–3x，把等待摊薄；
- 有人明显掉队时，任何人按 `1x` 立刻全员降速（同步命令），让掉队者追上，再调回去；
- 「仅房主可改」避免多人乱调。

### 2.3 自动降速（可选，默认关闭）

```
本地检测到 lag > downshiftThreshold 持续 1.5s
  → 发同步命令 SetMultiplier(1)
  → 全服降到 1x
  → lag < threshold/3 且距上次操作 > 10s
  → 发同步命令恢复原倍率
```

为什么不做"只降自己"：见 §1.3，本地降速 = 直接 desync。所以这个功能默认关闭，开启时设置页会明确写"全服生效"。

### 2.4 本 mod 做不到的部分（诚实边界）

- 下载/重连的进度条、join point 的全场停顿、P2P 的带宽瓶颈，都在 MP 的核心与服务器侧，**客户端 mod 无法解决**。
- 相关改造方案见工作区里的 `docs/mp-dedicated/`（tick 节奏解耦、快照内容寻址、增量重连、懒加载地图）。

---

## 3. 故障排查

| 现象 | 先看 | 大概率原因 |
|---|---|---|
| 倍率不生效 | `Player.log` 搜 `[MP Turbo]` | ① 没开「启用倍率」；② MP 方法签名变了（有 warning）；③ 当前是 Paused |
| 联机时倍率只有自己变 | 搜 `注册同步方法失败` | MP API 类型缺失 / 版本不匹配 |
| 一改倍率就 desync | 搜 `Simulation paused` / desync 报告 | 有人没装本 mod，或装了旧版 |
| HUD 不显示 | 设置里 `showHud` | 被关掉了；或不在 `ProgramState.Playing` |
| 掉帧严重 | 把倍率调回 1x 对比 | `maxRateCap` 设太高；后期单 tick 本来就贵 |

---

## 4. 自测清单（发版前跑一遍）

1. 单机新开一局：倍率 1x/2x/5x 各跑 30 秒，速度肉眼可见变化。
2. 单机读旧档（没有 `mpTurboMultiplier` 字段）：不报错，默认 2x。
3. 装 MP 开房 + 一个客户端：A 调 3x，B 的 HUD 同步变 3x；B 调 1x，A 同步变 1x。
4. 房主开启「仅房主可改」：客户端 B 的 `+/-` 变灰。
5. 断开 B 再重连：倍率仍是断线前的值（存在 `GameComponent` 里）。
6. 开「自动降速」，人为让 B 卡（限制帧率）：B 触发降速后 A 的倍率也变 1x。
7. 不装 MP 只装本 mod：设置页显示单机模式，倍率生效。
8. 把 MP 换成不兼容版本（或临时改名方法）：日志有 warning，游戏不崩。
