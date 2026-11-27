#!/usr/bin/env dotnet
// dotnet run --file tests/FriendEmoteAndMessageMigration.cs after building the plugin in Debug.
#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.8
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Data.Sqlite;

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

var directory = Directory.CreateTempSubdirectory("momotalk-emote-msg-");
try
{
    await VerifyFreshMessageStoreAndNoMergeAsync(directory.FullName);
    await VerifySilentSystemAndAiExclusionAsync(directory.FullName);
    await VerifyRenameAndTransferNoticesAsync(directory.FullName);
    await VerifyV1ToV2MigrationWithBackupAsync(directory.FullName);
    await VerifyUnknownMessageVersionPreservedAsync(directory.FullName);
    VerifyEmoteCaptureAndResolution();
    await VerifySystemLogClassificationAndRoutingAsync(directory.FullName);
    VerifyAiSystemHandling();

    Console.WriteLine("Friend emote, system log capture, silent archive, rename notices, MessageStore v1->v2 migration, and AI handling passed.");
}
finally
{
    directory.Delete(recursive: true);
}

async Task VerifyFreshMessageStoreAndNoMergeAsync(string root)
{
    var storeRoot = Path.Combine(root, "fresh");
    await using var store = (IAsyncDisposable)New("MessageStore", storeRoot, null);
    var ownerIdentity = New("PlayerIdentity", 1UL, "Local Player", 1U, "World A");
    var session = New("PlayerSession", 1UL, ownerIdentity);
    var friend = New("PlayerIdentity", 2UL, "Friend Name", 1U, "World A");
    var conv = TypeOf("ConversationDescriptor").GetMethod("FromFriend")!.Invoke(null, [friend])!;
    var key = Get(conv, "Key");
    var chatKind = Enum.ToObject(TypeOf("MessageKind"), 0);
    var systemKind = Enum.ToObject(TypeOf("MessageKind"), 1);

    var id1 = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 1_000L, false, "Hey!", chatKind), true)!;
    var id2 = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 1_100L, false, "Friend Name pats you.", systemKind), true)!;
    var id3 = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 1_200L, false, "Friend Name hugs you.", systemKind), true)!;
    var id4 = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 1_300L, false, "How are you?", chatKind), true)!;
    var id5 = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, ownerIdentity, 1_350L, true, "You cheer on Friend Name.", systemKind), true)!;

    Check(id1 < id2 && id2 < id3 && id3 < id4 && id4 < id5,
        "System emote messages (incoming and outgoing) must never merge with prior or subsequent chat or system messages within 500 ms.");

    var dbPath = Path.Combine(storeRoot, $"{1UL:X16}", "messages.sqlite3");
    using (var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        verify.Open();
        using var ver = verify.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        Check(Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture) == 2,
            "Fresh MessageStore must initialize at user_version 2.");
    }

    var convsTask = (Task)Call(store, "ReadConversationsAsync", 1UL)!;
    await convsTask;
    var summaries = ((IEnumerable)Get(convsTask, "Result")!).Cast<object>().ToArray();
    Check(summaries.Length == 1 && (string)Get(Get(summaries[0], "Latest")!, "Body")! == "You cheer on Friend Name."
        && (bool)Get(Get(summaries[0], "Latest")!, "IsSystem")!,
        "Conversation summary should expose outgoing system emote latest message.");

    await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 2_000L, false, "Friend Name dotes on you.", systemKind), true)!;
    var convsTask2 = (Task)Call(store, "ReadConversationsAsync", 1UL)!;
    await convsTask2;
    var latestAfterEmote = Get(((IEnumerable)Get(convsTask2, "Result")!).Cast<object>().Single(), "Latest")!;
    Check((bool)Get(latestAfterEmote, "IsSystem")! && (string)Get(latestAfterEmote, "Body")! == "Friend Name dotes on you.",
        "Conversation summary should expose incoming system emote latest message.");

    var history = Activator.CreateInstance(plugin.GetType("FFXIVMomoTalk.Windows.ConversationHistory", throwOnError: true)!,
        [store, 1UL, key])!;
    await (Task)Call(store, "ReadPageAsync", 1UL, key, null)!;
    Call(history, "Update");
    var bubbles = ((IEnumerable)Get(history, "Messages")!).Cast<object>().ToArray();
    Check(bubbles.Length == 6
        && !(bool)Get(bubbles[0], "IsSystem")!
        && (bool)Get(bubbles[1], "IsSystem")!
        && (bool)Get(bubbles[2], "IsSystem")!
        && !(bool)Get(bubbles[3], "IsSystem")!
        && (bool)Get(bubbles[4], "IsSystem")!
        && (bool)Get(bubbles[5], "IsSystem")!,
        "ConversationHistory should preserve incoming and outgoing system messages as separate unmerged items.");
}

