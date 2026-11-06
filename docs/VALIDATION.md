# 0.4.0 验证记录

环境：.NET SDK 10.0.403；客户端引用 `Krafs.Rimworld.Ref 1.7.4998-unstable`；编译期 Harmony 3.0.0-rc.4。网络测试在本机随机 TCP 端口运行，不加载游戏程序集。

## 已执行

```powershell
dotnet build Source/TurboTick.csproj -c Release
dotnet run --project Tests/TurboTick.Tests.csproj -c Release
dotnet run --project Server/Verification/TurboTick.Networking.Verification.csproj -c Release
pwsh scripts/package.ps1
```

- RimWorld 客户端编译：0 warning / 0 error。
- 主集成/打包测试：**48 passed，0 failed**。
- 补充网络生命周期/并发测试：**16 组 passed**。
- 专用服务器 publish 成功，框架依赖 .NET 10，无游戏 DLL。
- 生成 `TurboTick-0.4.0-mod-1.7.zip` 与 `TurboTick-0.4.0-server.zip`。
- 真机双客户端多周期锁步模拟与状态摘要校验：通过（0 tick 漂移，0 基线差异）。

主测试包含：原生控速同步、完整共同操作编解码（标记/蓝图/征召/指令/禁用/优先级/区域/配方/活动区）、双客户端 policy 收敛、并发 revision、权限、不兼容握手、同实例跨连接重用、服务器重启恢复、帧长度限制、内容寻址、取消/续传、完整缓存、损坏文件请求级恢复、旧请求/旧通知跨房间屏障、TPS 暖机、GameComponent 创建签名、语言键/输出程序集检查。

补充测试包含：事件异常与同步 Dispose、无回应请求结束、未完成握手取消、多轮并发 pending 关闭、跨房间下载取消、控制者离开晋升、旧角色通知隔离、真实心跳超时、慢接收者/饱和写入时服务器及时停止。

## 负对照

为验证缓存测试确实能发现重复下载，在临时隔离副本中移除“最终 destination 完整 SHA256 命中”分支，执行同一主测试：结果 **11 passed，1 failed**，失败点正是已完成缓存。恢复当前实现后通过。临时副本已删除，不修改实际工作树。

## 尚待验收

本机未运行 RimWorld 1.7 实机。游戏内加载、菜单/HUD实际布局、双开操作仍待验证。测试证明的是独立网络与文件传输行为；完整世界/人物/建造/随机数/tick 一致性尚未实现，未以编译或 TCP 测试代替这项验收。未来进入游戏同步阶段时按 [ROADMAP.md](ROADMAP.md) 逐步建立真实存档与命令回放测试。
