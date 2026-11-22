# Design

## Context

项目以国际服 Dalamud API 15 和 .NET 10 为目标，采用官方 `Dalamud.NET.Sdk` 模板的构建约定。玩家需要在游戏内向好友提交实际私聊，并在同一窗口看到游戏记录的结果。采用客户端已经提供的聊天与社交数据。 [Dalamud v15](https://dalamud.dev/versions/v15/) · [官方插件模板](https://github.com/goatcorp/SamplePlugin)

## Goals / Non-Goals

**Goals**

- 用一个插件提供从选好友、输入和发送到读取本地对话的流程。
- 在玩家换角、名单更新或客户端拒绝发送时明确归属与状态。

**Non-Goals**

- 首版的频道为好友 Tell，运行界面为游戏内窗口，历史范围为插件启用后采集的消息。

## Decisions

### 游戏内窗口

继续使用 Dalamud `WindowSystem` 和 ImGui，提供 `/momotalk` 入口。窗口以浅粉标题栏、浅色双栏和左右圆角气泡为视觉参考，配套原创图标与间距。会话列表占左栏，上方提供排序切换和好友选择入口。右栏保留目标角色名与所属服务器，底部提供单行正文和发送按钮。已有会话与好友选择共享同一份游戏名单，打开空会话时按需生成视图，第一条事件成功归档后才写会话记录。窄窗口在列表与对话之间切换。同名条目显示服务器，名称排序按完整角色名使用忽略大小写的比较规则，名称相同时按服务器与会话标识确定顺序。网页前端适合跨设备访问，当前游戏内方案可以省去额外服务。 [Dalamud Windowing 指南](https://dalamud.dev/plugin-development/technical-considerations/) · [参考配色](https://github.com/U1805/momotalk/blob/main/src/assets/css/mixin.scss)

### 好友资料与消息采集

- 在框架线程读取 `IPlayerState` 和本地 `InfoProxyFriendList` 已载入的条目。原生好友列表加载或刷新后生成当前角色的完整快照。名单未知和已加载为空是两个状态。名单未知时提示打开原生好友列表，限量暂存当前登录期间的待识别 Tell，退出时释放未解析事件。已归档的历史仍可查看，发送时使用已确认的当前名单。
- `ChatCapture` 订阅 `IChatGui.ChatMessageUnhandled` 中的 `TellIncoming` 与 `TellOutgoing`。优先从 `SeString` 的角色链接取角色名和 `HomeWorld`。出站事件的对端是收件人，发言者为当前角色。缺少世界信息时，只有能在当前好友快照中唯一确定对端的事件才可归档。
- `ConversationService` 以当前登录角色及对端的规范化名称和 Home World ID 定位会话。任何无法确认身份的出站或入站事件都不得合并进现存对话。把已解析的普通 C# 数据投递到后台存储队列，游戏帧内完成快照复制。

[好友数据加载约束](https://dalamud.dev/plugin-publishing/restrictions/) · [好友客户端结构](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyCommonList.cs) · [聊天事件](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IChatGui/) · [Tell 对端解析实例](https://github.com/twelvehouse/ChatAnywhere/blob/main/Core/ChatReceiver.cs)

### 游戏内发送

1. 发送按钮提交当前草稿、登录角色和所选好友键。`TellSender` 通过 `IFramework.RunOnFrameworkThread` 在游戏框架线程重新确认会话、角色和好友名单，使用名单中的角色名与世界名构造固定格式 `/tell First Last@HomeWorld 正文`。校验空白、换行和控制字符，并按目标客户端的限制检查编码后的输入长度，拒绝超限内容。
2. 有效请求交给框架线程后，窗口立即清空输入框并接受下一次提交。每次调用前登记当前角色和发送目标，通过 `FFXIVClientStructs` 的 `UIModule.ProcessChatBoxEntry` 执行原生聊天输入，结束后释放临时 UTF-8 字符串。
3. 后台用当前登录角色、对端和游戏归一化后的正文依次匹配 `TellOutgoing`。真实回显形成发送历史，接收方是否收到由双端实机验证。发送后在有界窗口内收集游戏拒发日志。能唯一定位的拒发原因写到对应出站记录并显示在气泡下。已拒发的提交退出后续拒发匹配。无回显或无法定位的拒发在聊天区底部显示警告。异常发生时输入框保留玩家当前正在编辑的内容。超出窗口的回显按正常消息归档。切换角色时取消待提交动作，已捕获的事件按旧角色排队完成。

[游戏 Tell 命令](https://na.finalfantasyxiv.com/uiguide/communication/communication-chat/chat_type.html) · [原生输入入口](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/UIModule.cs) · [框架线程 API](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IFramework/) · [插件发送实例](https://github.com/twelvehouse/ChatAnywhere/blob/main/Core/ChatSender.cs)

### 数据与头像

- 每个本机登录角色在 `IDalamudPluginInterface.GetPluginConfigDirectory()` 下拥有独立的 SQLite 数据库。`conversations` 保存对端键、显示名和最近消息索引，`messages` 保存会话 ID、到达顺序、UTC 时间、方向和可见正文。以 `(conversation_id, sent_at, id)` 作为历史分页索引，以 `(sent_at, id)` 解决时间并列。后台使用 `Microsoft.Data.Sqlite` 事务同时写入消息和会话摘要，UI 只读取当前登录角色的会话快照。同文案的后续发送是独立事件。相比把历史写入配置 JSON，SQLite 可以直接分页，并能保证摘要和消息同时提交。
- 头像服务用一个 NetStone 客户端按名称和 Home World 查询，复核角色档案的名字和服务器。缓存键是角色身份，缓存值包含公开 Lodestone ID、抓取时间与图片路径。按可见项请求，合并同角色并发查询，网络失败遵循退避。将本地图像交给 `ITextureProvider`，窗口只消费已完成的纹理。头像请求使用公开角色信息，聊天正文留在本机。

[SQLite 与后台 I/O](https://learn.microsoft.com/dotnet/standard/data/sqlite/async) · [NetStone](https://github.com/xivapi/NetStone) · [纹理 API](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ITextureProvider/)

## Risks / Trade-offs

- 好友列表由游戏在原生界面打开时加载 → 使用当前角色的名单就绪状态决定是否可发送，通过原生面板完成同步。
- 回显可能被其他聊天插件接管，部分游戏拒发日志也缺少目标 → 后台仅在有证据时标记对应气泡，其余情况显示警告。
- Dalamud 或 FFXIV 补丁可能改变数据布局及原生输入接口 → 以目标 API 自带的 FFXIVClientStructs 为准，在实机验证发送前置校验、跨服收发和回显。
- Lodestone 的档案可见性与网络状态会影响头像 → 使用占位图与本地缓存，保持聊天操作独立。 [Lodestone 可见性规则](https://na.finalfantasyxiv.com/lodestone/special/update_log/2/)
