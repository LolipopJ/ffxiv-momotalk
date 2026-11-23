# Proposal

## Why

参考 `https://u1805.github.io/momotalk/chat` 中《蔚蓝档案》MomoTalk 的标志性回复选项卡片，玩家在《最终幻想 XIV》游戏内私聊与群聊中也希望拥有直观快捷的 AI 辅助回复体验。将这一交互接入插件现有聊天窗口，既能还原点击选项直接回复的沉浸感，又能在组队、副本或日常社交中一键生成符合当前语境的自然短句。

## What Changes

- 新增基于 OpenAI 兼容 `/chat/completions` 接口的后台 AI 回复生成服务 `Chat/AiReplyService.cs`（复用共享 `HttpClient`，在后台异步执行，不阻塞 Dalamud 框架线程，不新增第三方 LLM SDK 依赖），并从 `MessageStore` 异步读取按可配置时间窗口与上下限（默认最近 10 分钟、最多 100 条、最少 5 条）过滤的带时间戳聊天上下文及支持端点连通性测试。
- 扩展 `Configuration.cs`，新增 `LlmEndpoint`、`LlmApiKey`、`LlmModel`、`LlmPrompt`、`LlmContextWindowMinutes`、`LlmContextMaxMessages`、`LlmContextMinMessages` 配置字段与 `IsAiReplyConfigured` 判定（保留 `CurrentVersion = 1`，遵循项目 MVP 约定不引入配置迁移或兼容层），并提供默认系统提示词（自然日常 FFXIV 聊天风格，自动匹配最近对话语言）与恢复默认能力。
- 将设置弹窗重构为顶部双标签页（`General` | `AI Reply`）：`General` 保留原有三个开关，`AI Reply` 提供 `Endpoint`、默认掩码且支持显隐切换的 `API Key`、`Model`、`Test Connection` 端点连通性测试及结果横幅、`Chat Context`（时间窗口分钟数 / 最大条数 / 最小条数）、多行 `System Prompt` 编辑框与 `Reset to Default` 按钮，所有按钮具备明确鼠标悬浮、按压与手型指针反馈，并在窄窗口最小尺寸下保持完整可用。
- 在聊天界面发送按钮左侧新增智能生成回复矢量图标按钮：当且仅当设置中 `Endpoint`、`API Key` 与 `Model` 三项均非空时显示该按钮；当前会话不可发送或正处于生成中时禁用按钮。
- 在对话流底部渲染全英文 UI 的 MomoTalk 风格回复选项卡片（标题为 `Reply`，浅灰蓝圆角底与细边框、右上角矢量绘制淡蓝几何三角纹理、左侧蓝色竖条与横分割线、1–3 个白底微阴影圆角居中按钮）；选项面板与在途请求仅存内存并按会话隔离，不写入 SQLite 历史库。
- 实现完整的生成与清理生命周期：点击生成时先移除当前会话已有选项面板并进入加载占位态，直至选项生成完毕或发生异常后才允许再次点击；点击任一选项立即通过 `ChatSender` 直接发送到当前频道并移除面板；用户在未点击选项的情况下正常发送新消息（含游戏原生聊天框发送后的出站回显）后自动移除已有选项面板。

## Capabilities

### New Capabilities

- `ai-reply`: 基于 OpenAI 兼容 `/chat/completions` 的 LLM 配置与默认提示词管理、聊天上下文组装、后台异步生成 1–3 条单行合规回复、按会话隔离的内存生命周期、防重生成控制及点击直发或发信自动清理机制。

### Modified Capabilities

- `momotalk-ui`: 在聊天栏发送按钮左侧新增条件显示的 AI 回复生成入口、在对话流底部呈现 MomoTalk 风格 `Reply` 选项卡片与加载占位态、并将设置弹窗扩展为 `General` / `AI Reply` 顶部双标签页布局。

## Impact

- 影响 `src/FFXIVMomoTalk/Configuration.cs`：新增 LLM 端点、密钥、模型、提示词配置字段及默认提示词常量与配置完整性判断。
- 影响 `src/FFXIVMomoTalk/Plugin.cs`：创建并管理 `AiReplyService` 实例及其与 `ChatCapture`、`MomoTalkWindow` 的生命周期接线和异步释放。
- 新增 `src/FFXIVMomoTalk/Chat/AiReplyService.cs`：封装 OpenAI 兼容请求构建、上下文提取、输出清洗与字节校验、按会话状态管理及出站消息自动清理逻辑。
- 影响 `src/FFXIVMomoTalk/Windows/ChatVisuals.cs`：新增 AI 生成图标（Sparkle）、MomoTalk 回复选项卡片配色、矢量几何三角纹理及卡片测量与绘制原语。
- 影响 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs`：扩展发送栏按钮布局、对话流底部选项/加载卡片渲染与直发交互、手动发送清除面板逻辑，以及顶部双标签页设置弹窗。
- 无新增 NuGet 包依赖，直接使用 .NET 10 内置 `HttpClient` 与 `System.Text.Json`。