async Task VerifyV1ToV2MigrationWithBackupAsync(string root)
{
    const ulong owner = 0x2002UL;
    var storeRoot = Path.Combine(root, "migrate-v1");
    var charDir = Path.Combine(storeRoot, $"{owner:X16}");
    Directory.CreateDirectory(charDir);
    var dbPath = Path.Combine(charDir, "messages.sqlite3");

    using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE conversations (
                id INTEGER PRIMARY KEY,
                kind INTEGER NOT NULL,
                stable_key TEXT NOT NULL,
                display_name TEXT NOT NULL,
                world_id INTEGER NOT NULL,
                world_name TEXT NOT NULL,
                last_message_id INTEGER,
                last_sent_at INTEGER,
                UNIQUE (kind, stable_key)
            );
            CREATE TABLE messages (
                id INTEGER PRIMARY KEY,
                conversation_id INTEGER NOT NULL REFERENCES conversations (id),
                sent_at INTEGER NOT NULL,
                outgoing INTEGER NOT NULL,
                sender_content_id INTEGER NOT NULL,
                sender_name TEXT NOT NULL,
                sender_world_id INTEGER NOT NULL,
                sender_world_name TEXT NOT NULL,
                body TEXT NOT NULL
            );
            CREATE TABLE message_rejections (
                message_id INTEGER PRIMARY KEY REFERENCES messages (id),
                reason TEXT NOT NULL
            );
            INSERT INTO conversations (id, kind, stable_key, display_name, world_id, world_name, last_message_id, last_sent_at)
            VALUES (1, 0, '0000000000000002', 'Legacy Friend', 1, 'World A', 2, 2000);
            INSERT INTO messages (id, conversation_id, sent_at, outgoing, sender_content_id, sender_name, sender_world_id, sender_world_name, body)
            VALUES (1, 1, 1000, 0, 2, 'Legacy Friend', 1, 'World A', 'Hello v1');
            INSERT INTO messages (id, conversation_id, sent_at, outgoing, sender_content_id, sender_name, sender_world_id, sender_world_name, body)
            VALUES (2, 1, 2000, 1, 8194, 'Local Player', 1, 'World A', 'Failed v1');
            INSERT INTO message_rejections (message_id, reason)
            VALUES (2, 'TellRecipientBusy');
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    var infoLogs = new List<string>();
    Action<string> onInfo = msg => infoLogs.Add(msg);
    await using var store = (IAsyncDisposable)New("MessageStore", storeRoot, onInfo);

    var ownerIdentity = New("PlayerIdentity", owner, "Local Player", 1U, "World A");
    var session = New("PlayerSession", owner, ownerIdentity);
    var friend = New("PlayerIdentity", 2UL, "Legacy Friend", 1U, "World A");
    var conv = TypeOf("ConversationDescriptor").GetMethod("FromFriend")!.Invoke(null, [friend])!;
    var key = Get(conv, "Key");

    var pageTask = (Task)Call(store, "ReadPageAsync", owner, key, null)!;
    await pageTask;
    var pageMessages = ((IEnumerable)Get(Get(pageTask, "Result")!, "Messages")!).Cast<object>().ToArray();
    Check(pageMessages.Length == 2
        && !(bool)Get(pageMessages[0], "IsSystem")!
        && (string)Get(pageMessages[0], "Body")! == "Hello v1"
        && (string?)Get(pageMessages[1], "Rejection") == "TellRecipientBusy",
        "V1 messages and rejections should migrate cleanly with MessageKind.Chat default.");

    var backupPath = dbPath + ".v1.bak";
    Check(File.Exists(backupPath), "Migrating messages.sqlite3 from v1 to v2 must create a .v1.bak backup.");
    Check(infoLogs.Any(log => log.Contains("v1 backup created", StringComparison.Ordinal) && log.Contains(backupPath, StringComparison.Ordinal)),
        "Backup log should record the backup path and rollback instructions.");

    using (var bak = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
    {
        bak.Open();
        using var ver = bak.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        Check(Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture) == 1,
            "Backup file must remain at v1 schema for rollback.");

        using var count = bak.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM messages;";
        Check(Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 2,
            "Backup file must preserve all original v1 messages.");
    }

    using (var migrated = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        migrated.Open();
        using var ver = migrated.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        Check(Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture) == 2,
            "Migrated messages.sqlite3 must be at user_version 2.");
    }

    var systemKind = Enum.ToObject(TypeOf("MessageKind"), 1);
    var emoteId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, conv, friend, 3000L, false, "Legacy Friend hugs you.", systemKind), true)!;
    Check(emoteId > 2, "Migrated v2 MessageStore should persist new system emote messages.");
}

async Task VerifyUnknownMessageVersionPreservedAsync(string root)
{
    const ulong owner = 0x3003UL;
    var storeRoot = Path.Combine(root, "unknown-v99");
    var charDir = Path.Combine(storeRoot, $"{owner:X16}");
    Directory.CreateDirectory(charDir);
    var dbPath = Path.Combine(charDir, "messages.sqlite3");

    using (var seed = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        seed.Open();
        using var cmd = seed.CreateCommand();
        cmd.CommandText = "CREATE TABLE sentinel (id INTEGER PRIMARY KEY); INSERT INTO sentinel (id) VALUES (99); PRAGMA user_version = 99;";
        cmd.ExecuteNonQuery();
    }

    await using var store = (IAsyncDisposable)New("MessageStore", storeRoot, null);
    try
    {
        await (Task)Call(store, "ReadConversationsAsync", owner)!;
        throw new InvalidOperationException("Expected InvalidDataException for unknown messages.sqlite3 version.");
    }
    catch (InvalidDataException)
    {
        Check((ulong)Get(store, "IncompatibleOwner")! == owner,
            "IncompatibleOwner must be set for unrecognized messages.sqlite3 version.");
    }

    Check(!File.Exists(dbPath + ".v99.bak") && !File.Exists(dbPath + ".v1.bak"),
        "Unknown versions must not create backup files.");
}

