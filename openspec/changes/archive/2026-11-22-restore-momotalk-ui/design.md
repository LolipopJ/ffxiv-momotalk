# Design

## Context

当前插件已完成好友快照、真实 Tell 收发、SQLite 本地分页历史与 Lodestone 头像缓存（见 `src/FFXIVMomoTalk/Chat/`），窗口层由 `MomoTalkWindow.cs`、`ChatVisuals.cs` 与 `ConversationHistory.cs` 构成（动机与范围见 `proposal.md`）。

后端现有模型已提供本次 UI 还原所需的全部数据：
- `FriendDirectory` 提供当前角色快照与已加载的好友字典（含 `Name`、`WorldId`、`World`）。
- `HistoryStore.ReadConversationsAsync` 返回的 `ConversationSummary` 已包含 `Latest`（`Body`、`SentAt`、`Outgoing`、`Rejection`）。
- `AvatarCache` 按可见项懒加载双方及好友的 Lodestone 纹理。

目标运行环境为 Windows x64、.NET 10、Dalamud API 15 与 ImGui，视觉参考为 [u1805/momotalk](https://u1805.github.io/momotalk/) 的默认 MomoTalk 主题。

## Goals / Non-Goals

**Goals:**
- 在 ImGui 内以零外部图片/字体资源的方式高度还原 `u1805/momotalk` 的视觉配色、比例、斜切按钮、气泡尖角、连续消息合并与系统消息条。
- 将网页版双 Tab 语义自然映射为 FFXIV 好友列表（含资料卡）与私聊会话，并在宽/窄窗口下保持可用。
- 保持 `Chat/` 层、SQLite 结构及既有 Tell 发送与历史分页行为不变。

**Non-Goals:**
- 不引入 `YuzuTalk` 副主题、字体缩放设置弹窗、多语言切换、截图导出、重置画布、发言人切换条或回复/羁绊剧情卡片。
- 不打包自定义 `.ttf`/`.woff2` 字体或额外图片资源文件。
- 不修改 SQLite 表结构、`FriendDirectory`、`ChatCapture`、`TellSender` 或 `AvatarCache` 的持久化与收发契约。

## Decisions

### 1. 视觉 Token 与纯 `ImDrawList` 矢量绘制

直接采用从 `u1805/momotalk` 提取的默认 MomoTalk 配色并集中维护在 `ChatVisuals.cs`：
- 顶栏 `#fc96ab`（白字/白图标）
- 导航条底色 `#4b5a6f`，选中/悬浮 `#68788f`，未选图标透明 50%
- 列表底色 `#f3f7f8`，分割线与悬浮/选中 `#e1e7ec`，搜索框白底 `#ffffff` + 描边 `#dce5ec`（聚焦 `#ffb342`）
- 主文字 `#2a323e`，次文字 `#87929e`，服务器胶囊文字 `#5d7c8c` / 描边 `#c2cfd6`
- 对话画布纯白 `#ffffff`，对方气泡 `#4b5a6f`，本人气泡 `#4a8ac6`，气泡正文 `#ecf2fb`
- 系统消息条 / 时间分隔胶囊底色 `#e1e7ec`，拒发警示文字沿用高对比深红 `#a6324c`（区别于普通时间/提示文字 `#5d6b7a`）
- 底部输入区底色 `#eeeeee`，胶囊输入框 `#ffffff` + 描边 `#dce5ec`（聚焦 `#ffb342`）

所有标志性图形（Momo 桃形气泡 Logo、导航联系人/气泡图标、放大镜、`-15deg` 斜切平行四边形按钮与右上角深灰指示三角、气泡左右三角尖角、资料卡圆形发起对话按钮、发送纸飞机箭头、返回 `<` 与关闭 `x`）均通过 `ImDrawList` 按 `ImGuiHelpers.GlobalScale` 矢量绘制，字号层次通过 `AddText(font, scaledSize, ...)` 区分。

- **备选方案**：打包 PNG 图标或嵌入 `Blueaka` / `Nunito` 字体。放弃原因是会增加插件发布包体积、许可证与多语种（中日英韩混排）缺字风险；纯矢量绘制与 Dalamud 既有字体在 DPI 缩放与 CJK 显示上更稳妥。

### 2. 无原生标题栏自绘外壳与显式窗口拖动

窗口启用 `ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse`，将 `WindowPadding` 设为 `(0, 0)`、`WindowRounding` 设为 `10 * scale`、`WindowBorderSize` 设为 `0`（由内层轻描边替代），保留原生右下角缩放手柄。

顶栏高度约 `46 * scale`（按桌面比例适配游戏内常用 `~820x560` 窗口尺寸）。考虑到部分玩家在 Dalamud 设置中启用了 `ConfigWindowsMoveFromTitleBarOnly`，顶栏非按钮区域铺设 `InvisibleButton` 显式响应左键拖拽并更新 `ImGui.SetWindowPos`，确保在任何配置下都能稳定拖动窗口；右侧独立绘制关闭按钮将 `IsOpen` 置为 `false`。未登录状态同样渲染该外壳并在内容区居中显示登录提示。

- **备选方案**：保留 ImGui 原生标题栏只改 `TitleBg` 颜色。无法实现自定义 Logo、粗体标题、无默认折叠三角及与左导航条无缝齐边的 MomoTalk 轮廓，故采用自绘顶栏。

### 3. 四区布局与窄窗口状态机

以 `availWidth < 640 * scale` 判定窄窗口：
- **宽屏模式**：顶栏下方横向排列 `左导航条 (54 * scale)` + `中列表栏 (Clamp(avail * 0.38, 240 * scale, 320 * scale))` + `1px #e1e7ec 竖分隔线` + `右侧内容区 (剩余宽度)`。
- **窄屏模式**：维护单一视图状态 `narrowShowingDetail`（默认 `false`）：
  - `false`（列表态）：上方全宽渲染当前 Tab 列表，底部固定 `48 * scale` 深灰蓝双 Tab 导航条（联系人 | 会话）。
  - `true`（详情/聊天态）：隐藏底部导航条，全宽渲染好友资料卡或聊天画布，并在右侧内容顶栏最左提供 `[<]` 返回按钮切回列表态。

### 4. 双 Tab 状态、列表排布与搜索排序

将原 `MomoTalkWindow` 的“新建会话”下拉框移除，改为显式导航状态：
- `activeTab`：`Contacts`（联系人）或 `Chats`（会话），默认进入 `Chats`。
- `inspectedFriend`：联系人 Tab 当前选中的好友，仅驱动右侧资料卡渲染，不向 `Chats` 列表注入未发消息的空会话。
- `selectedPeer`：会话 Tab 当前激活的对话对象；仅当玩家在资料卡点击发起对话快捷按钮、在联系人列表双击某好友、或在会话列表选中某条目时更新，并切换到 `Chats` Tab（窄屏同步置 `narrowShowingDetail = true`）。
- 列表顶部工具条：`Contacts` 与 `Chats` 均提供白底胶囊搜索框（按当前 Tab 过滤角色名或服务器名，大小写不敏感子串匹配）；`Chats` 右侧附带 `-15deg` 斜切排序按钮在 `Recent` / `Name` 之间切换（保留 `selectedPeer` 与既定排序 tie-breaker）；`Contacts` 固定按名称与服务器排序。
- 列表行高度与三列信息：左为圆形头像，右为固定/自适应宽度的右对齐**服务器名称**（`#87929e`），中间剩余宽度裁切绘制粗体角色名（`#2a323e`）与单行副标题（`Chats` 显示最近消息正文预览并带省略截断，`Contacts` 保持简洁留白或已有会话提示）。

### 5. 对话气泡分组、时间分隔与系统条拒发

在渲染 `ConversationHistory.Messages` 时按相邻关系实时计算显示元数据（无须改存储结构）：
- **时间分隔条**：首条消息前，或 `curr.SentAt - prev.SentAt >= 5 分钟`，或本地自然日变化时，在消息上方绘制居中 `#e1e7ec` 圆角时间胶囊（当天显示 `HH:mm`，跨天/跨年显示短日期+时间）。
- **连续气泡分组**：当满足以下任一条件时判定为 `isGroupStart`：首条消息、`curr.Outgoing != prev.Outgoing`、本条前插入了时间分隔条、或上一条消息带有 `Rejection` 系统条。
  - `isGroupStart`：绘制发言者 Lodestone 头像（对方在左，本人在右）、气泡外侧指向头像的三角尖角；对方消息在气泡上方绘制角色名（`#2a323e`）。
  - 同组后续消息：跳过头像、角色名与尖角，保留头像列占位缩进并以更紧凑的垂直间距（如 `4 * scale`）叠放。
- **精确时间提示**：每个气泡记录外接矩形，鼠标悬停时通过 `ImGui.SetTooltip` 显示完整本地时间戳。
- **拒发与状态系统条**：
  - 单条消息存在 `Rejection` 时，紧贴在该条出站气泡下方绘制居中/对齐的 MomoTalk `#e1e7ec` 圆角系统消息条（文字采用警示色并自动换行），同时打断下一条消息的连续分组。
  - 无法定位的拒发警告、输入校验错误、好友不可用或写库失败提示，以同等紧凑的 `#e1e7ec` 系统条绘制在底部 `#eeeeee` 输入栏正上方。
- **向上分页锚点保持**：保留 `ConversationHistory` 的 `PrependAnchor` 机制；记录首条消息（含其前置时间分隔条起点）的 Y 坐标，加载更早页后按差值修正滚动偏移，确保阅读位置不漂移。

### 6. UI 模块组织

保持模块职责单一且不过度抽象，在 `src/FFXIVMomoTalk/Windows/` 内按职责拆分为：
- `ChatVisuals.cs`：MomoTalk 配色 Token、矢量图标、斜切按钮、圆角气泡+尖角、系统条/时间胶囊、头像与截断文本绘制原语。
- `MomoTalkWindow.cs`：窗口生命周期、外壳与宽/窄布局编排、双 Tab 状态、列表/资料卡/聊天/输入区交互与现有 `Chat/` 服务衔接（若单文件过长，可将时间线或列表/资料卡绘制拆至同命名空间下的轻量局部/辅助类）。

## Risks / Trade-offs

- **自绘顶栏在特殊窗口设置下拖动失效** → 通过顶栏 `InvisibleButton` 显式追踪左键拖拽偏移更新窗口位置，不依赖 ImGui 默认标题栏命中。
- **缺少原网页字体导致标题字重偏轻** → 通过调大顶栏字号与轻微描边/加粗绘制弥补标题辨识度，同时保留 Dalamud 对中日英韩角色名与聊天的原生渲染支持。
- **长角色名与长服务器名在窄列表行碰撞** → 先按服务器名测量并预留右侧区域，中间区域使用独立裁切矩形与单行省略号截断，确保服务器名始终可读。
- **动态时间分隔条影响历史分页滚动补偿** → 以首条消息块的实际起始 Y（包含其前置时间条）作为锚点基准，避免前插旧消息后首条消息是否带时间条变化导致跳动。
