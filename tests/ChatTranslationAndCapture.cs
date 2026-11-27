#!/usr/bin/env dotnet
// dotnet run --file tests/ChatTranslationAndCapture.cs after building the plugin in Debug.
#:property PublishAot=false

using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

var pluginPath = Path.GetFullPath("src/FFXIVMomoTalk/bin/Debug/FFXIVMomoTalk.dll");
var dalamudPath = Environment.GetEnvironmentVariable("DALAMUD_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Path.GetDirectoryName(pluginPath)!, $"{name.Name}.dll");
    if (!File.Exists(path))
        path = Path.Combine(dalamudPath, $"{name.Name}.dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
};

var plugin = Assembly.LoadFrom(pluginPath);
var dalamud = Assembly.LoadFrom(Path.Combine(dalamudPath, "Dalamud.dll"));

// 1. Plugin-simulated sender filtering
var chatCapture = plugin.GetType("FFXIVMomoTalk.Chat.ChatCapture", throwOnError: true)!;
var readPeer = chatCapture.GetMethod("ReadPeer", BindingFlags.Static | BindingFlags.NonPublic)!;
var isValidPlayerSender = chatCapture.GetMethod("IsValidPlayerSender", BindingFlags.Static | BindingFlags.NonPublic)!;
var isValidName = chatCapture.GetMethod("IsValidGlobalPlayerName", BindingFlags.Static | BindingFlags.NonPublic)!;

foreach (var valid in new[] { "Akatsuki B.", "Akatsuki Bridge", "A. Bridge", "A. B.", "Y'shtola Rhul", "Jean-Luc P." })
    Check((bool)isValidName.Invoke(null, [valid])!, $"Expected real Global FFXIV name '{valid}' to be accepted.");

foreach (var invalid in new[] { "[CT] Akatsuki B-bridge", "[CT] Akatsuki B.", "(Echo) Akatsuki Bridge", "翻译机器人", "CT: Akatsuki Bridge", "Akatsuki", "CT Akatsuki Bridge", "123 Name", "" })
    Check(!(bool)isValidName.Invoke(null, [invalid])!, $"Expected simulated sender '{invalid}' to be rejected.");

var seStringType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.SeString", throwOnError: true)!;
var payloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payload", throwOnError: true)!;
var textPayloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload", throwOnError: true)!;
var playerPayloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload", throwOnError: true)!;

object MakeSeString(params object[] payloads)
{
    var arr = Array.CreateInstance(payloadType, payloads.Length);
    for (var i = 0; i < payloads.Length; i++)
        arr.SetValue(payloads[i], i);
    return Activator.CreateInstance(seStringType, [arr])!;
}

var tellPeerType = plugin.GetType("FFXIVMomoTalk.Chat.TellPeer", throwOnError: true)!;
var realPartySender = MakeSeString(
    Activator.CreateInstance(textPayloadType, ["\uE0B2"])!,
    Activator.CreateInstance(playerPayloadType, ["Akatsuki Bridge", 72u])!,
    Activator.CreateInstance(textPayloadType, ["Akatsuki B.\uE05DTonberry"])!);
var realPeer = Activator.CreateInstance(tellPeerType, ["Akatsuki Bridge", 72u, null, true])!;
Check((bool)isValidPlayerSender.Invoke(null, [realPartySender, realPeer])!,
    "Real player sender with party PUA icon, PlayerPayload, and cross-world suffix should pass.");

var fakeCtWithPayload = MakeSeString(
    Activator.CreateInstance(textPayloadType, ["[CT] "])!,
    Activator.CreateInstance(playerPayloadType, ["Akatsuki Bridge", 72u])!,
    Activator.CreateInstance(textPayloadType, ["Akatsuki B-bridge"])!);
Check(!(bool)isValidPlayerSender.Invoke(null, [fakeCtWithPayload, realPeer])!,
    "Plugin-prefixed sender carrying a PlayerPayload should still be rejected.");

var fakeCtPlain = MakeSeString(Activator.CreateInstance(textPayloadType, ["[CT] Akatsuki B-bridge"])!);
var fakePlainPeer = readPeer.Invoke(null, [fakeCtPlain])!;
Check(!(bool)isValidPlayerSender.Invoke(null, [fakeCtPlain, fakePlainPeer])!,
    "Plain-text Chat Translate sender should be rejected.");

var realPlain = MakeSeString(Activator.CreateInstance(textPayloadType, ["\uE0B1Akatsuki B.\uE05DTonberry"])!);
var realPlainPeer = readPeer.Invoke(null, [realPlain])!;
Check((bool)isValidPlayerSender.Invoke(null, [realPlain, realPlainPeer])!,
    "Plain-text abbreviated real player sender should pass.");

// 2. Translation target language configuration default & override
var locType = plugin.GetType("FFXIVMomoTalk.Loc", throwOnError: true)!;
var setLanguage = locType.GetMethod("SetLanguage", BindingFlags.Static | BindingFlags.Public)!;
var configType = plugin.GetType("FFXIVMomoTalk.Configuration", throwOnError: true)!;
dynamic config = Activator.CreateInstance(configType)!;
setLanguage.Invoke(null, ["en", "zh-Hans"]);
Check(config.LlmTranslationLanguage is null && config.EffectiveTranslationLanguage == "zh-Hans",
    "Translation language should default to the active plugin language.");
config.LlmTranslationLanguage = "ja";
Check(config.LlmTranslationLanguage == "ja" && config.EffectiveTranslationLanguage == "ja",
    "Explicit translation language should override the plugin language.");
config.LlmTranslationLanguage = "invalid-code";
Check(config.LlmTranslationLanguage is null && config.EffectiveTranslationLanguage == "zh-Hans",
    "Unknown translation language codes should fall back to following plugin language.");

// 3. Window selection (up to 5 before, selected, up to 4 after = 10 max)
var aiReplyType = plugin.GetType("FFXIVMomoTalk.Chat.AiReplyService", throwOnError: true)!;
var identityType = plugin.GetType("FFXIVMomoTalk.Chat.PlayerIdentity", throwOnError: true)!;
var messageType = plugin.GetType("FFXIVMomoTalk.Chat.ChatMessage", throwOnError: true)!;
var descriptorType = plugin.GetType("FFXIVMomoTalk.Chat.ConversationDescriptor", throwOnError: true)!;
var keyType = plugin.GetType("FFXIVMomoTalk.Chat.ConversationKey", throwOnError: true)!;
var selectWindow = aiReplyType.GetMethod("SelectTranslationWindow", BindingFlags.Static | BindingFlags.NonPublic)!;

var speaker = Activator.CreateInstance(identityType, 2UL, "Akatsuki Bridge", 72u, "Tonberry")!;
var allMessages = Array.CreateInstance(messageType, 15);
for (var i = 0; i < 15; i++)
    allMessages.SetValue(Activator.CreateInstance(messageType, (long)(i + 1), 1_700_000_000_000L + i * 1000L, i % 2 == 1, speaker, $"Message {i + 1}", null), i);

var middleWindow = ((IEnumerable)selectWindow.Invoke(null, [allMessages, 8L])!).Cast<object>().ToArray();
Check(middleWindow.Length == 10
    && (long)GetProp(middleWindow[0], "Id")! == 3L
    && (long)GetProp(middleWindow[^1], "Id")! == 12L,
    "Middle anchor should select 5 preceding, anchor, and 4 following messages (IDs 3..12).");

var headWindowRaw = selectWindow.Invoke(null, [allMessages, 2L])!;
var headWindow = ((IEnumerable)headWindowRaw).Cast<object>().ToArray();
Check(headWindow.Length == 6
    && (long)GetProp(headWindow[0], "Id")! == 1L
    && (long)GetProp(headWindow[^1], "Id")! == 6L,
    "Near-start anchor should clamp preceding messages cleanly (IDs 1..6).");

var tailWindow = ((IEnumerable)selectWindow.Invoke(null, [allMessages, 14L])!).Cast<object>().ToArray();
Check(tailWindow.Length == 7
    && (long)GetProp(tailWindow[0], "Id")! == 9L
    && (long)GetProp(tailWindow[^1], "Id")! == 15L,
    "Near-end anchor should clamp following messages cleanly (IDs 9..15).");

// 4. Translation prompt & parser
var buildSystemPrompt = aiReplyType.GetMethod("BuildTranslationSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
var buildUserPrompt = aiReplyType.GetMethod("BuildTranslationUserPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
var descriptor = Activator.CreateInstance(descriptorType, Activator.CreateInstance(keyType)!, "Free Company", 0u, string.Empty)!;
var sysPrompt = (string)buildSystemPrompt.Invoke(null, ["zh-Hans"])!;
var usrPrompt = (string)buildUserPrompt.Invoke(null, [descriptor, "FC", headWindowRaw, "zh-Hans"])!;
Check(sysPrompt.Contains("Simplified Chinese") && usrPrompt.Contains("\"id\":1") && usrPrompt.Contains("Message 6"),
    "Translation prompts should include target language and sequential message items.");

var parserType = plugin.GetType("FFXIVMomoTalk.Chat.AiTranslationParser", throwOnError: true)!;
var tryParse = parserType.GetMethod("TryParse", BindingFlags.Static | BindingFlags.NonPublic)!;
var parseArgs = new object?[]
{
    """
    <think>translating</think>
    ```json
    {"translations":[
      {"id":1,"translation":"第一行\r\n第二行"},
      {"id":2,"translation":"Message 2"},
      {"id":3,"translation":null}
    ]}
    ```
    """,
    headWindowRaw,
    null,
};
Check((bool)tryParse.Invoke(null, parseArgs)!, "Valid fenced JSON translation payload should parse.");
var parsed = (Dictionary<long, string?>)parseArgs[2]!;
Check(parsed[1L] == "第一行\n第二行" && parsed[2L] is null && parsed[3L] is null,
    "Parser should preserve newlines while suppressing identical or null translations.");

var badArgs = new object?[] { "Not JSON at all", headWindowRaw, null };
Check(!(bool)tryParse.Invoke(null, badArgs)!, "Non-JSON response should fail parsing.");

// 5. Bounded 1000-entry in-memory translation cache
var cacheType = plugin.GetType("FFXIVMomoTalk.Chat.ChatTranslationCache", throwOnError: true)!;
var cache = Activator.CreateInstance(cacheType, [1000])!;
var cacheSet = cacheType.GetMethod("Set")!;
var cacheTryGet = cacheType.GetMethod("TryGet")!;
for (var i = 1; i <= 1005; i++)
    cacheSet.Invoke(cache, [1UL, (long)i, $"Body {i}", $"译文 {i}"]);

Check((int)GetProp(cache, "Count")! == 1000, "In-memory translation cache must cap at 1000 entries.");
Check(cacheTryGet.Invoke(cache, [1UL, 1L, "Body 1"]) is null
    && cacheTryGet.Invoke(cache, [1UL, 5L, "Body 5"]) is null,
    "Oldest entries beyond 1000 should be evicted.");
Check((string?)cacheTryGet.Invoke(cache, [1UL, 6L, "Body 6"]) == "译文 6"
    && (string?)cacheTryGet.Invoke(cache, [1UL, 1005L, "Body 1005"]) == "译文 1005",
    "Recent 1000 entries should remain retrievable.");
Check(cacheTryGet.Invoke(cache, [1UL, 1005L, "Body 1005\nMerged"]) is null,
    "Cache lookup should ignore stale translation when bubble text changed after merge.");
Check(cacheTryGet.Invoke(cache, [2UL, 1005L, "Body 1005"]) is null,
    "Cache entries must be isolated by character ContentId.");

Console.WriteLine("Chat sender filtering, translation config, window selection, parser, and 1000-entry cache passed.");

static object? GetProp(object instance, string name) => instance.GetType().GetProperty(name)!.GetValue(instance);

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
