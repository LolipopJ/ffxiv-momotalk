#!/usr/bin/env dotnet
// dotnet run --file tests/LiveMergedMessage.cs
#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.8
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
#:include ../src/FFXIVMomoTalk/Chat/ChatModels.cs
#:include ../src/FFXIVMomoTalk/Chat/CharacterSqliteStore.cs
#:include ../src/FFXIVMomoTalk/Chat/MessageStore.cs
#:include ../src/FFXIVMomoTalk/Windows/ConversationHistory.cs

using FFXIVMomoTalk.Chat;
using FFXIVMomoTalk.Windows;

var directory = Directory.CreateTempSubdirectory("momotalk-history-");
try
{
    await using var store = new MessageStore(directory.FullName);
    var owner = new PlayerSession(1, new PlayerIdentity(1, "Owner", 1, "World"));
    var peer = new PlayerIdentity(2, "Friend", 1, "World");
    var conversation = ConversationDescriptor.FromFriend(peer);

    var latestId = await store.RecordAsync(new(owner, conversation, peer, 1_000, false, "Latest"));
    await store.RecordAsync(new(owner, conversation, peer, 0, false, "Earlier"));

    var history = new ConversationHistory(store, owner.ContentId, conversation.Key);
    // This read completes after the history's page request on the same database worker.
    await store.ReadRecentAsync(owner.ContentId, conversation.Key, 1);
    history.Update();

    await store.RecordAsync(new(owner, conversation, peer, 1_400, false, "Merged line"));
    history.Update();
    await store.ReadRecentAsync(owner.ContentId, conversation.Key, 1);
    history.Update();

    if (history.Messages.Count != 2 || history.Messages[^1] is not { SentAt: 1_000, Body: "Latest\nMerged line" } latest
        || latest.Id != latestId)
    {
        throw new InvalidOperationException("Live history missed the merged line in its lower-ID latest bubble.");
    }

    Console.WriteLine("Live history refreshed the lower-ID bubble after an out-of-order write.");
}
finally
{
    directory.Delete(recursive: true);
}
