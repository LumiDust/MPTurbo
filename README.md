# MP Turbo

给 [Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745)（环世界联机 mod）用的**时间倍率 + 全员同步 + 等待诊断**小 mod。

- 倍率：在游戏当前速度上再乘 **1–20 倍**（默认 2x，需要手动开启）。
- 同步：任何一个人改倍率，**全服同步**；可设为仅房主可改。
- 等待：右上角 HUD 显示落后多少 tick、谁在拖后腿；可选「落后自动降回 1x」。
- 兼容：**不引用 Multiplayer 的任何程序集**，全部走反射；没装 MP 时退化成单机倍率。

> 版本：0.1.0 · 目标 RimWorld **1.6**（MP 当前只支持 1.6） · 依赖 **Harmony**（必需）、Multiplayer（可选）

---

## 1. 它解决什么问题

Multiplayer 是确定性锁步：所有人跑同一份世界，靠命令流对齐。它自带 1x/3x/6x/15x 四档速度，但：

- 后期单 tick 变贵，15x 往往跑不动，玩家想要"2x/3x 这种中间档"；
- 多人时没人愿意为了等一个人把速度调回 1x；
- 出问题时看不出"到底是谁在拖后腿"。

MP Turbo 就是把这三件事补上：**中间档倍率**、**倍率全员同步**、**等待可视化**。

---

## 2. 安装

1. 订阅 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)。
2. 把本 mod 目录放进 `<游戏根>/Mods/`（或订阅创意工坊版本）。
3. mod 列表里放在 **Core / DLC / Harmony / Multiplayer 之后**。
4. **联机时所有玩家都要装**——MP 会校验 mod 列表，缺一个进不来；这也是保证倍率一致的前提。

---

## 3. 怎么用

| 操作 | 位置 |
|---|---|
| 开/关倍率 | 选项 → Mod 设置 → MP Turbo → 「启用倍率」 |
| 调倍率 | 同上滑块，或右上角 HUD 的 `−` / `+` / `1x` |
| 仅房主可改 | Mod 设置 → 「仅房主可改倍率」 |
| 自动降速 | Mod 设置 → 「落后时自动降回 1x（全服）」 |
| 看谁在拖后腿 | 右上角 HUD（可关） |

倍率是**叠加**在当前速度上的：当前 3x（Fast）+ 倍率 2x = 实际 6x。

---

## 4. 实现（为什么只补一个点）

原版的速度本来就是"倍率"：`TickManager.TickRateMultiplier` 返回 1/3/6/15。Multiplayer 把它换成了每张地图一份的 `AsyncTimeComp.TickRateMultiplier(TimeSpeed)`，世界层是 `AsyncWorldTimeComp.TickRateMultiplier(TimeSpeed)`，而 `TickPatch.ActualRateMultiplier` 正是从这两个方法取速率。

所以本 mod 只做一件事：**在这两个方法的返回值上乘系数**。

```csharp
// 装了 MP：补 MP 的两个 comp（反射找方法，不引用程序集）
harmony.Patch(AccessTools.Method("Multiplayer.Client.AsyncTimeComp:TickRateMultiplier"),
              postfix: new HarmonyMethod(typeof(TickRatePatches), nameof(MpPostfix)));

// 没装 MP：补原版 getter
harmony.Patch(AccessTools.Method(typeof(TickManager), "get_TickRateMultiplier"),
              postfix: new HarmonyMethod(typeof(TickRatePatches), nameof(VanillaPostfix)));
```

好处：

- 与 MP 自己的 Fast/Superfast/Ultrafast 走**同一条路径**，命令调度、desync 校验都不用动；
- 不碰 tick 主循环、不碰 Scribe/存档结构、不碰 UI；
- MP 换版本最多让"找不到方法"这条 warning 出现，mod 其余功能照常。

同步用一个方法：

```csharp
MP.RegisterSyncMethod(typeof(MpSync), nameof(SetMultiplier));   // 反射调用，参数 null
public static void SetMultiplier(float value) { ... }           // 注册后：本地调用 → 广播 → 全员执行
```

---

## 5. 已知限制（重要）

1. **只放大"地图/世界的 tick 倍率"，不放大全局 tick 计数器。** 少数以 `Find.TickManager.TicksGame` 计时的系统（部分任务/事件）不会跟着变快——这与 MP 里用 15x 的表现一致，不是本 mod 引入的。
2. **强制正常速度（袭击/事件）也会被乘。** 原版 `slower.ForcedNormalSpeed` 会把速率压到 1，本 mod 会把它乘上去；如果你希望威胁时强制 1x，请把倍率手动调回 1x。
3. **自动降速是全服行为。** 倍率必须全员一致，任何"只降自己"的做法都会 desync，所以自动降速走的是同步命令。
4. **不能防止有人绕过 UI 直接发命令。** 「仅房主可改」是客户端约束，不是服务器强制（MP 没有暴露权限 API）。
5. **与 RimWorld Together 不兼容。** Together 不走 MP 的同步通道，装了也没意义。

### 5.1 与工坊上已有 mod 的关系

2026-11-05 的工坊调研（218 个 ID 全量核验，见工作区 `docs/workshop-survey.md`）发现一个功能重叠项：

| ID | 名称 | 最后更新 | 与本 mod 的差异 |
|---|---|---|---|
| `2902186466` | Multiplayer - Custom Tickrates | 2024-09-04 | 同为"联机改 tick 速率"。本 mod 额外提供：倍率**全员同步**（任意人可改，可设仅房主）、**落后诊断 HUD**、**落后自动降速**、**零编译期依赖 MP** |

另外相关但不同层：`3797749592`（Desync Fix）、`3830002705`（Desync Probe）做的是哈希级 desync 诊断，本 mod 只显示 `ticksBehind`/`latency`/落后 tick，不重叠；`3812768044`（Host Settings Sync）同步的是房主设置，本 mod 同步的是倍率这一个值。

> 若 `2902186466` 已经做了同步，本 mod 的定位应收缩为"诊断 + 自动降速 + 兼容守护"。核对该 mod 的实现是下一步（见 `docs/COMPAT.md`）。

---

## 6. 编译

```powershell
# 默认：从 NuGet 拉 Krafs.Rimworld.Ref（不需要装游戏，CI 也是这么编的）
cd Source
dotnet build -c Release

# 离线：自己指定 1.6 的引用程序集目录（解包 Krafs.Rimworld.Ref 的 ref/net472）
dotnet build -c Release /p:RimWorldDir="<ref/net472 目录>"
# 产物：..\Assemblies\MPTurbo.dll
```

> 当前状态：**编译通过（0 warning / 0 error），尚未在实机（1.6 + Multiplayer）里跑过**。
> 首次实机验证请按 `docs/COMPAT.md` §4 的自测清单走一遍，重点看第 3 条（双端倍率同步）与第 6 条（自动降速）。

---

## 7. 目录

```
MPTurbo/
├─ About/About.xml
├─ Assemblies/MPTurbo.dll        # 构建产物
├─ Languages/{ChineseSimplified,English}/Keyed/MPTurbo.xml
├─ Source/                       # 6 个 .cs，见 docs/COMPAT.md 的分层说明
├─ docs/COMPAT.md                # 兼容性与"等待"处理方案（设计文档）
└─ README.md
```

## 8. 许可

MIT。欢迎 issue / PR。
