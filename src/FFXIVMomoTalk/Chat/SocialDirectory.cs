using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    private Task<IReadOnlyList<PlayerIdentity>?>? cachedFriendsQuery;
    private PlayerSession? cachedChannelsQueryFor;
    private Task<CachedChannels>? cachedChannelsQuery;
    private bool fcLoadedLive;
    private bool lsLoadedLive;
    private bool cwlsLoadedLive;
    private bool fcCachedInStore;
    private bool lsCachedInStore;
    private bool cwlsCachedInStore;
    private PlayerSession? staticsQueryFor;
    private Task<IReadOnlyList<StaticGroup>>? staticsQuery;
    private long staticsRevision = -1;
    private readonly ConcurrentQueue<(PlayerSession Owner, StaticGroup Group)> confirmedStatics = new();
    private InfoProxyInterface* friendProxy;
    private Hook<InfoProxyInterface.Delegates.RequestData>? requestHook;
    private Hook<InfoProxyInterface.Delegates.EndRequest>? endHook;
    private Hook<InfoProxyInterface.Delegates.ClearListData>? clearListHook;
    private Hook<InfoProxyInterface.Delegates.HandleZoneInitPacket>? zoneInitHook;
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
        DisposeFriendHooks();
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
            var snapshot = Current;
            if (snapshot.LiveChannels != ChannelAvailability.None || snapshot.Party is not null)
                Publish(snapshot with { LiveChannels = ChannelAvailability.None, Party = null });

            lastSocialRead = 0;
            return Current;
        }

        var proxy = (InfoProxyInterface*)InfoProxyFriendList.Instance();
        if (proxy != friendProxy)
            WatchProxy(proxy);

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
        fcLoadedLive = false;
        lsLoadedLive = false;
        cwlsLoadedLive = false;
        fcCachedInStore = false;
        lsCachedInStore = false;
        cwlsCachedInStore = false;
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
}
