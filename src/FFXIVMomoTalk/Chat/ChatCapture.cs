using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace FFXIVMomoTalk.Chat;

internal sealed class ChatCapture(
    IChatGui chat,
    IFramework framework,
    FriendDirectory directory,
    HistoryStore history,
    IPluginLog log) : IDisposable
{
    private const int PendingLimit = 128;
    private readonly Queue<UnresolvedTell> waiting = new();
    private PlayerSession? owner;
    private ulong failedWriteOwner;
    private bool disposed;

    public event Action<CapturedTell, Task<long>>? Captured;

    public ulong FailedWriteOwner => Volatile.Read(ref failedWriteOwner);

    public void RecordRejection(PlayerSession session, Task<long> echo, string reason) =>
        _ = ObserveWriteAsync(session.ContentId, history.RecordRejectionAsync(session.ContentId, echo, reason));

    public void Attach() => chat.ChatMessageUnhandled += OnChatMessage;

    public void Dispose()
    {
        disposed = true;
        chat.ChatMessageUnhandled -= OnChatMessage;
        waiting.Clear();
    }

    public void Update(FriendSnapshot snapshot)
    {
        if (!ReferenceEquals(owner, snapshot.Session))
        {
            owner = snapshot.Session;
            waiting.Clear();
        }

        if (snapshot.Friends is null)
            return;

        while (waiting.TryDequeue(out var tell))
            ArchiveIfFriend(snapshot, tell);
    }

    internal static TellPeer? ReadPeer(SeString sender)
    {
        if (sender.Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
        {
            return string.IsNullOrWhiteSpace(player.PlayerName)
                ? null
                : new TellPeer(player.PlayerName, player.World.RowId, null);
        }

        var text = sender.TextValue.Replace(SeIconChar.CrossWorld.ToIconString(), "@").Trim();
        if (text.Length == 0)
            return null;

        var separator = text.IndexOf('@');
        return separator < 0
            ? new TellPeer(text, 0, null)
            : new TellPeer(text[..separator].Trim(), 0, text[(separator + 1)..].Trim());
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (message.LogKind is not (XivChatType.TellIncoming or XivChatType.TellOutgoing))
            return;

        if (ReadPeer(SeString.Parse(message.OriginalSender.Data.Span)) is not { } peer)
            return;

        var body = message.OriginalMessage.ExtractText();
        if (string.IsNullOrEmpty(body))
            return;

        var tell = new UnresolvedTell(peer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            message.LogKind == XivChatType.TellOutgoing, body);
        if (framework.IsInFrameworkUpdateThread)
        {
            Receive(tell, directory.Update(rereadFriends: true));
        }
        else if (directory.Current.Session is { } observed)
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (disposed)
                    return;

                var snapshot = directory.Update(rereadFriends: true);
                if (ReferenceEquals(observed, snapshot.Session))
                    Receive(tell, snapshot);
            });
        }
    }

    private void Receive(UnresolvedTell tell, FriendSnapshot snapshot)
    {
        Update(snapshot);
        if (snapshot.Session is null)
            return;

        if (snapshot.Friends is not null)
        {
            ArchiveIfFriend(snapshot, tell);
            return;
        }

        if (waiting.Count == PendingLimit)
            waiting.Dequeue();

        waiting.Enqueue(tell);
    }

    private void ArchiveIfFriend(FriendSnapshot snapshot, UnresolvedTell tell)
    {
        if (snapshot.Session is not { } session || snapshot.Resolve(tell.Peer) is not { } peer)
            return;

        var captured = new CapturedTell(session, peer, tell.SentAt, tell.Outgoing, tell.Body);
        var write = history.RecordAsync(captured);
        _ = ObserveWriteAsync(session.ContentId, write);
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
            log.Error(exception, "Unable to save a captured Tell.");
        }
    }

    private sealed record UnresolvedTell(TellPeer Peer, long SentAt, bool Outgoing, string Body);
}
