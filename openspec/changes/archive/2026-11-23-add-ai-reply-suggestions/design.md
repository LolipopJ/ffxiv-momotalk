# Design

## Context

当前插件已具备完整的多频道收发、本地持久化与 MomoTalk 界面框架（动机与范围见 `proposal.md`）：
- `Configuration`（`CurrentVersion = 1`）负责读写插件 JSON 配置；按仓库 MVP 约定，新增字段直接附加默认初始值，不引入配置版本号升级或兼容迁移逻辑。
- `SocialDirectory` 在框架线程维护当前登录角色 `PlayerSession` 与活跃频道、好友、固定队快照。
- `ChatCapture` 监听游戏聊天消息并触发 `Captured` 事件；`ChatSender` 负责单行无控制字符及 500 字节 UTF-8 校验，并通过 `UIModule.ProcessChatBoxEntry` 提交真实频道命令。
- `MomoTalkWindow`、`ConversationHistory` 与 `ChatVisuals` 实现自绘无标题栏界面、对话流末尾临时气泡（`showTyping`）及叠加设置弹窗（当前最小窗口约束 `340x360`）。

运行目标为 Windows x64、.NET 10 与 Dalamud API 15。项目不引入额外第三方 LLM SDK，直接基于 .NET 10 内置 `HttpClient` 与 `System.Text.Json` 对接 OpenAI 兼容 `/chat/completions` 接口。

## Goals / Non-Goals

**Goals:**
- 以最小、模块化实现接入 OpenAI 兼容 AI 回复选项生成，不阻塞 Dalamud 框架线程，不污染 SQLite 历史库。
- 保证配置未填全时发送栏零视觉侵入，配置完整后在发送按钮左侧提供直观入口，并在对话流末尾渲染忠实还原 `u1805/momotalk` 的全英文 `Reply` 卡片。
- 确保每个会话拥有独立内存状态机、防重入生成锁、乱序回调防护及点击直发/手动发信自动清理闭环。
- 改造设置弹窗为 `General` | `AI Reply` 顶部双标签页，在最小窗口尺寸下依然可完整编辑配置与提示词。

**Non-Goals:**
- 不将 AI 生成的选项面板持久化到 SQLite，重启或换角后不恢复旧选项。
- 不支持 Anthropic 原生或 Gemini 原生等多协议下拉切换（统一通过 OpenAI 兼容接口接入）。
- 不允许未经玩家点击自动向游戏频道发送消息，也不提供「仅填入输入框而不发送」的模式切换。
- 不新增独立 HTTP 依赖库或配置迁移层。

## Decisions

### 1. 配置模型与默认提示词（`Configuration.cs`）

在 `Configuration` 中直接新增配置字段并保持 `CurrentVersion = 1`：
- `LlmEndpoint`（默认 `string.Empty`）
- `LlmApiKey`（默认 `string.Empty`）
- `LlmModel`（默认 `string.Empty`）
- `LlmPrompt`（默认 `DefaultLlmPrompt`）
- `LlmContextWindowMinutes`（默认 `10`，范围 `1..1440`）
- `LlmContextMaxMessages`（默认 `100`，范围 `1..500`）
- `LlmContextMinMessages`（默认 `5`，范围 `0..500`，生成与编辑失焦时与 `LlmContextMaxMessages` 协调上限）
- 计算属性 `IsAiReplyConfigured`（`[JsonIgnore]`）：当且仅当 `LlmEndpoint`、`LlmApiKey` 与 `LlmModel` 三者 `string.IsNullOrWhiteSpace` 均为假时返回 `true`。若用户历史配置文件缺少这些字段，Newtonsoft.Json 反序列化后保持安全默认（`LlmPrompt` 默认回落至 `DefaultLlmPrompt`，上下文窗口参数回落为 `10` / `100` / `5`，其余为空）。

`DefaultLlmPrompt` 定位为**自然日常 FFXIV 聊天风格**：指示模型扮演正在《最终幻想 XIV》中聊天的玩家，参考最近对话上下文自动匹配对话使用的语言（中文、英文、日文等），生成 1 至 3 条简短、自然、语气或角度略有差异的单行日常回复，避免客服腔、长篇大论与多余解释。

