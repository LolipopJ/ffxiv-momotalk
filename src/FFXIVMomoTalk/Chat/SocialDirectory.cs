using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory(
    IClientState clientState,
    IPlayerState playerState,
    ICondition condition,
    IDataManager data,
    IGameInteropProvider interop,
    CacheStore cache,
    StaticStore statics,
    MessageStore messages,
    IPluginLog log) : IDisposable
{
    private SocialSnapshot current = SocialSnapshot.Empty(null);
    private PlayerSession? requestedFor;
    private PlayerSession? loadedFor;
    private PlayerSession? cachedQueryFor;
    private Task<IReadOnlyList<CachedFriend>?>? cachedFriendsQuery;
    private PlayerSession? cachedChannelsQueryFor;
    private Task<CachedChannels>? cachedChannelsQuery;
    private readonly ChannelState[] channels =
    [
        new(ConversationKind.FreeCompany, ChannelAvailability.FreeCompany, InfoProxyId.FreeCompany, requiresHomeWorld: true, "Free Company info"),
        new(ConversationKind.Linkshell, ChannelAvailability.Linkshells, InfoProxyId.Linkshell, requiresHomeWorld: true, "Linkshell list"),
        new(ConversationKind.CrossWorldLinkshell, ChannelAvailability.CrossWorldLinkshells, InfoProxyId.CrossWorldLinkshell, requiresHomeWorld: false, "Cross-World Linkshell list"),
    ];
    private PlayerSession? staticsQueryFor;
    private Task<IReadOnlyList<StaticGroup>>? staticsQuery;
    private long staticsRevision = -1;
    private readonly ConcurrentQueue<(PlayerSession Owner, StaticGroup Group)> confirmedStatics = new();
    private InfoProxyInterface* friendProxy;
    private readonly Dictionary<nint, Hook<InfoProxyInterface.Delegates.RequestData>> requestHooks = new();
    private readonly Dictionary<nint, Hook<InfoProxyInterface.Delegates.EndRequest>> endHooks = new();
    private readonly Dictionary<nint, Hook<InfoProxyInterface.Delegates.ClearListData>> clearListHooks = new();
    private readonly Dictionary<nint, Hook<InfoProxyInterface.Delegates.HandleZoneInitPacket>> zoneInitHooks = new();
    private long lastFriendsRead;
    private long lastSocialRead;
    private bool loggedOut;
    private readonly Dictionary<ulong, PlayerIdentity> syncedPartyMembers = new();

    public SocialSnapshot Current => Volatile.Read(ref current);

    public bool IsPlayerReady(PlayerSession session) =>
        playerState.IsLoaded && playerState.ContentId == session.ContentId
        && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];

    public void Attach()
    {
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        Update(rereadSocial: true);
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        DisposeProxyHooks();
    }

    public void OpenNativeList(PlayerSession owner, ConversationKind kind)
    {
        if (!ReferenceEquals(UpdateSession(), owner) || !IsPlayerReady(owner))
            return;

        var agentId = kind switch
        {
            ConversationKind.Tell => AgentId.Friendlist,
            ConversationKind.FreeCompany => AgentId.FreeCompany,
            ConversationKind.Linkshell => AgentId.Linkshell,
            ConversationKind.CrossWorldLinkshell => AgentId.CrossWorldLinkShell,
            _ => (AgentId?)null,
        };
        var agents = AgentModule.Instance();
        if (agents is null || agentId is not { } id)
            return;

        var agent = agents->GetAgentByInternalId(id);
        if (agent is not null && agent->IsActivatable())
            agent->Show();
    }

    // Called by the framework update, and immediately before handling a chat message, submitting one, or opening context menu.
    public SocialSnapshot Update(bool rereadFriends = false, bool rereadSocial = false)
    {
        var session = UpdateSession();
        if (session is null)
            return Current;

        PollCachedFriends(session);
        PollCachedChannels(session);
        PollStatics(session);

        if (!IsPlayerReady(session))
        {
            foreach (var channel in channels)
                channel.ConfirmedEmptyFor = null;

            var snapshot = Current;
            if (snapshot.LiveChannels != ChannelAvailability.None || snapshot.Party is not null)
                Publish(snapshot with { LiveChannels = ChannelAvailability.None, Party = null });

            lastSocialRead = 0;
            return Current;
        }

        var proxy = (InfoProxyInterface*)InfoProxyFriendList.Instance();
        if (proxy != friendProxy)
            WatchFriendProxy(proxy);

        WatchChannelProxies();

        var shouldReadFriends = rereadFriends
            || lastFriendsRead == 0
            || Stopwatch.GetElapsedTime(lastFriendsRead) >= TimeSpan.FromMilliseconds(500);

        if (shouldReadFriends && !IsInCrossWorldDuty() && friendProxy is not null)
        {
            if (ReferenceEquals(loadedFor, session))
            {
                ReadFriends(session, forcePersist: false);
            }
            else if (requestedFor is null && HasPopulatedFriends(friendProxy))
            {
                loadedFor = session;
                ReadFriends(session, forcePersist: false);
            }
        }

        if (rereadSocial || lastSocialRead == 0
            || Stopwatch.GetElapsedTime(lastSocialRead) >= TimeSpan.FromMilliseconds(500))
        {
            ReadSocialState(session);
        }

        return Current;
    }

    private PlayerSession? UpdateSession()
    {
        if (loggedOut || !clientState.IsLoggedIn)
        {
            if (Current.Session is not null)
                Reset(null);

            return null;
        }

        if (playerState.IsLoaded && playerState.ContentId != 0
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

        // Preserve the active session across loading screens and duty transitions while still logged in.
        return Current.Session;
    }

    private void Reset(PlayerSession? session)
    {
        requestedFor = null;
        loadedFor = null;
        lastFriendsRead = 0;
        lastSocialRead = 0;
        foreach (var channel in channels)
            channel.Reset();
        syncedPartyMembers.Clear();
        cachedQueryFor = session;
        cachedFriendsQuery = session is not null ? cache.ReadCachedFriendsAsync(session.ContentId) : null;
        cachedChannelsQueryFor = session;
        cachedChannelsQuery = session is not null ? cache.ReadCachedChannelsAsync(session.ContentId) : null;
        staticsQueryFor = session;
        staticsRevision = statics.Revision;
        staticsQuery = session is not null ? statics.ReadStaticsAsync(session.ContentId) : null;
        Publish(SocialSnapshot.Empty(session));
    }

    private static bool IsInCrossWorldDuty()
    {
        var module = InfoModule.Instance();
        return module is not null && module->IsInCrossWorldDuty();
    }

    private void ObserveTask(Task task, string message) =>
        _ = task.ContinueWith(
            completed => log.Error(completed.Exception, message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void Publish(SocialSnapshot snapshot) => Volatile.Write(ref current, snapshot);

    private void OnLogin() => loggedOut = false;

    private void OnLogout(int type, int code)
    {
        loggedOut = true;
        Reset(null);
    }

    private void WatchFriendProxy(InfoProxyInterface* proxy)
    {
        friendProxy = proxy;
        requestedFor = null;
        loadedFor = null;
        EnsureProxyHooks(proxy, "friend list");
    }

    private void WatchChannelProxies()
    {
        var module = InfoModule.Instance();
        if (module is null)
            return;

        foreach (var channel in channels)
        {
            var proxy = module->GetInfoProxyById(channel.ProxyId);
            if (proxy == channel.Proxy)
                continue;

            channel.Proxy = proxy;
            channel.RequestedFor = null;
            channel.ConfirmedEmptyFor = null;
            EnsureProxyHooks(proxy, channel.Description);
        }
    }

    private void EnsureProxyHooks(InfoProxyInterface* proxy, string description)
    {
        if (proxy is null || proxy->VirtualTable is null)
            return;

        try
        {
            EnsureHook(clearListHooks, (nint)proxy->VirtualTable->ClearListData,
                getHook => (InfoProxyInterface* self) => OnClearListData(self, getHook()));
            EnsureHook(zoneInitHooks, (nint)proxy->VirtualTable->HandleZoneInitPacket,
                getHook => (InfoProxyInterface* self, ZoneInitPacket* packet) => OnHandleZoneInitPacket(self, packet, getHook()));
            EnsureHook(endHooks, (nint)proxy->VirtualTable->EndRequest,
                getHook => (InfoProxyInterface* self) => OnEndRequest(self, getHook()));
            EnsureHook(requestHooks, (nint)proxy->VirtualTable->RequestData,
                getHook => (InfoProxyInterface* self) => OnRequestData(self, getHook()));
        }
        catch (Exception exception)
        {
            log.Error(exception, "Unable to observe the native {Description}.", description);
        }
    }

    private void EnsureHook<TDelegate>(
        Dictionary<nint, Hook<TDelegate>> hooks,
        nint address,
        Func<Func<Hook<TDelegate>>, TDelegate> createDetour)
        where TDelegate : Delegate
    {
        if (address == 0 || hooks.ContainsKey(address))
            return;

        Hook<TDelegate>? created = null;
        var hook = interop.HookFromAddress(address, createDetour(() => created!));
        created = hook;
        try
        {
            hook.Enable();
        }
        catch
        {
            hook.Dispose();
            throw;
        }

        hooks[address] = hook;
    }

    private void DisposeProxyHooks()
    {
        DisposeHooks(requestHooks);
        DisposeHooks(endHooks);
        DisposeHooks(clearListHooks);
        DisposeHooks(zoneInitHooks);
        friendProxy = null;
        foreach (var channel in channels)
            channel.Proxy = null;
    }

    private static void DisposeHooks<TDelegate>(Dictionary<nint, Hook<TDelegate>> hooks)
        where TDelegate : Delegate
    {
        foreach (var hook in hooks.Values)
            hook.Dispose();
        hooks.Clear();
    }

    private ChannelState? FindChannelByProxy(InfoProxyInterface* proxy)
    {
        if (proxy is null)
            return null;

        foreach (var channel in channels)
        {
            if (channel.Proxy == proxy)
                return channel;
        }

        return null;
    }

    private void OnClearListData(InfoProxyInterface* self, Hook<InfoProxyInterface.Delegates.ClearListData> hook)
    {
        hook.Original(self);
        if (self == friendProxy)
        {
            requestedFor = null;
            loadedFor = null;
        }
        else if (FindChannelByProxy(self) is { } channel)
        {
            channel.ConfirmedEmptyFor = null;
            if (!channel.InRequestData)
                channel.RequestedFor = null;
        }
    }

    private void OnHandleZoneInitPacket(
        InfoProxyInterface* self,
        ZoneInitPacket* packet,
        Hook<InfoProxyInterface.Delegates.HandleZoneInitPacket> hook)
    {
        hook.Original(self, packet);
        if (self == friendProxy)
        {
            requestedFor = null;
            loadedFor = null;
        }
        else if (FindChannelByProxy(self) is { } channel)
        {
            channel.RequestedFor = null;
            channel.ConfirmedEmptyFor = null;
        }
    }

    private bool OnRequestData(InfoProxyInterface* self, Hook<InfoProxyInterface.Delegates.RequestData> hook)
    {
        if (self == friendProxy)
        {
            var session = UpdateSession();
            var previousRequest = requestedFor;
            var previousLoad = loadedFor;
            requestedFor = session;
            loadedFor = null;

            var sent = hook.Original(self);
            if (!sent && ReferenceEquals(requestedFor, session))
            {
                requestedFor = previousRequest;
                loadedFor = previousLoad;
            }

            return sent;
        }

        if (FindChannelByProxy(self) is { } channel)
        {
            var session = UpdateSession();
            var previousRequest = channel.RequestedFor;
            var previousConfirmedEmpty = channel.ConfirmedEmptyFor;
            channel.RequestedFor = session;
            channel.ConfirmedEmptyFor = null;
            channel.InRequestData = true;

            bool sent;
            try
            {
                sent = hook.Original(self);
            }
            finally
            {
                channel.InRequestData = false;
            }

            if (!sent && ReferenceEquals(channel.RequestedFor, session))
            {
                channel.RequestedFor = previousRequest;
                channel.ConfirmedEmptyFor = previousConfirmedEmpty;
            }

            return sent;
        }

        return hook.Original(self);
    }

    private void OnEndRequest(InfoProxyInterface* self, Hook<InfoProxyInterface.Delegates.EndRequest> hook)
    {
        hook.Original(self);
        if (self == friendProxy)
        {
            if (requestedFor is not { } owner || !ReferenceEquals(UpdateSession(), owner))
                return;

            requestedFor = null;
            if (IsInCrossWorldDuty())
                return;

            loadedFor = owner;
            ReadFriends(owner, forcePersist: true);
        }
        else if (FindChannelByProxy(self) is { } channel
            && channel.RequestedFor is { } owner
            && ReferenceEquals(UpdateSession(), owner))
        {
            channel.RequestedFor = null;
            OnChannelRequestEnd(channel, owner);
        }
    }
}
