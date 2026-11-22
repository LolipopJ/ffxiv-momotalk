namespace FFXIVMomoTalk.Chat;

internal readonly record struct PlayerKey(string Name, uint WorldId)
{
    public static PlayerKey From(string name, uint worldId) => new(name.Trim().ToUpperInvariant(), worldId);
}

internal sealed record PlayerIdentity(string Name, uint WorldId, string World)
{
    public PlayerKey Key { get; } = PlayerKey.From(Name, WorldId);

    public string DisplayName => $"{Name} @ {World}";
}

internal sealed class PlayerSession(ulong contentId, PlayerIdentity player)
{
    public ulong ContentId { get; } = contentId;

    public PlayerIdentity Player { get; } = player;
}

internal sealed record FriendSnapshot(PlayerSession? Session, IReadOnlyDictionary<PlayerKey, PlayerIdentity>? Friends)
{
    public PlayerIdentity? FindRecipient(PlayerKey key, out string? unavailable)
    {
        if (Friends is null)
        {
            unavailable = "Open the in-game Friend List to send a Tell.";
            return null;
        }

        var friend = Friends.GetValueOrDefault(key);
        unavailable = friend is null ? "This character is no longer on your Friend List." : null;
        return friend;
    }

    public PlayerIdentity? Resolve(TellPeer peer)
    {
        if (Friends is null)
            return null;

        var key = PlayerKey.From(peer.Name, peer.WorldId);
        if (peer.WorldId != 0)
            return Friends.GetValueOrDefault(key);

        PlayerIdentity? match = null;
        foreach (var friend in Friends.Values)
        {
            if (friend.Key.Name != key.Name || (peer.World is not null
                && !StringComparer.OrdinalIgnoreCase.Equals(peer.World, friend.World)))
                continue;

            if (match is not null)
                return null;

            match = friend;
        }

        return match;
    }
}

internal readonly record struct TellPeer(string Name, uint WorldId, string? World);

internal sealed record CapturedTell(PlayerSession Session, PlayerIdentity Peer, long SentAt, bool Outgoing, string Body);

internal readonly record struct MessageCursor(long SentAt, long Id) : IComparable<MessageCursor>
{
    public int CompareTo(MessageCursor other)
    {
        var timeOrder = SentAt.CompareTo(other.SentAt);
        return timeOrder == 0 ? Id.CompareTo(other.Id) : timeOrder;
    }
}

internal sealed record TellMessage(long Id, long SentAt, bool Outgoing, string Body, string? Rejection)
{
    public MessageCursor Cursor => new(SentAt, Id);
}

internal sealed record ConversationSummary(PlayerIdentity Peer, TellMessage? Latest);

internal sealed record MessagePage(IReadOnlyList<TellMessage> Messages, bool HasOlder);
