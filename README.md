# MomoTalk (FFXIV Dalamud Plugin)

English | [简体中文](README.zh.md)

It all came back to me... I'm actually a Sensei who teaches and mentors students in Kivotos, and by some strange twist of fate, I was transported to the continent of Eorzea. Here, those chosen by the god (Hydaelyn) are granted the Blessing of Light to channel the power of aether. Taking on the role of a mysterious figure known as the "Warrior of Light", I travel freely, meeting companions of all personalities and unique abilities, and together we defeat formidable foes and save the fractured world.

Ahem—this is a Dalamud plugin that brings **Blue Archive**'s **MomoTalk** chat interface into **FINAL FANTASY XIV**, letting you send private messages (`/tell`) to your friends in-game **the adult way**.

## Features

- **Faithful UI Style**: Feels just like office.
- **Global Floating Launcher**: A draggable MomoTalk entry button.
- **Friend `/tell` Messaging**: Send and receive real in-game private messages with friends.
- **Persistent Chat History**: Never lose track of conversations with your friends.

## Usage

1. Type `/momotalk` in the in-game chat box, or click the floating MomoTalk icon on screen to toggle the window.
2. Open **Social** → **Friend List** to load your friends' data.
3. Start Talking!

## Requirements & Build

- FINAL FANTASY XIV (International) with **Dalamud API 15**
- **.NET 10 SDK** (Windows x64)

```powershell
dotnet restore .\FFXIVMomoTalk.slnx --locked-mode
dotnet build .\FFXIVMomoTalk.slnx -c Release
```

## Credits & Acknowledgements

- Visual design, color palette, layout, and UI interaction references are adapted from **[U1805/momotalk](https://github.com/U1805/momotalk)**.
- **MomoTalk** is inspired by the in-game messenger from **Blue Archive** (Nexon Games / Yostar).
