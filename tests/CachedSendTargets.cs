#!/usr/bin/env dotnet
// dotnet run --file tests/CachedSendTargets.cs
#:property PublishAot=false
#:include ../src/FFXIVMomoTalk/Chat/ChatModels.cs
using FFXIVMomoTalk.Chat;

var owner = new PlayerSession(1, new PlayerIdentity(1, "Owner", 1, "World"));
var friend = new PlayerIdentity(2, "Friend", 2, "Other World");
var fc = new ChannelInfo(ConversationKey.FreeCompany(3), "Company");
var ls = new ChannelInfo(ConversationKey.Linkshell(4), "Linkshell", Slot: 1);
var cwls = new ChannelInfo(ConversationKey.CrossWorldLinkshell(5), "Cross-world linkshell", Slot: 2);

var cached = SocialSnapshot.Empty(owner) with
{
    Friends = new Dictionary<ulong, PlayerIdentity> { [friend.ContentId] = friend },
    FreeCompany = fc,
    Linkshells = new Dictionary<int, ChannelInfo> { [1] = ls },
    CrossWorldLinkshells = new Dictionary<int, ChannelInfo> { [2] = cwls },
};
ExpectTarget(cached, cwls.Key, "/cwl2");
ExpectTarget(cached, ConversationKey.Tell(friend.ContentId), "/tell Friend@Other World");
ExpectTarget(cached, fc.Key, "/fc");
ExpectTarget(cached, ls.Key, "/l1");

var current = cached with
{
    CrossWorldLinkshells = new Dictionary<int, ChannelInfo> { [4] = cwls with { Slot = 4 } },
    LiveChannels = ChannelAvailability.CrossWorldLinkshells,
};
ExpectTarget(current, cwls.Key, "/cwl4");
ExpectUnavailable(current, ConversationKey.CrossWorldLinkshell(6), "CrossWorldLinkshellLeft");
ExpectUnavailable(SocialSnapshot.Empty(owner), cwls.Key, "CrossWorldLinkshellUnavailable");
ExpectUnavailable(SocialSnapshot.Empty(owner), ConversationKey.Tell(friend.ContentId), "OpenFriendsToSendTell");

var emptyRead = new Dictionary<int, ChannelInfo>();

// 1. Unconfirmed empty poll keeps cached CWLS and avoids wiping cache.
var unconfirmedEmpty = ChannelReconciliation.ReconcileLive(
    cached.CrossWorldLinkshells,
    emptyRead,
    forceConfirm: false,
    emptyConfirmed: false,
    cachedInStore: true);
if (unconfirmedEmpty.IsLive || unconfirmedEmpty.ShouldPersist || unconfirmedEmpty.Channels.Count != 1)
    throw new InvalidOperationException("Unconfirmed empty poll should preserve cached channels without persisting.");

var preserved = cached with { CrossWorldLinkshells = unconfirmedEmpty.Channels };
ExpectTarget(preserved, cwls.Key, "/cwl2");

// 2. Unconfirmed empty poll before cache loads does not mark live or overwrite store.
var coldEmpty = ChannelReconciliation.ReconcileLive(
    emptyRead,
    emptyRead,
    forceConfirm: false,
    emptyConfirmed: false,
    cachedInStore: false);
if (coldEmpty.IsLive || coldEmpty.ShouldPersist)
    throw new InvalidOperationException("Unconfirmed empty poll before cache load should stay unconfirmed.");

// 3. Completed proxy request (EndRequest) confirming empty clears cached CWLS and marks store dirty.
var confirmedEmpty = ChannelReconciliation.ReconcileLive(
    cached.CrossWorldLinkshells,
    emptyRead,
    forceConfirm: true,
    emptyConfirmed: false,
    cachedInStore: true);
if (!confirmedEmpty.IsLive || !confirmedEmpty.ShouldPersist || confirmedEmpty.Channels.Count != 0)
    throw new InvalidOperationException("Confirmed empty refresh should clear cached channels and persist.");

var afterLeave = cached with
{
    CrossWorldLinkshells = confirmedEmpty.Channels,
    LiveChannels = ChannelAvailability.CrossWorldLinkshells,
};
ExpectUnavailable(afterLeave, cwls.Key, "CrossWorldLinkshellLeft");

// 4. Subsequent background poll after confirmed empty stays live without re-persisting.
var steadyEmpty = ChannelReconciliation.ReconcileLive(
    emptyRead,
    emptyRead,
    forceConfirm: false,
    emptyConfirmed: true,
    cachedInStore: true);
if (!steadyEmpty.IsLive || steadyEmpty.ShouldPersist)
    throw new InvalidOperationException("Steady confirmed-empty poll should stay live without rewriting cache.");

// 5. Non-empty live read preserves previously cached real name when live entry lacks one.
var namedCache = new Dictionary<int, ChannelInfo>
{
    [2] = cwls with { HasRealName = true },
};
var unnamedLive = new Dictionary<int, ChannelInfo>
{
    [4] = new(cwls.Key, "CWLS 4", Slot: 4, HasRealName: false),
};
var mergedLive = ChannelReconciliation.ReconcileLive(
    namedCache,
    unnamedLive,
    forceConfirm: false,
    emptyConfirmed: false,
    cachedInStore: true);
if (!mergedLive.IsLive || !mergedLive.ShouldPersist
    || !mergedLive.Channels[4].HasRealName || mergedLive.Channels[4].Name != cwls.Name)
{
    throw new InvalidOperationException("Live read should retain cached real channel name when slot shifts.");
}

Console.WriteLine("Cached send targets, updated slots, missing memberships, and empty-poll guards verified.");

static void ExpectTarget(SocialSnapshot snapshot, ConversationKey key, string prefix)
{
    var target = snapshot.FindSendTarget(key, out var reason);
    if (target?.CommandPrefix != prefix || reason is not null)
        throw new InvalidOperationException($"Expected {prefix} for {key.Kind}, got {target?.CommandPrefix ?? reason}.");
}

static void ExpectUnavailable(SocialSnapshot snapshot, ConversationKey key, string reason)
{
    if (snapshot.FindSendTarget(key, out var actual) is not null || actual != reason)
        throw new InvalidOperationException($"Expected {reason} for {key.Kind}, got {actual}.");
}