void VerifyEmoteCaptureAndResolution()
{
    var lumina = Assembly.LoadFrom(Path.Combine(dalamudPath, "Lumina.dll"));
    var chatCapture = TypeOf("ChatCapture");
    var chatTypeEnum = dalamud.GetType("Dalamud.Game.Text.XivChatType", throwOnError: true)!;
    var relKindEnum = dalamud.GetType("Dalamud.Game.Text.XivChatRelationKind", throwOnError: true)!;

    var standardEmote = Enum.Parse(chatTypeEnum, "StandardEmote");
    var customEmote = Enum.Parse(chatTypeEnum, "CustomEmote");
    var localRel = Enum.Parse(relKindEnum, "LocalPlayer");
    var otherRel = Enum.Parse(relKindEnum, "OtherPlayer");
    var partyRel = Enum.Parse(relKindEnum, "PartyMember");
    var allianceRel = Enum.Parse(relKindEnum, "AllianceMember");
    var npcRel = Enum.Parse(relKindEnum, "FriendlyNpc");
    var petRel = Enum.Parse(relKindEnum, "PetOrCompanion");
    var noneRel = Enum.Parse(relKindEnum, "None");

    var tryClassify = chatCapture.GetMethod("TryClassifyTargetedEmote", BindingFlags.Static | BindingFlags.NonPublic)!;

    bool Classify(object chatType, object src, object dst, out bool outgoing)
    {
        var args = new object?[] { chatType, src, dst, false };
        var matched = (bool)tryClassify.Invoke(null, args)!;
        outgoing = (bool)args[3]!;
        return matched;
    }

    Check(Classify(standardEmote, otherRel, localRel, out var dir1) && !dir1, "OtherPlayer -> LocalPlayer StandardEmote must match incoming.");
    Check(Classify(standardEmote, partyRel, localRel, out var dir2) && !dir2, "PartyMember -> LocalPlayer StandardEmote must match incoming.");
    Check(Classify(standardEmote, allianceRel, localRel, out var dir3) && !dir3, "AllianceMember -> LocalPlayer StandardEmote must match incoming.");
    Check(Classify(standardEmote, localRel, otherRel, out var dir4) && dir4, "LocalPlayer -> OtherPlayer StandardEmote must match outgoing.");
    Check(Classify(standardEmote, localRel, partyRel, out var dir5) && dir5, "LocalPlayer -> PartyMember StandardEmote must match outgoing.");
    Check(Classify(standardEmote, localRel, allianceRel, out var dir6) && dir6, "LocalPlayer -> AllianceMember StandardEmote must match outgoing.");
    Check(!Classify(customEmote, otherRel, localRel, out _), "Incoming CustomEmote must be ignored.");
    Check(!Classify(customEmote, localRel, otherRel, out _), "Outgoing CustomEmote must be ignored.");
    Check(!Classify(standardEmote, localRel, localRel, out _), "Self-emote must be ignored.");
    Check(!Classify(standardEmote, otherRel, otherRel, out _), "Emote between other players must be ignored.");
    Check(!Classify(standardEmote, otherRel, noneRel, out _), "Untargeted other-player emote must be ignored.");
    Check(!Classify(standardEmote, localRel, noneRel, out _), "Untargeted local-player emote must be ignored.");
    Check(!Classify(standardEmote, npcRel, localRel, out _), "NPC -> LocalPlayer emote must be ignored.");
    Check(!Classify(standardEmote, localRel, npcRel, out _), "LocalPlayer -> NPC emote must be ignored.");
    Check(!Classify(standardEmote, petRel, localRel, out _), "Pet -> LocalPlayer emote must be ignored.");
    Check(!Classify(standardEmote, noneRel, localRel, out _), "Synthetic None -> LocalPlayer emote must be ignored.");

    var seStringType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.SeString", throwOnError: true)!;
    var payloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payload", throwOnError: true)!;
    var textPayloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload", throwOnError: true)!;
    var playerPayloadType = dalamud.GetType("Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload", throwOnError: true)!;
    var readOnlySeStringType = lumina.GetType("Lumina.Text.ReadOnly.ReadOnlySeString", throwOnError: true)!;
    var encodeMethod = seStringType.GetMethod("Encode", BindingFlags.Instance | BindingFlags.Public)!;

    object MakeReadOnlySeString(params object[] payloads)
    {
        var arr = Array.CreateInstance(payloadType, payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
            arr.SetValue(payloads[i], i);
        var se = Activator.CreateInstance(seStringType, [arr])!;
        var bytes = (byte[])encodeMethod.Invoke(se, null)!;
        return Activator.CreateInstance(readOnlySeStringType, [bytes])!;
    }

    var localPlayer = New("PlayerIdentity", 1UL, "Local Player", 72U, "Tonberry");
    var emptySe = MakeReadOnlySeString();
    var readEmotePeer = chatCapture.GetMethod("ReadEmotePeer", BindingFlags.Static | BindingFlags.NonPublic)!;

    var sameWorldEmote = MakeReadOnlySeString(
        Activator.CreateInstance(playerPayloadType, ["Akatsuki Bridge", 72U])!,
        Activator.CreateInstance(textPayloadType, [" pats you gently on the head."])!);
    var peer1 = readEmotePeer.Invoke(null, [emptySe, sameWorldEmote, localPlayer]);
    Check(peer1 is not null && (string)Get(peer1, "Name")! == "Akatsuki Bridge" && (uint)Get(peer1, "WorldId")! == 72U,
        "Same-world standard emote PlayerPayload in message should resolve.");

    var crossWorldWithLocalPayload = MakeReadOnlySeString(
        Activator.CreateInstance(playerPayloadType, ["Local Player", 72U])!,
        Activator.CreateInstance(textPayloadType, ["は"])!,
        Activator.CreateInstance(playerPayloadType, ["Friend Cross", 63U])!,
        Activator.CreateInstance(textPayloadType, ["になでられた。"])!);
    var peer2 = readEmotePeer.Invoke(null, [emptySe, crossWorldWithLocalPayload, localPlayer]);
    Check(peer2 is not null && (string)Get(peer2, "Name")! == "Friend Cross" && (uint)Get(peer2, "WorldId")! == 63U,
        "LocalPlayer payload should be excluded when resolving cross-world or multi-payload emote messages.");

    var outgoingToFriend = MakeReadOnlySeString(
        Activator.CreateInstance(textPayloadType, ["You pat "])!,
        Activator.CreateInstance(playerPayloadType, ["Friend Cross", 63U])!,
        Activator.CreateInstance(textPayloadType, ["."])!);
    var peer3 = readEmotePeer.Invoke(null, [emptySe, outgoingToFriend, localPlayer]);
    Check(peer3 is not null && (string)Get(peer3, "Name")! == "Friend Cross" && (uint)Get(peer3, "WorldId")! == 63U,
        "Outgoing local-player standard emote targeting a friend should resolve the target peer.");

    var fakePayloadEmote = MakeReadOnlySeString(
        Activator.CreateInstance(playerPayloadType, ["[CT] Bot", 72U])!,
        Activator.CreateInstance(textPayloadType, [" waves at you."])!);
    Check(readEmotePeer.Invoke(null, [emptySe, fakePayloadEmote, localPlayer]) is null,
        "Invalid global player names in emote payloads should be ignored.");

    var capture = New("ChatCapture", null, null, null, null, null, null, null);
    var observedType = chatCapture.GetNestedType("ObservedLogEntry", BindingFlags.NonPublic)!;
    var observedPlayerType = chatCapture.GetNestedType("ObservedPlayer", BindingFlags.NonPublic)!;
    var categoryEnum = chatCapture.GetNestedType("SystemLogCategory", BindingFlags.NonPublic)!;
    var specType = chatCapture.GetNestedType("SystemLogSpec", BindingFlags.NonPublic)!;
    var observedList = (IList)chatCapture.GetField("recentObservedLogs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;

    object MakeObservedEmote(string srcName, ushort srcWorld, string dstName, ushort dstWorld) =>
        Activator.CreateInstance(observedType, [
            Enum.Parse(categoryEnum, "StandardEmote"),
            standardEmote,
            100U,
            Activator.CreateInstance(observedPlayerType, [srcName, srcWorld]),
            Activator.CreateInstance(observedPlayerType, [dstName, dstWorld]),
            null,
            null,
            false,
            Activator.CreateInstance(specType),
            System.Diagnostics.Stopwatch.GetTimestamp(),
        ])!;

    observedList.Add(MakeObservedEmote("Friend Fallback", 72, "Local Player", 72));
    var tryResolve = chatCapture.GetMethod("TryResolveEmotePeer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var plainIncoming = MakeReadOnlySeString(Activator.CreateInstance(textPayloadType, ["Friend Fallback waves at you."])!);
    var fallbackIn = tryResolve.Invoke(capture, [emptySe, plainIncoming, "Friend Fallback waves at you.", false, localPlayer]);
    Check(fallbackIn is not null && (string)Get(fallbackIn, "Name")! == "Friend Fallback" && (uint)Get(fallbackIn, "WorldId")! == 72U,
        "TryResolveEmotePeer should fall back to observed LogMessage source when no PlayerPayload is present on incoming emotes.");

    observedList.Add(MakeObservedEmote("Local Player", 72, "Friend Target", 63));
    var plainOutgoing = MakeReadOnlySeString(Activator.CreateInstance(textPayloadType, ["You wave at Friend Target."])!);
    var fallbackOut = tryResolve.Invoke(capture, [emptySe, plainOutgoing, "You wave at Friend Target.", true, localPlayer]);
    Check(fallbackOut is not null && (string)Get(fallbackOut, "Name")! == "Friend Target" && (uint)Get(fallbackOut, "WorldId")! == 63U,
        "TryResolveEmotePeer should fall back to observed LogMessage target when no PlayerPayload is present on outgoing emotes.");

    var social = New("SocialDirectory", null, null, null, null, null, null, null, null, null);
    var sender = New("ChatSender", null, null, social, capture, null);
    var ownerIdentity = New("PlayerIdentity", 1UL, "Local Player", 72U, "Tonberry");
    var friendIdentity = New("PlayerIdentity", 2UL, "Friend Target", 63U, "Kujata");
    var session = New("PlayerSession", 1UL, ownerIdentity);
    var conv = TypeOf("ConversationDescriptor").GetMethod("FromFriend")!.Invoke(null, [friendIdentity])!;
    var systemKind = Enum.ToObject(TypeOf("MessageKind"), 1);
    var outgoingEmote = New("CapturedMessage", session, conv, ownerIdentity, 1_700_000_000_000L, true, "You pat Friend Target.", systemKind);
    Call(sender, "OnCaptured", outgoingEmote, Task.FromResult(1L));
    var pending = (IList)sender.GetType().GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
    Check(pending.Count == 0, "ChatSender must not track outgoing system emote messages as pending Tells.");
}

void VerifyAiSystemHandling()
{
    var aiReplyType = TypeOf("AiReplyService");
    var identityType = TypeOf("PlayerIdentity");
    var sessionType = TypeOf("PlayerSession");
    var descriptorType = TypeOf("ConversationDescriptor");
    var keyType = TypeOf("ConversationKey");
    var messageType = TypeOf("ChatMessage");
    var messageKindType = TypeOf("MessageKind");
    var chatKind = Enum.ToObject(messageKindType, 0);
    var systemKind = Enum.ToObject(messageKindType, 1);

    var owner = Activator.CreateInstance(identityType, 1UL, "Local Player", 1U, "World")!;
    var friend = Activator.CreateInstance(identityType, 2UL, "Friend One", 1U, "World")!;
    var session = Activator.CreateInstance(sessionType, 1UL, owner)!;
    var descriptor = descriptorType.GetMethod("FromFriend")!.Invoke(null, [friend])!;
    var messages = Array.CreateInstance(messageType, 4);
    messages.SetValue(Activator.CreateInstance(messageType, 1L, 1_700_000_000_000L, false, friend, "Hi there", null, chatKind), 0);
    messages.SetValue(Activator.CreateInstance(messageType, 2L, 1_700_000_001_000L, false, friend, "Friend One pats you.", null, systemKind), 1);
    messages.SetValue(Activator.CreateInstance(messageType, 3L, 1_700_000_002_000L, true, owner, "Hello!", null, chatKind), 2);
    messages.SetValue(Activator.CreateInstance(messageType, 4L, 1_700_000_003_000L, true, owner, "You pat Friend One.", null, systemKind), 3);

    var buildUserPrompt = aiReplyType.GetMethod("BuildUserPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
    var prompt = (string)buildUserPrompt.Invoke(null, [session, descriptor, "World", messages, null, 1_700_000_010_000L, null])!;
    Check(prompt.Contains("[System]: Friend One pats you.", StringComparison.Ordinal)
        && prompt.Contains("[System]: You pat Friend One.", StringComparison.Ordinal),
        "AI reply prompt should include both incoming and outgoing system emote messages labeled as [System].");

    var selectWindow = aiReplyType.GetMethod("SelectTranslationWindow", BindingFlags.Static | BindingFlags.NonPublic)!;
    var window = ((IEnumerable)selectWindow.Invoke(null, [messages, 1L])!).Cast<object>().ToArray();
    Check(window.Length == 2 && (long)Get(window[0], "Id")! == 1L && (long)Get(window[1], "Id")! == 3L,
        "Translation window selection must skip both incoming and outgoing system emote messages.");

    var systemAnchorWindow = ((IEnumerable)selectWindow.Invoke(null, [messages, 4L])!).Cast<object>().ToArray();
    Check(systemAnchorWindow.Length == 0,
        "Translation window selection must return empty if anchored on a system message.");

    var silentKind = Enum.ToObject(messageKindType, 2);
    var silentMsg = Activator.CreateInstance(messageType, 5L, 1_700_000_004_000L, false, owner, "Duty clear 10:12", null, silentKind)!;
    Check((bool)Get(silentMsg, "IsSystem")! && (bool)Get(silentMsg, "IsSilentSystem")! && !(bool)Get(silentMsg, "IncludeInAiContext")!,
        "SilentSystem messages must render as system rows while staying excluded from AI context.");

    var withSilent = Array.CreateInstance(messageType, 2);
    withSilent.SetValue(messages.GetValue(0), 0);
    withSilent.SetValue(silentMsg, 1);
    var silentWindow = ((IEnumerable)selectWindow.Invoke(null, [withSilent, 1L])!).Cast<object>().ToArray();
    Check(silentWindow.Length == 1 && (long)Get(silentWindow[0], "Id")! == 1L,
        "Translation window selection must also skip SilentSystem messages.");
}

async Task VerifySilentSystemAndAiExclusionAsync(string root)
{
    var storeRoot = Path.Combine(root, "silent-system");
    await using var store = (IAsyncDisposable)New("MessageStore", storeRoot, null);
    var ownerIdentity = New("PlayerIdentity", 10UL, "Local Player", 72U, "Tonberry");
    var session = New("PlayerSession", 10UL, ownerIdentity);
    var friend = New("PlayerIdentity", 20UL, "Friend One", 72U, "Tonberry");
    var untouchedFriend = New("PlayerIdentity", 21UL, "Untouched Friend", 72U, "Tonberry");
    var convKeyType = TypeOf("ConversationKey");
    var descType = TypeOf("ConversationDescriptor");
    var friendConv = descType.GetMethod("FromFriend")!.Invoke(null, [friend])!;
    var untouchedConv = descType.GetMethod("FromFriend")!.Invoke(null, [untouchedFriend])!;
    var staticKey = convKeyType.GetMethod("Static")!.Invoke(null, ["000000000000000A-0000000000000014"])!;
    var staticConv = Activator.CreateInstance(descType, [staticKey, "Raid Static", 0U, string.Empty])!;

    var chatKind = Enum.ToObject(TypeOf("MessageKind"), 0);
    var systemKind = Enum.ToObject(TypeOf("MessageKind"), 1);
    var silentKind = Enum.ToObject(TypeOf("MessageKind"), 2);

    // SilentSystem on a brand-new conversation must not surface in ReadConversationsAsync,
    // while still being readable from ReadPageAsync and excluded from ReadAiContextAsync.
    var dutyId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, staticConv, ownerIdentity, 5_000L, false, "The Futures Rewritten (Ultimate) clear time 18:42", silentKind), true)!;
    Check(dutyId > 0, "SilentSystem message should persist to messages table.");

    var convsEmptyTask = (Task)Call(store, "ReadConversationsAsync", 10UL)!;
    await convsEmptyTask;
    var summariesEmpty = ((IEnumerable)Get(convsEmptyTask, "Result")!).Cast<object>().ToArray();
    Check(summariesEmpty.Length == 0, "SilentSystem-only conversation must not appear in Chats list summaries.");

    var staticPageTask = (Task)Call(store, "ReadPageAsync", 10UL, staticKey, null)!;
    await staticPageTask;
    var staticPageMsgs = ((IEnumerable)Get(Get(staticPageTask, "Result")!, "Messages")!).Cast<object>().ToArray();
    Check(staticPageMsgs.Length == 1 && (bool)Get(staticPageMsgs[0], "IsSilentSystem")!,
        "SilentSystem message must still be visible when opening the conversation timeline.");

    // Existing conversation: SilentSystem must NOT overwrite last_sent_at/last_message_id,
    // must block 500ms incoming merge across it, and must be excluded from ReadAiContextAsync.
    var chat1Id = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, friend, 1_000L, false, "First line", chatKind), true)!;
    var silentBetweenId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, friend, 1_100L, false, "Silent notice", silentKind), true)!;
    var chat2Id = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, friend, 1_200L, false, "Second line after silent", chatKind), true)!;
    Check(chat2Id > silentBetweenId && silentBetweenId > chat1Id,
        "Incoming chat message within 500ms must not merge across an intervening SilentSystem message.");

    var interactiveId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, ownerIdentity, 2_000L, true, "You defeated Friend One in a duel!", systemKind), true)!;
    var trailingSilentId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, ownerIdentity, 9_000L, true, "Removed from friends.", silentKind), true)!;
    Check(trailingSilentId > interactiveId, "Trailing SilentSystem message should insert with a newer row id.");

    var convsTask = (Task)Call(store, "ReadConversationsAsync", 10UL)!;
    await convsTask;
    var summaries = ((IEnumerable)Get(convsTask, "Result")!).Cast<object>().ToArray();
    Check(summaries.Length == 1
        && (long)Get(Get(summaries[0], "Latest")!, "Id")! == interactiveId
        && (long)Get(summaries[0], "LastSentAt")! == 2_000L,
        "SilentSystem must not update conversations.last_message_id or last_sent_at.");

    var aiCtxTask = (Task)Call(store, "ReadAiContextAsync", 10UL, Get(friendConv, "Key"), 20)!;
    await aiCtxTask;
    var aiCtx = ((IEnumerable)Get(aiCtxTask, "Result")!).Cast<object>().ToArray();
    Check(aiCtx.Length == 3 && aiCtx.All(m => !(bool)Get(m, "IsSilentSystem")!),
        "ReadAiContextAsync must include Chat and interactive System messages while excluding SilentSystem messages.");

    var staticAiTask = (Task)Call(store, "ReadAiContextAsync", 10UL, staticKey, 20)!;
    await staticAiTask;
    var staticAi = ((IEnumerable)Get(staticAiTask, "Result")!).Cast<object>().ToArray();
    Check(staticAi.Length == 0, "Static duty SilentSystem milestones must never be included in AI reply context.");

    // RecordExistingOnlyAsync skips untouched friend and appends to existing friend.
    var skippedId = await (Task<long>)Call(store, "RecordExistingOnlyAsync",
        New("CapturedMessage", session, untouchedConv, ownerIdentity, 9_500L, true, "Removed untouched.", silentKind), false)!;
    Check(skippedId == 0L, "RecordExistingOnlyAsync must skip conversations that do not exist.");

    var existingAppendId = await (Task<long>)Call(store, "RecordExistingOnlyAsync",
        New("CapturedMessage", session, friendConv, ownerIdentity, 9_600L, true, "Removed existing.", silentKind), false)!;
    Check(existingAppendId > trailingSilentId, "RecordExistingOnlyAsync must persist to an existing conversation.");

    // RecordUniqueSystemAsync deduplicates identical SilentSystem body.
    var dup1 = await (Task<long>)Call(store, "RecordUniqueSystemAsync",
        New("CapturedMessage", session, staticConv, ownerIdentity, 10_000L, false, "FC MOTD: Raid at 8pm", silentKind), true)!;
    var dup2 = await (Task<long>)Call(store, "RecordUniqueSystemAsync",
        New("CapturedMessage", session, staticConv, ownerIdentity, 10_500L, false, "FC MOTD: Raid at 8pm", silentKind), true)!;
    Check(dup1 > 0 && dup2 == 0L, "RecordUniqueSystemAsync must skip duplicate SilentSystem bodies.");
}

