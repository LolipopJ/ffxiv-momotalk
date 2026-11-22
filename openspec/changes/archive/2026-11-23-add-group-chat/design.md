# Design

## Context

当前插件已建立完整的单聊管线（见 `proposal.md` 动机与范围）：
- `FriendDirectory` 在框架线程跟踪登录会话与原生好友列表（`InfoProxyFriendList`）并在 SQLite 缓存好友。
- `ChatCapture` 订阅 `IChatGui.ChatMessageUnhandled` 采集 `TellIncoming` / `TellOutgoing`。
- `TellSender` 通过 `UIModule.ProcessChatBoxEntry` 提交 `/tell`，以 5 秒窗口对齐出站回显与拒发日志。
- `HistoryStore`（当前 `SchemaVersion = 2`）按角色 ContentId 维护独立 SQLite 数据库与分页读取。
- `AvatarCache` 按 `ContentId` 索引并按 1 天周期增量复核 Lodestone 公开头像，渲染端仅对视口可见气泡触发加载。
- `MomoTalkWindow`、`ConversationHistory` 与 `ChatVisuals` 实现无标题栏双 Tab 响应式界面。

本次变更的目标运行环境仍为 Windows x64、.NET 10、Dalamud API 15 与 `FFXIVClientStructs`（v7.57）。依据仓库 MVP 约定，数据库架构升级不做旧版兼容与迁移，遇不可识别旧库直接报错并保留原文件。

## Goals / Non-Goals

**Goals:**
- 将会话、消息、采集、发送、历史存储与头像缓存演进为统一的多频道抽象，同时支持私聊（Tell）、部队（FC）、跨服通信贝（CWLS 1–8）、本服通信贝（LS 1–8）与固定队（Static）。
- 确保 FC、LS 与 CWLS 使用不随名称或槽位改变的稳定主键，支持退群留存、重加复用与改名同步。
- 通过游戏原生小队列表右键菜单无缝创建/打开固定队，并在普通/副本小队与跨服小队间实现严格的 1:1 成员集合匹配。
- 在群聊中为每位发言者展示 Lodestone 圆形头像，同时沿用视口懒加载避免大群触发 Lodestone 限流。
- 将 MomoTalk UI 扩展为「好友 / 群组 / 会话」三 Tab（群组位于会话上方），保持既有一致的视觉风格。

**Non-Goals:**
- 不提供 `SchemaVersion 2 -> 3` 的数据迁移或双读兼容路径。
- 不支持修改已创建固定队的成员名单，也不提供删除固定队功能。
- 不自动替玩家打开或轮询请求原生社交窗口数据包（严格遵守被动观察游戏状态原则）。
- 不扩展非好友私聊，不纳入喊话、呼喊、团队（Alliance）、新人频道或 PvP 小队等其它频道。

## Decisions

### 1. 统一会话键模型与频道稳定标识

将原仅靠 `ulong peerContentId` 标识会话的设计重构为 `ConversationKey(ConversationKind Kind, string StableKey)`：

| `ConversationKind` | 稳定键来源（`StableKey`） | 发送命令 | 说明 |
|---|---|---|---|
| `Tell` (0) | `{peerContentId:X16}` | `/tell {Name}@{World} {body}` | 与现有好友私聊一致 |
| `FreeCompany` (1) | `{fcId:X16}` | `/fc {body}` | 取自 `InfoProxyFreeCompany.Instance()->Id`（不可变 64 位 ID） |
| `Linkshell` (2) | `{lsId:X16}` | `/l{slot} {body}` | 取自 `InfoProxyLinkshell.Instance()->LinkShells[i].Id`（不可变 64 位 ID） |
| `CrossWorldLinkshell` (3) | `{foundationTime}` | `/cwl{slot} {body}` | 取自 `InfoProxyCrossWorldLinkshell.Instance()->CrossWorldLinkshells[i].FoundationTime`（不可变创立时间戳） |
| `Static` (4) | 排序后全员 `ContentId` 拼接串（`{c1:X16}-{c2:X16}-...`） | `/p {body}` | 成员集合不可变，确定性主键天然保证同一阵容不会重复创建 |

- **备选方案**：
  - 使用槽位号（如 `cwls:1`）作为主键：退出后新加贝或调整顺序会导致新旧历史混杂，直接否决。
  - 读取 `CrossWorldLinkshellEntry` 未映射偏移寻找内部 CWLS ID：跨版本极易因结构体布局变化崩溃；直接使用 `FFXIVClientStructs` 已映射的 `FoundationTime` 更稳健，且已与需求对齐。
  - 为固定队生成随机 GUID 另加成员集合唯一性检查：因固定队创建后成员不可变更，用排序后的 `ContentId` 规范串作为稳定键更简洁，并在数据库层直接保证 1:1 唯一性。