- **备选方案**：
  - 增加配置版本号并编写迁移逻辑：违背仓库 MVP 约定（不做兼容性与迁移路径），且新增可空/带默认值字段无需迁移即可正常工作，予以否决。

### 2. 后台 AI 回复服务与状态机（`Chat/AiReplyService.cs`）

新增独立服务 `AiReplyService`（在 `Plugin.cs` 创建并随 `DisposeAsync` 释放），内部持有单个共享 `HttpClient`（默认超时 30 秒）与按 `ConversationKey` 索引的内存字典：

1. **会话状态机与乱序防护**：
   - 每个会话对应 `ConversationReplyState`：`Status`（`Idle` / `Loading` / `Ready` / `Error`）、`Options`（`IReadOnlyList<string>`）、`ErrorMessage`、`GenerationId` 与独立 `CancellationTokenSource`。
   - 触发生成时：校验 `config.IsAiReplyConfigured`、目标可发送且当前未处于 `Loading`；先清空该会话旧 `Options` 与 `ErrorMessage`，递增 `GenerationId`，切为 `Loading` 并启动 `Task.Run` 后台请求。
   - 后台任务结束回写前核对 `ReferenceEquals(currentSession, capturedSession)` 与 `GenerationId` 一致性，防止玩家在请求期间切换会话、重新触发或换角导致陈旧响应覆盖新状态。
   - 切换登录角色（`SyncSession`）或插件 `DisposeAsync` 时取消全部在途请求并清空字典。

2. **Endpoint 规范化**：
   - 去除首尾空白与末尾 `/` 后：若已以 `/chat/completions` 结尾（忽略大小写）则直接使用；否则追加 `/chat/completions`。要求 Scheme 为 `http` 或 `https`，无效地址直接返回配置错误而不发起网络调用。

3. **双层提示词拼装与时间窗口上下文组装**：
   - **System Message** = 用户可编辑的 `LlmPrompt`（为空时回退 `DefaultLlmPrompt`） + 固定追加的内置格式与游戏合规约束段（要求仅输出 JSON `{"replies": ["...", "..."]}`、1 至 3 条、单行无换行、无编号前缀、精简适配游戏内聊天）。这样玩家无论如何修改风格提示词都不会破坏结构化解析。
   - **User Message**：附带当前会话类型（`Tell` / `FreeCompany` / `Linkshell` / `CrossWorldLinkshell` / `Static`）、会话名称、当前本地时间（`Current time: yyyy-MM-dd HH:mm`）、用户备注（若有），以及经后台 `MessageStore.ReadRecentAsync` 读取（读取失败时回退界面可见消息）后按 `LlmContextWindowMinutes` / `LlmContextMaxMessages` / `LlmContextMinMessages` 筛选出的消息列表：统计落入最近 `N` 分钟的消息数，再钳制到 `[min(Min, Max, count), min(Max, count)]`，每条附带本地时间戳 `[HH:mm]`（跨日为 `[yyyy-MM-dd HH:mm]`）与 `[You]`/`[角色名]` 发言者标签。若当前尚无历史消息，则明确指示生成 1 至 3 条适合当前会话对象的开场打招呼选项。
   - **端点连通性测试（`TryTestConnection`）**：在后台对规范化后的 `/chat/completions` 发起一次轻量无 `response_format` 探测请求并测量往返耗时，复用脱敏错误格式化返回 `Connected (<ms> ms).` 或明确报错；编辑任意关键字段或关闭设置弹窗时自动取消并重置测试状态。

4. **兼容请求与响应清洗**：
   - 优先发送携带 `response_format = { "type": "json_object" }` 的请求体；若服务端返回 HTTP 400/422 且响应提示不支持 `response_format`（或解析参数报错），自动剥离 `response_format` 字段降级重试一次。
   - 响应 `choices[0].message.content` 解析顺序：
     1. 剥离可能包裹的 Markdown 代码块（````json ... ````）后尝试解析 JSON 对象（读取 `replies` / `options` 字符串数组）或顶层 JSON 字符串数组。
     2. 若仍非合法 JSON，降级按行切分，剥离常见项目符号或编号前缀（如 `1.`、`1、`、`- `、`* `）并剔除明显引导语。
   - 对每条候选文本执行统一合规清洗：替换连续空白与换行符为单空格、剔除控制字符、去首尾空白、去重；结合目标频道命令前缀（如 `/tell Name@World `、`/fc `、`/p ` 等）校验 `Encoding.UTF8.GetByteCount(command) <= 500`，过滤超长项并保留前 1 至 3 条。若最终可用条数为 0，转入 `Error` 状态并提示可理解的错误信息。

