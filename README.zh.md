# FFXIV MomoTalk

[English](README.md) | **简体中文**

我想起来了……其实我是在基沃托斯教书育人的人民 Sensei，因为一场意外才穿越到了艾欧泽亚大陆。在这里，被神（海德林）选中的人将被授予“光之加护”，导引以太之力。我扮演着一位名为“光之战士”的神秘角色，在自由的旅行中邂逅性格各异、能力独特的同伴们，和他们一起击败强敌，拯救破碎的世界。

咳咳，这是一款 Dalamud 插件，旨在将《蔚蓝档案》的 **MomoTalk** 聊天界面带入《最终幻想 XIV》，让你在游戏内用**大人的方式**与好友发送私聊消息（`/tell`）。

## 主要功能

- **还原界面风格**：像回到为师的办公室一样亲切。
- **全局悬浮入口**：可拖动的 MomoTalk 入口。
- **好友私聊收发**：支持发送与接收真实好友私聊。
- **记忆消息历史**：不再丢失与好友的互动。

## 使用方法

1. 在游戏聊天框输入 `/momotalk`，或直接点击画面上的 MomoTalk 悬浮图标开关窗口。
2. 打开“社交”-“好友名单”，获取好友数据。
3. 开始 Talk 吧！

## 环境与构建

- 《最终幻想 XIV》国际服与 **Dalamud API 15**
- **.NET 10 SDK**（Windows x64）

```powershell
dotnet restore .\FFXIVMomoTalk.slnx --locked-mode
dotnet build .\FFXIVMomoTalk.slnx -c Release
```

## 致谢与引用说明

- 本插件的界面风格、配色体系、组件排布与交互动效参考并借用了开源项目 **[U1805/momotalk](https://github.com/U1805/momotalk)**。
- **MomoTalk** 概念源自游戏《**蔚蓝档案（Blue Archive）**》（Nexon Games / Yostar）。
