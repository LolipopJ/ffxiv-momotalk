using System.Diagnostics;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class ChatCapture
{
    private static readonly TimeSpan NewFriendWaitTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GroupSystemWaitTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaticEndGrace = TimeSpan.FromSeconds(5);

    private readonly List<PendingNewFriend> waitingNewFriends = new();
    private readonly List<PendingGroupSystem> waitingGroupSystem = new();
    private readonly Dictionary<ulong, PlayerIdentity> lastKnownFriends = new();
    private readonly Dictionary<ConversationKey, ChannelInfo> lastKnownLinkshells = new();
    private readonly Dictionary<ConversationKey, ChannelInfo> lastKnownCrossWorldLinkshells = new();
    private readonly Lock fcAnnouncementGate = new();
    private readonly Dictionary<(ulong Owner, string StableKey), string> lastRecordedFcAnnouncements = new();
    private ChannelInfo? lastKnownFreeCompany;
    private (StaticGroup Group, long ObservedAt)? lastMatchedStatic;

    private void ClearSystemTrackingState()
    {
        waitingNewFriends.Clear();
        waitingGroupSystem.Clear();
        lastKnownFriends.Clear();
        lastKnownFreeCompany = null;
        lastKnownLinkshells.Clear();
        lastKnownCrossWorldLinkshells.Clear();
        lastMatchedStatic = null;
        lock (fcAnnouncementGate)
            lastRecordedFcAnnouncements.Clear();
    }

    private void TrackLastKnownState(SocialSnapshot snapshot)
    {
        if (snapshot.Friends is { Count: > 0 } friends)
        {
            foreach (var (cid, friend) in friends)
                lastKnownFriends[cid] = friend;
        }

        if (snapshot.FreeCompany is { } fc)
            lastKnownFreeCompany = fc;

        foreach (var ls in snapshot.Linkshells.Values)
            lastKnownLinkshells[ls.Key] = ls;

        foreach (var cwls in snapshot.CrossWorldLinkshells.Values)
            lastKnownCrossWorldLinkshells[cwls.Key] = cwls;

        if (snapshot.FindMatchedStatic() is { } matched)
            lastMatchedStatic = (matched, Stopwatch.GetTimestamp());
    }

    private void ReceiveSystemMessage(
        IncomingChat incoming,
        SystemLogCategory category,
        SocialSnapshot snapshot,
        PlayerSession session)
    {
        switch (category)
        {
            case SystemLogCategory.FriendDuelResult or SystemLogCategory.FriendTradeComplete:
                if (incoming.Peer is not { } interactionPeer)
                    return;

                var tell = new UnresolvedTell(
                    interactionPeer,
                    incoming.SentAt,
                    incoming.TellOutgoing,
                    incoming.Body,
                    MessageKind.System);
                if (snapshot.Friends is not null)
                {
                    ArchiveIfFriend(snapshot, tell);
                    return;
                }

                if (waitingTells.Count == PendingLimit)
                    waitingTells.Dequeue();

                waitingTells.Enqueue(tell);
                return;

            case SystemLogCategory.FriendAdded:
                if (incoming.Peer is not { } addedPeer)
                    return;

                if (snapshot.Friends is not null && snapshot.ResolveTellPeer(addedPeer) is { } addedFriend)
                {
                    var captured = new CapturedMessage(
                        session,
                        ConversationDescriptor.FromFriend(addedFriend),
                        session.Player,
                        incoming.SentAt,
                        Outgoing: true,
                        incoming.Body,
                        MessageKind.System);
                    Archive(captured, updateDisplayName: true);
                    return;
                }

                if (waitingNewFriends.Count >= PendingLimit)
                    waitingNewFriends.RemoveAt(0);

                waitingNewFriends.Add(new PendingNewFriend(addedPeer, incoming.SentAt, incoming.Body, Stopwatch.GetTimestamp()));
                return;

            case SystemLogCategory.FriendRemoved:
                if (incoming.Peer is not { } removedPeer)
                    return;

                var removedFriend = snapshot.ResolveTellPeer(removedPeer)
                    ?? (lastKnownFriends.Count > 0 ? SocialSnapshot.MatchPlayer(lastKnownFriends.Values, removedPeer) : null);
                if (removedFriend is null)
                    return;

                var removalMessage = new CapturedMessage(
                    session,
                    ConversationDescriptor.FromFriend(removedFriend),
                    session.Player,
                    incoming.SentAt,
                    Outgoing: true,
                    incoming.Body,
                    MessageKind.SilentSystem);
                Archive(removalMessage, updateDisplayName: false, onlyIfConversationExists: true);
                return;

            case SystemLogCategory.StaticDuty:
                if (snapshot.FindMatchedStatic() is { } matchedStatic)
                {
                    var dutyMsg = new CapturedMessage(
                        session,
                        matchedStatic.ToDescriptor(),
                        SystemSender,
                        incoming.SentAt,
                        Outgoing: false,
                        incoming.Body,
                        MessageKind.SilentSystem);
                    Archive(dutyMsg, updateDisplayName: true);
                    return;
                }

                if (snapshot.Party is { } party && snapshot.Statics is null)
                {
                    if (waitingParty.Count == PendingLimit)
                        waitingParty.Dequeue();

                    waitingParty.Enqueue(new UnresolvedPartyMessage(
                        party.RosterKey,
                        SystemSender,
                        incoming.SentAt,
                        Outgoing: false,
                        incoming.Body,
                        MessageKind.SilentSystem));
                    return;
                }

                if (snapshot.Party is null
                    && incoming.AllowLastKnown
                    && lastMatchedStatic is { } recentStatic
                    && Stopwatch.GetElapsedTime(recentStatic.ObservedAt) <= StaticEndGrace)
                {
                    var endedMsg = new CapturedMessage(
                        session,
                        recentStatic.Group.ToDescriptor(),
                        SystemSender,
                        incoming.SentAt,
                        Outgoing: false,
                        incoming.Body,
                        MessageKind.SilentSystem);
                    Archive(endedMsg, updateDisplayName: true);
                }

                return;

            case SystemLogCategory.FreeCompanyMember
                or SystemLogCategory.FreeCompanyGovernance
                or SystemLogCategory.FreeCompanyRename
                or SystemLogCategory.FreeCompanyAnnouncement
                or SystemLogCategory.LinkshellEvent
                or SystemLogCategory.CrossWorldLinkshellEvent:
                _ = TryArchiveGroupSystemMessage(incoming, snapshot, session, allowQueue: true);
                return;
        }
    }

    private void FlushPendingSystemQueues(SocialSnapshot snapshot)
    {
        if (snapshot.Session is not { } session)
            return;

        if (waitingNewFriends.Count > 0)
        {
            for (var i = waitingNewFriends.Count - 1; i >= 0; i--)
            {
                if (Stopwatch.GetElapsedTime(waitingNewFriends[i].QueuedAt) > NewFriendWaitTtl)
                    waitingNewFriends.RemoveAt(i);
            }

            if (snapshot.Friends is not null)
            {
                for (var i = 0; i < waitingNewFriends.Count;)
                {
                    var pending = waitingNewFriends[i];
                    if (snapshot.ResolveTellPeer(pending.Peer) is { } friend)
                    {
                        waitingNewFriends.RemoveAt(i);
                        var captured = new CapturedMessage(
                            session,
                            ConversationDescriptor.FromFriend(friend),
                            session.Player,
                            pending.SentAt,
                            Outgoing: true,
                            pending.Body,
                            MessageKind.System);
                        Archive(captured, updateDisplayName: true);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
        }

        if (waitingGroupSystem.Count > 0)
        {
            for (var i = waitingGroupSystem.Count - 1; i >= 0; i--)
            {
                if (Stopwatch.GetElapsedTime(waitingGroupSystem[i].QueuedAt) > GroupSystemWaitTtl)
                    waitingGroupSystem.RemoveAt(i);
            }

            for (var i = 0; i < waitingGroupSystem.Count;)
            {
                if (TryArchiveGroupSystemMessage(waitingGroupSystem[i].Message, snapshot, session, allowQueue: false))
                    waitingGroupSystem.RemoveAt(i);
                else
                    i++;
            }
        }
    }

    private bool TryArchiveGroupSystemMessage(
        IncomingChat incoming,
        SocialSnapshot snapshot,
        PlayerSession session,
        bool allowQueue)
    {
        switch (incoming.Kind)
        {
            case ConversationKind.FreeCompany:
                var fc = ResolveFreeCompanyChannel(snapshot, incoming);
                if (fc is null)
                {
                    if (allowQueue && (incoming.QueueUntilResolved || !snapshot.LiveChannels.HasFlag(ChannelAvailability.FreeCompany)))
                        EnqueueGroupSystem(incoming);
                    return false;
                }

                ConversationDescriptor descriptor;
                bool updateDisplayName;
                if (incoming.SystemCategory == SystemLogCategory.FreeCompanyRename
                    && !string.IsNullOrWhiteSpace(incoming.ChannelName))
                {
                    descriptor = new ConversationDescriptor(fc.Key, incoming.ChannelName.Trim());
                    updateDisplayName = true;
                }
                else
                {
                    descriptor = fc.ToDescriptor();
                    updateDisplayName = fc.HasRealName;
                }

                var fcMessage = new CapturedMessage(
                    session,
                    descriptor,
                    SystemSender,
                    incoming.SentAt,
                    Outgoing: false,
                    incoming.Body,
                    MessageKind.SilentSystem);

                if (incoming.SystemCategory == SystemLogCategory.FreeCompanyAnnouncement)
                    ArchiveFreeCompanyAnnouncement(fcMessage, incoming.DedupKey ?? incoming.Body, updateDisplayName);
                else
                    Archive(fcMessage, updateDisplayName);
                return true;

            case ConversationKind.Linkshell or ConversationKind.CrossWorldLinkshell:
                var isCrossWorld = incoming.Kind == ConversationKind.CrossWorldLinkshell;
                var activeChannels = isCrossWorld ? snapshot.CrossWorldLinkshells : snapshot.Linkshells;
                var lastKnown = isCrossWorld ? lastKnownCrossWorldLinkshells : lastKnownLinkshells;
                var availability = isCrossWorld ? ChannelAvailability.CrossWorldLinkshells : ChannelAvailability.Linkshells;

                var channel = TryResolveChannelByUniqueName(activeChannels.Values, incoming.ChannelName);
                if (channel is null && incoming.AllowLastKnown)
                    channel = TryResolveChannelByUniqueName(lastKnown.Values, incoming.ChannelName);

                if (channel is null)
                {
                    if (allowQueue
                        && (incoming.QueueUntilResolved || (activeChannels.Count == 0 && !snapshot.LiveChannels.HasFlag(availability))))
                    {
                        EnqueueGroupSystem(incoming);
                    }

                    return false;
                }

                var lsMessage = new CapturedMessage(
                    session,
                    channel.ToDescriptor(),
                    SystemSender,
                    incoming.SentAt,
                    Outgoing: false,
                    incoming.Body,
                    MessageKind.SilentSystem);
                Archive(lsMessage, channel.HasRealName);
                return true;

            default:
                return false;
        }
    }

    private ChannelInfo? ResolveFreeCompanyChannel(SocialSnapshot snapshot, IncomingChat incoming)
    {
        var expectedName = incoming.SystemCategory == SystemLogCategory.FreeCompanyRename
            ? null
            : incoming.ChannelName?.Trim();

        if (snapshot.FreeCompany is { } currentFc && MatchesChannelNameOrUnnamed(currentFc, expectedName))
            return currentFc;

        if (incoming.AllowLastKnown && lastKnownFreeCompany is { } previousFc
            && MatchesChannelNameOrUnnamed(previousFc, expectedName))
        {
            return previousFc;
        }

        return null;
    }

    private static bool MatchesChannelNameOrUnnamed(ChannelInfo channel, string? expectedName) =>
        string.IsNullOrWhiteSpace(expectedName)
        || !channel.HasRealName
        || StringComparer.OrdinalIgnoreCase.Equals(channel.Name.Trim(), expectedName);

    private void EnqueueGroupSystem(IncomingChat incoming)
    {
        if (waitingGroupSystem.Count >= PendingLimit)
            waitingGroupSystem.RemoveAt(0);

        waitingGroupSystem.Add(new PendingGroupSystem(incoming, Stopwatch.GetTimestamp()));
    }

    private void ArchiveFreeCompanyAnnouncement(CapturedMessage captured, string dedupKey, bool updateDisplayName)
    {
        var normalized = dedupKey.Trim();
        if (normalized.Length == 0)
            return;

        var cacheKey = (captured.Session.ContentId, captured.Conversation.Key.StableKey);
        lock (fcAnnouncementGate)
        {
            if (lastRecordedFcAnnouncements.TryGetValue(cacheKey, out var recorded)
                && StringComparer.Ordinal.Equals(recorded, normalized))
            {
                return;
            }

            lastRecordedFcAnnouncements[cacheKey] = normalized;
        }

        _ = PersistFreeCompanyAnnouncementAsync(captured, cacheKey, normalized, updateDisplayName);
    }

    private async Task PersistFreeCompanyAnnouncementAsync(
        CapturedMessage captured,
        (ulong Owner, string StableKey) cacheKey,
        string dedupKey,
        bool updateDisplayName)
    {
        var ownerId = cacheKey.Owner;
        var useCacheStore = cache.IncompatibleOwner != ownerId;
        if (useCacheStore)
        {
            try
            {
                var stored = await cache.ReadFreeCompanyAnnouncementAsync(ownerId, cacheKey.StableKey).ConfigureAwait(false);
                if (StringComparer.Ordinal.Equals(stored, dedupKey))
                    return;
            }
            catch (Exception exception)
            {
                log.Error(exception, "Unable to read cached Free Company announcement.");
                useCacheStore = false;
            }
        }

        var write = useCacheStore
            ? messages.RecordAsync(captured, updateDisplayName)
            : messages.RecordUniqueSystemAsync(captured, updateDisplayName);
        await ObserveWriteAsync(ownerId, write).ConfigureAwait(false);

        if (!write.IsCompletedSuccessfully)
        {
            lock (fcAnnouncementGate)
            {
                if (lastRecordedFcAnnouncements.TryGetValue(cacheKey, out var current)
                    && StringComparer.Ordinal.Equals(current, dedupKey))
                {
                    lastRecordedFcAnnouncements.Remove(cacheKey);
                }
            }

            return;
        }

        if (write.Result > 0)
            Captured?.Invoke(captured, write);

        if (useCacheStore)
            await ObserveCacheAsync(cache.SaveFreeCompanyAnnouncementAsync(ownerId, cacheKey.StableKey, dedupKey)).ConfigureAwait(false);
    }

    private sealed record PendingNewFriend(TellPeer Peer, long SentAt, string Body, long QueuedAt);

    private sealed record PendingGroupSystem(IncomingChat Message, long QueuedAt);
}
