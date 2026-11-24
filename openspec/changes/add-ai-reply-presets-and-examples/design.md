# Design

## Context

当前 AI 回复功能已在 `v1.0.x` 发布（背景与动机见 `proposal.md`）：
- `src/FFXIVMomoTalk/Configuration.cs`（`CurrentVersion = 1`）当前仅以单个 `LlmPrompt` 字符串保存全局提示词（默认 `DefaultLlmPrompt`）。
- `src/FFXIVMomoTalk/Chat/AiReplyService.cs` 通过 `[system, user]` 双消息结构调用 OpenAI 兼容 `/chat/completions` 接口，并由 `BuildSystemPrompt` 拼装风格提示词与 JSON 输出约束。
- `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs` 的 `DrawSettingsOverlay` 在 `AI Reply` 标签页展示 `System Prompt` 与 `Reset to Default` 按钮（卡片最宽 `420px`，需支持 `340x360` 最小窗口滚动）。
- 工作区正在引入 6 语言本地化基础设施（`Loc.cs`、`Localization/Strings.*.resx` 与 `tests/Localization.cs`，覆盖 `en`、`ja`、`de`、`fr`、`zh-Hans`、`zh-Hant`）。

按仓库 `AGENTS.md` 规范：影响已发布 `Configuration` 结构的变更需同步升级版本号并在 `src/FFXIVMomoTalk/Migrations/Configuration/` 实现可备份、事务化/原子化且可从失败重试的迁移路径；且运行环境为 Windows x64、.NET 10 与 Dalamud API 15，不新增第三方包依赖。

## Goals / Non-Goals

**Goals:**
- 新增内置英文风格预设目录（`default`、`sensei` 及五种酒馆 Roleplay 性格），每套预设包含纯英文系统提示词与 2–3 组纯英文结构化对话示例（每组 1 句情境发言 + 3 条不同角度单行示范回复）。
- 支持 `custom` 独立模式（可自由使用任意语言文本编辑提示词与最多 10 组结构化示例）与一键 `Copy to Custom`，在预设间切换不会覆盖 `custom` 数据。
- 保持 `[system, user]` 双消息请求结构，将有效结构化示例序列化为 JSON 内嵌至 `system` 提示词中，同时确保中/英/日等实时聊天仍匹配对话本身语言。
- 实现 `Configuration` `v1 → v2` 安全升级路径：先做本地恢复备份，自动区分未改动默认提示词与自定义提示词，原子落盘并完整保护未知版本或异常配置。
- 将所有新增 UI 标签纳入 6 语言本地化资源并在 `tests/Localization.cs` 校验通过。

**Non-Goals:**
- 不采用多轮伪造 `user`/`assistant` 对话历史注入 few-shot（方案 B）。
- 不为对话示例拆分单独二级弹窗子页（方案 C），所有配置保持在 `AI Reply` 可滚动标签页内所见即所得。
- 不将预设、自定义提示词或示例存入 SQLite 数据库，继续归属 `Configuration` JSON 管理。
- 不支持原地修改内置预设；修改预设内容需先复制到 `Custom`。
- 不引入额外 NuGet 依赖库。

## Decisions

### 1. 内置英文风格预设目录（`src/FFXIVMomoTalk/Chat/AiReplyPresets.cs`）

在 `Chat/AiReplyPresets.cs` 中以不可变类型定义预设目录与预设清单，所有预设正文（提示词与自带示例）严格使用英文撰写，同时在界面层通过 `DisplayNameKey` 映射到 6 语言 `.resx` 展示名：

| 下拉顺序 | Preset ID (`LlmPromptPreset`) | 本地化键 | 风格定位与示例特点 |
|---|---|---|---|
| 1 | `custom` | `AiPresetCustom` | 用户自定义提示词与对话示例，支持任意语言 |
| 2 | `default` | `AiPresetDefault` | 保留原有 FFXIV 日常闲聊自然风格（`DefaultPrompt`）与日常冒险者示例 |
| 3 | `sensei` | `AiPresetSensei` | 《蔚蓝档案》Sensei 风格：温和可靠、略带幽默、善于倾听并鼓励同伴 |
| 4 | `tavern-reliable` | `AiPresetTavernReliable` | 酒馆 RP - 可靠：沉稳笃定、重诺扛事的冒险者老友口吻 |
| 5 | `tavern-cunning` | `AiPresetTavernCunning` | 酒馆 RP - 狡诈：机敏圆滑、带点调侃算计却不失分寸的酒馆常客 |
| 6 | `tavern-wise` | `AiPresetTavernWise` | 酒馆 RP - 睿智：克制沉静、善用简短比喻与冒险见闻点拨他人 |
| 7 | `tavern-aloof` | `AiPresetTavernAloof` | 酒馆 RP - 高冷：话少冷静、外冷内热、点到即止的简洁表达 |
| 8 | `tavern-cheerful` | `AiPresetTavernCheerful` | 酒馆 RP - 活泼：元气明快、热情接梗、擅长活跃气氛的酒馆伙伴 |

