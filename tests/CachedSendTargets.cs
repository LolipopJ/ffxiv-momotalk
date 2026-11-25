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

Console.WriteLine("Cached send targets, updated slots, and missing memberships verified.");

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
