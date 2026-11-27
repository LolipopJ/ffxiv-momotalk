#!/usr/bin/env dotnet
// dotnet run --file tests/TellReconciliation.cs after building the plugin in Debug.
#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.8
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

using System.Collections;
using System.Diagnostics;
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

const string body = "Same text";
const string busy = "TellRecipientBusy";
const string timeout = "TellFailedCheckGameChat";
var plugin = Assembly.LoadFrom(pluginPath);
var directory = Directory.CreateTempSubdirectory("momotalk-tells-");
try
{
    await using var store = (IAsyncDisposable)New("MessageStore", directory.FullName);
    var owner = New("PlayerIdentity", 1UL, "Local Player", 1U, "World A");
    var session = New("PlayerSession", 1UL, owner);
    var peer = New("PlayerIdentity", 2UL, "Example Name", 1U, "World A");
    var conversation = TypeOf("ConversationDescriptor").GetMethod("FromFriend")!.Invoke(null, [peer])!;
    var key = Get(conversation, "Key");
    var target = New("SendTarget", key, "/tell Example Name@World A", peer);
    var capture = New("ChatCapture", null, null, null, store, null, null);
    var social = New("SocialDirectory", null, null, null, null, null, null, null, null, null);
    var snapshot = TypeOf("SocialSnapshot").GetMethod("Empty")!.Invoke(null, [session]);
    SetField(social, "current", snapshot);
    var sender = New("ChatSender", null, null, social, capture, null);
    var pending = (IList)sender.GetType().GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
    var pendingType = sender.GetType().GetNestedType("PendingMessage", BindingFlags.NonPublic)!;

    var rejected = Submit();
    Call(sender, "Reject", rejected, busy);
    var retry = Submit();
    var outgoing = New("CapturedMessage", session, conversation, owner, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, body);
    var echo = (Task<long>)Call(store, "RecordAsync", outgoing, true)!;
    Call(sender, "OnCaptured", outgoing, echo);
    Check(Get(retry, "Echo") == echo && Get(rejected, "Echo") is null,
        "The immediate outgoing echo must belong to the current submission.");
    SetField(sender, "submitting", null);
    Call(sender, "Update", session);
    var retryHistory = await ReadHistoryAsync();
    Check(retryHistory.Length == 2 && retryHistory.Any(row => (string?)Get(row, "Rejection") == busy)
        && Get(retryHistory.Single(row => (long)Get(row, "Id")! == echo.Result), "Rejection") is null,
        "The failed first attempt and successful echo must both survive a history reload.");

    var expired = Submit();
    expired.GetType().GetProperty("StartedAt")!.SetValue(expired, Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
    SetField(sender, "submitting", null);
    Call(sender, "Update", session);
    Check((await ReadHistoryAsync()).Count(row => (string?)Get(row, "Rejection") == timeout) == 1,
        "A submitted Tell that never echoes must be saved with a timeout reason.");

    var loadContext = (Task)Call(store, "ReadAiContextAsync", 1UL, key, 5)!;
    await loadContext;
    var aiContext = ((IEnumerable)Get(loadContext, "Result")!).Cast<object>().Single();
    Check((long)Get(aiContext, "Id")! == echo.Result, "AI context must skip failed Tell attempts.");

    pending.Clear();
    var active = Submit();
    var otherWorld = New("TellPeer", "Example Name", 0U, "World B", false);
    Call(sender, "HandleRejection", session, 50U, otherWorld, true, false, busy);
    Check(Get(active, "Rejection") is null, "A named rejection from another world must leave the active Tell alone.");
    var otherWorldId = New("TellPeer", "Example Name", 2U, null, true);
    Call(sender, "HandleRejection", session, 50U, otherWorldId, true, false, busy);
    Check(Get(active, "Rejection") is null, "A native world ID mismatch must leave the active Tell alone.");
    var rightWorld = New("TellPeer", "Example Name", 1U, null, true);
    Call(sender, "HandleRejection", session, 50U, rightWorld, true, false, busy);
    Check(Get(active, "Rejection") is not null, "A matching native world ID must still resolve a pending Tell.");
    SetField(sender, "submitting", null);

    Console.WriteLine("Tell echo priority, failed-attempt history, and rejection world matching passed.");

    object Submit()
    {
        var attempt = Activator.CreateInstance(pendingType, session, target, body)!;
        attempt.GetType().GetProperty("StartedAt")!.SetValue(attempt, Stopwatch.GetTimestamp());
        attempt.GetType().GetProperty("SentAt")!.SetValue(attempt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        pending.Add(attempt);
        SetField(sender, "submitting", attempt);
        return attempt;
    }

    async Task<object[]> ReadHistoryAsync()
    {
        var load = (Task)Call(store, "ReadPageAsync", 1UL, key, null)!;
        await load;
        return ((IEnumerable)Get(Get(load, "Result")!, "Messages")!).Cast<object>().ToArray();
    }
}
finally
{
    directory.Delete(recursive: true);
}

Type TypeOf(string name) => plugin.GetType($"FFXIVMomoTalk.Chat.{name}", throwOnError: true)!;
object New(string name, params object?[] arguments) => Activator.CreateInstance(TypeOf(name), arguments)!;

static object? Get(object instance, string property) => instance.GetType().GetProperty(property)!.GetValue(instance);

static void SetField(object instance, string name, object? value) =>
    instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

static object? Call(object instance, string name, params object?[] arguments) =>
    (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public)
        ?? instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic))!.Invoke(instance, arguments);

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
