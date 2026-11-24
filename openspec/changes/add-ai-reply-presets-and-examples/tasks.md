# Tasks

## 1. 预设目录与配置模型

- [ ] 1.1 新增 `src/FFXIVMomoTalk/Chat/AiReplyPresets.cs`，定义不可变预设与示例数据结构，内置 `default`、`sensei`、`tavern-reliable`、`tavern-cunning`、`tavern-wise`、`tavern-aloof`、`tavern-cheerful` 七套纯英文提示词与每套 2–3 组纯英文结构化对话示例（每组含情境文本与 3 条不同角度的单行回复），并运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。
- [ ] 1.2 更新 `src/FFXIVMomoTalk/Configuration.cs` 将 `CurrentVersion` 升级为 `2`，定义 `AiReplyExample`、`LlmPromptPreset`（默认 `"default"`）、`LlmCustomPrompt` 与 `LlmCustomExamples` 替代旧版 `LlmPrompt`，实现 `EffectiveLlmPrompt`、`EffectiveLlmExamples`、`IsCustomPreset` 与深拷贝方法 `CopyPresetToCustom`；确认预设切换不覆盖自定义内容且 Debug 编译通过。

## 2. 配置 v1 → v2 迁移与回退保护

- [ ] 2.1 新增 `src/FFXIVMomoTalk/Migrations/Configuration/V1ToV2.cs` 并在 `Configuration.Load` 中接入迁移流程：升级 `v1` 前先创建不覆盖既有备份的 `.v1.bak` 副本并记录回退步骤；保留全部既有设置与 `$type` 元数据，将未修改默认文本的 `LlmPrompt` 映射至 `"default"`、改动过的 `LlmPrompt` 映射至 `"custom"` 并迁入 `LlmCustomPrompt`，初始化 `LlmCustomExamples` 并移除旧 `LlmPrompt`；经反序列化校验后以临时文件原子替换写入；遇未知/高版本或异常时保留原文件并记录含数据域、源版本 `1`、目标版本 `2` 与失败步骤的错误日志。
- [ ] 2.2 新增最小配置迁移验证脚本（`tests/AiReplyConfiguration.cs`）并在 Debug 编译后执行 `dotnet run --file tests/AiReplyConfiguration.cs`，验证全新 `v2` 初始化、`v1` 默认配置升至 `default`、`v1` 自定义提示词升至 `custom` 且保留原值与生成 `.v1.bak`、未知/高版本原文件保持不变、失败后可重试及通过备份还原回退的全部场景通过。

## 3. AI 回复服务提示词拼装

- [ ] 3.1 更新 `src/FFXIVMomoTalk/Chat/AiReplyService.cs`：在 `TryGenerate` 的 `lock (gate)` 内对 `EffectiveLlmPrompt` 与有效结构化示例（情境非空白且含 1–3 条非空白单行回复）进行线程安全快照；扩展 `BuildSystemPrompt` 在风格提示词与 `Mandatory output format constraints` 之间内嵌 `{"replies":[...]}` 目标 JSON 示例并显式声明仅作语气与多样化参考、实际回复 MUST 匹配近期聊天语言（无有效示例时不输出空示例段落），保持现有 `[system, user]` 双消息结构。
- [ ] 3.2 在 `tests/AiReplyConfiguration.cs` 中补充 `AiReplyService.BuildSystemPrompt` 最小验证用例并运行验证：确认含有效示例时输出合法转义的 JSON 示例块与语言匹配指令、空白或缺少有效回复的草稿示例被跳过、无示例时不产生空段落，且多语言自定义内容及特殊字符序列化正确。

## 4. 设置弹窗 UI 与 6 语言本地化

- [ ] 4.1 同步更新 `src/FFXIVMomoTalk/Localization/Strings.*.resx`（`en`、`ja`、`de`、`fr`、`zh-Hans`、`zh-Hant` 六份资源文件）：移除不再使用的 `ResetToDefault`，新增 8 个风格下拉键（`AiPresetCustom`、`AiPresetDefault`、`AiPresetSensei`、`AiPresetTavernReliable`、`AiPresetTavernCunning`、`AiPresetTavernWise`、`AiPresetTavernAloof`、`AiPresetTavernCheerful`）及复制与示例区界面键（`CopyToCustom`、`ConversationExamples`、`ExampleItemTitle`、`ExampleContext`、`ExampleContextHint`、`ExampleReplies`、`ExampleReplyHint`、`AddExample`、`AddReply`、`Remove`、`NoExamplesYet`）；编译 Debug 后运行 `dotnet run --file tests/Localization.cs` 确认六语言资源键集与占位符校验零错误通过。
- [ ] 4.2 更新 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs` 与 `src/FFXIVMomoTalk/Windows/ChatVisuals.cs` 的 `AI Reply` 设置页：移除 `Reset to Default` 按钮，在系统提示词上方添加本地化风格下拉框；选中预设时右侧绘制动态测宽的 `Copy to Custom` 按钮并以只读样式展示预设提示词与只读示例预览卡片；选中 `Custom` 时隐藏复制按钮，支持任意文本编辑提示词（使用 `##ai-prompt-{presetId}` 隔离 ImGui 缓冲区）与 `Conversation Examples` 结构化编辑卡片（最多 10 组、每组 `Context` + 1–3 条 `Replies` 增删并延迟提交变更），且将新增控件与示例卡片实测高度准确计入 `naturalBodyHeight` 保证 `340x360` 最小窗口完整可滚动；运行 `dotnet build .\FFXIVMomoTalk.slnx -c Debug` 确认编译通过。

## 5. 全量集成验证、变更日志与实机验收

- [ ] 5.1 按 Keep a Changelog 1.1.0 更新根目录 `CHANGELOG.md` 的 `## [Unreleased]` 章节，随后运行 `dotnet restore .\FFXIVMomoTalk.slnx --locked-mode`、`dotnet build .\FFXIVMomoTalk.slnx -c Debug`、`dotnet build .\FFXIVMomoTalk.slnx -c Release`、`dotnet run --file tests/Localization.cs`、`dotnet run --file tests/AiReplyConfiguration.cs` 与 `openspec validate --all --strict`，确认全部命令零错误通过。
- [ ] 5.2 在《最终幻想 XIV》国际服 Dalamud 实机验收完整链路：验证老版 `v1` 配置自动备份 `.v1.bak` 并升级保留原设置；验证切换 6 种界面语言时预设下拉选项与示例区界面文案正确本地化而预设提示词与内置示例保持纯英文；验证预设切换、`Copy to Custom`、在 `Custom` 增删多组示例与回复并重启后持久保留；验证最小窗口尺寸（`340x360`）下设置页滚动顺畅且所有控件可点击；并在中文、日文与英文聊天会话中生成回复确认风格体现与语言匹配准确。