每套预设配备 2–3 组结构化英文示例，每组固定提供 3 条风格一致但切入角度不同的单行示范回复（例如直接回应、轻松调侃、延伸追问），且每套预设提示词均保留自动匹配近期对话语言（如中文、英文、日文）的指令。

- **备选方案**：将预设内容外置为嵌入 JSON 或放入 `.resx` → 否决：预设正文固定为英文且需在无 UI 上下文时由 `Configuration` 与 `AiReplyService` 直接读取，C# 静态定义零 I/O 开销且类型安全，仅把界面展示名接入 `.resx` 最简洁。

### 2. 配置模型演进与 `v1 → v2` 安全迁移（`Configuration.cs` & `Migrations/Configuration/V1ToV2.cs`）

1. **配置数据模型（`Configuration.cs`）**：
   - 将 `CurrentVersion` 从 `1` 提升至 `2`。
   - 定义可序列化的 `AiReplyExample`（属性：`Context` 为 `string`、`Replies` 为 `List<string>`）与常量上限（`MaxCustomExamples = 10`、`MaxExampleReplies = 3`）。
   - 用以下字段替换原 `LlmPrompt`：
     - `LlmPromptPreset`（`string`，默认 `"default"`；未知值时归一化回落为 `"default"`）
     - `LlmCustomPrompt`（`string`，默认 `AiReplyPresets.DefaultPrompt`）
     - `LlmCustomExamples`（`List<AiReplyExample>`，默认空列表）
   - 提供 `[JsonIgnore]` 辅助属性与方法：`IsCustomPreset`、`EffectiveLlmPrompt`（预设返回内置提示词；`custom` 返回 `LlmCustomPrompt`，全空白时兜底 `DefaultPrompt`）、`EffectiveLlmExamples`（预设返回内置示例；`custom` 返回 `LlmCustomExamples`），以及 `CopyPresetToCustom(string presetId)`（深拷贝选中预设的提示词与示例覆盖写入 `LlmCustomPrompt` / `LlmCustomExamples` 并切换为 `"custom"`）。

2. **`v1 → v2` 迁移管线（`Migrations/Configuration/V1ToV2.cs`）**：
   - 由 `Configuration.Load(pluginInterface, log)` 在正常读取前执行：
     - **文件不存在**：直接返回新建 `v2` 默认配置，不创建冗余文件。
     - **`Version == 2`**：直接由 Dalamud `GetPluginConfig()` 读取（读取或反序列化异常则标记 `PreserveExistingFile = true` 并记录错误）。
     - **`Version == 1`**：调用 `V1ToV2.Migrate(pluginInterface.ConfigFile.FullName, log)`：
       1. **创建可恢复备份**：先将原配置文件以不覆盖既有备份的方式复制为 `<ConfigFile>.v1.bak`（若已存在则依次递增 `.v1.bak.1` 等），并在日志中输出备份路径与手动回退步骤（删除新配置并将备份改回原文件名）。
       2. **JSON 转换**：基于 `Newtonsoft.Json.Linq.JObject` 原样保留所有已有配置键（包含 `UiLanguage`、三个展示开关、悬浮位置、`LlmEndpoint`、`LlmApiKey`、`LlmModel`、上下文窗口三个数值及 Newtonsoft `$type` 元数据）；读取旧 `LlmPrompt`，统一换行符并 `Trim` 后与 `v1` 的 `DefaultLlmPrompt` 对比——若为空或与旧默认值一致，则写入 `LlmPromptPreset = "default"` 与 `LlmCustomPrompt = AiReplyPresets.DefaultPrompt`；若玩家此前自定义过内容，则写入 `LlmPromptPreset = "custom"` 并完整保留原 `LlmPrompt` 到 `LlmCustomPrompt`；初始化 `LlmCustomExamples = []`，移除 `LlmPrompt`，将 `Version` 置为 `2`。
       3. **反序列化校验**：在写入磁盘前先将转换后的 JSON 反序列化为 `Configuration` 并校验 `Version == 2` 与字段有效性。
       4. **原子保存**：写入同目录临时文件并刷新后原子替换目标配置文件（最后清理残留临时文件）。
       5. **失败处理**：任一步骤失败均记录明确错误日志（包含数据域 `Configuration`、源版本 `1`、目标版本 `2`、失败步骤与备份文件路径），保持磁盘文件完整并置 `PreserveExistingFile = true`，下次启动可重新尝试迁移。
     - **未知或高于 `CurrentVersion` 的版本**：记录清晰版本错误日志，不修改或覆写原文件，返回 `new Configuration { PreserveExistingFile = true }`。