async Task VerifyRenameAndTransferNoticesAsync(string root)
{
    var storeRoot = Path.Combine(root, "renames");
    await using var store = (IAsyncDisposable)New("MessageStore", storeRoot, null);
    var ownerIdentity = New("PlayerIdentity", 30UL, "Local Player", 72U, "Tonberry");
    var session = New("PlayerSession", 30UL, ownerIdentity);
    var friendOld = New("PlayerIdentity", 40UL, "Old Name", 72U, "Tonberry");
    var untouchedFriend = New("PlayerIdentity", 41UL, "Never Chatted", 72U, "Tonberry");
    var descType = TypeOf("ConversationDescriptor");
    var keyType = TypeOf("ConversationKey");
    var convKindEnum = TypeOf("ConversationKind");
    var channelInfoType = TypeOf("ChannelInfo");
    var chatKind = Enum.ToObject(TypeOf("MessageKind"), 0);

    var friendConv = descType.GetMethod("FromFriend")!.Invoke(null, [friendOld])!;
    var chatId = await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, friendConv, friendOld, 1_000L, false, "Hi", chatKind), true)!;

    var friendRenamed = New("PlayerIdentity", 40UL, "New Name", 63U, "Kujata");
    var untouchedRenamed = New("PlayerIdentity", 41UL, "Still Never Chatted", 63U, "Kujata");
    var identityArray = Array.CreateInstance(TypeOf("PlayerIdentity"), 2);
    identityArray.SetValue(friendRenamed, 0);
    identityArray.SetValue(untouchedRenamed, 1);
    Func<string, string, string, string, string> friendFmt = (on, ow, nn, nw) => $"{on} ({ow}) -> {nn} ({nw})";
    await (Task)Call(store, "UpdateFriendDescriptorsAsync", 30UL, identityArray, friendFmt, 2_000L)!;

    var friendPageTask = (Task)Call(store, "ReadPageAsync", 30UL, Get(friendConv, "Key"), null)!;
    await friendPageTask;
    var friendMsgs = ((IEnumerable)Get(Get(friendPageTask, "Result")!, "Messages")!).Cast<object>().ToArray();
    Check(friendMsgs.Length == 2
        && (bool)Get(friendMsgs[1], "IsSilentSystem")!
        && (string)Get(friendMsgs[1], "Body")! == "Old Name (Tonberry) -> New Name (Kujata)",
        "Friend rename/transfer should append a SilentSystem notice to existing friend conversations.");

    var untouchedPageTask = (Task)Call(store, "ReadPageAsync", 30UL, keyType.GetMethod("Tell")!.Invoke(null, [41UL]), null)!;
    await untouchedPageTask;
    Check(((IEnumerable)Get(Get(untouchedPageTask, "Result")!, "Messages")!).Cast<object>().Count() == 0,
        "Friend rename/transfer must not create empty conversations for untouched friends.");

    var convsAfterFriend = (Task)Call(store, "ReadConversationsAsync", 30UL)!;
    await convsAfterFriend;
    var friendSummary = ((IEnumerable)Get(convsAfterFriend, "Result")!).Cast<object>().Single();
    Check((long)Get(Get(friendSummary, "Latest")!, "Id")! == chatId
        && (string)Get(Get(friendSummary, "Conversation")!, "DisplayName")! == "New Name",
        "Friend rename should update descriptor without bumping last_message_id.");

    // Linkshell placeholder -> real name must NOT create rename notice; real name -> new real name MUST.
    var lsKey = keyType.GetMethod("Linkshell")!.Invoke(null, [9001UL])!;
    var lsPlaceholderConv = Activator.CreateInstance(descType, [lsKey, "Linkshell 1", 0U, string.Empty])!;
    await (Task<long>)Call(store, "RecordAsync",
        New("CapturedMessage", session, lsPlaceholderConv, ownerIdentity, 3_000L, true, "Hello LS", chatKind), false)!;

    Func<string, string, string> lsFmt = (o, n) => $"LS {o} -> {n}";
    var lsFirstReal = Array.CreateInstance(channelInfoType, 1);
    lsFirstReal.SetValue(Activator.CreateInstance(channelInfoType, [lsKey, "Hunt Train", 1, true]), 0);
    await (Task)Call(store, "UpdateChannelNamesAsync", 30UL, Enum.Parse(convKindEnum, "Linkshell"), lsFirstReal, lsFmt, null, 3_500L)!;

    var lsAfterFirstTask = (Task)Call(store, "ReadPageAsync", 30UL, lsKey, null)!;
    await lsAfterFirstTask;
    Check(((IEnumerable)Get(Get(lsAfterFirstTask, "Result")!, "Messages")!).Cast<object>().Count() == 1,
        "Replacing placeholder Linkshell name with first real name must not emit a rename notice.");

    var lsSecondReal = Array.CreateInstance(channelInfoType, 1);
    lsSecondReal.SetValue(Activator.CreateInstance(channelInfoType, [lsKey, "S-Rank Hunters", 1, true]), 0);
    await (Task)Call(store, "UpdateChannelNamesAsync", 30UL, Enum.Parse(convKindEnum, "Linkshell"), lsSecondReal, lsFmt, null, 4_000L)!;

    var lsAfterRenameTask = (Task)Call(store, "ReadPageAsync", 30UL, lsKey, null)!;
    await lsAfterRenameTask;
    var lsMsgs = ((IEnumerable)Get(Get(lsAfterRenameTask, "Result")!, "Messages")!).Cast<object>().ToArray();
    Check(lsMsgs.Length == 2
        && (bool)Get(lsMsgs[1], "IsSilentSystem")!
        && (string)Get(lsMsgs[1], "Body")! == "LS Hunt Train -> S-Rank Hunters",
        "Renaming a Linkshell from one real name to another should record a SilentSystem notice.");

    // Static rename should record a SilentSystem notice without surfacing in Chats list.
    var staticKey = keyType.GetMethod("Static")!.Invoke(null, ["000000000000001E-0000000000000028"])!;
    await (Task)Call(store, "RenameConversationAsync", 30UL, staticKey, "New Static", "Static Old -> New Static", ownerIdentity, 5_000L)!;
    var staticPageTask = (Task)Call(store, "ReadPageAsync", 30UL, staticKey, null)!;
    await staticPageTask;
    var staticMsgs = ((IEnumerable)Get(Get(staticPageTask, "Result")!, "Messages")!).Cast<object>().ToArray();
    Check(staticMsgs.Length == 1
        && (bool)Get(staticMsgs[0], "IsSilentSystem")!
        && (string)Get(staticMsgs[0], "Body")! == "Static Old -> New Static",
        "Static rename should silently persist a SilentSystem notice.");

    var convsAfterStatic = (Task)Call(store, "ReadConversationsAsync", 30UL)!;
    await convsAfterStatic;
    var staticInChats = ((IEnumerable)Get(convsAfterStatic, "Result")!).Cast<object>()
        .Any(s => Get(Get(s, "Conversation")!, "Key")!.Equals(staticKey));
    Check(!staticInChats, "Silent static rename on an empty static must not surface it in Chats list.");
}

