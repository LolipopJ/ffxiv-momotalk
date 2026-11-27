using System.Diagnostics;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory
{
    private void PollCachedFriends(PlayerSession session)
    {
        if (cachedFriendsQuery is not { IsCompleted: true } completed)
            return;

        var owner = cachedQueryFor;
        cachedFriendsQuery = null;
        cachedQueryFor = null;

        if (!ReferenceEquals(owner, session) || ReferenceEquals(loadedFor, session) || Current.Friends is not null)
            return;

        if (completed.IsCompletedSuccessfully)
        {
            if (completed.Result is not { } cached)
                return;

            var friends = new Dictionary<ulong, PlayerIdentity>(cached.Count);
            var addedAt = new Dictionary<ulong, long>();
            foreach (var entry in cached)
            {
                friends[entry.Player.ContentId] = entry.Player;
                if (entry.AddedAt > 0)
                    addedAt[entry.Player.ContentId] = entry.AddedAt;
            }

            Publish(Current with { Friends = friends, FriendAddedAt = addedAt });
        }
        else if (completed.Exception is { } exception)
        {
            log.Error(exception, "Failed to read cached friends list.");
        }
    }

    private static bool HasPopulatedFriends(InfoProxyInterface* proxy)
    {
        var list = (InfoProxyFriendList*)proxy;
        return proxy->EntryCount is > 0 and <= 200 && list->CharData is not null;
    }

    private void ReadFriends(PlayerSession owner, bool forcePersist)
    {
        lastFriendsRead = Stopwatch.GetTimestamp();
        if (IsInCrossWorldDuty())
            return;

        var list = (InfoProxyFriendList*)friendProxy;
        var count = friendProxy->EntryCount;
        if (count > 200 || (count != 0 && list->CharData is null))
            return;

        // Avoid overwriting cached friends with an empty buffer during background polls (e.g. duty/zone resets).
        if (!forcePersist && count == 0 && Current.Friends is { Count: > 0 })
        {
            loadedFor = null;
            return;
        }

        const int minValidAddedAt = 946_684_800; // 2000-01-01T00:00:00Z
        var worlds = data.GetExcelSheet<World>();
        var friends = new Dictionary<ulong, PlayerIdentity>((int)count);
        var friendAddedAt = new Dictionary<ulong, long>();
        var timestamps = list->TimeStamps;
        var previousAddedAt = Current.FriendAddedAt;
        foreach (ref readonly var entry in list->CharDataSpan)
        {
            if (entry.ContentId == 0
                || entry.WaitingForFriendListApproval
                || (entry.State & InfoProxyCommonList.CharacterData.OnlineStatus.WaitingForFriendListApproval) != 0
                || !worlds.TryGetRow(entry.HomeWorld, out var world) || !world.IsPublic)
                continue;

            var name = entry.NameString;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var friend = new PlayerIdentity(entry.ContentId, name, world.RowId, world.Name.ToString());
            if (!friends.TryAdd(friend.ContentId, friend))
                continue;

            long addedAt = 0;
            if (entry.Sort < timestamps.Length && timestamps[entry.Sort] >= minValidAddedAt)
                addedAt = timestamps[entry.Sort];
            else if (!forcePersist && previousAddedAt is not null && previousAddedAt.TryGetValue(entry.ContentId, out var prevTs)
                && prevTs >= minValidAddedAt)
                addedAt = prevTs;

            if (addedAt > 0)
                friendAddedAt[friend.ContentId] = addedAt;
        }

        if (!forcePersist && friends.Count == 0)
        {
            loadedFor = null;
            return;
        }

        var previous = Current.Friends;
        Publish(Current with { Friends = friends, FriendAddedAt = friendAddedAt });

        if (forcePersist || FriendsChanged(previous, previousAddedAt, friends, friendAddedAt))
            PersistFriends(owner.ContentId, friends, friendAddedAt);
    }

    private void PersistFriends(
        ulong owner,
        Dictionary<ulong, PlayerIdentity> friends,
        Dictionary<ulong, long> friendAddedAt)
    {
        if (cache.IncompatibleOwner != owner)
        {
            var cachedFriends = friends.Values
                .Select(friend => new CachedFriend(friend, friendAddedAt.GetValueOrDefault(friend.ContentId)))
                .ToArray();
            ObserveTask(cache.SyncFriendsAsync(owner, cachedFriends), "Failed to update cached friends list.");
        }

        if (friends.Count > 0)
        {
            var identities = friends.Values.ToArray();
            if (statics.IncompatibleOwner != owner)
                ObserveTask(statics.SyncMemberIdentitiesAsync(owner, identities), "Failed to update static member names.");

            var culture = Loc.Culture;
            ObserveTask(
                messages.UpdateFriendDescriptorsAsync(
                    owner,
                    identities,
                    (oldName, oldWorld, newName, newWorld) => FormatFriendChangeNotice(culture, oldName, oldWorld, newName, newWorld)),
                "Failed to update friend conversation names.");
        }
    }

    internal static string FormatFriendChangeNotice(
        CultureInfo culture,
        string oldName,
        string oldWorld,
        string newName,
        string newWorld)
    {
        var nameChanged = !string.IsNullOrWhiteSpace(oldName)
            && !StringComparer.Ordinal.Equals(oldName, newName);
        var worldChanged = !string.IsNullOrWhiteSpace(oldWorld)
            && !string.IsNullOrWhiteSpace(newWorld)
            && !StringComparer.OrdinalIgnoreCase.Equals(oldWorld, newWorld);

        if (nameChanged && worldChanged)
            return string.Format(culture, Loc.TextFor(culture, "FriendRenamedAndTransferred"), oldName, oldWorld, newName, newWorld);
        if (nameChanged)
            return string.Format(culture, Loc.TextFor(culture, "FriendRenamed"), oldName, newName);
        if (worldChanged)
            return string.Format(culture, Loc.TextFor(culture, "FriendWorldTransferred"), newName, oldWorld, newWorld);

        return string.Empty;
    }

    private static bool FriendsChanged(
        IReadOnlyDictionary<ulong, PlayerIdentity>? previous,
        IReadOnlyDictionary<ulong, long>? previousAddedAt,
        Dictionary<ulong, PlayerIdentity> next,
        Dictionary<ulong, long> nextAddedAt)
    {
        if (previous is null || previous.Count != next.Count)
            return true;

        foreach (var (cid, friend) in next)
        {
            if (!previous.TryGetValue(cid, out var old) || old != friend)
                return true;

            var oldTs = previousAddedAt?.GetValueOrDefault(cid) ?? 0;
            var newTs = nextAddedAt.GetValueOrDefault(cid);
            if (oldTs != newTs)
                return true;
        }

        return false;
    }
}
