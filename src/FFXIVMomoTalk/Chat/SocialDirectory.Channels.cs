using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory
{
    private static readonly Dictionary<int, ChannelInfo> EmptyChannels = new();

    private void OnChannelRequestEnd(ChannelState channel, PlayerSession owner)
    {
        if (!IsPlayerReady(owner) || IsInCrossWorldDuty())
            return;

        var canRead = channel.RequiresHomeWorld ? IsOnHomeWorld() : IsOnHomeDataCenter();
        if (!canRead)
            return;

        ReadSocialState(owner, confirmedChannel: channel.Kind);
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
            var snapshot = Current;
            if (!ReferenceEquals(snapshot.Session, session))
                return;

            var nextSnapshot = snapshot;
            var changed = false;
            List<(ConversationKind Kind, IReadOnlyList<ChannelInfo> Channels, IReadOnlyDictionary<ConversationKey, string>? PreviousNames)>? toPersist = null;

            foreach (var channel in channels)
            {
                var (hasCached, cachedMap) = GetCachedChannels(cached, channel.Kind);
                channel.CachedInStore = hasCached;

                var currentMap = GetSnapshotChannels(snapshot, channel.Kind);
                var nextMap = channel.LoadedLive
                    ? ChannelReconciliation.MergeNames(currentMap, cachedMap)
                    : cachedMap;

                if (ChannelReconciliation.ChannelsChanged(currentMap, nextMap))
                {
                    nextSnapshot = WithChannels(nextSnapshot, channel.Kind, nextMap);
                    changed = true;
                }

                if (channel.LoadedLive && (!channel.CachedInStore || ChannelReconciliation.ChannelsChanged(cachedMap, nextMap)))
                {
                    channel.CachedInStore = true;
                    toPersist ??= [];
                    toPersist.Add((channel.Kind, nextMap.Values.ToArray(), CollectRealChannelNames(cachedMap)));
                }
            }

            if (changed)
                Publish(nextSnapshot);

            if (toPersist is not null)
            {
                foreach (var (kind, list, previousNames) in toPersist)
                    PersistChannels(session.ContentId, kind, list, previousNames);
            }
        }
        else if (completed.Exception is { } exception)
        {
            // Prevent repeated write attempts every 500 ms when the cache store failed to open (e.g. schema mismatch).
            foreach (var channel in channels)
                channel.CachedInStore = true;
            log.Error(exception, "Failed to read cached group channels.");

            var snapshot = Current;
            if (ReferenceEquals(snapshot.Session, session))
            {
                foreach (var channel in channels)
                {
                    if (!channel.LoadedLive)
                        continue;

                    var currentChannels = GetSnapshotChannels(snapshot, channel.Kind);
                    if (currentChannels.Count > 0)
                        UpdateMessageChannelNames(session.ContentId, channel.Kind, currentChannels.Values.ToArray());
                }
            }
        }
    }

    private void ReadSocialState(PlayerSession owner, ConversationKind? confirmedChannel = null)
    {
        lastSocialRead = Stopwatch.GetTimestamp();

        var ui = UIModule.Instance();
        var canReadMembership = ui is not null && ui->IsUIReady() && !IsInCrossWorldDuty();
        var readHomeWorld = canReadMembership && IsOnHomeWorld();
        var readHomeDataCenter = canReadMembership && IsOnHomeDataCenter();
        var party = ReadActiveParty(owner);

        var snapshot = Current;
        if (!ReferenceEquals(snapshot.Session, owner))
            return;

        var liveChannels = ChannelAvailability.None;
        var nextSnapshot = snapshot;
        var changed = false;
        List<(ConversationKind Kind, IReadOnlyList<ChannelInfo> Channels, IReadOnlyDictionary<ConversationKey, string>? PreviousNames)>? toPersist = null;

        foreach (var channel in channels)
        {
            var canRead = channel.RequiresHomeWorld ? readHomeWorld : readHomeDataCenter;
            if (!canRead)
            {
                channel.ConfirmedEmptyFor = null;
                continue;
            }

            var live = ReadChannels(channel.Kind);
            if (live is null)
            {
                channel.ConfirmedEmptyFor = null;
                continue;
            }

            var currentMap = GetSnapshotChannels(snapshot, channel.Kind);
            var reconciled = ChannelReconciliation.ReconcileLive(
                currentMap,
                live,
                forceConfirm: confirmedChannel == channel.Kind,
                emptyConfirmed: ReferenceEquals(channel.ConfirmedEmptyFor, owner),
                channel.CachedInStore);

            if (!reconciled.IsLive)
                continue;

            liveChannels |= channel.Availability;
            channel.LoadedLive = true;
            channel.ConfirmedEmptyFor = reconciled.Channels.Count == 0 ? owner : null;

            if (ChannelReconciliation.ChannelsChanged(currentMap, reconciled.Channels))
            {
                nextSnapshot = WithChannels(nextSnapshot, channel.Kind, reconciled.Channels);
                changed = true;
            }

            if (cachedChannelsQuery is null && reconciled.ShouldPersist)
            {
                channel.CachedInStore = true;
                toPersist ??= [];
                toPersist.Add((channel.Kind, reconciled.Channels.Values.ToArray(), CollectRealChannelNames(currentMap)));
            }
        }

        if (snapshot.LiveChannels != liveChannels || changed || PartyChanged(snapshot.Party, party))
            Publish(nextSnapshot with { Party = party, LiveChannels = liveChannels });

        if (toPersist is not null)
        {
            foreach (var (kind, list, previousNames) in toPersist)
                PersistChannels(owner.ContentId, kind, list, previousNames);
        }

        if (party is not null)
            SyncPartyMembers(owner, party);
    }

    private static Dictionary<int, ChannelInfo>? ReadChannels(ConversationKind kind) => kind switch
    {
        ConversationKind.FreeCompany => ReadFreeCompany(),
        ConversationKind.Linkshell => ReadLinkshells(),
        ConversationKind.CrossWorldLinkshell => ReadCrossWorldLinkshells(),
        _ => null,
    };

    private static Dictionary<int, ChannelInfo>? ReadFreeCompany()
    {
        var fc = InfoProxyFreeCompany.Instance();
        if (fc is null)
            return null;

        if (fc->Id == 0)
            return EmptyChannels;

        var name = fc->NameString.Trim();
        var hasRealName = name.Length > 0;
        return SingleChannel(new ChannelInfo(
            ConversationKey.FreeCompany(fc->Id),
            hasRealName ? name : "Free Company",
            Slot: null,
            HasRealName: hasRealName));
    }

    private static Dictionary<int, ChannelInfo>? ReadLinkshells()
    {
        var proxy = InfoProxyLinkshell.Instance();
        if (proxy is null)
            return null;

        var result = new Dictionary<int, ChannelInfo>();
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

    private static Dictionary<int, ChannelInfo>? ReadCrossWorldLinkshells()
    {
        var proxy = InfoProxyCrossWorldLinkshell.Instance();
        if (proxy is null)
            return null;

        var result = new Dictionary<int, ChannelInfo>();
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

    private static IReadOnlyDictionary<int, ChannelInfo> GetSnapshotChannels(SocialSnapshot snapshot, ConversationKind kind) => kind switch
    {
        ConversationKind.FreeCompany => snapshot.FreeCompany is { } fc ? SingleChannel(fc) : EmptyChannels,
        ConversationKind.Linkshell => snapshot.Linkshells,
        ConversationKind.CrossWorldLinkshell => snapshot.CrossWorldLinkshells,
        _ => EmptyChannels,
    };

    private static (bool HasCached, IReadOnlyDictionary<int, ChannelInfo> Channels) GetCachedChannels(
        CachedChannels cached,
        ConversationKind kind) => kind switch
        {
            ConversationKind.FreeCompany => (cached.HasFreeCompany, cached.FreeCompany is { } fc ? SingleChannel(fc) : EmptyChannels),
            ConversationKind.Linkshell => (cached.HasLinkshells, cached.Linkshells),
            ConversationKind.CrossWorldLinkshell => (cached.HasCrossWorldLinkshells, cached.CrossWorldLinkshells),
            _ => (false, EmptyChannels),
        };

    private static SocialSnapshot WithChannels(
        SocialSnapshot snapshot,
        ConversationKind kind,
        IReadOnlyDictionary<int, ChannelInfo> next) => kind switch
        {
            ConversationKind.FreeCompany => snapshot with { FreeCompany = next.Values.FirstOrDefault() },
            ConversationKind.Linkshell => snapshot with { Linkshells = next },
            ConversationKind.CrossWorldLinkshell => snapshot with { CrossWorldLinkshells = next },
            _ => snapshot,
        };

    private static Dictionary<int, ChannelInfo> SingleChannel(ChannelInfo channel) => new() { [0] = channel };

    private void PersistChannels(
        ulong owner,
        ConversationKind kind,
        IReadOnlyList<ChannelInfo> channels,
        IReadOnlyDictionary<ConversationKey, string>? previousNames = null)
    {
        if (cache.IncompatibleOwner != owner)
            ObserveTask(cache.SyncCachedChannelsAsync(owner, kind, channels), "Failed to update cached group channels.");

        UpdateMessageChannelNames(owner, kind, channels, previousNames);
    }

    private void UpdateMessageChannelNames(
        ulong owner,
        ConversationKind kind,
        IReadOnlyList<ChannelInfo> channels,
        IReadOnlyDictionary<ConversationKey, string>? previousNames = null)
    {
        if (!channels.Any(channel => channel.HasRealName))
            return;

        var culture = Loc.Culture;
        var resourceKey = kind switch
        {
            ConversationKind.FreeCompany => "FreeCompanyRenamed",
            ConversationKind.Linkshell => "LinkshellRenamed",
            ConversationKind.CrossWorldLinkshell => "CrossWorldLinkshellRenamed",
            _ => null,
        };
        Func<string, string, string>? formatRename = resourceKey is null
            ? null
            : (oldName, newName) => string.Format(culture, Loc.TextFor(culture, resourceKey), oldName, newName);

        ObserveTask(
            messages.UpdateChannelNamesAsync(owner, kind, channels, formatRename, previousNames),
            "Failed to update group conversation names.");
    }

    private static Dictionary<ConversationKey, string>? CollectRealChannelNames(IReadOnlyDictionary<int, ChannelInfo> channels)
    {
        if (channels.Count == 0)
            return null;

        Dictionary<ConversationKey, string>? names = null;
        foreach (var channel in channels.Values)
        {
            if (!channel.HasRealName || string.IsNullOrWhiteSpace(channel.Name))
                continue;

            names ??= new Dictionary<ConversationKey, string>(channels.Count);
            names[channel.Key] = channel.Name;
        }

        return names;
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

    private sealed class ChannelState(
        ConversationKind kind,
        ChannelAvailability availability,
        InfoProxyId proxyId,
        bool requiresHomeWorld,
        string description)
    {
        public ConversationKind Kind { get; } = kind;

        public ChannelAvailability Availability { get; } = availability;

        public InfoProxyId ProxyId { get; } = proxyId;

        public bool RequiresHomeWorld { get; } = requiresHomeWorld;

        public string Description { get; } = description;

        public InfoProxyInterface* Proxy { get; set; }

        public PlayerSession? RequestedFor { get; set; }

        public PlayerSession? ConfirmedEmptyFor { get; set; }

        public bool InRequestData { get; set; }

        public bool LoadedLive { get; set; }

        public bool CachedInStore { get; set; }

        public void Reset()
        {
            RequestedFor = null;
            ConfirmedEmptyFor = null;
            InRequestData = false;
            LoadedLive = false;
            CachedInStore = false;
        }
    }
}
