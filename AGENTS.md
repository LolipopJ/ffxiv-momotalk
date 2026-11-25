# AGENTS.md

FFXIV MomoTalk 是面向《最终幻想 XIV》国际服的游戏内好友私聊与群聊插件。当前构建目标为 Windows x64、.NET 10 和 Dalamud API 15。使用 `/momotalk` 打开聊天窗口。

## 仓库布局

```text
FFXIVMomoTalk.slnx              解决方案与构建入口
CHANGELOG.md                    版本变更日志（Keep a Changelog 1.1.0）
src/FFXIVMomoTalk/Plugin.cs     Dalamud 插件入口与生命周期
src/FFXIVMomoTalk/Chat/         好友/群组/小队快照、多频道收发、分域 SQLite 持久化与头像缓存
src/FFXIVMomoTalk/Migrations/   数据与配置的版本迁移（有迁移时创建）
src/FFXIVMomoTalk/Windows/      ImGui 窗口、会话视图与绘制
openspec/specs/                 当前规范
openspec/changes/               变更提案、设计、任务与归档
```

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

## 实现约定

- 采用满足当前需求的最小完整实现。从可运行的端到端流程开始，逐层增加能力。保持模块职责清晰，让现有入口在改动期间可用。避免临时替代方案和预设的抽象、配置或间接层。
- 优先复用现有代码、平台能力和项目依赖。先查文档与类型，再决定是否新增依赖。成熟库能减少自有代码或提升可靠性时，优先使用。
- 在 Dalamud 框架线程读写原生游戏状态。把可独立使用的托管数据交给后台处理，SQLite 和 Lodestone I/O 留在框架线程之外。
- Tell 由玩家主动提交。发送前核验当前角色和好友关系，仅将游戏的 `TellOutgoing` 回显写为本人发出的消息。聊天正文按登录角色隔离并保存在本机，头像查询仅使用公开角色资料。
- 实施 OpenSpec 变更时，先读取 CLI 列出的变更产物。产物正文使用简体中文，保留英文结构标题及 `SHALL`、`MUST` 等规范关键字。

### 代码组织与 AI 上下文

- 代码文件接近 600 行时评估拆分。以职责和共同变更范围确定文件边界，把经常一起修改的成员放在同一个文件。
- 优先将输入输出明确、可独立维护的职责提取为独立类型。共享状态和生命周期的类型可用 `partial` 按职责拆分，分部文件命名为 `<类型>.<职责>.cs`。主文件集中放置构造、生命周期和共享状态。
- 用 `rg` 或符号搜索定位声明和调用点，再读取当前任务需要的代码段。查看大型构建和测试日志时，先提取相关诊断和结果。

## 兼容性

影响已发布版本的用户数据、插件配置、公开命令或接口的变更，必须在同一变更中完成兼容性处理。

- 明确受影响的版本、读写方和调用方，并更新相关 OpenSpec 契约和验收场景。用户应能从受影响的已发布版本直接升级，保留已有数据和兼容用法。
- 优先以兼容的方式扩展既有约定。公开命令或接口需要移除时，先提供替代入口并说明弃用和计划移除的版本。破坏性变更须在 `CHANGELOG.md` 中说明升级与回退的方法及限制。

## 数据持久化

- 按角色隔离的 SQLite 数据存放在 `<PluginConfigDir>/characters/{ContentId:X16}/`，按数据域拆分为独立数据库并各自维护独立的 `PRAGMA user_version`：
  - `messages.sqlite3`：聊天记录、会话摘要与发送拒发原因（不可再生的高价值核心数据）。
  - `notes.sqlite3`：用户手动填写的好友与群组备注（不可再生的用户创作数据）。
  - `statics.sqlite3`：固定队配置与成员名单（游戏无原生对应物，不可直接重建）。
  - `cache.sqlite3`：好友缓存、群组频道缓存、隐藏会话状态与同步元数据（可由游戏重建或低损恢复）。
- 设计新的持久化数据时，必须先评估其**可再生性**、**丢失代价**与 **schema 演进频率**：不可从游戏重建、由用户手工录入或长期累积的高价值数据，MUST 放置在单独的数据库中存储，避免其他模块的 schema 变更连带要求重置该数据；可从游戏重新拉取的缓存、临时状态或低影响 UI 标记应归入 `cache.sqlite3`。
- 各数据库独立处理版本错误和迁移失败。仅停止受影响数据库的读写，其余数据库继续使用。

## 数据与配置迁移

- 迁移实现统一放在 `src/FFXIVMomoTalk/Migrations/`，按数据域或配置建子目录，文件名标明源版本和目标版本，如 `Migrations/Messages/V1ToV2.cs`、`Migrations/Configuration/V1ToV2.cs`。配置加载器和各存储初始化入口先执行对应迁移，再开始正常读写。
- SQLite 按各库的 `PRAGMA user_version` 逐步升级，配置按 `Configuration.Version` 逐步升级。保留已发布版本的迁移路径，已迁移数据的修正通过后续版本步骤完成。
- 每个 SQLite 步骤在后台串行执行，schema、数据和 `user_version` 在同一事务中提交。步骤失败时回滚该事务，并允许从最后提交的版本重试。配置完整转换并校验后原子保存。报告失败时注明数据域、源版本、目标版本和失败步骤。 [SQLite 事务](https://sqlite.org/lang_transaction.html)
- 聊天记录、备注、固定队和用户配置等不可再生数据遇到不可逆升级时，应先保留可恢复的本地备份并记录恢复步骤。SQLite 备份须使用能产生一致快照的方式。缓存重建仅限已确认可再生的受影响数据。 [SQLite 备份](https://sqlite.org/backup.html)
- 数据版本未知或超出支持范围时，保留原文件并明确报告错误。只有对应数据确实不存在时才初始化空状态。

## 验证与提交

- 按改动范围运行针对性验证。仅在重现已报告的回归或保护否则难以察觉的行为时新增最小测试或测试支架。避免推测性的防御代码。
- 含迁移的变更须验证全新初始化、从受影响的旧版本直接升级、关键数据保留和失败后重试。涉及备份恢复或其他回退路径时一并验证。
- 原生好友名单、实际频道收发、小队右键入口和插件加载以国际服 Dalamud 实机结果为准。汇报实际执行的检查，并在对应验收通过后勾选 OpenSpec 任务。
- 每次编写完毕代码后，按 [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) 规范更新根目录 `CHANGELOG.md`：将变更记入 `## [Unreleased]` 下对应的标准分类（`Added`、`Changed`、`Deprecated`、`Removed`、`Fixed`、`Security`），破坏性变更以 `**Breaking:**` 标明；条目使用简洁英文描述面向用户的实际变化，并保持文末版本对比链接有效。
- 每次完成编码任务后，用 `caveman-commit` 技能生成简短的 Conventional Commit 消息。