5. **出站自动清除监听**：
   - `AiReplyService` 订阅 `ChatCapture.Captured`：当检测到 `captured.Outgoing` 且属于当前活跃 `PlayerSession` 时，立即移除 `captured.Conversation.Key` 的已生成选项面板（兼容从游戏原生聊天框发送的情况）。

- **备选方案**：
  - 纯文本逐行输出而非 JSON 优先：部分模型容易输出 `Here are 3 options:` 等前言或带引号解释，清洗稳定度不如 JSON 优先 + 行拆分降级，因此选择方案 A。
  - 引入官方 OpenAI NuGet 包：会引入额外传递依赖并增加插件包体，而单接口 JSON 请求用内置 `HttpClient` + `System.Text.Json` 即可简洁完成。

### 3. 视觉绘制原语与 MomoTalk 卡片样式（`Windows/ChatVisuals.cs`）

参考 `u1805/momotalk` 网页样式与用户提供的视觉稿，全部使用矢量绘制原语实现以适应任意窗口缩放：
- **配色常量**：卡片背景使用浅灰蓝（如 `#EFF5F8` / `#F2F6F9`）、细边框（`#D9E2E8`）、右上淡蓝几何三角纹理（多组透明叠层浅蓝三角形 `#D8EAF5` / `#CBE2F0`，通过 `PushClipRect` 裁切在圆角卡片右上角内部）、左侧亮蓝竖条（`#3B82F6` / `#4A8AC6`）、分隔线（`#CBD5E1`）、纯白圆角选项按钮（`#FFFFFF`，带微阴影与悬停/按压态切换）。
- **AI 生成按钮图标**：在 `ChatVisuals` 新增 `DrawAiSparkleIcon`（四芒星主星 + 右上角点缀小星），复用输入栏与发送按钮的配色体系区分可用、悬停与禁用状态。
- **卡片测量与绘制**：新增 `MeasureReplyCard` 与 `DrawReplyCard`（及加载态骨架/脉冲提示绘制），支持窗口宽度动态缩放和长文本在白底按钮内的自动换行与居中排版。

- **备选方案**：
  - 嵌入一张 PNG 背景图作为右上角几何纹理：会随窗口宽高比例拉伸或失真，需要维护额外嵌入资源；用矢量 `AddTriangleFilled` 配合裁切矩形绘制更锐利且零额外资源体积。

### 4. 发送栏、对话流与双标签页设置弹窗集成（`Windows/MomoTalkWindow.cs`）

1. **发送栏（`DrawSendBar`）**：
   - 当 `config.IsAiReplyConfigured` 为 `true` 时，在发送按钮左侧预留 `aiSize`（与发送按钮同高）的图标按钮位，并对应缩减输入胶囊 `pillWidth`；未配置时完全不占空间。
   - 当 `sendTarget is null` 或 `aiReply.IsLoading(currentConv.Key)` 时禁用 AI 按钮；可用时点击触发 `aiReply.GenerateAsync(session, currentConv, sendTarget, snapshot, view.Messages, notes.GetValueOrDefault(currentConv.Key))`。
   - 为生成按钮提供直观 Tooltip（如生成中提示 `"Generating replies..."`，可用时提示 `"Generate AI replies"`）。

