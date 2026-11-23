using System.Globalization;

namespace FFXIVMomoTalk.Chat;

internal enum ConversationKind
{
    Tell = 0,
    FreeCompany = 1,
    Linkshell = 2,
    CrossWorldLinkshell = 3,
    Static = 4,
}

internal readonly record struct ConversationKey(ConversationKind Kind, string StableKey)
{
    public bool IsGroup => Kind != ConversationKind.Tell;

    public string StorageId => $"{(int)Kind}:{StableKey}";

    public static ConversationKey Tell(ulong peerContentId) =>
        new(ConversationKind.Tell, peerContentId.ToString("X16", CultureInfo.InvariantCulture));

    public static ConversationKey FreeCompany(ulong freeCompanyId) =>
        new(ConversationKind.FreeCompany, freeCompanyId.ToString("X16", CultureInfo.InvariantCulture));

    public static ConversationKey Linkshell(ulong linkshellId) =>
        new(ConversationKind.Linkshell, linkshellId.ToString("X16", CultureInfo.InvariantCulture));

    public static ConversationKey CrossWorldLinkshell(uint foundationTime) =>
        new(ConversationKind.CrossWorldLinkshell, foundationTime.ToString(CultureInfo.InvariantCulture));

    public static ConversationKey Static(string rosterKey) =>
        new(ConversationKind.Static, rosterKey);

    public bool TryGetTellContentId(out ulong contentId)
    {
        if (Kind == ConversationKind.Tell
            && ulong.TryParse(StableKey, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out contentId))
        {
            return true;
        }

        contentId = 0;
        return false;
    }

    public static string BuildStaticRosterKey(IEnumerable<ulong> contentIds) =>
        string.Join('-', contentIds.Distinct().Order().Select(id => id.ToString("X16", CultureInfo.InvariantCulture)));
}

internal sealed record PlayerIdentity(ulong ContentId, string Name, uint WorldId, string World)
{
    public string DisplayName => string.IsNullOrEmpty(World) ? Name : $"{Name} @ {World}";
}

internal sealed class PlayerSession(ulong contentId, PlayerIdentity player)
{
    public ulong ContentId { get; } = contentId;

    public PlayerIdentity Player { get; } = player;
}

internal sealed record ConversationDescriptor(
    ConversationKey Key,
    string DisplayName,
    uint WorldId = 0,
    string WorldName = "")
{
    public static ConversationDescriptor FromFriend(PlayerIdentity peer) =>
        new(ConversationKey.Tell(peer.ContentId), peer.Name, peer.WorldId, peer.World);

    public PlayerIdentity? ToTellPeer() =>
        Key.TryGetTellContentId(out var contentId)
            ? new PlayerIdentity(contentId, DisplayName, WorldId, WorldName)
            : null;
}

internal sealed record ChannelInfo(ConversationKey Key, string Name, int? Slot = null, bool HasRealName = true)
{
    public ConversationDescriptor ToDescriptor() => new(Key, Name);
}

internal sealed record CachedChannels(
    bool HasFreeCompany,
    ChannelInfo? FreeCompany,
    bool HasLinkshells,
    IReadOnlyDictionary<int, ChannelInfo> Linkshells,
    bool HasCrossWorldLinkshells,
    IReadOnlyDictionary<int, ChannelInfo> CrossWorldLinkshells);

internal sealed record StaticGroup(
    string StableKey,
    string Name,
    long CreatedAt,
    IReadOnlyList<PlayerIdentity> Members)
{
    public ConversationKey Key => ConversationKey.Static(StableKey);

    public ConversationDescriptor ToDescriptor() => new(Key, Name);
}

internal sealed record ActiveParty(string RosterKey, IReadOnlyList<PlayerIdentity> Members);

internal sealed record SendTarget(
    ConversationKey Key,
    string CommandPrefix,
    PlayerIdentity? TellPeer = null);

[Flags]
internal enum ChannelAvailability
{
    None = 0,
    FreeCompany = 1 << 0,
    Linkshells = 1 << 1,
    CrossWorldLinkshells = 1 << 2,
}

