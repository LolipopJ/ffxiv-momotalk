# Tasks

## 1. 统一多频道模型与 SQLite v3 持久化

- [x] 1.1 在 `src/FFXIVMomoTalk/Chat/ChatModels.cs` 定义统一会话类型 `ConversationKind`（`Tell`、`FreeCompany`、`Linkshell`、`CrossWorldLinkshell`、`Static`）、会话键 `ConversationKey`、发言者与消息模型、频道快照及固定队模型，并确保项目编译通过（`dotnet build .\FFXIVMomoTalk.slnx -c Debug`）。
- [x] 1.2 将 `src/FFXIVMomoTalk/Chat/HistoryStore.cs` 升级至 `SchemaVersion = 3`，实现 `conversations`（按 `(kind, stable_key)` 唯一）、含发言者字段的 `messages`、`hidden_conversations`、`static_groups` 与 `static_members` 表的读写、固定队创建与改名、频道名称同步及旧版数据库报错保留逻辑，确认 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过且无残留 v2 兼容路径。

## 2. 原生社交与小队状态目录

- [x] 2.1 扩展 `src/FFXIVMomoTalk/Chat/FriendDirectory.cs`（或重构为统一社交目录），在框架线程读取 `InfoProxyFreeCompany`（`Id` / `NameString`）、`InfoProxyLinkshell`（槽位 `1..8` 的 `Id` 与名称）、`InfoProxyCrossWorldLinkshell`（槽位 `1..8` 的 `FoundationTime` 与名称）以及已保存固定队列表，实现频道改名自动同步到 `HistoryStore`，并验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。
- [x] 2.2 在社交目录中接入当前小队快照读取，同时覆盖 `GroupManager.MainGroup`（普通与副本内小队）与 `InfoProxyCrossRealm`（跨服小队），过滤 `ContentId == 0` 的非真实玩家并生成排序后的规范全员键以执行 1:1 固定队精确匹配，验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。

## 3. 多频道收发管线与群聊发言者头像

- [x] 3.1 扩展 `src/FFXIVMomoTalk/Chat/ChatCapture.cs` 采集 `FreeCompany`、`CrossLinkShell1..8`、`Ls1..8`、`Party` 与 `CrossParty` 消息：用 `SourceKind == LocalPlayer` 区分本人回显，按小队快照、好友缓存、`RaptureLogModule.MsgSourceArray` 与成员代理依次解析他人发言者 `ContentId` 与服务器，并仅在小队与固定队全员严格 1:1 匹配时归档小队消息；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。
- [x] 3.2 将 `src/FFXIVMomoTalk/Chat/TellSender.cs` 泛化为多频道发送管线，支持 `/tell`、`/fc`、`/cwl1..8`（按稳定键动态反查当前槽位）、`/l1..8` 与 `/p`，在框架线程提交前核验角色与频道/全员匹配状态，复用 500 字节上限、`SanitizeString` 与 5 秒回显/拒发对账；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。
- [x] 3.3 扩展 `src/FFXIVMomoTalk/Chat/AvatarCache.cs`，在保留 `ContentId` 主键与 1 天增量复核的同时支持 `ContentId == 0` 发言者按 `(WorldId, Name)` 稳定缓存 Lodestone 头像；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。

## 4. 三 Tab 界面、群组视图、群聊气泡与原生小队右键入口

- [x] 4.1 扩展 `src/FFXIVMomoTalk/Windows/ChatVisuals.cs` 新增「群组」导航矢量图标及 FC / CWLS / LS / Static 圆形频道徽章绘制原语，并更新 `src/FFXIVMomoTalk/Windows/ConversationHistory.cs` 适配统一 `ConversationKey` 与带发言者信息的消息分页；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。
- [x] 4.2 更新 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs` 导航为「好友 / 群组 / 会话」三 Tab（宽屏左侧自上而下、窄屏底栏自左而右，群组位于会话上方，导航图标使用不透明次级色），实现「群组」列表与右侧群组详情卡（含固定队成员头像与名单及全员匹配状态），以及「会话」统一混排列表（群聊预览 `发言者: 内容` / `You: 内容`、行尾频道标签、已退出 FC/通信贝正常展示无特殊样式）与右键菜单（固定队支持 `Rename Group`，全部会话支持 `Delete Chat` 隐藏并永久保留历史，不提供删除固定队）。
- [x] 4.3 更新 `MomoTalkWindow.cs` 对话画布与输入栏：群聊时间线按发言者分组聚合连续气泡，为每位他人发言者展示 Lodestone 头像与角色名（不显示跨服服务器），本人消息沿用 `Show Self Avatar` 设置并在开启时右对齐显示 `You` 名称；输入栏按频道切换占位提示并在已退出频道或未与固定队全员组队时禁用发送并展示系统灰条提示；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 通过。
- [x] 4.4 在 `MomoTalkWindow.cs` 实现与设置弹窗一致的「创建固定队」弹窗（展示当前小队全员与群名输入框）与「修改固定队名称」弹窗，并在 `src/FFXIVMomoTalk/Plugin.cs` 注入 `IContextMenu` 监听原生小队列表右键菜单，按当前小队是否匹配已保存固定队动态显示「从当前小队创建」或「打开固定队群组」；验证 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 与 `dotnet build .\FFXIVMomoTalk.slnx -c Release` 均成功。

## 5. 构建校验与国际服实机验收

- [x] 5.1 运行 `dotnet restore .\FFXIVMomoTalk.slnx --locked-mode`、`dotnet build .\FFXIVMomoTalk.slnx -c Debug`、`dotnet build .\FFXIVMomoTalk.slnx -c Release` 与 `openspec validate --all --strict`，确认全部命令零错误通过。
- [x] 5.2 在国际服 Dalamud 实机验证 FC、CWLS 1–8 与 LS 1–8 登录加载、消息收发、本人回显识别、非好友发言者头像（含 `RaptureLogModule.MsgSourceArray` 与回退缓存）、退群后历史保留且列表正常展示、换槽/重加复用历史与频道改名延续历史。
- [x] 5.3 在国际服 Dalamud 实机验证普通小队、副本内小队与跨服小队下原生小队列表右键菜单（「从当前小队创建」弹窗创建与「打开固定队群组」直达）、多固定队精确 1:1 匹配与缺员/多员不入群、固定队右键改名、副本内好友缓存保留、以及宽/窄窗口三 Tab（「好友 / 群组 / 会话」）完整交互。