- **备选方案**：维持 `Version = 1` 不删除 `LlmPrompt` 而直接新增字段 → 否决：违背项目不保留过时字段包袱的原则，且 `AGENTS.md` 明确要求对已发布配置结构变更提供 `Migrations/Configuration/V1ToV2.cs` 逐步迁移、备份与校验逻辑。

### 3. System Prompt 内嵌结构化示例（`Chat/AiReplyService.cs`）

- **请求结构**：保持现有 `[system, user]` 双消息调用，不额外拼接人造历史轮次。
- **线程安全快照**：在 `AiReplyService.TryGenerate` 的 `lock (gate)` 临界区内读取并深拷贝 `config.EffectiveLlmPrompt` 与有效示例快照存入 `GenerationRequest`，避免后台请求执行期间 UI 编辑引发竞态。
- **示例校验与过滤**：仅纳入同时满足 `Context` 经单行清洗非空白、且 `Replies` 中至少有 1 条（最多 3 条）经单行清洗非空白的示例；空白草稿示例会被自动忽略。
- **`BuildSystemPrompt` 拼装格式**：
  1. 有效风格提示词（为空时兜底 `AiReplyPresets.DefaultPrompt`）。
  2. 若存在至少 1 条有效示例，插入结构化示例段落，通过 `System.Text.Json.JsonSerializer.Serialize` 序列化 `{"replies":[...]}` 确保特殊字符与引号转义安全，并明确提示模型示例仅用于传达角色口吻与选项多样性、实际回复必须使用近期对话语言：
     ```text
     Dialogue examples (tone and option-variety reference only; always reply in the language used by the recent conversation messages):
     Example 1:
     Incoming: "..."
     Expected JSON: {"replies":["...","...","..."]}
     ```
  3. 固定追加既有的 `Mandatory output format constraints (always follow): ...` 约束段（继续按当前频道命令前缀计算最大 UTF-8 字节预算）。

- **备选方案（方案 B）**：把每组示例拆成独立 `user` / `assistant` 对话轮次 → 否决：在中文或日文 FFXIV 聊天场景下，多轮全英文 `assistant` 历史更容易把中小模型输出语言拽偏回英文，增加 token 消耗且对部分中转站兼容性较弱。

### 4. `AI Reply` 设置页交互与 6 语言本地化（`MomoTalkWindow.cs`, `ChatVisuals.cs`, `Localization/Strings.*.resx`）

- **顶部风格选择行**（位于 `SystemPrompt` 标题下方、提示词文本框上方）：
  - 下拉框按顺序列出 `Custom` 与 7 套内置预设（显示名取自 `Loc.Text`）。
  - 当选中内置预设时，下拉框宽度缩减为 `innerW - copyBtnW - gap`，右侧绘制次级按钮 `Copy to Custom`（按本地化文案宽度动态测量并安全限制范围）；点击后调用 `config.CopyPresetToCustom(...)`、`saveConfig()` 并即时切入 `Custom` 编辑态。
  - 当选中 `Custom` 时，隐藏 `Copy to Custom` 按钮，下拉框占满整行宽度。
- **提示词框（移除 `Reset to Default`）**：
  - 控件 ID 采用预设作用域隔离（如 `##ai-prompt-{presetId}`），确保切换预设或点击 `Copy to Custom` 时 ImGui 不会残留上一个状态的 `InputTextMultiline` 内部编辑缓冲区。
  - 预设态下使用 `ImGuiInputTextFlags.ReadOnly` 与浅灰底色（`ChatVisuals.ListBg`）区分只读状态；`Custom` 态下使用白底可编辑并在改动时即时 `saveConfig()`。
