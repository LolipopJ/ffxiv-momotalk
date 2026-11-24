# Proposal

## Why

当前插件仅支持好友私聊（Tell）的采集、发送与回看，玩家日常在《最终幻想 XIV》中最常使用的跨服通讯贝、通讯贝、部队（FC）以及固定队小队沟通仍分散在游戏原生聊天框中。将这些群聊频道统一接入 MomoTalk，并为每位发言者展示 Lodestone 头像、在退群重加或频道改名后延续历史记录，能让玩家在同一个沉浸式聊天窗口里完整追踪所有核心社交对话。

## What Changes

- 新增跨服通讯贝（CWLS 1–8）、通讯贝（LS 1–8）与部队（FC）群聊消息的采集与真实发送（分别对应 `/cwl1..8`、`/l1..8` 与 `/fc`）。
- 为各群聊频道建立独立于槽位与名称的稳定身份键：FC 使用原生 `Id`，普通 LS 使用原生 `Id`，CWLS 使用 `FoundationTime`；退出频道后永久保留本地历史记录，重新加入后自动复用，频道改名后保留既有记录并更新展示名称。
- 新增固定队（Static）群聊：支持同时保存多个固定队，通过游戏原生小队列表右键菜单触发「从当前小队创建」（弹窗输入群名）或「打开固定队群组」；仅当当前所在小队（兼容普通/副本小队与跨服小队）的真实玩家成员与固定队成员严格 1:1 完全一致时，将小队消息归入该固定队并允许通过 `/p` 发送；支持右键修改固定队名称，不支持修改成员名单，不提供删除固定队功能。
- 群聊时间线为每位发言者展示 Lodestone 圆形头像与角色名（不显示跨服服务器），连续消息按发言者分组聚合；优先复用发言者 `ContentId` 索引头像并在缺失时按角色名与服务器回退缓存。
- 扩展 MomoTalk 界面为「好友 / 群组 / 会话」三 Tab 导航（`[Contacts]`、`[Groups]`、`[Chats]`，群组位于会话上方）：在「群组」展示当前已加入的 FC / CWLS / LS 与已保存固定队及详情卡，在「会话」统一展示私聊与所有活跃/已退出群聊（已退出 FC / 通讯贝正常显示、不作特殊处理，打开后仅禁用发送并提示原因，保留 `Delete Chat` 仅隐藏列表项且永久保存历史的行为）。
- **BREAKING**：升级每角色本地 SQLite 历史库架构（`SchemaVersion` 升级至 3）以统一存储私聊、群聊、发言者元数据与固定队成员名单；按项目 MVP 约定不兼容旧版数据库格式，遇到旧版文件时报告错误并保留原文件。

## Capabilities

### New Capabilities

- `group-chat`: 跨服通讯贝（CWLS 1–8）、通讯贝（LS 1–8）、部队（FC）与固定队群聊的频道身份识别、收发归档、退出重加与改名历史延续、小队右键创建/打开与精确全员匹配、以及群聊发言者 Lodestone 头像解析。

### Modified Capabilities

- `momotalk-ui`: 将导航从双 Tab 扩展为「好友 / 群组 / 会话」三 Tab 布局，新增群组列表与群组详情卡、群聊统一会话行与发言者预览、按发言者聚合且隐藏跨服服务器的群聊气泡（开启本人头像时显示 `You` 名称对齐气泡）、固定队创建/改名弹窗及群聊右键菜单。

## Impact

- 影响 `src/FFXIVMomoTalk/Plugin.cs`：新增 `IContextMenu` 服务注入并接入群组频道目录与小队右键菜单生命周期。
- 影响 `src/FFXIVMomoTalk/Chat/`：扩展统一会话与消息模型（`ChatModels.cs`）、新增群组/小队原生状态读取与目录（复用/重构 `FriendDirectory.cs`）、将采集与发送管线扩展为支持 Tell / FC / CWLS / LS / Party（`ChatCapture.cs`、`TellSender.cs`）、升级 SQLite 持久化层（`HistoryStore.cs`）并扩展 `AvatarCache.cs` 以支持群聊非好友发言者的头像缓存。
- 影响 `src/FFXIVMomoTalk/Windows/`：扩展 `MomoTalkWindow.cs`、`ConversationHistory.cs` 与 `ChatVisuals.cs`，支持三 Tab 导航、群组视图、群聊气泡与弹窗交互。
- 依赖原生游戏结构与 Dalamud API 15：`InfoProxyFreeCompany`、`InfoProxyLinkshell`、`InfoProxyCrossWorldLinkshell`、`GroupManager`、`InfoProxyCrossRealm`、`RaptureLogModule` 与 `IContextMenu`，需在国际服实机验证。
