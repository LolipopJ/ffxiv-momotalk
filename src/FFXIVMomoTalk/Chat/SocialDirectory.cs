using System.Diagnostics;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe class SocialDirectory(
    IClientState clientState,
    IPlayerState playerState,
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

    // Called by the framework update, and immediately before handling a chat message, submitting one, or opening context menu.
    public SocialSnapshot Update(bool rereadFriends = false, bool rereadSocial = false)
    {
        var session = UpdateSession();
        if (session is null)
            return Current;

        PollCachedFriends(session);
        PollCachedChannels(session);
        PollStatics(session);

        if (!playerState.IsLoaded)
            return Current;

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

    public StaticGroup? CreateStatic(PlayerSession owner, ActiveParty party, string rawName)
    {
        var trimmed = rawName.Trim();
        if (trimmed.Length == 0 || !ReferenceEquals(Current.Session, owner) || statics.IncompatibleOwner == owner.ContentId)
            return null;

        var existingStatics = Current.Statics ?? new Dictionary<string, StaticGroup>(StringComparer.Ordinal);
        if (existingStatics.TryGetValue(party.RosterKey, out var existing))
            return existing;

        var group = new StaticGroup(
            party.RosterKey,
            trimmed,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            party.Members);

        var nextStatics = new Dictionary<string, StaticGroup>(existingStatics, StringComparer.Ordinal)
        {
            [group.StableKey] = group,
        };
        Publish(Current with { Statics = nextStatics });

        var createTask = statics.CreateStaticAsync(owner.ContentId, group);
        ObserveTask(createTask, "Failed to save static group.");
        _ = createTask.ContinueWith(
            completed => ObserveTask(
                messages.RenameConversationAsync(owner.ContentId, completed.Result.Key, completed.Result.Name),
                "Failed to update static conversation name."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

        return group;
    }

    public bool RenameStatic(PlayerSession owner, string stableKey, string rawName)
    {
        var trimmed = rawName.Trim();
        if (trimmed.Length == 0 || !ReferenceEquals(Current.Session, owner) || statics.IncompatibleOwner == owner.ContentId)
            return false;

        if (Current.Statics is null || !Current.Statics.TryGetValue(stableKey, out var existing))
            return false;

        if (StringComparer.Ordinal.Equals(existing.Name, trimmed))
            return true;

        var updated = existing with { Name = trimmed };
        var nextStatics = new Dictionary<string, StaticGroup>(Current.Statics, StringComparer.Ordinal)
        {
            [stableKey] = updated,
        };
        Publish(Current with { Statics = nextStatics });

        var renameTask = statics.RenameStaticAsync(owner.ContentId, stableKey, trimmed);
        ObserveTask(renameTask, "Failed to rename static group.");
        _ = renameTask.ContinueWith(
            _ => ObserveTask(
                messages.RenameConversationAsync(owner.ContentId, ConversationKey.Static(stableKey), trimmed),
                "Failed to update static conversation name."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

        return true;
    }

    // Must be called on the Dalamud framework thread.
    public PlayerIdentity? ResolveGroupSender(
        SocialSnapshot snapshot,
        XivChatType chatType,
        ConversationKind kind,
        TellPeer? peer,
        bool isLocalPlayer,
        out bool outgoing)
    {
        outgoing = isLocalPlayer;
        if (snapshot.Session is not { } session)
            return null;

        if (isLocalPlayer)
            return session.Player;

        if (peer is not { } parsed)
            return null;

        var name = parsed.Name.Trim();
        if (name.Length == 0)
            return null;

        var worlds = data.GetExcelSheet<World>();
        var worldId = parsed.WorldId;
        var worldName = worldId != 0 ? ResolveWorldName(worlds, worldId) : string.Empty;

        if (worldId == 0 && !string.IsNullOrWhiteSpace(parsed.World))
        {
            if (TryFindWorldByName(worlds, parsed.World.Trim(), out var matchedWorld))
            {
                worldId = matchedWorld.RowId;
                worldName = matchedWorld.Name.ToString();
            }
            else
            {
                worldName = parsed.World.Trim();
            }
        }

        // In Static/Party, match against current party first before defaulting world.
        var lookupPeer = new TellPeer(name, worldId, worldName.Length > 0 ? worldName : null);
        if (kind == ConversationKind.Static && snapshot.ResolvePartyMember(lookupPeer) is { } partyMember)
        {
            outgoing = partyMember.ContentId == session.ContentId;
            return outgoing ? session.Player : partyMember;
        }

        if (worldId == 0 && (kind is ConversationKind.FreeCompany or ConversationKind.Linkshell or ConversationKind.Static))
        {
            worldId = session.Player.WorldId;
            worldName = session.Player.World;
            lookupPeer = new TellPeer(name, worldId, worldName);
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(name, session.Player.Name.Trim())
            && (worldId == 0 || worldId == session.Player.WorldId))
        {
            outgoing = true;
            return session.Player;
        }

        if (snapshot.ResolvePartyMember(lookupPeer) is { } activePartyMatch)
            return activePartyMatch;

        if (snapshot.ResolveTellPeer(lookupPeer) is { } friendMatch)
            return friendMatch;

        var proxyCid = LookupMemberProxyContentId(kind, name, worldId);
        if (proxyCid != 0)
            return new PlayerIdentity(proxyCid, name, worldId, worldName);

        var logCid = LookupLogSourceContentId(chatType, name, worldId);
        return new PlayerIdentity(logCid, name, worldId, worldName);
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

    private void PollCachedChannels(PlayerSession session)
    {
        if (cachedChannelsQuery is not { IsCompleted: true } completed)
            return;

        var owner = cachedChannelsQueryFor;
        cachedChannelsQuery = null;
        cachedChannelsQueryFor = null;

        if (!ReferenceEquals(owner, session))
            return;

        if (completed.IsCompletedSuccessfully)
        {
            var cached = completed.Result;
            fcCachedInStore = cached.HasFreeCompany;
            lsCachedInStore = cached.HasLinkshells;
            cwlsCachedInStore = cached.HasCrossWorldLinkshells;

            var snapshot = Current;
            if (!ReferenceEquals(snapshot.Session, session))
                return;

            var nextFc = fcLoadedLive
                ? MergeChannelName(snapshot.FreeCompany, cached.FreeCompany)
                : cached.FreeCompany;
            var nextLs = lsLoadedLive
                ? MergeChannelNames(snapshot.Linkshells, cached.Linkshells)
                : cached.Linkshells;
            var nextCwls = cwlsLoadedLive
                ? MergeChannelNames(snapshot.CrossWorldLinkshells, cached.CrossWorldLinkshells)
                : cached.CrossWorldLinkshells;

            if (snapshot.FreeCompany != nextFc
                || ChannelsChanged(snapshot.Linkshells, nextLs)
                || ChannelsChanged(snapshot.CrossWorldLinkshells, nextCwls))
            {
                Publish(snapshot with
                {
                    FreeCompany = nextFc,
                    Linkshells = nextLs,
                    CrossWorldLinkshells = nextCwls,
                });
            }

            if (fcLoadedLive && nextFc is not null && (!fcCachedInStore || cached.FreeCompany != nextFc))
            {
                fcCachedInStore = true;
                PersistChannels(session.ContentId, ConversationKind.FreeCompany, [nextFc]);
            }

            if (lsLoadedLive && nextLs.Count > 0 && (!lsCachedInStore || ChannelsChanged(cached.Linkshells, nextLs)))
            {
                lsCachedInStore = true;
                PersistChannels(session.ContentId, ConversationKind.Linkshell, nextLs.Values.ToArray());
            }

            if (cwlsLoadedLive && nextCwls.Count > 0
                && (!cwlsCachedInStore || ChannelsChanged(cached.CrossWorldLinkshells, nextCwls)))
            {
                cwlsCachedInStore = true;
                PersistChannels(session.ContentId, ConversationKind.CrossWorldLinkshell, nextCwls.Values.ToArray());
            }
        }
        else if (completed.Exception is { } exception)
        {
            // Prevent repeated write attempts every 500 ms when the cache store failed to open (e.g. schema mismatch).
            fcCachedInStore = true;
            lsCachedInStore = true;
            cwlsCachedInStore = true;
            log.Error(exception, "Failed to read cached group channels.");

            var snapshot = Current;
            if (ReferenceEquals(snapshot.Session, session))
            {
                if (fcLoadedLive && snapshot.FreeCompany is { HasRealName: true } liveFc)
                    UpdateMessageChannelNames(session.ContentId, ConversationKind.FreeCompany, [liveFc]);

                if (lsLoadedLive && snapshot.Linkshells.Count > 0)
                    UpdateMessageChannelNames(session.ContentId, ConversationKind.Linkshell, snapshot.Linkshells.Values.ToArray());

                if (cwlsLoadedLive && snapshot.CrossWorldLinkshells.Count > 0)
                {
                    UpdateMessageChannelNames(session.ContentId, ConversationKind.CrossWorldLinkshell,
                        snapshot.CrossWorldLinkshells.Values.ToArray());
                }
            }
        }
    }

    private void PollStatics(PlayerSession session)
    {
        if (staticsQuery is { IsCompleted: true } completed)
        {
            var owner = staticsQueryFor;
            staticsQuery = null;
            staticsQueryFor = null;

            if (ReferenceEquals(owner, session))
            {
                if (completed.IsCompletedSuccessfully)
                {
                    var loadedStatics = new Dictionary<string, StaticGroup>(completed.Result.Count, StringComparer.Ordinal);
                    foreach (var group in completed.Result)
                        loadedStatics[group.StableKey] = group;

                    Publish(Current with { Statics = loadedStatics });
                }
                else if (completed.Exception is { } exception)
                {
                    log.Error(exception, "Failed to read static groups.");
                }
            }
        }

        if (staticsQuery is null && staticsRevision != statics.Revision)
        {
            staticsRevision = statics.Revision;
            staticsQueryFor = session;
            staticsQuery = statics.ReadStaticsAsync(session.ContentId);
        }
    }

    private void ReadSocialState(PlayerSession owner)
    {
        lastSocialRead = Stopwatch.GetTimestamp();

        var inCrossWorldDuty = IsInCrossWorldDuty();
        var onHomeWorld = !inCrossWorldDuty && IsOnHomeWorld();
        var onHomeDataCenter = !inCrossWorldDuty && IsOnHomeDataCenter();

        var liveFc = onHomeWorld ? ReadFreeCompany() : null;
        var liveLs = onHomeWorld ? ReadLinkshells() : null;
        var liveCwls = onHomeDataCenter ? ReadCrossWorldLinkshells() : null;
        var party = ReadActiveParty(owner);

        var snapshot = Current;
        if (!ReferenceEquals(snapshot.Session, owner))
            return;

        var nextFc = snapshot.FreeCompany;
        var persistFc = false;
        if (liveFc is not null)
        {
            nextFc = MergeChannelName(liveFc, snapshot.FreeCompany);
            fcLoadedLive = true;
            persistFc = snapshot.FreeCompany != nextFc || !fcCachedInStore;
        }

        var nextLs = snapshot.Linkshells;
        var persistLs = false;
        if (liveLs is { Count: > 0 })
        {
            nextLs = MergeChannelNames(liveLs, snapshot.Linkshells);
            lsLoadedLive = true;
            persistLs = ChannelsChanged(snapshot.Linkshells, nextLs) || !lsCachedInStore;
        }

        var nextCwls = snapshot.CrossWorldLinkshells;
        var persistCwls = false;
        if (liveCwls is { Count: > 0 })
        {
            nextCwls = MergeChannelNames(liveCwls, snapshot.CrossWorldLinkshells);
            cwlsLoadedLive = true;
            persistCwls = ChannelsChanged(snapshot.CrossWorldLinkshells, nextCwls) || !cwlsCachedInStore;
        }

        if (snapshot.FreeCompany != nextFc
            || ChannelsChanged(snapshot.Linkshells, nextLs)
            || ChannelsChanged(snapshot.CrossWorldLinkshells, nextCwls)
            || PartyChanged(snapshot.Party, party))
        {
            Publish(snapshot with
            {
                FreeCompany = nextFc,
                Linkshells = nextLs,
                CrossWorldLinkshells = nextCwls,
                Party = party,
            });
        }

        if (cachedChannelsQuery is null)
        {
            if (persistFc && nextFc is not null)
            {
                fcCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.FreeCompany, [nextFc]);
            }

            if (persistLs && nextLs.Count > 0)
            {
                lsCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.Linkshell, nextLs.Values.ToArray());
            }

            if (persistCwls && nextCwls.Count > 0)
            {
                cwlsCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.CrossWorldLinkshell, nextCwls.Values.ToArray());
            }
        }

        if (party is not null)
            SyncPartyMembers(owner, party);
    }

    private static ChannelInfo? ReadFreeCompany()
    {
        var fc = InfoProxyFreeCompany.Instance();
        if (fc is null || fc->Id == 0)
            return null;

        var name = fc->NameString.Trim();
        var hasRealName = name.Length > 0;
        return new ChannelInfo(
            ConversationKey.FreeCompany(fc->Id),
            hasRealName ? name : "Free Company",
            Slot: null,
            HasRealName: hasRealName);
    }

    private static Dictionary<int, ChannelInfo> ReadLinkshells()
    {
        var result = new Dictionary<int, ChannelInfo>();
        var proxy = InfoProxyLinkshell.Instance();
        if (proxy is null)
            return result;

        var chatProxy = InfoProxyChat.Instance();
        var shells = proxy->LinkShells;
        for (var i = 0; i < shells.Length; i++)
        {
            ref var entry = ref shells[i];
            if (entry.Id == 0)
                continue;

            var namePtr = proxy->GetLinkshellName(entry.Id);
            var name = namePtr.HasValue ? namePtr.ToString().Trim() : string.Empty;
            if (name.Length == 0 && chatProxy is not null)
            {
                var chatNamePtr = chatProxy->GetLinkShellName((uint)i);
                if (chatNamePtr.HasValue)
                    name = chatNamePtr.ToString().Trim();
            }

            var slot = i + 1;
            var hasRealName = name.Length > 0;
            result[slot] = new ChannelInfo(
                ConversationKey.Linkshell(entry.Id),
                hasRealName ? name : $"Linkshell {slot}",
                slot,
                hasRealName);
        }

        return result;
    }

    private static Dictionary<int, ChannelInfo> ReadCrossWorldLinkshells()
    {
        var result = new Dictionary<int, ChannelInfo>();
        var proxy = InfoProxyCrossWorldLinkshell.Instance();
        if (proxy is null)
            return result;

        var shells = proxy->CrossWorldLinkshells;
        for (var i = 0; i < shells.Length; i++)
        {
            ref var entry = ref shells[i];
            if (entry.FoundationTime == 0)
                continue;

            var name = entry.Name.Length > 0 ? entry.Name.ToString().Trim() : string.Empty;
            if (name.Length == 0)
            {
                var ptr = proxy->GetCrossworldLinkshellName((uint)i);
                if (ptr is not null && ptr->Length > 0)
                    name = ptr->ToString().Trim();
            }

            var slot = i + 1;
            var hasRealName = name.Length > 0;
            result[slot] = new ChannelInfo(
                ConversationKey.CrossWorldLinkshell(entry.FoundationTime),
                hasRealName ? name : $"CWLS {slot}",
                slot,
                hasRealName);
        }

        return result;
    }

    private ActiveParty? ReadActiveParty(PlayerSession session)
    {
        var worlds = data.GetExcelSheet<World>();
        var mainMembers = ReadMainGroupParty(session, worlds);
        var crossMembers = InfoProxyCrossRealm.IsCrossRealmParty()
            ? ReadCrossRealmParty(session, worlds)
            : null;

        var selected = crossMembers is not null && crossMembers.Count > mainMembers.Count
            ? crossMembers
            : mainMembers;

        if (selected.Count < 2 || !selected.ContainsKey(session.ContentId))
            return null;

        var ordered = selected.Values
            .OrderBy(m => m.ContentId == session.ContentId ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ContentId)
            .ToArray();
        return new ActiveParty(ConversationKey.BuildStaticRosterKey(selected.Keys), ordered);
    }

    private static Dictionary<ulong, PlayerIdentity> ReadMainGroupParty(PlayerSession session, ExcelSheet<World> worlds)
    {
        var members = new Dictionary<ulong, PlayerIdentity>();
        var groupManager = GroupManager.Instance();
        if (groupManager is null)
            return members;

        var group = groupManager->GetGroup();
        if (group is null || group->MemberCount == 0)
            return members;

        var count = Math.Min((int)group->MemberCount, group->PartyMembers.Length);
        for (var i = 0; i < count; i++)
        {
            ref var member = ref group->PartyMembers[i];
            if (member.ContentId == 0)
                continue;

            if (member.ContentId == session.ContentId)
            {
                members[session.ContentId] = session.Player;
                continue;
            }

            var name = member.NameString.Trim();
            if (name.Length == 0)
                continue;

            var worldId = (uint)member.HomeWorld;
            members[member.ContentId] = new PlayerIdentity(member.ContentId, name, worldId, ResolveWorldName(worlds, worldId));
        }

        return members;
    }

    private static Dictionary<ulong, PlayerIdentity> ReadCrossRealmParty(PlayerSession session, ExcelSheet<World> worlds)
    {
        var members = new Dictionary<ulong, PlayerIdentity>();
        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm is null)
            return members;

        var groupIndex = crossRealm->LocalPlayerGroupIndex;
        if (groupIndex >= crossRealm->CrossRealmGroups.Length)
            return members;

        ref var group = ref crossRealm->CrossRealmGroups[groupIndex];
        var count = Math.Min((int)group.GroupMemberCount, group.GroupMembers.Length);
        for (var i = 0; i < count; i++)
        {
            ref var member = ref group.GroupMembers[i];
            if (member.ContentId == 0)
                continue;

            if (member.ContentId == session.ContentId)
            {
                members[session.ContentId] = session.Player;
                continue;
            }

            var name = member.NameString.Trim();
            if (name.Length == 0)
                continue;

            var worldId = member.HomeWorld > 0 ? (uint)(ushort)member.HomeWorld : 0u;
            members[member.ContentId] = new PlayerIdentity(member.ContentId, name, worldId, ResolveWorldName(worlds, worldId));
        }

        return members;
    }

    private void PersistChannels(ulong owner, ConversationKind kind, IReadOnlyList<ChannelInfo> channels)
    {
        if (cache.IncompatibleOwner != owner)
            ObserveTask(cache.SyncCachedChannelsAsync(owner, kind, channels), "Failed to update cached group channels.");

        UpdateMessageChannelNames(owner, kind, channels);
    }

    private void UpdateMessageChannelNames(ulong owner, ConversationKind kind, IReadOnlyList<ChannelInfo> channels)
    {
        if (channels.Any(channel => channel.HasRealName))
        {
            ObserveTask(messages.UpdateChannelNamesAsync(owner, kind, channels),
                "Failed to update group conversation names.");
        }
    }

    private static ChannelInfo? MergeChannelName(ChannelInfo? live, ChannelInfo? existing)
    {
        if (live is null)
            return null;

        if (!live.HasRealName && existing is { HasRealName: true } && existing.Key == live.Key)
            return live with { Name = existing.Name, HasRealName = true };

        return live;
    }

    private static Dictionary<int, ChannelInfo> MergeChannelNames(
        IReadOnlyDictionary<int, ChannelInfo> live,
        IReadOnlyDictionary<int, ChannelInfo> existing)
    {
        Dictionary<ConversationKey, ChannelInfo>? existingByKey = null;
        var merged = new Dictionary<int, ChannelInfo>(live.Count);
        foreach (var (slot, channel) in live)
        {
            var resolved = channel;
            if (!channel.HasRealName && existing.Count > 0)
            {
                if (existingByKey is null)
                {
                    existingByKey = new Dictionary<ConversationKey, ChannelInfo>(existing.Count);
                    foreach (var item in existing.Values)
                    {
                        if (item.HasRealName)
                            existingByKey.TryAdd(item.Key, item);
                    }
                }

                if (existingByKey.TryGetValue(channel.Key, out var cached))
                    resolved = channel with { Name = cached.Name, HasRealName = true };
            }

            merged[slot] = resolved;
        }

        return merged;
    }

    private static bool HasPopulatedFriends(InfoProxyInterface* proxy)
    {
        var list = (InfoProxyFriendList*)proxy;
        return proxy->EntryCount is > 0 and <= 200 && list->CharData is not null;
    }

    private bool IsOnHomeWorld() =>
        playerState.HomeWorld.RowId != 0 && playerState.CurrentWorld.RowId == playerState.HomeWorld.RowId;

    private bool IsOnHomeDataCenter()
    {
        if (IsOnHomeWorld())
            return true;

        return playerState.HomeWorld.ValueNullable is { } home
            && playerState.CurrentWorld.ValueNullable is { } currentWorld
            && home.DataCenter.RowId != 0
            && currentWorld.DataCenter.RowId == home.DataCenter.RowId;
    }

    private void SyncPartyMembers(PlayerSession owner, ActiveParty party)
    {
        List<PlayerIdentity>? changed = null;
        foreach (var member in party.Members)
        {
            if (syncedPartyMembers.TryGetValue(member.ContentId, out var prev) && prev == member)
                continue;

            syncedPartyMembers[member.ContentId] = member;
            changed ??= [];
            changed.Add(member);
        }

        if (changed is null || statics.IncompatibleOwner == owner.ContentId)
            return;

        ObserveTask(statics.SyncMemberIdentitiesAsync(owner.ContentId, changed), "Failed to sync static member names.");
    }

    private static ulong LookupMemberProxyContentId(ConversationKind kind, string name, uint worldId)
    {
        var world = (ushort)worldId;
        switch (kind)
        {
            case ConversationKind.FreeCompany:
                return QueryCommonList(InfoProxyFreeCompanyMember.Instance(), name, world);
            case ConversationKind.Linkshell:
                return QueryCommonList(InfoProxyLinkshellMember.Instance(), name, world);
            case ConversationKind.CrossWorldLinkshell:
                return QueryCommonList(InfoProxyCrossWorldLinkshellMember.Instance(), name, world);
            default:
                return 0;
        }
    }

    private static ulong QueryCommonList(InfoProxyFreeCompanyMember* proxy, string name, ushort worldId) =>
        proxy is null || proxy->EntryCount == 0 || proxy->CharData is null
            ? 0
            : QueryCommonListSpan(proxy->CharDataSpan, name, worldId);

    private static ulong QueryCommonList(InfoProxyLinkshellMember* proxy, string name, ushort worldId) =>
        proxy is null || proxy->EntryCount == 0 || proxy->CharData is null
            ? 0
            : QueryCommonListSpan(proxy->CharDataSpan, name, worldId);

    private static ulong QueryCommonList(InfoProxyCrossWorldLinkshellMember* proxy, string name, ushort worldId) =>
        proxy is null || proxy->EntryCount == 0 || proxy->CharData is null
            ? 0
            : QueryCommonListSpan(proxy->CharDataSpan, name, worldId);

    private static ulong QueryCommonListSpan(
        ReadOnlySpan<InfoProxyCommonList.CharacterData> span,
        string name,
        ushort worldId)
    {
        ulong matched = 0;
        foreach (ref readonly var entry in span)
        {
            if (entry.ContentId == 0)
                continue;

            if (worldId != 0 && entry.HomeWorld != worldId)
                continue;

            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.NameString.Trim(), name))
                continue;

            if (matched != 0 && matched != entry.ContentId)
                return 0;

            matched = entry.ContentId;
        }

        return matched;
    }

    private static ulong LookupLogSourceContentId(XivChatType chatType, string senderName, uint worldId)
    {
        var logModule = RaptureLogModule.Instance();
        if (logModule is null || logModule->MsgSourceArray is null)
            return 0;

        var length = logModule->MsgSourceArrayLength;
        if (length is <= 0 or > 4096)
            return 0;

        var currentIndex = (int)logModule->GetCurrentLogIndex();
        var minIndex = Math.Max(0, currentIndex - 4);
        var bestIndex = -1;
        ulong bestCid = 0;

        for (var i = 0; i < length; i++)
        {
            var entry = logModule->MsgSourceArray[i];
            if (entry.ContentId == 0
                || entry.LogMessageIndex < minIndex
                || entry.LogMessageIndex > currentIndex + 1
                || entry.ChatType != (short)chatType)
            {
                continue;
            }

            if (worldId != 0 && entry.World > 0 && (uint)(ushort)entry.World != worldId)
                continue;

            if (entry.LogMessageIndex < bestIndex)
                continue;

            if (logModule->GetLogMessageDetail(entry.LogMessageIndex, out var rawSender, out _, out _, out _, out _, out _)
                && rawSender.Length > 0)
            {
                var parsed = ChatCapture.ReadPeer(SeString.Parse(rawSender));
                if (parsed is null || !StringComparer.OrdinalIgnoreCase.Equals(parsed.Value.Name.Trim(), senderName))
                    continue;
            }

            bestIndex = entry.LogMessageIndex;
            bestCid = entry.ContentId;
        }

        return bestCid;
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

    private static bool IsInCrossWorldDuty()
    {
        var module = InfoModule.Instance();
        return module is not null && module->IsInCrossWorldDuty();
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

    private void ObserveTask(Task task, string message) =>
        _ = task.ContinueWith(
            completed => log.Error(completed.Exception, message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void Publish(SocialSnapshot snapshot) => Volatile.Write(ref current, snapshot);

    private static string ResolveWorldName(ExcelSheet<World> worlds, uint worldId) =>
        worldId != 0 && worlds.TryGetRow(worldId, out var world) ? world.Name.ToString() : string.Empty;

    private static bool TryFindWorldByName(ExcelSheet<World> worlds, string name, out World matched)
    {
        foreach (var world in worlds)
        {
            if (world.IsPublic && StringComparer.OrdinalIgnoreCase.Equals(world.Name.ToString(), name))
            {
                matched = world;
                return true;
            }
        }

        matched = default;
        return false;
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

    private static bool ChannelsChanged(
        IReadOnlyDictionary<int, ChannelInfo> previous,
        IReadOnlyDictionary<int, ChannelInfo> next)
    {
        if (previous.Count != next.Count)
            return true;

        foreach (var (slot, channel) in next)
        {
            if (!previous.TryGetValue(slot, out var old) || old != channel)
                return true;
        }

        return false;
    }

    private static bool PartyChanged(ActiveParty? previous, ActiveParty? next)
    {
        if (previous is null && next is null)
            return false;
        if (previous is null || next is null)
            return true;
        if (!StringComparer.Ordinal.Equals(previous.RosterKey, next.RosterKey)
            || previous.Members.Count != next.Members.Count)
            return true;

        for (var i = 0; i < previous.Members.Count; i++)
        {
            if (previous.Members[i] != next.Members[i])
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
