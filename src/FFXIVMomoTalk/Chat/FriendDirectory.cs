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
    IPluginLog log) : IDisposable
{
    private FriendSnapshot current = new(null, null);
    private PlayerSession? requestedFor;
    private PlayerSession? loadedFor;
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

        var proxy = (InfoProxyInterface*)InfoProxyFriendList.Instance();
        if (proxy != friendProxy)
            WatchProxy(proxy);

        if (ReferenceEquals(loadedFor, session) && (rereadFriends
            || Stopwatch.GetElapsedTime(lastRead) >= TimeSpan.FromMilliseconds(500)))
        {
            ReadFriends(session);
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

            var player = new PlayerIdentity(playerState.CharacterName, world.RowId, world.Name.ToString());
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
        Volatile.Write(ref current, new FriendSnapshot(session, null));
    }

    private void WatchProxy(InfoProxyInterface* proxy)
    {
        requestHook?.Dispose();
        endHook?.Dispose();
        requestHook = null;
        endHook = null;
        friendProxy = proxy;
        Reset(Current.Session);
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
        var previous = Current;
        var previousRequest = requestedFor;
        var previousLoad = loadedFor;
        requestedFor = session;
        loadedFor = null;
        Volatile.Write(ref current, new FriendSnapshot(session, null));

        var sent = requestHook!.Original(self);
        if (!sent && ReferenceEquals(requestedFor, session))
        {
            requestedFor = previousRequest;
            loadedFor = previousLoad;
            Volatile.Write(ref current, previous);
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
        ReadFriends(owner);
    }

    private void ReadFriends(PlayerSession owner)
    {
        lastRead = Stopwatch.GetTimestamp();
        var list = (InfoProxyFriendList*)friendProxy;
        var count = friendProxy->EntryCount;
        if (count > 200 || (count != 0 && list->CharData is null))
        {
            Volatile.Write(ref current, new FriendSnapshot(owner, null));
            return;
        }

        var worlds = data.GetExcelSheet<World>();
        var friends = new Dictionary<PlayerKey, PlayerIdentity>((int)count);
        foreach (ref readonly var entry in list->CharDataSpan)
        {
            if (entry.WaitingForFriendListApproval || (entry.State & InfoProxyCommonList.CharacterData.OnlineStatus.WaitingForFriendListApproval) != 0
                || !worlds.TryGetRow(entry.HomeWorld, out var world) || !world.IsPublic)
                continue;

            var name = entry.NameString;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var friend = new PlayerIdentity(name, world.RowId, world.Name.ToString());
            friends.TryAdd(friend.Key, friend);
        }

        Volatile.Write(ref current, new FriendSnapshot(owner, friends));
    }

    private void OnLogin() => loggedOut = false;

    private void OnLogout(int type, int code)
    {
        loggedOut = true;
        Reset(null);
    }
}
