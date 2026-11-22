# AGENTS.md

FFXIV MomoTalk 是面向《最终幻想 XIV》国际服的游戏内好友私聊插件。当前构建目标为 Windows x64、.NET 10 和 Dalamud API 15。使用 `/momotalk` 打开聊天窗口。

## MVP 与兼容性

**项目目前处于 MVP 阶段，所有变更均无需考虑前向兼容性。旧版无需识别新版接口、配置或数据格式。**

- 代码只支持当前版本的接口、配置与持久化格式。变更时在同一次修改中更新定义、所有读写方、调用方和相关规范。
- 直接移除废弃的字段和代码路径。禁止为旧版本增加兼容层、双读双写、回退、数据迁移或回填。
- 遇到当前版本无法识别的已有数据，明确报告错误并保留原数据。仅在数据确实不存在时初始化空状态。

## 仓库布局

```text
FFXIVMomoTalk.slnx              解决方案与构建入口
src/FFXIVMomoTalk/Plugin.cs     Dalamud 插件入口与生命周期
src/FFXIVMomoTalk/Chat/         好友快照、Tell 收发、SQLite 历史与头像缓存
src/FFXIVMomoTalk/Windows/      ImGui 窗口、会话视图与绘制
openspec/specs/                 当前规范
openspec/changes/               变更提案、设计、任务与归档
```

## 实现约定

- 采用满足当前需求的最小完整实现。从可运行的端到端流程开始，逐层增加能力。保持模块职责清晰，让现有入口在改动期间可用。避免临时替代方案和预设的抽象、配置或间接层。
- 优先复用现有代码、平台能力和项目依赖。先查文档与类型，再决定是否新增依赖。成熟库能减少自有代码或提升可靠性时，优先使用。
- 在 Dalamud 框架线程读写原生游戏状态。把可独立使用的托管数据交给后台处理，SQLite 和 Lodestone I/O 留在框架线程之外。
- Tell 由玩家主动提交。发送前核验当前角色和好友关系，仅将游戏的 `TellOutgoing` 回显写为本人发出的消息。聊天正文按登录角色隔离并保存在本机，头像查询仅使用公开角色资料。
- 实施 OpenSpec 变更时，先读取 CLI 列出的变更产物。产物正文使用简体中文，保留英文结构标题及 `SHALL`、`MUST` 等规范关键字。

## 常用命令

在仓库根目录运行。构建需要 .NET 10 SDK 和对应的 Dalamud 开发程序集。

```powershell
dotnet restore .\FFXIVMomoTalk.slnx --locked-mode
dotnet build .\FFXIVMomoTalk.slnx -c Debug
dotnet build .\FFXIVMomoTalk.slnx -c Release
openspec list
openspec validate --all --strict
```

依赖变更时更新 `src/FFXIVMomoTalk/packages.lock.json`。Release 构建生成 `src/FFXIVMomoTalk/bin/Release/FFXIVMomoTalk/latest.zip`。

## 验证与提交

- 按改动范围运行针对性验证。仅在重现已报告的回归或保护否则难以察觉的行为时新增最小测试或测试支架。避免推测性的防御代码。
- 原生好友名单、实际 Tell 收发和插件加载以国际服 Dalamud 实机结果为准。汇报实际执行的检查，并在对应验收通过后勾选 OpenSpec 任务。
- 每次完成编码任务后，用 `caveman-commit` 技能生成简短的 Conventional Commit 消息。