- **结构化对话示例区（页内紧凑卡片）**：
  - 区块标题 `Conversation Examples` 左侧对齐；处于 `Custom` 模式且未达 `MaxCustomExamples (10)` 上限时右侧显示 `+ Add` 按钮。
  - **预设只读预览卡**：采用圆角浅灰卡片（`ListBg` + `InputBorder`），展示 `Example N`、自动换行的 `Context` 文本及带编号的 1–3 条候选回复文本，渲染前基于文本宽度精确测量换行高度。
  - **`Custom` 可编辑卡片**：每张卡片包含顶部 `Example N` 与删除按钮（通过延迟操作列表安全删除）、单行 `Context` 输入框（复用 `DrawSettingTextField`）、`Replies` 列表（1–3 条单行输入框，条目数 > 1 时行尾显示删除按钮）及不足 3 条时显示的 `+ Add Reply` 按钮。
  - **窄窗口（`340x360`）滚动完整性**：在 `DrawSettingsOverlay` 计算 `naturalBodyHeight` 时，把风格下拉行、提示词框、示例区块标题及所有示例卡的实际测量高度全部计入总高度，使外层限制高度与内部 `##settings-body-aireply` 滚动条严丝合缝。
- **6 语言本地化键变更**：
  - 所有 6 个 `.resx` 文件（`Strings.resx`、`Strings.ja.resx`、`Strings.de.resx`、`Strings.fr.resx`、`Strings.zh-Hans.resx`、`Strings.zh-Hant.resx`）同步移除 `ResetToDefault`，并新增：
    - 下拉选项：`AiPresetCustom`、`AiPresetDefault`、`AiPresetSensei`、`AiPresetTavernReliable`、`AiPresetTavernCunning`、`AiPresetTavernWise`、`AiPresetTavernAloof`、`AiPresetTavernCheerful`
    - 复制与示例 UI：`CopyToCustom`、`ConversationExamples`、`ExampleItemTitle`、`ExampleContext`、`ExampleContextHint`、`ExampleReplies`、`ExampleReplyHint`、`AddExample`、`AddReply`、`Remove`、`NoExamplesYet`

- **备选方案（方案 C）**：把对话示例放到独立的二级抽屉子页 → 否决：切换预设时无法同屏浏览预设提示词与示例效果，且多一层视图状态管理。

## Risks / Trade-offs

- **英文示例在非英语对话中引起语言偏移** → 仅将示例以标注参考身份放入 `system` 段、绝不构造英文 `assistant` 轮次，并在示例标题与输出指令两处强调必须匹配近期消息语言；实机需在中/日/英环境下分别验证。
- **多语言按钮文案长度差异导致设置弹窗小窗拥挤** → 对 `Copy to Custom`、`+ Add` 等行内按钮按 `ChatVisuals.CalcTextSize` 动态测宽并留足内边距，其余单行/多行输入随 `innerW` 自适应，并在 `340x360` 最小窗口实测德语/法语等较长文案无裁切重叠。
- **ImGui 控件 ID 冲突或增删示例卡片时焦点/缓冲串位** → 为不同预设的提示词框和按索引排列的示例/回复输入框绑定明确作用域 ID（提示词含 `presetId`，示例与回复在增删当帧后统一应用变更），避免同帧列表结构变化引起的状态错乱。
- **`v1` 配置升级失败风险** → 先备份 `.v1.bak`、再内存转换并反序列化验证、最后临时文件原子替换；任一环节失败保留原文件并设 `PreserveExistingFile = true`，防止配置被写坏或覆写丢失。
- **6 语言 `.resx` 漏键或占位符不一致** → 通过已有 `tests/Localization.cs` 在每次修改后自动校验 6 份资源键集、非空值与格式化参数一致性。

## Migration Plan

1. 玩家从已发布 `v1.0.x`（`Configuration.Version == 1`）升级启动新版本插件时，`Configuration.Load` 自动检测版本号并触发 `Migrations/Configuration/V1ToV2.cs`：
   - 若配置文件不存在，直接初始化 `v2` 默认配置（选中 `default` 预设，空自定义示例）。
   - 若为 `v1`，先在同一目录创建不覆盖旧备份的 `.v1.bak` 文件并记录日志；保留原有所有连接、显示与上下文参数，将旧版 `LlmPrompt` 按是否改动映射至 `default` 或 `custom`（保留原文），原子写入 `v2` 配置。
   - 若版本未知或高于 `2`，保留磁盘原文件不变并记录错误。
2. **手动回退方法**：若玩家需降级回 `v1.0.x` 或从迁移异常恢复，只需关闭插件/游戏，删除新配置文件并将同目录下的 `.v1.bak` 备份重命名还原为原配置文件名即可恢复原有全部 `v1` 设置。
3. 迁移实现完成后通过针对性验证脚本覆盖：全新初始化、`v1` 默认提示词升级至 `default`、`v1` 自定义提示词升级至 `custom` + 保留原值 + 生成备份文件、未知/高版本保留原文件、写入失败后重试以及回退还原验证。
