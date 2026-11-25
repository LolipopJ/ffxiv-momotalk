using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory
{
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

            if (fcLoadedLive && (!fcCachedInStore || cached.FreeCompany != nextFc))
            {
                fcCachedInStore = true;
                PersistChannels(session.ContentId, ConversationKind.FreeCompany, nextFc is null ? [] : [nextFc]);
            }

            if (lsLoadedLive && (!lsCachedInStore || ChannelsChanged(cached.Linkshells, nextLs)))
            {
                lsCachedInStore = true;
                PersistChannels(session.ContentId, ConversationKind.Linkshell, nextLs.Values.ToArray());
            }

            if (cwlsLoadedLive && (!cwlsCachedInStore || ChannelsChanged(cached.CrossWorldLinkshells, nextCwls)))
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

    private void ReadSocialState(PlayerSession owner)
    {
        lastSocialRead = Stopwatch.GetTimestamp();

        var inCrossWorldDuty = IsInCrossWorldDuty();
        var onHomeWorld = !inCrossWorldDuty && IsOnHomeWorld();
        var onHomeDataCenter = !inCrossWorldDuty && IsOnHomeDataCenter();

        var liveLs = onHomeWorld ? ReadLinkshells() : null;
        var liveCwls = onHomeDataCenter ? ReadCrossWorldLinkshells() : null;
        var party = ReadActiveParty(owner);

        var snapshot = Current;
        if (!ReferenceEquals(snapshot.Session, owner))
            return;

        // A new native buffer can be empty before it receives membership data.
        // Only a list that was already live can confirm departures with an empty read.
        var liveChannels = ChannelAvailability.None;
        var nextFc = snapshot.FreeCompany;
        var persistFc = false;
        if (onHomeWorld && TryReadFreeCompany(out var liveFc)
            && (liveFc is not null || snapshot.LiveChannels.HasFlag(ChannelAvailability.FreeCompany)))
        {
            nextFc = MergeChannelName(liveFc, snapshot.FreeCompany);
            liveChannels |= ChannelAvailability.FreeCompany;
            fcLoadedLive = true;
            persistFc = snapshot.FreeCompany != nextFc || !fcCachedInStore;
        }

        var nextLs = snapshot.Linkshells;
        var persistLs = false;
        if (liveLs is not null
            && (liveLs.Count > 0 || snapshot.LiveChannels.HasFlag(ChannelAvailability.Linkshells)))
        {
            nextLs = MergeChannelNames(liveLs, snapshot.Linkshells);
            liveChannels |= ChannelAvailability.Linkshells;
            lsLoadedLive = true;
            persistLs = ChannelsChanged(snapshot.Linkshells, nextLs) || !lsCachedInStore;
        }

        var nextCwls = snapshot.CrossWorldLinkshells;
        var persistCwls = false;
        if (liveCwls is not null
            && (liveCwls.Count > 0 || snapshot.LiveChannels.HasFlag(ChannelAvailability.CrossWorldLinkshells)))
        {
            nextCwls = MergeChannelNames(liveCwls, snapshot.CrossWorldLinkshells);
            liveChannels |= ChannelAvailability.CrossWorldLinkshells;
            cwlsLoadedLive = true;
            persistCwls = ChannelsChanged(snapshot.CrossWorldLinkshells, nextCwls) || !cwlsCachedInStore;
        }

        if (snapshot.LiveChannels != liveChannels
            || snapshot.FreeCompany != nextFc
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
                LiveChannels = liveChannels,
            });
        }

        if (cachedChannelsQuery is null)
        {
            if (persistFc)
            {
                fcCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.FreeCompany, nextFc is null ? [] : [nextFc]);
            }

            if (persistLs)
            {
                lsCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.Linkshell, nextLs.Values.ToArray());
            }

            if (persistCwls)
            {
                cwlsCachedInStore = true;
                PersistChannels(owner.ContentId, ConversationKind.CrossWorldLinkshell, nextCwls.Values.ToArray());
            }
        }

        if (party is not null)
            SyncPartyMembers(owner, party);
    }

    private static bool TryReadFreeCompany(out ChannelInfo? channel)
    {
        channel = null;
        var fc = InfoProxyFreeCompany.Instance();
        if (fc is null)
            return false;

        if (fc->Id != 0)
        {
            var name = fc->NameString.Trim();
            var hasRealName = name.Length > 0;
            channel = new ChannelInfo(
                ConversationKey.FreeCompany(fc->Id),
                hasRealName ? name : "Free Company",
                Slot: null,
                HasRealName: hasRealName);
        }

        return true;
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
}
