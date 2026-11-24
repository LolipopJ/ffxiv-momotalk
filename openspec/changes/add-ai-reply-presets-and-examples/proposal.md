# Proposal

## Why

当前 AI 回复仅提供单一全局系统提示词输入框与 `Reset to Default` 按钮，既无法在不同角色扮演语境间快速切换，也缺乏示范回复来稳定控制语气与多样化角度。引入多种预设性格风格（涵盖 FFXIV 日常闲聊、蔚蓝档案 Sensei 与五种奇幻酒馆 Roleplay 性格）和结构化「对方说 → 1–3 条候选回复」对话示例，并支持自定义编辑与一键复制预设到自定义，能让玩家在不同社交场合稳定获得更贴合语气的回复建议。

## What Changes

- 新增内置英文风格预设目录 `src/FFXIVMomoTalk/Chat/AiReplyPresets.cs`，包含 7 个预设（保留现有 FFXIV 日常聊天风格的 `Default (FFXIV Casual)`、新增 `Blue Archive - Sensei` 以及 5 种奇幻酒馆 Roleplay 性格：`Tavern RP - Reliable`、`Tavern RP - Cunning`、`Tavern RP - Wise`、`Tavern RP - Aloof`、`Tavern RP - Cheerful`）与 `Custom` 选项；所有预设提示词与自带对话示例均采用纯英文编写，每个预设提供 2–3 组结构化示例（每组含情境发言与 3 条不同角度的单行示范回复）。
- 在 `AI Reply` 设置页移除 `Reset to Default` 按钮，在系统提示词编辑框上方新增风格下拉选择框（依次为 `Custom` 与 7 个内置预设）；选中预设时在下拉框右侧显示 `Copy to Custom` 按钮并将预设提示词与示例以只读方式呈现，选中 `Custom` 时隐藏复制按钮并允许使用任意文本自由编辑提示词与示例，来回切换预设不会丢失 `Custom` 中保存的内容。
- 新增结构化对话示例与请求组装机制：每条示例由对方/情境文本和 1–3 条候选回复列表组成；在 `AiReplyService` 中保持现有 `[system, user]` 双消息请求结构，将有效对话示例序列化为目标 `{"replies":[...]}` JSON 块内嵌于系统提示词中，并显式指示示例仅示范语气与选项多样性、实际回复必须匹配近期聊天语言。
- 完善 6 语言 UI 本地化（`en`、`ja`、`de`、`fr`、`zh-Hans`、`zh-Hant`）：通过 `Loc` 资源目录本地化下拉选项名称、`Copy to Custom`、对话示例区块标题、添加/删除按钮与提示文案，并移除不再使用的 `ResetToDefault` 资源键；内置预设提示词与示例正文保持纯英文。
- 将插件配置升级至 `Version = 2` 并新增 `src/FFXIVMomoTalk/Migrations/Configuration/V1ToV2.cs`：升级前先创建可恢复的本地备份；完整保留既有配置字段，未修改默认提示词的 `v1` 配置自动映射为 `default` 预设，自定义过提示词的 `v1` 配置自动映射为 `custom` 并保留原提示词文本；未知或更高版本配置保持原文件不被覆盖并记录错误。

## Capabilities

### New Capabilities

### Modified Capabilities

- `ai-reply`: 新增内置风格预设与结构化对话示例管理、系统提示词内嵌 JSON 示例与语言跟随约束、预设复制到自定义语义，以及配置 `v1 → v2` 安全升级与备份机制。
- `momotalk-ui`: 将 `AI Reply` 设置页的 `Reset to Default` 替换为系统提示词上方的风格预设下拉框与条件 `Copy to Custom` 按钮，新增页内结构化对话示例只读预览与 `Custom` 编辑卡片，并将新增界面文案接入 6 语言本地化目录。

## Impact

- 影响 `src/FFXIVMomoTalk/Configuration.cs` 与新增 `src/FFXIVMomoTalk/Migrations/Configuration/V1ToV2.cs`：将 `Configuration.CurrentVersion` 升级至 `2`，以 `LlmPromptPreset`、`LlmCustomPrompt` 与 `LlmCustomExamples` 替代旧版 `LlmPrompt`，实现原子化配置迁移、本地备份与版本保护。
- 新增 `src/FFXIVMomoTalk/Chat/AiReplyPresets.cs` 并影响 `src/FFXIVMomoTalk/Chat/AiReplyService.cs`：集中定义 7 个内置英文风格预设与英文结构化示例，在触发生成时线程安全地快照有效提示词与示例并组装到系统提示词。
- 影响 `src/FFXIVMomoTalk/Windows/MomoTalkWindow.cs`、`src/FFXIVMomoTalk/Windows/ChatVisuals.cs` 与 `src/FFXIVMomoTalk/Localization/Strings.*.resx`（6 种语言资源文件）：更新 `AI Reply` 设置页布局、只读/可编辑提示词框与结构化示例卡片及窄窗口高度测量，补充新增本地化键并移除 `ResetToDefault`。
- 影响 `CHANGELOG.md` 与最小验证脚本（`tests/`）：记录版本变更并验证 `v1 → v2` 配置迁移、系统提示词组装及 6 语言资源一致性（`tests/Localization.cs`）。
- 无新增 NuGet 依赖，全部复用现有 .NET 10 `System.Text.Json`、`Newtonsoft.Json`、`ImGui` 与 `Loc` 基础设施。
