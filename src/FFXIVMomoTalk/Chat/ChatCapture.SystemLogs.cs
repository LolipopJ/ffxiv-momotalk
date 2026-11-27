using System.Diagnostics;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Text.ReadOnly;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class ChatCapture
{
    private const int ObservedLogLimit = 32;
    private static readonly TimeSpan ObservedLogTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TradePartnerTtl = TimeSpan.FromMinutes(10);

    private readonly Lock observedLogGate = new();
    private readonly List<ObservedLogEntry> recentObservedLogs = new();
    private PendingTrade? activeTrade;

    internal static bool TryResolveLocalPeerPair(
        ObservedPlayer? source,
        ObservedPlayer? target,
        PlayerIdentity? localPlayer,
        out TellPeer peer,
        out bool outgoing)
    {
        if (source is { } src && target is { } dst && localPlayer is not null)
        {
            var srcIsLocal = MatchesLocalObservedPlayer(src, localPlayer);
            var dstIsLocal = MatchesLocalObservedPlayer(dst, localPlayer);
            if (srcIsLocal && !dstIsLocal)
            {
                peer = new TellPeer(dst.Name, dst.WorldId, null, HasPlayerPayload: true);
                outgoing = true;
                return true;
            }

            if (dstIsLocal && !srcIsLocal)
            {
                peer = new TellPeer(src.Name, src.WorldId, null, HasPlayerPayload: true);
                outgoing = false;
                return true;
            }
        }

        peer = default;
        outgoing = false;
        return false;
    }

    internal static ChannelInfo? TryResolveChannelByUniqueName(IEnumerable<ChannelInfo> channels, string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
            return null;

        var target = channelName.Trim();
        ChannelInfo? matched = null;
        foreach (var channel in channels)
        {
            if (!channel.HasRealName || !StringComparer.OrdinalIgnoreCase.Equals(channel.Name.Trim(), target))
                continue;

            if (matched is not null && matched.Key != channel.Key)
                return null;

            matched = channel;
        }

        return matched;
    }

    private void ClearObservedLogs()
    {
        lock (observedLogGate)
        {
            recentObservedLogs.Clear();
            activeTrade = null;
        }
    }

    private void EnqueueObservedLog(ObservedLogEntry entry)
    {
        lock (observedLogGate)
        {
            PruneObservedLogsLocked();
            if (recentObservedLogs.Count >= ObservedLogLimit)
                recentObservedLogs.RemoveAt(0);

            recentObservedLogs.Add(entry);
        }
    }

    private bool TryConsumeObservedLog(
        Func<ObservedLogEntry, bool> preferredMatch,
        Func<ObservedLogEntry, bool>? fallbackMatch,
        out ObservedLogEntry? consumed)
    {
        lock (observedLogGate)
        {
            PruneObservedLogsLocked();

            for (var i = 0; i < recentObservedLogs.Count; i++)
            {
                if (preferredMatch(recentObservedLogs[i]))
                {
                    consumed = recentObservedLogs[i];
                    recentObservedLogs.RemoveAt(i);
                    return true;
                }
            }

            if (fallbackMatch is not null)
            {
                for (var i = 0; i < recentObservedLogs.Count; i++)
                {
                    if (fallbackMatch(recentObservedLogs[i]))
                    {
                        consumed = recentObservedLogs[i];
                        recentObservedLogs.RemoveAt(i);
                        return true;
                    }
                }
            }
        }

        consumed = null;
        return false;
    }

    private void PruneObservedLogsLocked()
    {
        for (var i = recentObservedLogs.Count - 1; i >= 0; i--)
        {
            if (Stopwatch.GetElapsedTime(recentObservedLogs[i].ObservedAt) > ObservedLogTtl)
                recentObservedLogs.RemoveAt(i);
        }

        if (activeTrade is { } trade && Stopwatch.GetElapsedTime(trade.UpdatedAt) > TradePartnerTtl)
            activeTrade = null;
    }

    private void OnLogMessage(ILogMessage message)
    {
        var logKind = (XivChatType)(message.GameData.ValueNullable?.LogKind.RowId ?? 0);
        var logId = message.LogMessageId;
        var now = Stopwatch.GetTimestamp();

        if (logKind == XivChatType.StandardEmote)
        {
            if (TryReadPlayerEntity(message.SourceEntity, out var emoteSource)
                && TryReadPlayerEntity(message.TargetEntity, out var emoteTarget))
            {
                EnqueueObservedLog(new ObservedLogEntry(
                    SystemLogCategory.StandardEmote,
                    logKind,
                    logId,
                    emoteSource,
                    emoteTarget,
                    ChannelName: null,
                    ExtraText: null,
                    Outgoing: false,
                    Spec: default,
                    ObservedAt: now));
            }

            return;
        }

        if (logKind == XivChatType.SystemMessage && TryObserveTradeLog(message, logId, now))
            return;

        if (!TryClassifySystemLog(logId, logKind, out var spec))
            return;

        ObservedPlayer? sourcePlayer = TryReadPlayerEntity(message.SourceEntity, out var src) ? src : null;
        ObservedPlayer? targetPlayer = TryReadPlayerEntity(message.TargetEntity, out var dst) ? dst : null;
        var channelName = spec.ChannelNameSource switch
        {
            ChannelNameSource.TargetEntityName => ReadEntityText(message.TargetEntity),
            ChannelNameSource.StringParameter0 => ReadStringParameter(message, 0),
            _ => null,
        };
        var extraText = spec.Category == SystemLogCategory.FreeCompanyAnnouncement ? channelName : null;

        EnqueueObservedLog(new ObservedLogEntry(
            spec.Category,
            logKind,
            logId,
            sourcePlayer,
            targetPlayer,
            spec.Category == SystemLogCategory.FreeCompanyAnnouncement ? null : channelName,
            extraText,
            Outgoing: false,
            Spec: spec,
            ObservedAt: now));
    }

    private bool TryObserveTradeLog(ILogMessage message, uint logId, long now)
    {
        switch (logId)
        {
            case 32 or 34:
                if (TryReadPlayerEntity(message.TargetEntity, out var partner))
                {
                    lock (observedLogGate)
                        activeTrade = new PendingTrade(partner, Outgoing: logId == 32, UpdatedAt: now);
                }

                return true;

            case 33:
                if (TryReadPlayerEntity(message.TargetEntity, out var waitingPartner))
                {
                    lock (observedLogGate)
                    {
                        var outgoing = activeTrade is { } existing
                            && existing.Partner.WorldId == waitingPartner.WorldId
                            && StringComparer.OrdinalIgnoreCase.Equals(existing.Partner.Name, waitingPartner.Name)
                            && existing.Outgoing;
                        activeTrade = new PendingTrade(waitingPartner, outgoing, now);
                    }
                }

                return true;

            case 35 or 37:
                lock (observedLogGate)
                {
                    if (activeTrade is { } existing)
                        activeTrade = existing with { UpdatedAt = now };
                }

                return true;

            case 36:
                lock (observedLogGate)
                    activeTrade = null;
                return true;

            case 38:
                PendingTrade? completed = null;
                lock (observedLogGate)
                {
                    PruneObservedLogsLocked();
                    completed = activeTrade;
                    activeTrade = null;
                }

                if (completed is { } trade)
                {
                    var spec = new SystemLogSpec(
                        SystemLogCategory.FriendTradeComplete,
                        ConversationKind.Tell,
                        MessageKind.System);
                    EnqueueObservedLog(new ObservedLogEntry(
                        SystemLogCategory.FriendTradeComplete,
                        XivChatType.SystemMessage,
                        logId,
                        trade.Partner,
                        TargetPlayer: null,
                        ChannelName: null,
                        ExtraText: null,
                        Outgoing: trade.Outgoing,
                        Spec: spec,
                        ObservedAt: now));
                }

                return true;

            default:
                return false;
        }
    }

    private bool TryHandleSystemLogMessage(IChatMessage message)
    {
        if (!IsSystemLogChatType(message.LogKind))
            return false;

        var body = message.OriginalMessage.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(body))
            return false;

        if (!TryConsumeObservedLog(
            entry => entry.Category != SystemLogCategory.StandardEmote
                && entry.LogKind == message.LogKind
                && MatchesRenderedSystemBody(entry, body),
            entry => entry.Category != SystemLogCategory.StandardEmote
                && entry.LogKind == message.LogKind
                && CanFallbackFifoMatch(entry),
            out var observed) || observed is null)
        {
            return false;
        }

        var localPlayer = directory.Current.Session?.Player;
        var senderSe = message.OriginalSender.IsEmpty
            ? SeString.Empty
            : SeString.Parse(message.OriginalSender.Data.Span);

        TellPeer? peer = null;
        var outgoing = observed.Outgoing;
        switch (observed.Category)
        {
            case SystemLogCategory.FriendDuelResult:
                if (!TryResolveDuelPeer(observed, message.OriginalMessage, localPlayer, out var duelPeer, out outgoing))
                    return true;
                peer = duelPeer;
                break;

            case SystemLogCategory.FriendTradeComplete:
                if (observed.SourcePlayer is not { } tradePartner)
                    return true;
                peer = new TellPeer(tradePartner.Name, tradePartner.WorldId, null, HasPlayerPayload: true);
                break;

            case SystemLogCategory.FriendAdded or SystemLogCategory.FriendRemoved:
                peer = TryResolveSinglePlayerPeer(observed.SourcePlayer, message.OriginalMessage, localPlayer);
                if (peer is null)
                    return true;
                outgoing = true;
                break;
        }

        if (ShouldFilterMessage(config, senderSe, peer, body))
            return true;

        var spec = observed.Spec;
        var dedupKey = observed.Category == SystemLogCategory.FreeCompanyAnnouncement
            ? (!string.IsNullOrWhiteSpace(observed.ExtraText) ? observed.ExtraText : body)
            : null;

        DispatchIncoming(new IncomingChat(
            message.LogKind,
            spec.ConversationKind,
            Slot: 0,
            Peer: peer,
            IsLocalPlayer: outgoing,
            TellOutgoing: outgoing,
            SentAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: body,
            MessageKind: spec.MessageKind,
            SystemCategory: observed.Category,
            ChannelName: observed.ChannelName,
            DedupKey: dedupKey,
            AllowLastKnown: spec.AllowLastKnown,
            QueueUntilResolved: spec.QueueUntilResolved,
            ExistingConversationOnly: spec.ExistingConversationOnly,
            SkipPreDispatchReread: spec.SkipPreDispatchSocialReread));
        return true;
    }

    internal static bool TryResolveDuelPeer(
        ObservedLogEntry observed,
        ReadOnlySeString renderedMessage,
        PlayerIdentity? localPlayer,
        out TellPeer peer,
        out bool outgoing)
    {
        if (TryResolveLocalPeerPair(observed.SourcePlayer, observed.TargetPlayer, localPlayer, out peer, out outgoing))
            return true;

        if (localPlayer is null)
        {
            peer = default;
            outgoing = false;
            return false;
        }

        var links = CollectValidPlayerLinks(renderedMessage, localPlayer: null);
        if (links.Count == 2)
        {
            var firstIsLocal = IsLocalPlayerIdentity(links[0].Name, links[0].WorldId, localPlayer);
            var secondIsLocal = IsLocalPlayerIdentity(links[1].Name, links[1].WorldId, localPlayer);
            if (firstIsLocal != secondIsLocal)
            {
                outgoing = firstIsLocal;
                peer = firstIsLocal ? links[1] : links[0];
                return true;
            }
        }

        peer = default;
        outgoing = false;
        return false;
    }

    private static TellPeer? TryResolveSinglePlayerPeer(
        ObservedPlayer? observedPlayer,
        ReadOnlySeString renderedMessage,
        PlayerIdentity? localPlayer)
    {
        var links = CollectValidPlayerLinks(renderedMessage, localPlayer);
        if (observedPlayer is { } player)
        {
            foreach (var link in links)
            {
                if ((player.WorldId == 0 || link.WorldId == player.WorldId)
                    && SocialSnapshot.MatchesCharacterName(link.Name, player.Name, allowAbbreviation: false))
                {
                    return link;
                }
            }

            if (!MatchesLocalObservedPlayer(player, localPlayer))
                return new TellPeer(player.Name, player.WorldId, null, HasPlayerPayload: true);
        }

        return links.Count == 1 ? links[0] : null;
    }

    private static bool MatchesRenderedSystemBody(ObservedLogEntry entry, string body)
    {
        if (!string.IsNullOrWhiteSpace(entry.ExtraText))
            return body.Contains(entry.ExtraText, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(entry.ChannelName))
            return body.Contains(entry.ChannelName, StringComparison.Ordinal);

        if (entry.SourcePlayer is { } src && body.Contains(src.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entry.TargetPlayer is { } dst && body.Contains(dst.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        return entry.SourcePlayer is null && entry.TargetPlayer is null;
    }

    private static bool CanFallbackFifoMatch(ObservedLogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.ChannelName) && string.IsNullOrWhiteSpace(entry.ExtraText);

    private static bool TryReadPlayerEntity(ILogMessageEntity? entity, out ObservedPlayer player)
    {
        if (entity is { IsPlayer: true })
        {
            var name = entity.Name.ExtractText().Trim();
            if (IsValidGlobalPlayerName(name))
            {
                player = new ObservedPlayer(name, entity.HomeWorldId);
                return true;
            }
        }

        player = default;
        return false;
    }

    private static string? ReadEntityText(ILogMessageEntity? entity)
    {
        if (entity is null)
            return null;

        var text = entity.Name.ExtractText().Trim();
        return text.Length > 0 ? text : null;
    }

    private static string? ReadStringParameter(ILogMessage message, int index)
    {
        if (!message.TryGetStringParameter(index, out var value))
            return null;

        var text = value.ExtractText().Trim();
        return text.Length > 0 ? text : null;
    }

    private static bool MatchesLocalObservedPlayer(ObservedPlayer player, PlayerIdentity? localPlayer) =>
        localPlayer is not null
        && (player.WorldId == 0 || player.WorldId == localPlayer.WorldId)
        && SocialSnapshot.MatchesCharacterName(localPlayer.Name, player.Name, allowAbbreviation: true);

    internal readonly record struct ObservedPlayer(string Name, ushort WorldId);

    internal sealed record ObservedLogEntry(
        SystemLogCategory Category,
        XivChatType LogKind,
        uint LogMessageId,
        ObservedPlayer? SourcePlayer,
        ObservedPlayer? TargetPlayer,
        string? ChannelName,
        string? ExtraText,
        bool Outgoing,
        SystemLogSpec Spec,
        long ObservedAt);

    private readonly record struct PendingTrade(ObservedPlayer Partner, bool Outgoing, long UpdatedAt);
}
