using System.Diagnostics;
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
            foreach (var friend in cached)
                friends[friend.ContentId] = friend;

            Publish(Current with { Friends = friends });
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

    private void WatchProxy(InfoProxyInterface* proxy)
    {
        DisposeFriendHooks();
        friendProxy = proxy;
        requestedFor = null;
        loadedFor = null;
        if (proxy is null)
            return;

        try
        {
            requestHook = interop.HookFromAddress<InfoProxyInterface.Delegates.RequestData>(
                (nint)proxy->VirtualTable->RequestData, OnRequestData);
            endHook = interop.HookFromAddress<InfoProxyInterface.Delegates.EndRequest>(
                (nint)proxy->VirtualTable->EndRequest, OnEndRequest);
            clearListHook = interop.HookFromAddress<InfoProxyInterface.Delegates.ClearListData>(
                (nint)proxy->VirtualTable->ClearListData, OnClearListData);
            zoneInitHook = interop.HookFromAddress<InfoProxyInterface.Delegates.HandleZoneInitPacket>(
                (nint)proxy->VirtualTable->HandleZoneInitPacket, OnHandleZoneInitPacket);
            clearListHook.Enable();
            zoneInitHook.Enable();
            endHook.Enable();
            requestHook.Enable();
        }
        catch (Exception exception)
        {
            DisposeFriendHooks();
            log.Error(exception, "Unable to observe the native friend list.");
        }
    }

    private void DisposeFriendHooks()
    {
        requestHook?.Dispose();
        endHook?.Dispose();
        clearListHook?.Dispose();
        zoneInitHook?.Dispose();
        requestHook = null;
        endHook = null;
        clearListHook = null;
        zoneInitHook = null;
    }

    private void OnClearListData(InfoProxyInterface* self)
    {
        clearListHook!.Original(self);
        if (self == friendProxy)
        {
            requestedFor = null;
            loadedFor = null;
        }
    }

    private void OnHandleZoneInitPacket(InfoProxyInterface* self, FFXIVClientStructs.FFXIV.Client.Game.Network.ZoneInitPacket* packet)
    {
        zoneInitHook!.Original(self, packet);
        if (self == friendProxy)
        {
            requestedFor = null;
            loadedFor = null;
        }
    }

    private bool OnRequestData(InfoProxyInterface* self)
    {
        if (self != friendProxy)
            return requestHook!.Original(self);

        var session = UpdateSession();
        var previousRequest = requestedFor;
        var previousLoad = loadedFor;
        requestedFor = session;
        loadedFor = null;

        var sent = requestHook!.Original(self);
        if (!sent && ReferenceEquals(requestedFor, session))
        {
            requestedFor = previousRequest;
            loadedFor = previousLoad;
        }

        return sent;
    }

    private void OnEndRequest(InfoProxyInterface* self)
    {
        endHook!.Original(self);
        if (self != friendProxy || requestedFor is not { } owner || !ReferenceEquals(UpdateSession(), owner))
            return;

        requestedFor = null;
        if (IsInCrossWorldDuty())
            return;

        loadedFor = owner;
        ReadFriends(owner, forcePersist: true);
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

        var worlds = data.GetExcelSheet<World>();
        var friends = new Dictionary<ulong, PlayerIdentity>((int)count);
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
            friends.TryAdd(friend.ContentId, friend);
        }

        if (!forcePersist && friends.Count == 0)
        {
            loadedFor = null;
            return;
        }

        var previous = Current.Friends;
        Publish(Current with { Friends = friends });

        if (forcePersist || FriendsChanged(previous, friends))
            PersistFriends(owner.ContentId, friends.Values.ToArray());
    }

    private void PersistFriends(ulong owner, IReadOnlyList<PlayerIdentity> friends)
    {
        if (cache.IncompatibleOwner != owner)
            ObserveTask(cache.SyncFriendsAsync(owner, friends), "Failed to update cached friends list.");

        if (friends.Count > 0)
        {
            if (statics.IncompatibleOwner != owner)
                ObserveTask(statics.SyncMemberIdentitiesAsync(owner, friends), "Failed to update static member names.");

            ObserveTask(messages.UpdateFriendDescriptorsAsync(owner, friends),
                "Failed to update friend conversation names.");
        }
    }

    private static bool FriendsChanged(
        IReadOnlyDictionary<ulong, PlayerIdentity>? previous,
        Dictionary<ulong, PlayerIdentity> next)
    {
        if (previous is null || previous.Count != next.Count)
            return true;

        foreach (var (cid, friend) in next)
        {
            if (!previous.TryGetValue(cid, out var old) || old != friend)
                return true;
        }

        return false;
    }
}
