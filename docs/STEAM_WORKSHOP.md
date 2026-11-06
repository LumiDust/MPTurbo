# Steam 创意工坊发布

TurboTick 面向 **RimWorld 1.7**，游戏 AppID 为 **294100**。当前为 **0.4.0 版本**。本指南不执行 Steam 上传。

## 1. 构建并准备上传目录

维护环境需要 .NET 10 SDK 和 PowerShell 7，在仓库根目录执行：

```powershell
pwsh scripts/package.ps1
pwsh scripts/prepare-workshop.ps1
```

第二条命令生成稳定的 `workshop-local/TurboTick/` 上传目录。也可以直接指定游戏内的本地 mod 目录：

```powershell
pwsh scripts/prepare-workshop.ps1 -OutputDirectory "<游戏目录>/Mods/TurboTick"
```

上传该 **mod 目录**，而不是源码仓库或 ZIP。独立服务器单独通过 GitHub 构建产物分发，不放进创意工坊客户端内容。

## 2. 核对发布材料

- 名称：`TurboTick`；固定 packageId：`lumidust.turbotick`。
- [About.xml](../About/About.xml) 声明版本 `0.4.0`、游戏版本 `1.7`、唯一依赖 Harmony；版本号与 [Directory.Build.props](https://github.com/LumiDust/TurboTick/blob/main/Directory.Build.props) 一致。
- [LoadFolders.xml](../LoadFolders.xml) 加载根目录和 `1.7/`；三个自有 DLL 位于 `1.7/Assemblies/`。
- 预览图 [Preview.png](../About/Preview.png) 已提供：640×360 PNG；图标 [ModIcon.png](../About/ModIcon.png) 为 64×64。
- 本项目采用“预览图小于 1 MB”的保守标准。640×360 是项目推荐尺寸，不声明为 Steam 官方强制像素。
- 页面描述可复制 [WORKSHOP_DESCRIPTION.txt](WORKSHOP_DESCRIPTION.txt)，保留开发原型与功能范围说明。

## 3. 在游戏中上传

1. 使用长期维护该条目的 Steam 账号，登录 Steam 客户端并从库启动持有有效许可的 RimWorld。首次贡献时按提示接受 [Steam Workshop 协议](https://steamcommunity.com/workshop/workshoplegalagreement/)。
2. 将准备好的目录放进游戏 `Mods/`，启用 Harmony 和 TurboTick，重启后检查日志、设置页、连接与倍率功能。
3. 回到主菜单 **Mods**，选择本地 `TurboTick`。使用其上传/发布入口；准确按钮名称与开发模式条件以当前 1.7 界面为准。
4. 提交前核对标题、描述、预览、内容目录与可见性。首传选择 **Private/Hidden**，好友联调再用 **Friends-only**。如果上传器没有可见性选项，先确认初始设置再提交，避免开发原型意外公开。
5. 上传成功后打开条目页，核对作者、内容、预览与可见性。在作者编辑菜单的“必需物品 / Required Items”添加 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)。
6. 记录网页 URL 中的真实条目 ID，并检查刚上传目录的 `About/PublishedFileId.txt`；二者一致后备份。不要填占位 ID 或其他 mod 的 ID。
7. 用另一测试环境订阅验证。实机验收与发布范围达标后，再将可见性切为 **Public**；Unlisted 和 Private 的访问权限不同。

## 4. 后续更新与 ID 保留

更新继续使用同一 Steam 账号、packageId 和 Workshop 条目 ID。先构建，再运行 `prepare-workshop.ps1` 更新稳定目录，最后用游戏的更新入口提交并填写 Change Notes。

- `package.ps1` 每次重建临时发布包，干净模板包不带 Workshop ID。
- `prepare-workshop.ps1` 保留目标目录现有 ID，并备份至本机 `workshop-local/PublishedFileId.txt`。若目标文件缺失，会尝试从该备份恢复。
- 首传前没有 ID 时，脚本不会生成它。`workshop-local/` 和 `PublishedFileId.txt` 均由 Git 忽略。
- 从游戏目录上传后，应继续使用那个稳定目录更新。首次 ID 的实际回写/暂存位置需通过上传日志与当前 1.7 客户端核对，不能只凭 API 名称推定。

## 5. 当前发布状态

封面、图标、描述、版本元数据、打包和上传目录准备脚本已齐备。**尚未实际上传 Steam，也未完成游戏内双端验收**；完整共享殖民地操作仍按 [路线图](ROADMAP.md) 开发。

服务器目前可从 [GitHub Actions](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml) 下载，正式发布时可将 server ZIP 附在 GitHub Release。运行需要 .NET 10 Runtime；启动方式见 [README](../README.md)。

## 官方依据与核验范围

- [Steamworks Workshop Implementation](https://partner.steamgames.com/doc/features/workshop/implementation)：创建/更新流程、保存条目 ID、首次协议确认。接受协议前隐藏不等于主动设置永久私密。
- [ISteamUGC](https://partner.steamgames.com/doc/api/ISteamUGC)：上传内容与主预览、SubmitItemUpdate、许可/登录错误及更新日志。
- [附加预览要求](https://partner.steamgames.com/doc/api/ISteamUGC#AddItemPreviewFile)：官方明确列出附加预览 under 1 MB；本次未直接取得主图上限的完整条款，未将两者混为一谈。
- [可见性枚举](https://partner.steamgames.com/doc/api/ISteamRemoteStorage#ERemoteStoragePublishedFileVisibility) 与 [条目依赖](https://partner.steamgames.com/doc/api/ISteamUGC#AddDependency)。

核对日期：2026-11-05。游戏内按钮、首次可见性与真实 ID 回写位置尚待首次实机发布核验。
