using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe class FriendDirectory(
    IClientState clientState,
    IPlayerState playerState,
    IDataManager data,
    IGameInteropProvider interop,
    HistoryStore history,
    IPluginLog log) : IDisposable
{
    private FriendSnapshot current = new(null, null);
    private PlayerSession? requestedFor;
    private PlayerSession? loadedFor;
    private PlayerSession? cachedQueryFor;
    private Task<IReadOnlyList<PlayerIdentity>?>? cachedFriendsQuery;
    private InfoProxyInterface* friendProxy;
    private Hook<InfoProxyInterface.Delegates.RequestData>? requestHook;
    private Hook<InfoProxyInterface.Delegates.EndRequest>? endHook;
    private long lastRead;
    private bool loggedOut;

    public FriendSnapshot Current => Volatile.Read(ref current);

    public void Attach()
    {
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        Update();
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        requestHook?.Dispose();
        endHook?.Dispose();
    }

    // Called by the framework update, and immediately before handling a Tell or submitting one.
    public FriendSnapshot Update(bool rereadFriends = false)
    {
        var session = UpdateSession();
        if (session is null)
            return Current;

        PollCachedFriends(session);

        var proxy = (InfoProxyInterface*)InfoProxyFriendList.Instance();
        if (proxy != friendProxy)
            WatchProxy(proxy);

        if (ReferenceEquals(loadedFor, session) && (rereadFriends
            || Stopwatch.GetElapsedTime(lastRead) >= TimeSpan.FromMilliseconds(500)))
        {
            ReadFriends(session, forcePersist: false);
        }

        return Current;
    }

    private PlayerSession? UpdateSession()
    {
        if (!loggedOut && clientState.IsLoggedIn && playerState.IsLoaded && playerState.ContentId != 0
            && playerState.HomeWorld.ValueNullable is { } world && !string.IsNullOrEmpty(playerState.CharacterName))
        {
            if (Current.Session is { } session && session.ContentId == playerState.ContentId)
                return session;

            var player = new PlayerIdentity(playerState.ContentId, playerState.CharacterName, world.RowId,
                world.Name.ToString());
            var next = new PlayerSession(playerState.ContentId, player);
            Reset(next);
            return next;
        }

        if (Current.Session is not null)
            Reset(null);

        return null;
    }

    private void Reset(PlayerSession? session)
    {
        requestedFor = null;
        loadedFor = null;
        cachedQueryFor = session;
        cachedFriendsQuery = session is not null ? history.ReadCachedFriendsAsync(session.ContentId) : null;
        Volatile.Write(ref current, new FriendSnapshot(session, null));
    }

    private void PollCachedFriends(PlayerSession session)
    {
        if (cachedFriendsQuery is not { IsCompleted: true } completed)
            return;

        var owner = cachedQueryFor;
        cachedFriendsQuery = null;
        cachedQueryFor = null;

        if (!ReferenceEquals(owner, session) || ReferenceEquals(loadedFor, session))
            return;

        if (completed.IsCompletedSuccessfully)
        {
            if (completed.Result is not { } cached)
                return;

            var friends = new Dictionary<ulong, PlayerIdentity>(cached.Count);
            foreach (var friend in cached)
                friends[friend.ContentId] = friend;

            Volatile.Write(ref current, new FriendSnapshot(session, friends));
        }
        else if (completed.Exception is { } exception)
        {
            log.Error(exception, "Failed to read cached friends list.");
        }
    }

    private void WatchProxy(InfoProxyInterface* proxy)
    {
        requestHook?.Dispose();
        endHook?.Dispose();
        requestHook = null;
        endHook = null;
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
            endHook.Enable();
            requestHook.Enable();
        }
        catch (Exception exception)
        {
            requestHook?.Dispose();
            endHook?.Dispose();
            requestHook = null;
            endHook = null;
            log.Error(exception, "Unable to observe the native friend list.");
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
        loadedFor = owner;
        ReadFriends(owner, forcePersist: true);
    }

    private void ReadFriends(PlayerSession owner, bool forcePersist)
    {
        lastRead = Stopwatch.GetTimestamp();
        var list = (InfoProxyFriendList*)friendProxy;
        var count = friendProxy->EntryCount;
        if (count > 200 || (count != 0 && list->CharData is null))
            return;

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

        var previous = Current.Friends;
        Volatile.Write(ref current, new FriendSnapshot(owner, friends));

        if (forcePersist || FriendsChanged(previous, friends))
            PersistFriends(owner.ContentId, friends.Values.ToArray());
    }

    private void PersistFriends(ulong owner, IReadOnlyList<PlayerIdentity> friends)
    {
        _ = history.SyncFriendsAsync(owner, friends).ContinueWith(
            task => log.Error(task.Exception, "Failed to update cached friends list."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
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

    private void OnLogin() => loggedOut = false;

    private void OnLogout(int type, int code)
    {
        loggedOut = true;
        Reset(null);
    }
}