async Task VerifySystemLogClassificationAndRoutingAsync(string root)
{
    var chatCapture = TypeOf("ChatCapture");
    var chatTypeEnum = dalamud.GetType("Dalamud.Game.Text.XivChatType", throwOnError: true)!;
    var msgKindEnum = TypeOf("MessageKind");
    var convKindEnum = TypeOf("ConversationKind");
    var tryClassify = chatCapture.GetMethod("TryClassifySystemLog", BindingFlags.Static | BindingFlags.NonPublic)!;

    bool Classify(uint id, string chatTypeName, out string category, out string convKind, out string msgKind)
    {
        var args = new object?[] { id, Enum.Parse(chatTypeEnum, chatTypeName), null };
        var ok = (bool)tryClassify.Invoke(null, args)!;
        if (ok && args[2] is { } spec)
        {
            category = Get(spec, "Category")!.ToString()!;
            convKind = Get(spec, "ConversationKind")!.ToString()!;
            msgKind = Get(spec, "MessageKind")!.ToString()!;
        }
        else
        {
            category = convKind = msgKind = string.Empty;
        }

        return ok;
    }

    Check(Classify(1887, "FreeCompanyAnnouncement", out var c1, out var k1, out var m1) && c1 == "FreeCompanyMember" && k1 == "FreeCompany" && m1 == "SilentSystem", "FC member join must classify as SilentSystem.");
    Check(Classify(3087, "FreeCompanyAnnouncement", out var c2, out _, out var m2) && c2 == "FreeCompanyAnnouncement" && m2 == "SilentSystem", "FC announcement 3087 must classify as SilentSystem.");
    Check(Classify(6065, "MessageBook", out var c3, out var k3, out var m3) && c3 == "FreeCompanyGovernance" && k3 == "FreeCompany" && m3 == "SilentSystem", "FC message book 6065 must classify as FC SilentSystem.");
    Check(Classify(75, "SystemMessage", out var c4, out var k4, out var m4) && c4 == "LinkshellEvent" && k4 == "Linkshell" && m4 == "SilentSystem", "LS member join 75 must classify as SilentSystem.");
    Check(Classify(9284, "SystemMessage", out var c5, out var k5, out var m5) && c5 == "CrossWorldLinkshellEvent" && k5 == "CrossWorldLinkshell" && m5 == "SilentSystem", "CWLS leader promotion 9284 must classify as SilentSystem.");
    Check(Classify(4679, "Progress", out var c6, out var k6, out var m6) && c6 == "StaticDuty" && k6 == "Static" && m6 == "SilentSystem", "Duty clear time 4679 must classify as Static SilentSystem.");
    Check(Classify(1531, "SystemMessage", out var c7, out var k7, out var m7) && c7 == "StaticDuty" && k7 == "Static" && m7 == "SilentSystem", "Duty start 1531 must classify as Static SilentSystem.");
    Check(Classify(932, "SystemMessage", out var c8, out var k8, out var m8) && c8 == "StaticDuty" && k8 == "Static" && m8 == "SilentSystem", "Vote abandon 932 must classify as Static SilentSystem.");
    Check(Classify(7409, "SystemMessage", out var c9, out var k9, out var m9) && c9 == "FriendDuelResult" && k9 == "Tell" && m9 == "System", "Friend duel 7409 must classify as interactive System.");
    Check(Classify(78, "SystemMessage", out var c10, out var k10, out var m10) && c10 == "FriendAdded" && k10 == "Tell" && m10 == "System", "Friend added 78 must classify as interactive System.");
    Check(Classify(12, "SystemMessage", out var c11, out var k11, out var m11) && c11 == "FriendRemoved" && k11 == "Tell" && m11 == "SilentSystem", "Friend removed 12 must classify as SilentSystem.");
    Check(!Classify(3085, "FreeCompanyLoginLogout", out _, out _, out _), "FC login/logout must be excluded.");
    Check(!Classify(5255, "SystemMessage", out _, out _, out _), "Pull countdown must be excluded.");
    Check(!Classify(1291, "Sign", out _, out _, out _), "Target sign must be excluded.");
    Check(!Classify(1231, "LootRoll", out _, out _, out _), "Loot roll must be excluded.");

    // Unique channel name matching rejects duplicates.
    var channelInfoType = TypeOf("ChannelInfo");
    var keyType = TypeOf("ConversationKey");
    var ls1 = Activator.CreateInstance(channelInfoType, [keyType.GetMethod("Linkshell")!.Invoke(null, [1UL]), "Alpha", 1, true])!;
    var ls2 = Activator.CreateInstance(channelInfoType, [keyType.GetMethod("Linkshell")!.Invoke(null, [2UL]), "Alpha", 2, true])!;
    var ls3 = Activator.CreateInstance(channelInfoType, [keyType.GetMethod("Linkshell")!.Invoke(null, [3UL]), "Beta", 3, true])!;
    var lsArr = Array.CreateInstance(channelInfoType, 3);
    lsArr.SetValue(ls1, 0);
    lsArr.SetValue(ls2, 1);
    lsArr.SetValue(ls3, 2);
    var resolveChannel = chatCapture.GetMethod("TryResolveChannelByUniqueName", BindingFlags.Static | BindingFlags.NonPublic)!;
    Check(resolveChannel.Invoke(null, [lsArr, "Alpha"]) is null, "Duplicate-named channels must return null to avoid misrouting.");
    Check(resolveChannel.Invoke(null, [lsArr, "Beta"]) is { } beta && (string)Get(beta, "Name")! == "Beta", "Unique channel name must resolve cleanly.");

    // Duel peer pair resolution: involves local player vs two other players.
    var observedPlayerType = chatCapture.GetNestedType("ObservedPlayer", BindingFlags.NonPublic)!;
    var resolvePair = chatCapture.GetMethod("TryResolveLocalPeerPair", BindingFlags.Static | BindingFlags.NonPublic)!;
    var local = New("PlayerIdentity", 1UL, "Local Player", 72U, "Tonberry");
    var pLocal = Activator.CreateInstance(observedPlayerType, ["Local Player", (ushort)72])!;
    var pFriend = Activator.CreateInstance(observedPlayerType, ["Friend Duel", (ushort)63])!;
    var pOther = Activator.CreateInstance(observedPlayerType, ["Random Stranger", (ushort)72])!;

    var pairArgs1 = new object?[] { pLocal, pFriend, local, null, false };
    Check((bool)resolvePair.Invoke(null, pairArgs1)! && (bool)pairArgs1[4]! && (string)Get(pairArgs1[3]!, "Name")! == "Friend Duel",
        "Local winning duel against peer should resolve peer as outgoing.");

    var pairArgs2 = new object?[] { pFriend, pLocal, local, null, false };
    Check((bool)resolvePair.Invoke(null, pairArgs2)! && !(bool)pairArgs2[4]! && (string)Get(pairArgs2[3]!, "Name")! == "Friend Duel",
        "Peer winning duel against local should resolve peer as incoming.");

    var pairArgs3 = new object?[] { pFriend, pOther, local, null, false };
    Check(!(bool)resolvePair.Invoke(null, pairArgs3)!, "Duel between two non-local players must be ignored.");

    // CacheStore FC announcement deduplication persistence.
    var cacheRoot = Path.Combine(root, "fc-motd-cache");
    await using var cacheStore = (IAsyncDisposable)New("CacheStore", cacheRoot);
    var initialMotdTask = (Task<string?>)Call(cacheStore, "ReadFreeCompanyAnnouncementAsync", 55UL, "00000000000000AA")!;
    Check(await initialMotdTask is null, "Missing FC announcement in cache should return null.");
    await (Task)Call(cacheStore, "SaveFreeCompanyAnnouncementAsync", 55UL, "00000000000000AA", "Welcome raiders!")!;
    var savedMotd = await (Task<string?>)Call(cacheStore, "ReadFreeCompanyAnnouncementAsync", 55UL, "00000000000000AA")!;
    Check(savedMotd == "Welcome raiders!", "Saved FC announcement should round-trip via CacheStore.");
    await (Task)Call(cacheStore, "SaveFreeCompanyAnnouncementAsync", 55UL, "00000000000000AA", "Raid moved to 9pm!")!;
    var updatedMotd = await (Task<string?>)Call(cacheStore, "ReadFreeCompanyAnnouncementAsync", 55UL, "00000000000000AA")!;
    Check(updatedMotd == "Raid moved to 9pm!", "Updated FC announcement should overwrite previous cached MOTD.");
}

Type TypeOf(string name) => plugin.GetType($"FFXIVMomoTalk.Chat.{name}", throwOnError: true)!;
object New(string name, params object?[] arguments) => Activator.CreateInstance(TypeOf(name), arguments)!;
static object? Get(object instance, string property) => instance.GetType().GetProperty(property)!.GetValue(instance);
static object? Call(object instance, string name, params object?[] arguments) =>
    (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public)
        ?? instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic))!.Invoke(instance, arguments);
static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