### 2. 原生社交与小队状态目录（框架线程快照）

扩展/重组 `FriendDirectory` 为统一的 `SocialDirectory`（保持单一不可变快照供 UI 与采集/发送线程无锁读取），每帧（及采集/发送前）在框架线程更新：
- **好友**：保留原有 `InfoProxyFriendList` 钩子与 SQLite 本地缓存机制，并额外挂钩 `ClearListData` 与 `HandleZoneInitPacket`、在跨服副本（`IsInCrossWorldDuty()`）或切图加载（`!playerState.IsLoaded`）期间保持会话与已缓存好友列表不被清空或覆盖。
- **部队（FC）**：读取 `InfoProxyFreeCompany.Instance()` 的 `Id` 与 `NameString`。当 `Id != 0` 且名称非空时记为当前活跃部队，并在名称变更时同步更新已有会话的 `display_name`。
- **本服通信贝（LS 1–8）**：遍历 `InfoProxyLinkshell.Instance()->LinkShells`（索引 `0..7`），对 `Id != 0` 的槽位读取 `GetLinkshellName(entry.Id)`（必要时回退 `InfoProxyChat.GetLinkShellName(i)`），维护 `slot <-> (Id, Name)` 双向映射并同步改名。
- **跨服通信贝（CWLS 1–8）**：遍历 `InfoProxyCrossWorldLinkshell.Instance()->CrossWorldLinkshells`（索引 `0..7`），对 `FoundationTime != 0` 的槽位读取 `Name.ToString()`，维护 `slot <-> (FoundationTime, Name)` 双向映射并同步改名。
- **当前小队（普通/副本 + 跨服）**：
  - 先检查 `GroupManager.Instance()->MainGroup`：读取 `PartyMembers[0..MemberCount]` 中 `ContentId != 0` 的成员（自动排除陆行鸟搭档与剧情辅助 NPC）。
  - 若 `MainGroup` 有效真实玩家不足 2 人，则检查 `InfoProxyCrossRealm.Instance()`：当 `IsInCrossRealmParty()` 为真时，读取 `CrossRealmGroups[LocalPlayerGroupIndex].GroupMembers` 中 `ContentId != 0` 的成员。
  - 对读取到的真实玩家按 `ContentId` 排序生成当前小队的规范成员键 `ActivePartyRosterKey`，用于 O(1) 判定是否与任一已保存固定队 1:1 匹配。

### 3. 游戏原生小队列表右键菜单（`IContextMenu`）

在 `Plugin.cs` 注入 `IContextMenu`，在独立轻量协调器（或 `MomoTalkWindow` 回调接口）中订阅 `OnMenuOpened`：
- **显示条件**：玩家已登录、`args.MenuType == ContextMenuType.Default`、`args.AddonName` 属于小队列表面板（`_PartyList`、`PartyMemberList`，实机核验跨服小队列表是否涉及 `SocialList`），且当前 `SocialDirectory` 快照中的真实玩家小队人数 `>= 2`。
- **动态菜单项**：
  - 若 `ActivePartyRosterKey` 命中当前角色已保存的固定队：添加 `Open Static Group`（`UseDefaultPrefix = true`），点击后置前打开 MomoTalk（跳过入场动画），切至「会话」Tab 并定位该固定队会话。
  - 若未命中任何固定队：添加 `Create Static Group`，点击后捕获当前小队全员快照，置前打开 MomoTalk（跳过入场动画）并弹出创建固定队弹窗，等待玩家输入名称并确认保存。

### 4. 多频道采集、本人回显识别与发言者身份解析

扩展 `ChatCapture` 处理以下 `XivChatType`：
- `TellIncoming` / `TellOutgoing`：保持原有好友校验与待解析队列逻辑。
- `FreeCompany` (24)：映射至当前活跃 FC 稳定键。
- `CrossLinkShell1` (37) 与 `CrossLinkShell2..8` (101..107)：映射至槽位 `0..7` 对应的 CWLS 稳定键。
- `Ls1..Ls8` (16..23)：映射至槽位 `0..7` 对应的 LS 稳定键。
- `Party` (14) 与 `CrossParty` (32)：仅当 `ActivePartyRosterKey` 精确命中某个已保存固定队时映射至该固定队稳定键；否则忽略该条小队消息。

