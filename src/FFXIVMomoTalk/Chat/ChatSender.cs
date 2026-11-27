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

internal sealed record SendWarning(PlayerSession Session, ConversationKey? Conversation, string Message)
{
    public volatile bool AttachedToMessage;
}

internal sealed class ChatSender(
    IFramework framework,
    IChatGui chat,
    SocialDirectory directory,
    ChatCapture capture,
    IPluginLog log) : IDisposable
{
    internal const int MaxCommandBytes = 500;
    private const uint ErrorMessageLogKind = 60;
    private const string FallbackTellRejection = "TellFailedCheckGameChat";
    private const string FallbackGroupRejection = "MessageFailedCheckGameChat";
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromSeconds(5);

    private readonly List<PendingMessage> pending = new();
    private readonly ConcurrentQueue<SendWarning> warnings = new();
    private PendingMessage? submitting;
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
        foreach (var request in pending)
            SaveFailedTell(request);

        pending.Clear();
    }

    public bool TrySubmit(
        PlayerSession session, SendTarget target, string draft, out Task<bool> submission, out string error)
    {
        if (!TryBuildCommand(target, draft, out var body, out _, out error))
        {
            submission = Task.FromResult(false);
            return false;
        }

        submission = DispatchAsync(new PendingMessage(session, target, body));
        return true;
    }

    public SendWarning? TakeWarning() => warnings.TryDequeue(out var warning) ? warning : null;

    public void Update(PlayerSession? session)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var request = pending[index];
            var rejectedWithoutEcho = request.Conversation.Kind == ConversationKind.Tell
                && request.Echo is null && request.Rejection is not null;
            if (!rejectedWithoutEcho && IsActive(request, session))
                continue;

            if (!SaveFailedTell(request) && ReferenceEquals(request.Session, session)
                && request.Echo is null && request.Rejection is null)
            {
                Warn(request, FallbackRejection(request.Conversation.Kind));
            }

            pending.RemoveAt(index);
        }
    }

    internal static bool TryBuildCommand(
        SendTarget target, string draft, out string body, out string command, out string error)
    {
        body = string.Empty;
        command = string.Empty;
        if (draft.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            error = "MessageMustBeSingleLine";
            return false;
        }

        if (string.IsNullOrWhiteSpace(draft))
        {
            error = "EnterMessageBeforeSending";
            return false;
        }

        body = NormalizeBody(draft);
        command = $"{target.CommandPrefix} {body}";
        if (Encoding.UTF8.GetByteCount(command) > MaxCommandBytes)
        {
            error = "MessageTooLong";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task<bool> DispatchAsync(PendingMessage request)
    {
        try
        {
            await framework.RunOnFrameworkThread(() => Dispatch(request)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error(exception, "The native chat submission failed.");
            Warn(request, FallbackRejection(request.Conversation.Kind));
        }

        return request.StartedAt != 0;
    }

    private unsafe void Dispatch(PendingMessage request)
    {
        if (disposed)
            return;

        var isTell = request.Conversation.Kind == ConversationKind.Tell;
        var snapshot = directory.Update(rereadFriends: isTell, rereadSocial: !isTell);
        if (!ReferenceEquals(request.Session, snapshot.Session))
            return;

        if (!directory.IsPlayerReady(request.Session))
        {
            Warn(request, "GameChatUnavailable");
            return;
        }

        if (snapshot.FindSendTarget(request.Conversation, out var unavailable) is not { } target)
        {
            Warn(request, unavailable!);
            return;
        }

        request.Target = target;
        if (!TryBuildCommand(target, request.Body, out _, out var command, out var error))
        {
            Warn(request, error);
            return;
        }

        var ui = UIModule.Instance();
        if (ui is null)
        {
            Warn(request, "GameChatUnavailable");
            return;
        }

        var input = Utf8String.FromString(command);
        if (input is null)
        {
            Warn(request, "GameCouldNotPrepareMessage");
            return;
        }

        try
        {
            // Match the native chat input filter before submitting the original text.
            input->SanitizeString((AllowedEntities)0x27F);
            if (input->ToString() != command)
            {
                Warn(request, "GameCannotSendText");
                return;
            }

            pending.Add(request);
            submitting = request;
            request.StartedAt = Stopwatch.GetTimestamp();
            request.SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ui->ProcessChatBoxEntry(input);
        }
        finally
        {
            submitting = null;
            input->Dtor(true);
        }
    }

    private void OnCaptured(CapturedMessage captured, Task<long> archive)
    {
        if (!captured.Outgoing || captured.Kind != MessageKind.Chat)
            return;

        var body = NormalizeBody(captured.Body);
        var request = submitting is { } current && MatchesEcho(current)
            ? current
            : pending.FirstOrDefault(candidate => MatchesEcho(candidate)
                && (candidate.Conversation.Kind != ConversationKind.Tell || candidate.Rejection is null));
        if (request is null)
        {
            // Track Tells sent directly from the native in-game chat box so server rejections still attach.
            if (captured.Conversation.ToTellPeer() is { } tellPeer)
            {
                var target = new SendTarget(captured.Conversation.Key, $"/tell {tellPeer.Name}@{tellPeer.World}",
                    tellPeer);
                pending.Add(new PendingMessage(captured.Session, target, body)
                {
                    Echo = archive,
                    StartedAt = Stopwatch.GetTimestamp(),
                });
            }

            return;
        }

        request.Echo = archive;
        AttachRejection(request);

        bool MatchesEcho(PendingMessage candidate) =>
            candidate.Echo is null && IsActive(candidate, captured.Session)
            && candidate.Conversation == captured.Conversation.Key && candidate.Body == body;
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (submitting is not { Rejection: null } current)
            return;

        if (message.LogKind is XivChatType.ErrorMessage or XivChatType.SystemError)
        {
            var reason = current.Conversation.IsGroup
                ? ExtractErrorReason(message.OriginalMessage.ExtractText(), current.Conversation.Kind)
                : FallbackTellRejection;
            Reject(current, reason);
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        var id = message.LogMessageId;
        var isKnown = ClassifyTellLogMessage(id, out var hasRecipient, out var nativeCallOnly);
        var isSyncError = submitting is not null && message.GameData.ValueNullable?.LogKind.RowId == ErrorMessageLogKind;
        if (!isKnown && !isSyncError)
            return;

        if (!isKnown)
            nativeCallOnly = true;

        var snapshot = directory.Current;
        if (snapshot.Session is not { } session)
            return;

        if (!hasRecipient && submitting is { Conversation.IsGroup: true } activeGroup)
        {
            var groupReason = ReadGroupLogRejection(id);
            if (framework.IsInFrameworkUpdateThread)
                Reject(activeGroup, groupReason);
            return;
        }

        TellPeer? recipient = null;
        if (hasRecipient && message.TryGetStringParameter(0, out var parameter))
            recipient = ChatCapture.ReadPeer(SeString.Parse(parameter.Data.Span));

        if (recipient is null or { WorldId: 0, World: null }
            && message.TargetEntity is { IsPlayer: true, HomeWorldId: > 0 } target)
        {
            var name = target.Name.ExtractText();
            if (recipient is not { } observed || SocialSnapshot.MatchesCharacterName(name, observed.Name, allowAbbreviation: true))
                recipient = new TellPeer(name, target.HomeWorldId, null);
        }

        var reason = ReadTellRejection(id);
        if (framework.IsInFrameworkUpdateThread)
            HandleRejection(session, id, recipient, hasRecipient, nativeCallOnly, reason);
        else if (!nativeCallOnly)
            _ = framework.RunOnFrameworkThread(() => HandleRejection(session, id, recipient, hasRecipient, false, reason));
    }

    private void HandleRejection(
        PlayerSession session,
        uint logMessageId,
        TellPeer? recipient,
        bool hasRecipient,
        bool nativeCallOnly,
        string reason)
    {
        var snapshot = directory.Current;
        if (disposed || !ReferenceEquals(snapshot.Session, session))
            return;

        var peerKey = ResolveRejectionConversation(snapshot, session, recipient);
        if (peerKey is null && (hasRecipient || recipient is not null))
            return;

        if (submitting is { } current && (peerKey is null || current.Conversation == peerKey))
        {
            Reject(current, reason);
            return;
        }

        if (nativeCallOnly)
            return;

        var mayBeGroupRejection = peerKey is null && IsGroupRejectionLogMessage(logMessageId);
        var candidates = pending.Where(request => request.Rejection is null && IsActive(request, session)
            && (request.Conversation.Kind == ConversationKind.Tell || mayBeGroupRejection)
            && (peerKey is null || request.Conversation == peerKey)).ToArray();
        if (candidates.Length == 1)
        {
            var target = candidates[0];
            Reject(target, target.Conversation.IsGroup ? ReadGroupLogRejection(logMessageId) : reason);
        }
        else if (candidates.Length > 0)
        {
            var firstConversation = candidates[0].Conversation;
            ConversationKey? conversation = candidates.All(request => request.Conversation == firstConversation)
                ? firstConversation
                : null;
            var warning = mayBeGroupRejection ? ReadGroupLogRejection(logMessageId) : reason;
            warnings.Enqueue(new SendWarning(session, conversation, warning));
        }
        else if (peerKey is { } rejectedKey)
        {
            warnings.Enqueue(new SendWarning(session, rejectedKey, reason));
        }
    }

    private ConversationKey? ResolveRejectionConversation(SocialSnapshot snapshot, PlayerSession session, TellPeer? recipient)
    {
        if (recipient is not { } target)
            return null;

        if (snapshot.ResolveTellPeer(target) is { } resolved)
            return ConversationKey.Tell(resolved.ContentId);

        var mayHaveUnseparatedWorld = target.WorldId == 0 && target.World is null;
        ConversationKey? matched = null;
        foreach (var request in pending)
        {
            if (request.Rejection is not null || !IsActive(request, session) || request.Target.TellPeer is not { } candidate
                || !SocialSnapshot.MatchesWorld(candidate, target))
            {
                continue;
            }

            if (!SocialSnapshot.MatchesCharacterName(candidate.Name, target.Name, allowAbbreviation: !target.HasPlayerPayload)
                && !(mayHaveUnseparatedWorld && StringComparer.OrdinalIgnoreCase.Equals(target.Name, candidate.Name + candidate.World)))
            {
                continue;
            }

            if (matched is { } existing && existing != request.Conversation)
                return null;

            matched = request.Conversation;
        }

        return matched;
    }

    private static bool IsGroupRejectionLogMessage(uint id) =>
        id is 484 or 749 or 3831 or 3876 or 7382;

    private static bool ClassifyTellLogMessage(uint id, out bool hasRecipient, out bool nativeCallOnly)
    {
        hasRecipient = id is 49 or 50 or 1642 or 7485;
        nativeCallOnly = id is 483 or 484 or 3875 or 3877;
        return hasRecipient
            || nativeCallOnly
            || id is 51 or 55 or 690 or 691 or 749 or 1641 or 1643
                or 3831 or 3832 or 3872 or 3873 or 3874 or 3876 or 3881
                or 5655 or 7382 or 7486 or 7489 or 9743;
    }

    private static string ReadGroupLogRejection(uint logMessageId) => logMessageId switch
    {
        484 => "MessageDisallowedText",
        749 => "MessageRateLimited",
        3831 => "ChatUnavailableNow",
        3876 => "MessageParticipationDisabled",
        7382 => "MessageChatRestricted",
        _ => FallbackGroupRejection,
    };

    private static string ReadTellRejection(uint logMessageId) => logMessageId switch
    {
        49 => "TellRecipientBusy",
        50 or 7485 => "TellRecipientMayBeOffline",
        51 => "TellRecipientNotFound",
        55 => "TellRecipientBlacklisted",
        483 => "TellFailed",
        484 => "TellDisallowedText",
        690 => "TellUnavailableInDuty",
        691 => "TellRecipientInDuty",
        749 => "TellRateLimited",
        1641 => "TellUnavailableInPvP",
        1642 => "TellRecipientInPvP",
        1643 => "TellUnavailableInCustomPvP",
        3831 => "ChatUnavailableNow",
        3832 => "TellRecipientInRestrictedArea",
        3872 => "TellRecipientInDifferentDuty",
        3873 => "TellCommandUnavailableInDuty",
        3874 => "TellCannotReplyOutsideDuty",
        3875 => "TellCommandUnavailableNow",
        3876 => "TellParticipationDisabled",
        3877 => "TellRecipientWorldInvalid",
        3881 => "TellRefreshFriendList",
        5655 => "TellRecipientOffline",
        7382 => "TellChatRestricted",
        7486 => "TellRecruitmentEnded",
        7489 => "TellRecipientInDifferentDataCenter",
        9743 => "TellRecipientMutedOrBlocked",
        _ => FallbackTellRejection,
    };

    private static string ExtractErrorReason(string rawText, ConversationKind kind)
    {
        var trimmed = rawText.Trim();
        return trimmed.Length > 0 ? trimmed : FallbackRejection(kind);
    }

    private static string FallbackRejection(ConversationKind kind) =>
        kind == ConversationKind.Tell ? FallbackTellRejection : FallbackGroupRejection;

    private void Reject(PendingMessage request, string reason)
    {
        if (request.Rejection is not null)
            return;

        request.Rejection = new SendWarning(request.Session, request.Conversation, reason);
        if (request.Echo is null)
            warnings.Enqueue(request.Rejection);
        else
            AttachRejection(request);
    }

    private void AttachRejection(PendingMessage request)
    {
        if (request.Echo is not { } echo || request.Rejection is not { } rejection)
            return;

        capture.RecordRejection(request.Session, echo, rejection.Message);
        rejection.AttachedToMessage = true;
    }

    private bool SaveFailedTell(PendingMessage request)
    {
        if (request.Echo is not null || request.Target.TellPeer is not { } peer)
            return false;

        var rejection = request.Rejection;
        capture.RecordFailedTell(request.Session, peer, request.SentAt, request.Body, rejection?.Message ?? FallbackTellRejection);
        if (rejection is not null)
            rejection.AttachedToMessage = true;

        return true;
    }

    private void Warn(PendingMessage request, string reason) =>
        warnings.Enqueue(new SendWarning(request.Session, request.Conversation, reason));

    private static bool IsActive(PendingMessage request, PlayerSession? session) =>
        ReferenceEquals(request.Session, session) && Stopwatch.GetElapsedTime(request.StartedAt) < ReconciliationWindow;

    private static string NormalizeBody(string text) => text.Trim().Normalize(NormalizationForm.FormC);

    private sealed class PendingMessage(PlayerSession session, SendTarget target, string body)
    {
        public PlayerSession Session { get; } = session;

        public ConversationKey Conversation => Target.Key;

        public SendTarget Target { get; set; } = target;

        public string Body { get; } = body;

        public Task<long>? Echo { get; set; }

        public SendWarning? Rejection { get; set; }

        public long StartedAt { get; set; }

        public long SentAt { get; set; }
    }
}
