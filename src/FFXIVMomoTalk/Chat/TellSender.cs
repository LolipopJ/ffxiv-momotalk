using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FFXIVMomoTalk.Chat;

internal sealed record TellWarning(PlayerSession Session, PlayerKey? Peer, string Message)
{
    public volatile bool AttachedToMessage;
}

internal sealed class TellSender(
    IFramework framework,
    IChatGui chat,
    FriendDirectory directory,
    ChatCapture capture,
    IPluginLog log) : IDisposable
{
    private const int MaxCommandBytes = 500;
    private const uint RestrictedAreaLogId = 3832;
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromSeconds(8);
    private readonly List<PendingTell> pending = new();
    private readonly ConcurrentQueue<TellWarning> warnings = new();
    private PendingTell? submitting;
    private bool disposed;

    public void Attach()
    {
        capture.Captured += OnCaptured;
        chat.LogMessage += OnLogMessage;
    }

    public void Dispose()
    {
        disposed = true;
        capture.Captured -= OnCaptured;
        chat.LogMessage -= OnLogMessage;
        pending.Clear();
    }

    public bool TrySubmit(PlayerSession session, PlayerIdentity peer, string draft, out string error)
    {
        if (!TryBuildCommand(peer, draft, out var body, out _, out error))
            return false;

        _ = DispatchAsync(new PendingTell(session, peer.Key, body));
        return true;
    }

    public TellWarning? TakeWarning() => warnings.TryDequeue(out var warning) ? warning : null;

    public void Update(PlayerSession? session)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var request = pending[index];
            if (IsActive(request, session))
                continue;

            if (ReferenceEquals(request.Session, session) && request.Echo is null && request.Rejection is null)
                Warn(request, "The game did not confirm this Tell.");

            pending.RemoveAt(index);
        }
    }

    internal static bool TryBuildCommand(
        PlayerIdentity peer, string draft, out string body, out string command, out string error)
    {
        body = string.Empty;
        command = string.Empty;
        if (draft.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            error = "A Tell must be one line without control characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(draft))
        {
            error = "Enter a message before sending.";
            return false;
        }

        body = NormalizeBody(draft);
        command = $"/tell {peer.Name}@{peer.World} {body}";
        if (Encoding.UTF8.GetByteCount(command) > MaxCommandBytes)
        {
            error = "This Tell exceeds the game's 500-byte input limit.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task DispatchAsync(PendingTell request)
    {
        try
        {
            await framework.RunOnFrameworkThread(() => Dispatch(request)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error(exception, "The native Tell submission failed.");
            Warn(request, "The game could not submit this Tell.");
        }
    }

    private unsafe void Dispatch(PendingTell request)
    {
        if (disposed)
            return;

        var snapshot = directory.Update(rereadFriends: true);
        if (!ReferenceEquals(request.Session, snapshot.Session))
            return;

        if (snapshot.FindRecipient(request.Peer, out var unavailable) is not { } friend)
        {
            Warn(request, unavailable!);
            return;
        }

        if (!TryBuildCommand(friend, request.Body, out _, out var command, out var error))
        {
            Warn(request, error);
            return;
        }

        var ui = UIModule.Instance();
        if (ui is null)
        {
            Warn(request, "The game chat window is unavailable.");
            return;
        }

        var input = Utf8String.FromString(command);
        if (input is null)
        {
            Warn(request, "The game could not prepare this Tell.");
            return;
        }

        try
        {
            // Match the native chat input filter before submitting the original text.
            input->SanitizeString((AllowedEntities)0x27F);
            if (input->ToString() != command)
            {
                Warn(request, "The message contains text the game cannot send.");
                return;
            }

            request.StartedAt = Stopwatch.GetTimestamp();
            pending.Add(request);
            submitting = request;
            ui->ProcessChatBoxEntry(input);
        }
        catch
        {
            pending.Remove(request);
            throw;
        }
        finally
        {
            submitting = null;
            input->Dtor(true);
        }
    }

    private void OnCaptured(CapturedTell tell, Task<long> archive)
    {
        if (!tell.Outgoing)
            return;

        var body = NormalizeBody(tell.Body);
        var request = pending.FirstOrDefault(candidate => candidate.Echo is null && IsActive(candidate, tell.Session)
            && candidate.Peer == tell.Peer.Key && candidate.Body == body);
        if (request is null)
            return;

        request.Echo = archive;
        AttachRejection(request);
    }

    private void OnLogMessage(ILogMessage message)
    {
        var id = message.LogMessageId;
        var hasRecipient = id is 49 or 50 or 1642 or 7485;
        var nativeCallOnly = id is 51 or 483 or 484 or 3875 or 3876 or 7382;
        if (!hasRecipient && !nativeCallOnly && id is not (55 or RestrictedAreaLogId or 3872 or 3873 or 3874 or 3881 or 7486 or 9743))
            return;

        var snapshot = directory.Current;
        if (snapshot.Session is not { } session)
            return;

        TellPeer? recipient = hasRecipient && message.TryGetStringParameter(0, out var parameter)
            ? ChatCapture.ReadPeer(SeString.Parse(parameter.Data.Span)) : null;
        if (message.TargetEntity is { IsPlayer: true, HomeWorldId: > 0 } target)
        {
            var identity = new TellPeer(target.Name.ExtractText(), target.HomeWorldId, null);
            if (snapshot.Resolve(identity) is not null)
                recipient = identity;
        }
        var reason = ReadRejection(message);
        if (framework.IsInFrameworkUpdateThread)
            HandleRejection(session, recipient, hasRecipient, nativeCallOnly, reason);
        else if (!nativeCallOnly)
            _ = framework.RunOnFrameworkThread(() => HandleRejection(session, recipient, hasRecipient, false, reason));
    }

    private void HandleRejection(PlayerSession session, TellPeer? recipient, bool hasRecipient, bool nativeCallOnly, string reason)
    {
        var snapshot = directory.Current;
        if (disposed || !ReferenceEquals(snapshot.Session, session))
            return;

        var peer = recipient is { } target ? snapshot.Resolve(target)?.Key : null;
        if (submitting is { } current && (recipient is null || current.Peer == peer))
        {
            Reject(current, reason);
            return;
        }

        if (nativeCallOnly || (recipient is not null && peer is null))
            return;

        var candidates = pending.Where(request => request.Rejection is null && IsActive(request, session)
            && (peer is null || request.Peer == peer)).ToArray();
        if (candidates.Length == 1 && (!hasRecipient || peer is not null))
        {
            Reject(candidates[0], reason);
        }
        else if (candidates.Length > 0)
        {
            var firstPeer = candidates[0].Peer;
            PlayerKey? conversation = candidates.All(request => request.Peer == firstPeer) ? firstPeer : null;
            warnings.Enqueue(new TellWarning(session, conversation, reason));
        }
    }

    private static string ReadRejection(ILogMessage message)
    {
        if (message.ParameterCount == 0 && message.GameData.ValueNullable is { } row)
        {
            var text = row.Text.ExtractText().Trim();
            if (text.Length > 0)
                return text;
        }

        return message.LogMessageId == RestrictedAreaLogId
            ? "Unable to send the Tell because the recipient is in a restricted area."
            : "The game rejected the Tell. Check the in-game chat for details.";
    }

    private void Reject(PendingTell request, string reason)
    {
        if (request.Rejection is not null)
            return;

        request.Rejection = new TellWarning(request.Session, request.Peer, reason);
        if (request.Echo is null)
            warnings.Enqueue(request.Rejection);
        else
            AttachRejection(request);
    }

    private void AttachRejection(PendingTell request)
    {
        if (request.Echo is not { } echo || request.Rejection is not { } rejection)
            return;

        capture.RecordRejection(request.Session, echo, rejection.Message);
        rejection.AttachedToMessage = true;
    }

    private void Warn(PendingTell request, string reason) =>
        warnings.Enqueue(new TellWarning(request.Session, request.Peer, reason));

    private static bool IsActive(PendingTell request, PlayerSession? session) =>
        ReferenceEquals(request.Session, session) && Stopwatch.GetElapsedTime(request.StartedAt) < ReconciliationWindow;

    private static string NormalizeBody(string text) => text.Trim().Normalize(NormalizationForm.FormC);

    private sealed class PendingTell(PlayerSession session, PlayerKey peer, string body)
    {
        public PlayerSession Session { get; } = session;

        public PlayerKey Peer { get; } = peer;

        public string Body { get; } = body;

        public Task<long>? Echo { get; set; }

        public TellWarning? Rejection { get; set; }

        public long StartedAt { get; set; }
    }
}