**发言方向与发言者信息解析**：
- 群聊的 `Outgoing` 由 `message.SourceKind == XivChatRelationKind.LocalPlayer` 判定；若为本人，发言者直接使用 `session.Player`。
- 他人发言从 `OriginalSender` 解析 `PlayerPayload` 得到 `(Name, WorldId)`；当同服频道省略跨服标记且 `WorldId == 0` 时，按频道语义补全为当前角色本服（FC 与 LS 成员恒属本服，同服小队回退当前服务器）。
- `ContentId` 解析优先级：
  1. 固定队/小队消息：直接从当前小队快照匹配（100% 命中）。
  2. 好友列表：从 `SocialDirectory` 好友缓存匹配。
  3. 原生日志来源表：在框架线程检查 `RaptureLogModule.Instance()->MsgSourceArray` 尾部近期记录（按 `ChatType` 与 `World` 匹配最新条目）。
  4. 已加载成员代理：若 `InfoProxyFreeCompanyMember` / `InfoProxyCrossWorldLinkshellMember` / `InfoProxyLinkshellMember` 的 `CharDataSpan` 已有匹配项则复用其 `ContentId`。
  5. 回退：`ContentId = 0`，仍保留完整的 `(Name, WorldId, World)` 用于展示与 Lodestone 头像查询。

### 5. 多频道发送与回显对账（`ChatSender`）

将 `TellSender` 泛化为 `ChatSender`，复用全部原生安全约束（500 字节 UTF-8 上限、单行无控制字符校验、`Utf8String.SanitizeString((AllowedEntities)0x27F)`、`UIModule.ProcessChatBoxEntry`、5 秒回显/拒发对账与按频道归属的警告队列）：
- 发送前在框架线程再次读取最新 `SocialDirectory` 快照并核验发送权限：
  - `Tell`：确认仍在好友名单中。
  - `FreeCompany`：确认当前活跃 FC 的稳定键等于目标会话。
  - `CrossWorldLinkshell` / `Linkshell`：按稳定键反查该贝**当前所在槽位 `1..8`**（因此退贝重加换槽后仍准确发往正确槽位），未在列表中则拒绝发送。
  - `Static`：确认当前真实玩家小队与该固定队成员集合 1:1 完全一致，否则拒绝发送并提示原因。
- 发送命令：`/fc <body>`、`/cwl{N} <body>`、`/l{N} <body>`、`/p <body>`、`/tell {Name}@{World} <body>`（带正文单次发送不会切换玩家原生聊天框默认频道）。
- 回显对账：按 `(ConversationKey, NormalizedBody)` 匹配 `Outgoing == true` 的已采集消息，并继续支持拒发日志归因。

### 6. SQLite Schema v3 持久化设计

将 `HistoryStore.SchemaVersion` 升至 `3`，在每角色 `{ContentId:X16}.sqlite3` 中使用统一表结构：

```sql
CREATE TABLE IF NOT EXISTS conversations (
    id INTEGER PRIMARY KEY,
    kind INTEGER NOT NULL,
    stable_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    world_id INTEGER NOT NULL,
    world_name TEXT NOT NULL,
    last_message_id INTEGER,
    last_sent_at INTEGER,
    UNIQUE (kind, stable_key)
);

CREATE TABLE IF NOT EXISTS messages (
    id INTEGER PRIMARY KEY,
    conversation_id INTEGER NOT NULL REFERENCES conversations (id),
    sent_at INTEGER NOT NULL,
    outgoing INTEGER NOT NULL,
    sender_content_id INTEGER NOT NULL,
    sender_name TEXT NOT NULL,
    sender_world_id INTEGER NOT NULL,
    sender_world_name TEXT NOT NULL,
    body TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS hidden_conversations (
    conversation_id INTEGER PRIMARY KEY REFERENCES conversations (id)
);

CREATE TABLE IF NOT EXISTS static_groups (
    stable_key TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS static_members (
    static_key TEXT NOT NULL REFERENCES static_groups (stable_key),
    content_id INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    world_id INTEGER NOT NULL,
    world_name TEXT NOT NULL,
    PRIMARY KEY (static_key, content_id)
);
```

保留 `message_rejections`、`cached_friends`、`friend_notes` 与 `store_meta` 表及 `(conversation_id, sent_at, id)` 索引。
- 固定队创建时写入 `static_groups` 与 `static_members`，并可按需打开空会话；`Delete Chat` 仅写入 `hidden_conversations`，绝不删除 `messages` 或 `static_groups`。
- 固定队改名在单事务内同时更新 `static_groups.display_name` 与对应的 `conversations.display_name`（若已存在），并递增版本号通知 UI 刷新。
- 频道（FC / LS / CWLS）或好友名称变化时，按 `(kind, stable_key)` 更新 `conversations.display_name`（及好友的 `world_id` / `world_name`），历史消息完整保留。

### 7. 群聊发言者头像缓存扩展（`AvatarCache`）

- 将 `AvatarCache` 的缓存键由 `ulong ContentId` 扩展为结构化 `AvatarKey`：
  - `ContentId != 0`：沿用 `{ContentId:X16}` 文件前缀（保持改名复用 `LodestoneId` 与现有磁盘文件命名规则）。
  - `ContentId == 0`：采用 `w{WorldId}-{Sha256(lower(trim(Name)))[..16]}` 作为稳定磁盘前缀，同样走 1 天复核周期、条件 HTTP 请求与内容哈希热刷新。
