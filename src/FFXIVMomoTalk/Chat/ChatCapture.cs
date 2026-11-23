using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace FFXIVMomoTalk.Chat;

internal sealed class ChatCapture(
    IChatGui chat,
    IFramework framework,
    SocialDirectory directory,
    MessageStore messages,
    CacheStore cache,
    IPluginLog log) : IDisposable
{
    private const int PendingLimit = 128;
    private readonly Queue<UnresolvedTell> waitingTells = new();
    private readonly Queue<UnresolvedPartyMessage> waitingParty = new();
    private PlayerSession? owner;
    private ulong failedWriteOwner;
    private bool disposed;

    public event Action<CapturedMessage, Task<long>>? Captured;

    public ulong FailedWriteOwner => Volatile.Read(ref failedWriteOwner);

    public void ClearFailedWrite(ulong contentId) =>
        Interlocked.CompareExchange(ref failedWriteOwner, 0, contentId);

    public void RecordRejection(PlayerSession session, Task<long> echo, string reason) =>
        _ = ObserveWriteAsync(session.ContentId, messages.RecordRejectionAsync(session.ContentId, echo, reason));

    public void Attach() => chat.ChatMessageUnhandled += OnChatMessage;

    public void Dispose()
    {
        disposed = true;
        chat.ChatMessageUnhandled -= OnChatMessage;
        waitingTells.Clear();
        waitingParty.Clear();
    }

    public void Update(SocialSnapshot snapshot)
    {
        if (!ReferenceEquals(owner, snapshot.Session))
        {
            owner = snapshot.Session;
            waitingTells.Clear();
            waitingParty.Clear();
        }

        if (snapshot.Friends is not null)
        {
            while (waitingTells.TryDequeue(out var tell))
                ArchiveIfFriend(snapshot, tell);
        }

        if (snapshot.Statics is not null)
        {
            while (waitingParty.TryDequeue(out var partyMessage))
                ArchiveIfMatchedStatic(snapshot, partyMessage);
        }
    }

    internal static TellPeer? ReadPeer(SeString sender)
    {
        if (sender.Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
        {
            return string.IsNullOrWhiteSpace(player.PlayerName)
                ? null
                : new TellPeer(player.PlayerName, player.World.RowId, null, HasPlayerPayload: true);
        }

        var raw = sender.TextValue.Replace(SeIconChar.CrossWorld.ToIconString(), "@");
        var text = StripPrivateUse(raw).Trim();
        if (text.Length == 0)
            return null;

        var separator = text.IndexOf('@');
        return separator < 0
            ? new TellPeer(text, 0, null, HasPlayerPayload: false)
            : new TellPeer(text[..separator].Trim(), 0, text[(separator + 1)..].Trim(), HasPlayerPayload: false);
    }

    private static string StripPrivateUse(string value)
    {
        var hasPrivateUse = false;
        foreach (var ch in value)
        {
            if (ch is >= '\uE000' and <= '\uF8FF')
            {
                hasPrivateUse = true;
                break;
            }
        }

        if (!hasPrivateUse)
            return value;

        return string.Create(value.Length, value, static (span, src) =>
        {
            var len = 0;
            foreach (var ch in src)
            {
                if (ch is not (>= '\uE000' and <= '\uF8FF'))
                    span[len++] = ch;
            }

            span[len..].Fill(' ');
        }).Trim();
    }

    private static bool TryMapChatType(XivChatType logKind, out ConversationKind kind, out int slot)
    {
        slot = 0;
        switch (logKind)
        {
            case XivChatType.TellIncoming or XivChatType.TellOutgoing:
                kind = ConversationKind.Tell;
                return true;
            case XivChatType.FreeCompany:
                kind = ConversationKind.FreeCompany;
                return true;
            case XivChatType.Ls1: kind = ConversationKind.Linkshell; slot = 1; return true;
            case XivChatType.Ls2: kind = ConversationKind.Linkshell; slot = 2; return true;
            case XivChatType.Ls3: kind = ConversationKind.Linkshell; slot = 3; return true;
            case XivChatType.Ls4: kind = ConversationKind.Linkshell; slot = 4; return true;
            case XivChatType.Ls5: kind = ConversationKind.Linkshell; slot = 5; return true;
            case XivChatType.Ls6: kind = ConversationKind.Linkshell; slot = 6; return true;
            case XivChatType.Ls7: kind = ConversationKind.Linkshell; slot = 7; return true;
            case XivChatType.Ls8: kind = ConversationKind.Linkshell; slot = 8; return true;
            case XivChatType.CrossLinkShell1: kind = ConversationKind.CrossWorldLinkshell; slot = 1; return true;
            case XivChatType.CrossLinkShell2: kind = ConversationKind.CrossWorldLinkshell; slot = 2; return true;
            case XivChatType.CrossLinkShell3: kind = ConversationKind.CrossWorldLinkshell; slot = 3; return true;
            case XivChatType.CrossLinkShell4: kind = ConversationKind.CrossWorldLinkshell; slot = 4; return true;
            case XivChatType.CrossLinkShell5: kind = ConversationKind.CrossWorldLinkshell; slot = 5; return true;
            case XivChatType.CrossLinkShell6: kind = ConversationKind.CrossWorldLinkshell; slot = 6; return true;
            case XivChatType.CrossLinkShell7: kind = ConversationKind.CrossWorldLinkshell; slot = 7; return true;
            case XivChatType.CrossLinkShell8: kind = ConversationKind.CrossWorldLinkshell; slot = 8; return true;
            case XivChatType.Party or XivChatType.CrossParty:
                kind = ConversationKind.Static;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (!TryMapChatType(message.LogKind, out var kind, out var slot))
            return;

        var body = message.OriginalMessage.ExtractText();
        if (string.IsNullOrEmpty(body))
            return;

        var peer = ReadPeer(SeString.Parse(message.OriginalSender.Data.Span));
        var isLocalPlayer = message.SourceKind == XivChatRelationKind.LocalPlayer;
        if (kind == ConversationKind.Tell && peer is null)
            return;
        if (kind != ConversationKind.Tell && !isLocalPlayer && peer is null)
            return;

        var incoming = new IncomingChat(
            message.LogKind,
            kind,
            slot,
            peer,
            isLocalPlayer,
            message.LogKind == XivChatType.TellOutgoing,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            body);

        if (framework.IsInFrameworkUpdateThread)
        {
            Receive(incoming, directory.Update(rereadFriends: kind == ConversationKind.Tell, rereadSocial: kind != ConversationKind.Tell));
        }
        else if (directory.Current.Session is { } observed)
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (disposed)
                    return;

                var snapshot = directory.Update(rereadFriends: kind == ConversationKind.Tell, rereadSocial: kind != ConversationKind.Tell);
                if (ReferenceEquals(observed, snapshot.Session))
                    Receive(incoming, snapshot);
            });
        }
    }

    private void Receive(IncomingChat incoming, SocialSnapshot snapshot)
    {
        Update(snapshot);
        if (snapshot.Session is not { } session)
            return;

        if (incoming.Kind == ConversationKind.Tell)
        {
            var tell = new UnresolvedTell(incoming.Peer!.Value, incoming.SentAt, incoming.TellOutgoing, incoming.Body);
            if (snapshot.Friends is not null)
            {
                ArchiveIfFriend(snapshot, tell);
                return;
            }

            if (waitingTells.Count == PendingLimit)
                waitingTells.Dequeue();

            waitingTells.Enqueue(tell);
            return;
        }

        if (directory.ResolveGroupSender(snapshot, incoming.ChatType, incoming.Kind, incoming.Peer, incoming.IsLocalPlayer, out var outgoing)
            is not { } sender)
        {
            return;
        }

        switch (incoming.Kind)
        {
            case ConversationKind.FreeCompany:
                if (snapshot.FreeCompany is { } fc)
                    Archive(new CapturedMessage(session, fc.ToDescriptor(), sender, incoming.SentAt, outgoing, incoming.Body), fc.HasRealName);
                break;

            case ConversationKind.Linkshell:
                if (snapshot.Linkshells.TryGetValue(incoming.Slot, out var ls))
                    Archive(new CapturedMessage(session, ls.ToDescriptor(), sender, incoming.SentAt, outgoing, incoming.Body), ls.HasRealName);
                break;

            case ConversationKind.CrossWorldLinkshell:
                if (snapshot.CrossWorldLinkshells.TryGetValue(incoming.Slot, out var cwls))
                    Archive(new CapturedMessage(session, cwls.ToDescriptor(), sender, incoming.SentAt, outgoing, incoming.Body), cwls.HasRealName);
                break;

            case ConversationKind.Static:
                if (snapshot.Party is not { } party)
                    return;

                var partyMsg = new UnresolvedPartyMessage(party.RosterKey, sender, incoming.SentAt, outgoing, incoming.Body);
                if (snapshot.Statics is not null)
                {
                    ArchiveIfMatchedStatic(snapshot, partyMsg);
                    return;
                }

                if (waitingParty.Count == PendingLimit)
                    waitingParty.Dequeue();

                waitingParty.Enqueue(partyMsg);
                break;
        }
    }

    private void ArchiveIfFriend(SocialSnapshot snapshot, UnresolvedTell tell)
    {
        if (snapshot.Session is not { } session || snapshot.ResolveTellPeer(tell.Peer) is not { } friend)
            return;

        var sender = tell.Outgoing ? session.Player : friend;
        var captured = new CapturedMessage(session, ConversationDescriptor.FromFriend(friend), sender, tell.SentAt,
            tell.Outgoing, tell.Body);
        Archive(captured, updateDisplayName: true);
    }

    private void ArchiveIfMatchedStatic(SocialSnapshot snapshot, UnresolvedPartyMessage message)
    {
        if (snapshot.Session is not { } session || snapshot.Statics is null
            || !snapshot.Statics.TryGetValue(message.RosterKey, out var group))
        {
            return;
        }

        var captured = new CapturedMessage(session, group.ToDescriptor(), message.Sender, message.SentAt,
            message.Outgoing, message.Body);
        Archive(captured, updateDisplayName: true);
    }

    private void Archive(CapturedMessage captured, bool updateDisplayName)
    {
        var write = messages.RecordAsync(captured, updateDisplayName);
        _ = ObserveWriteAsync(captured.Session.ContentId, write);
        if (cache.IncompatibleOwner != captured.Session.ContentId)
            _ = ObserveCacheAsync(cache.UnhideConversationAsync(captured.Session.ContentId, captured.Conversation.Key));
        Captured?.Invoke(captured, write);
    }

    private async Task ObserveWriteAsync(ulong contentId, Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref failedWriteOwner, contentId);
            log.Error(exception, "Unable to save a captured message.");
        }
    }

    private async Task ObserveCacheAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Unable to unhide conversation in cache.");
        }
    }

    private sealed record IncomingChat(
        XivChatType ChatType,
        ConversationKind Kind,
        int Slot,
        TellPeer? Peer,
        bool IsLocalPlayer,
        bool TellOutgoing,
        long SentAt,
        string Body);

    private sealed record UnresolvedTell(TellPeer Peer, long SentAt, bool Outgoing, string Body);

    private sealed record UnresolvedPartyMessage(string RosterKey, PlayerIdentity Sender, long SentAt, bool Outgoing, string Body);
}
