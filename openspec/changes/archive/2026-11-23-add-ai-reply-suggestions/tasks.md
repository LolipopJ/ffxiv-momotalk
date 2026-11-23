# Tasks

## 1. 配置与默认值

- [x] 1.1 在 `src/FFXIVMomoTalk/Configuration.cs` 新增 `LlmEndpoint`、`LlmApiKey`、`LlmModel`、`LlmPrompt`、`DefaultLlmPrompt`（自然日常 FFXIV 聊天风格、自动匹配近期对话语言）与 `IsAiReplyConfigured` 计算属性，保持 `CurrentVersion = 1` 且无迁移兼容层；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。

## 2. OpenAI 兼容后台生成服务

- [x] 2.1 新增 `src/FFXIVMomoTalk/Chat/AiReplyService.cs`，实现共享 `HttpClient` 后台异步调用 OpenAI 兼容 `/chat/completions`（自动补全与兼容完整 URL）、`ConversationKey` 隔离的内存状态机（`Idle` / `Loading` / `Ready` / `Error`）、`GenerationId` 与会话防乱序校验、最近最多 20 条消息与备注组装（含空会话开场生成）以及「用户风格提示词 + 系统固定格式约束」拼装；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。
- [x] 2.2 在 `src/FFXIVMomoTalk/Chat/AiReplyService.cs` 完善 `response_format` 不支持时的自动降级重试、JSON 优先与代码块/行拆兜底解析、单行无控制字符及结合 `SendTarget.CommandPrefix` 校验 500 字节 UTF-8 上限的过滤与 1–3 条截断逻辑，并在 `src/FFXIVMomoTalk/Plugin.cs` 完成服务实例化、`ChatCapture.Captured` 出站自动清理订阅及 `DisposeAsync` 取消与释放接线；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。

## 3. MomoTalk 回复卡片与图标绘制原语

- [x] 3.1 在 `src/FFXIVMomoTalk/Windows/ChatVisuals.cs` 新增 AI 生成回复矢量图标（Sparkle）、`Reply` 卡片配色常量、矢量几何三角右上角纹理裁切绘制、加载占位卡片与 `MeasureReplyCard` / `DrawReplyCard`（左侧蓝色竖条 + `Reply` 标题 + 分割线 + 1–3 个白底微阴影圆角居中换行按钮及悬停/按压反馈）；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。

## 4. 聊天栏入口、对话流卡片交互与双标签页设置弹窗

- [x] 4.1 扩展 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs` 发送栏 `DrawSendBar` 与对话流 `DrawTimeline`：仅当 `config.IsAiReplyConfigured` 为真时在发送按钮左侧展示 AI 按钮并在不可发送或生成中时禁用；在对话流末尾（`showTyping` 脉冲气泡上方）绘制加载占位或 `Reply` 卡片与错误灰条回显，实现点击生成先清旧面板、点击选项经 `ChatSender.TrySubmit` 直接发送并清面板，以及 `SubmitDraft` 手动发信或 `SyncSession` 换角时清理对应状态；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。
- [x] 4.2 将 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs` 的 `DrawSettingsOverlay` 改造为顶部 `General` | `AI Reply` 双标签页弹窗：保留 `General` 原有 3 个开关，在 `AI Reply` 中提供 `Endpoint`、默认掩码且支持显隐切换的 `API Key`、`Model`、多行 `System Prompt` 与 `Reset to Default` 按钮并支持窄窗口最小尺寸（`340x360`）内部滚动与即时持久化；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。

## 5. 构建校验、变更日志与国际服实机验收

- [x] 5.1 更新根目录 `CHANGELOG.md` 的 `## [Unreleased]` 条目并运行 `dotnet restore .\FFXIVMomoTalk.slnx --locked-mode`、`dotnet build .\FFXIVMomoTalk.slnx -c Debug`、`dotnet build .\FFXIVMomoTalk.slnx -c Release` 与 `openspec validate --all --strict`，确认全部命令零错误通过。
- [x] 5.2 在国际服 Dalamud 实机验证以下交互闭环：未配置 LLM 时隐藏生成按钮；在 `AI Reply` 标签页填写配置、点击 `Test Connection` 验证端点连通性、确认页内按钮悬浮/按压反馈与手型光标、切换密钥显隐、调整 `Chat Context`（分钟数/最大/最小条数）与恢复默认提示词并确认即时持久化；在宽/窄窗口下生成 1–3 条 MomoTalk 风格回复选项并确认生成期间禁用按钮、再次生成前移除旧面板、点击选项直接发送至真实频道、未点选而从插件或原生聊天框发信后自动移除面板，以及网络/鉴权失败正确展示系统警告灰条并恢复按钮可用。