- 维持单并发 `SemaphoreSlim(1)` 与时间线 `ImGui.IsRectVisible` 视口触发策略，确保大型部队/通信贝仅拉取当前屏幕可见的发言者头像。

### 8. 三 Tab MomoTalk 界面与交互编排

- **导航条顺序**：宽屏左侧导航条自上而下、窄屏底栏自左而右固定为 `Contacts`（好友） -> `Groups`（群组） -> `Chats`（会话）。在 `ChatVisuals` 中新增矢量「群组」图标（使用与背景预混合的不透明次级色及中心人物镂空描边，避免半透明重叠色差）及各群聊类型的圆形矢量徽章（FC / CWLS / LS / Static）。
- **「群组」Tab**：
  - 列表展示当前活跃 FC、当前加入的 CWLS（按槽位 1–8）、当前加入的 LS（按槽位 1–8）以及全部已保存固定队，支持顶部实时搜索。
  - 右侧展示 MomoTalk 风格群组详情卡（居中大圆群徽章 + 右下角快捷聊天按钮、群名、频道类型胶囊；固定队下方展示成员头像与名单及当前小队匹配状态胶囊）。
  - 单击查看详情，双击或点击快捷聊天按钮进入「会话」Tab 打开该群聊；右键固定队仅提供 `Rename Group`。
- **「会话」Tab**：
  - 统一展示私聊与所有有历史记录（或当前打开）的群聊，保留 `Recent` / `Name` 与升降序排序。
  - 群聊副标题预览为 `You: <正文>` 或 `<发言者名>: <正文>`；行尾显示频道标签（如 `FC`、`CWLS 1`、`LS 1`、`Static`；已退出频道显示 `FC` / `CWLS` / `LS`，视觉样式与在群会话保持一致，不作特殊淡化或标记）。
  - 右键菜单：好友保留 `Open Lodestone` + `Delete Chat`；FC / CWLS / LS 提供 `Delete Chat`；固定队提供 `Rename Group` + `Delete Chat`。
- **群聊对话画布**：
  - 顶栏展示群名与频道标签；时间线连续分组键扩展为发言者身份（`Outgoing` + `SenderContentId` + `SenderName` + `SenderWorldId`），不同成员发言各自显示头像、角色名（不含跨服 `@ 服务器`）与左尖角，本人消息继续遵循 `Show Self Avatar` 配置并在开启本人头像时于右侧气泡上方右对齐显示 `You` 名称使气泡与头像垂直对齐。
  - 底部输入栏按频道切换占位提示，并按 `SocialDirectory` 实时判定可用性，不可发送时禁用发送按钮并在上方显示 MomoTalk 系统灰条提示。
- **固定队创建与改名弹窗**：
  - 复用设置弹窗的半透明遮罩 + 居中白底圆角卡片框架，支持回车确认、ESC/点遮罩关闭及空名称校验；弹窗打开期间禁用窗口 `RespectCloseHotkey` 避免按 ESC 误关主窗口。

## Risks / Trade-offs

- **原生代理登录态填充时机需实机验证** → 在国际服实机检查刚登录未打开原生部队/贝窗口时，`InfoProxyFreeCompany.Id`、`InfoProxyLinkshell.LinkShells[i].Id` 与 `InfoProxyCrossWorldLinkshell.CrossWorldLinkshells[i].FoundationTime` 是否已自动就绪。若某个代理需打开原生面板后才填充，则沿用好友缓存模式在本地持久化槽位到稳定键的缓存并提示刷新。
- **`RaptureLogModule.MsgSourceArray` 与 `ChatMessageUnhandled` 的写入时序** → 实机验证消息到达时 `MsgSourceArray` 是否已写入对应 `ContentId`；同时设计已包含成员代理与 `(Name, World)` 回退键，即便极端情况下未能取到 `ContentId` 也不会影响群聊头像加载。
- **跨服小队右键菜单的触发 Addon 名称差异** → 在国际服分别实机验证普通小队、副本内小队与跨服小队下右键 `_PartyList` 与 `PartyMemberList`（及 `SocialList`）时的 `args.AddonName`，确保两种小队形态都能稳定弹出固定队菜单项。
- **CWLS 使用 `FoundationTime` 的理论碰撞** → 若同一角色曾先后加入两个在完全同一秒创建的 CWLS 才会冲突，现实概率极低；相比读取未映射内存偏移更具跨补丁稳定性。
- **SQLite Schema v3 破坏性升级** → 遵循项目 MVP 约定不提供 v2 迁移；旧库存在时清晰报告错误并保留原文件不覆盖。