2. **对话流底部交互（`DrawConversationPane` & `DrawTimeline`）**：
   - 读取 `aiReply.GetState(currentConv.Key)`：若为 `Loading` 或 `Ready`（含 1–3 条选项），在消息列表之后、`showTyping` 脉冲气泡之前绘制 `Reply` 卡片。
   - 若为 `Error`，将错误文本交由 `DrawConversationPane` 既有的底部 `statusBanner`（系统警告灰条）展示；在重新触发生成、切换清除或手动发送时一并清理。
   - 滚动跟随：当该会话的 `Reply` 状态新进入 `Loading` 或从 `Loading` 变为 `Ready` 时，若用户在底部则触发 `ImGui.SetScrollHereY(1f)`，确保卡片自然展开可见。
   - **点击选项直发**：点击选项索引对应文案时，即时取当前 `SendTarget` 调用 `sender.TrySubmit(session!, target, optionText, out var error)`；提交成功则立即调用 `aiReply.Clear(currentConv.Key)` 移除面板，失败则写入 `draft.Warning` 展示报错。
   - **手动发送移除**：在 `SubmitDraft` 成功提交后同样调用 `aiReply.Clear(target.Key)`，与 `ChatCapture.Captured` 形成双保险（覆盖游戏原生聊天框发送与插件内提交）。

3. **双标签页设置弹窗（`DrawSettingsOverlay`）**：
   - 弹窗顶部标题栏下方新增 `General` | `AI Reply` 分段标签栏。
   - 弹窗宽度在 `General` 与 `AI Reply` 下可统一适配为响应式卡片宽度（例如 `MathF.Min(420f * scale, MathF.Max(280f * scale, winSize.X - 32f * scale))`），高度按最大可用窗口高度 `winSize.Y - 28f * scale` 限位，并在 `AI Reply` 内容区使用 `ImRaii.Child` 内部滚动，确保在 `340x360` 最小尺寸窗口内所有控件均能触达。
   - `General` 页保持原有 3 个开关不变。
   - `AI Reply` 页依次提供：
     - `Endpoint` 单行文本输入（支持占位示例 `https://api.openai.com/v1`）
     - `API Key` 密码掩码输入框 + 右侧 `Show` / `Hide` 显隐切换按钮
     - `Model` 单行文本输入（如 `gpt-4o-mini`）
     - `Test Connection` 主按钮及其下方的成功/错误结果横幅
     - `Chat Context` 三列数字配置（`Minutes` 默认 `10`、`Max` 默认 `100`、`Min` 默认 `5`）
     - `System Prompt` 多行文本输入（`InputTextMultiline`，支持合理最大长度如 `4096`）
     - `Reset to Default` 按钮（点击将 `config.LlmPrompt` 重置为 `Configuration.DefaultLlmPrompt` 并即时保存）
   - 优化 `ChatVisuals.DrawModalButton` 与设置页标签/关闭按钮配色与光标反馈：将弹窗次级按钮默认色、悬停色、按压色与边框色拆分为独立阶梯（并支持禁用态与手型指针），确保 `Show`/`Hide`、`Test Connection`、`Reset to Default` 与标签页均有清晰可见的鼠标悬浮与点击反馈。
   - 任意字段修改均触发 `saveConfig()` 即时持久化，且在 `config.PreserveExistingFile` 为真时保持既有错误横幅提示并尊重只读保护。

- **备选方案**：
  - 设置弹窗改为单页可滚动平铺全部选项：在最小窗口高度 `360px` 下会导致每次改写提示词或切换日常开关都要长距离滚动，视觉信息密度过高；顶部双标签页既保持 `General` 原有极简质感，又为 `AI Reply` 留足多行提示词编辑空间。

## Risks / Trade-offs

- **OpenAI 兼容服务商实现差异**（如部分中转站或本地模型不接受 `response_format`，或推理模型在正文中夹带多余说明或耗时较长）→ 设 30 秒请求超时，不支持 `response_format` 时自动降级重发一次，并采用 JSON 优先 + 行拆兜底与 500 字节过滤保障稳定性。
- **点击选项直接发送到游戏真实频道的误发风险** → 默认提示词明确限定短句、日常礼貌与匹配语境，UI 使用清晰的按钮排版（而非自动发送），并在发送前再次校验当前频道可用性与字节上限。
- **API Key 明文保存在插件本地 JSON 配置文件** → 与大多数游戏插件本地配置一致，保存在当前用户插件配置目录；UI 层默认以掩码展示密钥避免游戏直播或截屏时泄露。
- **ImGui 字体与多语言输入缓冲上限** → `InputTextMultiline` 预留足够 UTF-8 缓冲长度（4096 字节）并在 `AI Reply` 标签页采用 `ImRaii.Child` 滚动容器，避免在 `340x360` 最小窗口或高 DPI 缩放下溢出裁切。
