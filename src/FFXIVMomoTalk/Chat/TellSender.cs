using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FFXIVMomoTalk.Chat;

internal sealed record TellWarning(PlayerSession Session, ulong? Peer, string Message)
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
    private const uint ErrorMessageLogKind = 60;
    private const string FallbackRejection = "Failed to send Tell. Check the in-game chat for details.";
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromSeconds(5);

    private readonly List<PendingTell> pending = new();
    private readonly ConcurrentQueue<TellWarning> warnings = new();
    private PendingTell? submitting;
    private bool disposed;

    public void Attach()
    {
        capture.Captured += OnCaptured;
        chat.LogMessage += OnLogMessage;
        chat.ChatMessageUnhandled += OnChatMessage;
    }

    public void Dispose()
    {
        disposed = true;
        capture.Captured -= OnCaptured;
        chat.LogMessage -= OnLogMessage;
        chat.ChatMessageUnhandled -= OnChatMessage;
        pending.Clear();
    }

    public bool TrySubmit(PlayerSession session, PlayerIdentity peer, string draft, out string error)
    {
        if (!TryBuildCommand(peer, draft, out var body, out _, out error))
            return false;

        _ = DispatchAsync(new PendingTell(session, peer, body));
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
                Warn(request, FallbackRejection);

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
            Warn(request, FallbackRejection);
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

        request.Target = friend;
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
            && candidate.Peer == tell.Peer.ContentId && candidate.Body == body);
        if (request is null)
        {
            // Track Tells sent directly from the native in-game chat box so server rejections still attach.
            pending.Add(new PendingTell(tell.Session, tell.Peer, body)
            {
                Echo = archive,
                StartedAt = Stopwatch.GetTimestamp(),
            });
            return;
        }

        request.Echo = archive;
        AttachRejection(request);
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (submitting is not { Rejection: null } current)
            return;

        if (message.LogKind is XivChatType.ErrorMessage or XivChatType.SystemError)
            Reject(current, FallbackRejection);
    }

    private void OnLogMessage(ILogMessage message)
    {
        var id = message.LogMessageId;
        var isKnown = ClassifyLogMessage(id, out var hasRecipient, out var nativeCallOnly);
        var isSyncError = submitting is not null && message.GameData.ValueNullable?.LogKind.RowId == ErrorMessageLogKind;
        if (!isKnown && !isSyncError)
            return;

        if (!isKnown)
            nativeCallOnly = true;

        var snapshot = directory.Current;
        if (snapshot.Session is not { } session)
            return;

        string? rawRecipient = null;
        TellPeer? recipient = null;
        if (hasRecipient && message.TryGetStringParameter(0, out var parameter))
        {
            var parsed = SeString.Parse(parameter.Data.Span);
            rawRecipient = parsed.TextValue.Replace(SeIconChar.CrossWorld.ToIconString(), "@").Trim();
            recipient = ChatCapture.ReadPeer(parsed);
        }

        if (message.TargetEntity is { IsPlayer: true, HomeWorldId: > 0 } target)
        {
            var identity = new TellPeer(target.Name.ExtractText(), target.HomeWorldId, null);
            if (snapshot.Resolve(identity) is not null)
                recipient = identity;
        }

        var reason = ReadRejection(id);
        if (framework.IsInFrameworkUpdateThread)
            HandleRejection(session, recipient, rawRecipient, nativeCallOnly, reason);
        else if (!nativeCallOnly)
            _ = framework.RunOnFrameworkThread(() => HandleRejection(session, recipient, rawRecipient, false, reason));
    }

    private void HandleRejection(
        PlayerSession session,
        TellPeer? recipient,
        string? rawRecipient,
        bool nativeCallOnly,
        string reason)
    {
        var snapshot = directory.Current;
        if (disposed || !ReferenceEquals(snapshot.Session, session))
            return;

        var peer = ResolveRejectionPeer(snapshot, session, recipient, rawRecipient);
        if (submitting is { } current && (peer is null || current.Peer == peer))
        {
            Reject(current, reason);
            return;
        }

        if (nativeCallOnly || (!string.IsNullOrWhiteSpace(rawRecipient) && peer is null))
            return;

        var candidates = pending.Where(request => request.Rejection is null && IsActive(request, session)
            && (peer is null || request.Peer == peer)).ToArray();
        if (candidates.Length == 1)
        {
            Reject(candidates[0], reason);
        }
        else if (candidates.Length > 0)
        {
            var firstPeer = candidates[0].Peer;
            ulong? conversation = candidates.All(request => request.Peer == firstPeer) ? firstPeer : null;
            warnings.Enqueue(new TellWarning(session, conversation, reason));
        }
        else if (peer is { } rejectedPeer)
        {
            warnings.Enqueue(new TellWarning(session, rejectedPeer, reason));
        }
    }

    private ulong? ResolveRejectionPeer(
        FriendSnapshot snapshot,
        PlayerSession session,
        TellPeer? recipient,
        string? rawRecipient)
    {
        if (recipient is { } target && snapshot.Resolve(target) is { } resolved)
            return resolved.ContentId;

        if (string.IsNullOrWhiteSpace(rawRecipient))
            return null;

        ulong? matchedPeer = null;
        foreach (var request in pending)
        {
            if (request.Rejection is not null || !IsActive(request, session))
                continue;

            var candidate = request.Target;
            var matches = StringComparer.OrdinalIgnoreCase.Equals(rawRecipient, candidate.Name)
                || StringComparer.OrdinalIgnoreCase.Equals(rawRecipient, $"{candidate.Name}@{candidate.World}")
                || StringComparer.OrdinalIgnoreCase.Equals(rawRecipient, $"{candidate.Name}{candidate.World}")
                || rawRecipient.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;

            if (matchedPeer is { } existing && existing != request.Peer)
                return null;

            matchedPeer = request.Peer;
        }

        return matchedPeer;
    }

    private static bool ClassifyLogMessage(uint id, out bool hasRecipient, out bool nativeCallOnly)
    {
        hasRecipient = id is 49 or 50 or 1642 or 7485;
        nativeCallOnly = id is 483 or 484 or 3875 or 3877;
        return hasRecipient
            || nativeCallOnly
            || id is 51 or 55 or 690 or 691 or 749 or 1641 or 1643
                or 3831 or 3832 or 3872 or 3873 or 3874 or 3876 or 3881
                or 5655 or 7382 or 7486 or 7489 or 9743;
    }

    private static string ReadRejection(uint logMessageId) => logMessageId switch
    {
        49 => "Unable to send the Tell because the recipient is busy.",
        50 or 7485 => "Unable to send the Tell. The recipient may be offline or unavailable.",
        51 => "Unable to send the Tell. The recipient is offline or could not be found.",
        55 => "Unable to send the Tell because the recipient is blacklisted.",
        483 => "Unable to send the Tell.",
        484 => "Unable to send the Tell because it contains disallowed text.",
        690 => "Unable to use Tell in the current duty.",
        691 => "Unable to send the Tell because the recipient is in a duty.",
        749 => "Unable to send the Tell due to chat rate limiting.",
        1641 => "Unable to use Tell during PvP.",
        1642 => "Unable to send the Tell because the recipient is in PvP.",
        1643 => "Unable to use Tell during a custom PvP match.",
        3831 => "Unable to chat right now.",
        3832 => "Unable to send the Tell because the recipient is in a restricted area.",
        3872 => "Unable to send the Tell because the recipient is not in the same duty.",
        3873 => "Unable to use Tell commands in the current duty.",
        3874 => "Unable to reply to a Tell from outside the current duty.",
        3875 => "Unable to use the Tell command right now.",
        3876 => "Unable to send the Tell because chat participation is disabled.",
        3877 => "Unable to send the Tell because the recipient's World is invalid.",
        3881 => "Unable to send the Tell. Open the Friend List to refresh recipient info and try again.",
        5655 => "Unable to send the Tell because the recipient is offline.",
        7382 => "Unable to send the Tell because chat is restricted.",
        7486 => "Unable to send the Tell because the Party Finder recruitment has ended.",
        7489 => "Unable to send the Tell because the recipient's World is not in this Data Center.",
        9743 => "Unable to send the Tell because the recipient is muted or blocked.",
        _ => FallbackRejection,
    };

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

    private sealed class PendingTell(PlayerSession session, PlayerIdentity target, string body)
    {
        public PlayerSession Session { get; } = session;

        public ulong Peer => Target.ContentId;

        public PlayerIdentity Target { get; set; } = target;

        public string Body { get; } = body;

        public Task<long>? Echo { get; set; }

        public TellWarning? Rejection { get; set; }

        public long StartedAt { get; set; }
    }
}