internal sealed record SocialSnapshot(
    PlayerSession? Session,
    IReadOnlyDictionary<ulong, PlayerIdentity>? Friends,
    ChannelInfo? FreeCompany,
    IReadOnlyDictionary<int, ChannelInfo> Linkshells,
    IReadOnlyDictionary<int, ChannelInfo> CrossWorldLinkshells,
    IReadOnlyDictionary<string, StaticGroup>? Statics,
    ActiveParty? Party,
    ChannelAvailability LiveChannels = ChannelAvailability.None)
{
    public static SocialSnapshot Empty(PlayerSession? session) =>
        new(session, null, null, new Dictionary<int, ChannelInfo>(), new Dictionary<int, ChannelInfo>(), null, null);

    public PlayerIdentity? FindTellRecipient(ulong contentId, out string? unavailable)
    {
        if (Friends is null)
        {
            unavailable = "Open the in-game Friend List to send a Tell.";
            return null;
        }

        var friend = Friends.GetValueOrDefault(contentId);
        unavailable = friend is null ? "This character is no longer on your Friend List." : null;
        return friend;
    }

    public SendTarget? FindSendTarget(ConversationKey key, out string? unavailable)
    {
        switch (key.Kind)
        {
            case ConversationKind.Tell:
                if (!key.TryGetTellContentId(out var cid))
                {
                    unavailable = "This character is unavailable.";
                    return null;
                }

                var friend = FindTellRecipient(cid, out unavailable);
                return friend is null
                    ? null
                    : new SendTarget(key, $"/tell {friend.Name}@{friend.World}", friend);

            case ConversationKind.FreeCompany:
                if ((LiveChannels & ChannelAvailability.FreeCompany) == 0)
                {
                    unavailable = "Free Company chat is unavailable right now.";
                    return null;
                }

                if (FreeCompany?.Key == key)
                {
                    unavailable = null;
                    return new SendTarget(key, "/fc");
                }

                unavailable = "You are no longer a member of this Free Company.";
                return null;

            case ConversationKind.Linkshell:
                if ((LiveChannels & ChannelAvailability.Linkshells) == 0)
                {
                    unavailable = "Linkshell chat is unavailable right now.";
                    return null;
                }

                if (FindActiveSlot(key) is int lsSlot and >= 1 and <= 8)
                {
                    unavailable = null;
                    return new SendTarget(key, $"/l{lsSlot}");
                }

                unavailable = "You are no longer a member of this Linkshell.";
                return null;

            case ConversationKind.CrossWorldLinkshell:
                if ((LiveChannels & ChannelAvailability.CrossWorldLinkshells) == 0)
                {
                    unavailable = "Cross-World Linkshell chat is unavailable right now.";
                    return null;
                }

                if (FindActiveSlot(key) is int cwSlot and >= 1 and <= 8)
                {
                    unavailable = null;
                    return new SendTarget(key, $"/cwl{cwSlot}");
                }

                unavailable = "You are no longer a member of this Cross-World Linkshell.";
                return null;

            case ConversationKind.Static:
                if (Statics is null || !Statics.TryGetValue(key.StableKey, out var group))
                {
                    unavailable = "This static group is unavailable.";
                    return null;
                }

                if (Party is not { } party || !StringComparer.Ordinal.Equals(party.RosterKey, group.StableKey))
                {
                    unavailable = "Form a party with all static members to send messages.";
                    return null;
                }

                unavailable = null;
                return new SendTarget(key, "/p");

            default:
                unavailable = "Unsupported conversation type.";
                return null;
        }
    }

    public int? FindActiveSlot(ConversationKey key)
    {
        switch (key.Kind)
        {
            case ConversationKind.Linkshell:
                foreach (var ls in Linkshells.Values)
                {
                    if (ls.Key == key)
                        return ls.Slot;
                }

                return null;

            case ConversationKind.CrossWorldLinkshell:
                foreach (var cwls in CrossWorldLinkshells.Values)
                {
                    if (cwls.Key == key)
                        return cwls.Slot;
                }

                return null;

            default:
                return null;
        }
    }

    public StaticGroup? FindMatchedStatic() =>
        Party is { } party && Statics is not null && Statics.TryGetValue(party.RosterKey, out var group)
            ? group
            : null;

    public bool IsStaticMatched(string stableKey) =>
        Party is { } party && StringComparer.Ordinal.Equals(party.RosterKey, stableKey);

    public string FormatChannelTag(ConversationDescriptor conversation) => conversation.Key.Kind switch
    {
        ConversationKind.Tell => conversation.WorldName,
        ConversationKind.FreeCompany => "FC",
        ConversationKind.CrossWorldLinkshell => FindActiveSlot(conversation.Key) is { } cwSlot ? $"CWLS {cwSlot}" : "CWLS",
        ConversationKind.Linkshell => FindActiveSlot(conversation.Key) is { } lsSlot ? $"LS {lsSlot}" : "LS",
        ConversationKind.Static => "Static",
        _ => string.Empty,
    };

    public ConversationDescriptor RefreshDescriptor(ConversationDescriptor conversation)
    {
        switch (conversation.Key.Kind)
        {
            case ConversationKind.Tell:
                if (Friends is not null && conversation.Key.TryGetTellContentId(out var cid)
                    && Friends.TryGetValue(cid, out var friend))
                {
                    return ConversationDescriptor.FromFriend(friend);
                }

                return conversation;

            case ConversationKind.FreeCompany:
                return FreeCompany is { HasRealName: true } fc && fc.Key == conversation.Key
                    ? conversation with { DisplayName = fc.Name }
                    : conversation;

            case ConversationKind.Linkshell:
                foreach (var ls in Linkshells.Values)
                {
                    if (ls.HasRealName && ls.Key == conversation.Key)
                        return conversation with { DisplayName = ls.Name };
                }

                return conversation;

            case ConversationKind.CrossWorldLinkshell:
                foreach (var cwls in CrossWorldLinkshells.Values)
                {
                    if (cwls.HasRealName && cwls.Key == conversation.Key)
                        return conversation with { DisplayName = cwls.Name };
                }

                return conversation;

            case ConversationKind.Static:
                return Statics is not null && Statics.TryGetValue(conversation.Key.StableKey, out var group)
                    ? conversation with { DisplayName = group.Name }
                    : conversation;

            default:
                return conversation;
        }
    }

    public PlayerIdentity? ResolveTellPeer(TellPeer peer)
    {
        if (Friends is null)
            return null;

        return MatchPlayer(Friends.Values, peer);
    }

    public PlayerIdentity? ResolvePartyMember(TellPeer peer) =>
        Party is null ? null : MatchPlayer(Party.Members, peer);

    internal static bool MatchesCharacterName(string fullName, string observedName, bool allowAbbreviation)
    {
        var target = observedName.AsSpan().Trim();
        var candidate = fullName.AsSpan().Trim();
        if (target.IsEmpty || candidate.IsEmpty)
            return false;

        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!allowAbbreviation)
            return false;

        return TrySplitTwoPartName(candidate, out var candFirst, out var candLast)
            && TrySplitTwoPartName(target, out var obsFirst, out var obsLast)
            && MatchesNameToken(candFirst, obsFirst)
            && MatchesNameToken(candLast, obsLast);
    }

    private static bool TrySplitTwoPartName(ReadOnlySpan<char> value, out ReadOnlySpan<char> first, out ReadOnlySpan<char> last)
    {
        first = default;
        last = default;
        var space = value.IndexOf(' ');
        if (space > 0)
        {
            first = value[..space].Trim();
            last = value[(space + 1)..].Trim();
            return !first.IsEmpty && !last.IsEmpty && !last.Contains(' ');
        }

        if (value.Length >= 3 && value[1] == '.')
        {
            first = value[..2];
            last = value[2..].Trim();
            return !last.IsEmpty && !last.Contains(' ');
        }

        return false;
    }

    private static bool MatchesNameToken(ReadOnlySpan<char> fullToken, ReadOnlySpan<char> observedToken) =>
        fullToken.Equals(observedToken, StringComparison.OrdinalIgnoreCase)
        || (observedToken.Length == 2
            && observedToken[1] == '.'
            && !fullToken.IsEmpty
            && char.ToUpperInvariant(fullToken[0]) == char.ToUpperInvariant(observedToken[0]));

    private static PlayerIdentity? MatchPlayer(IEnumerable<PlayerIdentity> candidates, TellPeer peer) =>
        MatchPlayerCore(candidates, peer, allowAbbreviation: false)
        ?? (!peer.HasPlayerPayload ? MatchPlayerCore(candidates, peer, allowAbbreviation: true) : null);

    private static PlayerIdentity? MatchPlayerCore(
        IEnumerable<PlayerIdentity> candidates,
        TellPeer peer,
        bool allowAbbreviation)
    {
        var targetName = peer.Name.Trim();
        PlayerIdentity? match = null;
        foreach (var candidate in candidates)
        {
            if (!MatchesCharacterName(candidate.Name, targetName, allowAbbreviation))
                continue;

            if (peer.WorldId != 0)
            {
                if (candidate.WorldId != peer.WorldId)
                    continue;
            }
            else if (peer.World is not null
                && !StringComparer.OrdinalIgnoreCase.Equals(peer.World, candidate.World))
            {
                continue;
            }

            if (match is not null)
                return null;

            match = candidate;
        }

        return match;
    }
}

internal readonly record struct TellPeer(string Name, uint WorldId, string? World, bool HasPlayerPayload = false);

internal sealed record CapturedMessage(
    PlayerSession Session,
    ConversationDescriptor Conversation,
    PlayerIdentity Sender,
    long SentAt,
    bool Outgoing,
    string Body);

internal readonly record struct MessageCursor(long SentAt, long Id) : IComparable<MessageCursor>
{
    public int CompareTo(MessageCursor other)
    {
        var timeOrder = SentAt.CompareTo(other.SentAt);
        return timeOrder == 0 ? Id.CompareTo(other.Id) : timeOrder;
    }
}

internal sealed record ChatMessage(
    long Id,
    long SentAt,
    bool Outgoing,
    PlayerIdentity Sender,
    string Body,
    string? Rejection)
{
    public MessageCursor Cursor => new(SentAt, Id);
}

internal sealed record ConversationSummary(ConversationDescriptor Conversation, ChatMessage? Latest, long? LastSentAt);

internal sealed record MessagePage(IReadOnlyList<ChatMessage> Messages, bool HasOlder);
