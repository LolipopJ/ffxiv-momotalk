using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVMomoTalk.Chat;

internal enum AiTranslationStatus
{
    Idle,
    Loading,
    Error,
}

internal readonly record struct AiTranslationSnapshot(
    AiTranslationStatus Status,
    long? AnchorMessageId,
    AiNotice? ErrorMessage,
    long GenerationId)
{
    public static AiTranslationSnapshot Idle { get; } = new(AiTranslationStatus.Idle, null, null, 0);
}

internal sealed partial class AiReplyService
{
    internal const int TranslationMessagesBefore = 5;
    internal const int TranslationMessagesAfter = 4;

    public long TranslationRevision => Interlocked.Read(ref translationRevision);

    public string? GetTranslation(ulong ownerContentId, long messageId, string currentBody) =>
        translationCache.TryGet(ownerContentId, messageId, currentBody);

    public AiTranslationSnapshot GetTranslationState(ConversationKey key)
    {
        lock (gate)
        {
            return translationStates.TryGetValue(key, out var state)
                ? new AiTranslationSnapshot(state.Status, state.AnchorMessageId, state.ErrorMessage, state.GenerationId)
                : AiTranslationSnapshot.Idle;
        }
    }

    public void ClearTranslationState(ConversationKey key)
    {
        lock (gate)
        {
            if (translationStates.Remove(key, out var existing))
            {
                existing.Cts?.Cancel();
                existing.Cts = null;
            }
        }
    }

    public bool TryTranslateMessages(
        PlayerSession session,
        ConversationDescriptor conversation,
        string channelTag,
        IReadOnlyList<ChatMessage> visibleMessages,
        long anchorMessageId)
    {
        var window = SelectTranslationWindow(visibleMessages, anchorMessageId);
        if (window.Length == 0)
            return false;

        lock (gate)
        {
            if (disposed || !config.IsAiReplyConfigured)
                return false;

            if (!ReferenceEquals(activeSession, session))
                SyncSessionLocked(session);

            if (translationStates.TryGetValue(conversation.Key, out var existing)
                && existing.Status == AiTranslationStatus.Loading)
            {
                return false;
            }

            var endpoint = config.LlmEndpoint;
            var apiKey = config.LlmApiKey;
            var model = config.LlmModel;
            var targetLanguage = config.EffectiveTranslationLanguage;

            var generationId = ++nextGenerationId;
            var requestCts = new CancellationTokenSource();
            translationStates[conversation.Key] = new ConversationTranslationState
            {
                Status = AiTranslationStatus.Loading,
                AnchorMessageId = anchorMessageId,
                ErrorMessage = null,
                GenerationId = generationId,
                Cts = requestCts,
            };

            var request = new TranslationRequest(
                session,
                conversation,
                channelTag,
                endpoint,
                apiKey,
                model,
                targetLanguage,
                window,
                generationId,
                requestCts);

            inFlight[generationId] = Task.Run(() => RunTranslateAsync(request));
            return true;
        }
    }

    internal static ChatMessage[] SelectTranslationWindow(IReadOnlyList<ChatMessage> messages, long anchorMessageId)
    {
        var translatable = new List<ChatMessage>(messages.Count);
        var anchorIndex = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.IsSystem)
                continue;

            if (message.Id == anchorMessageId)
                anchorIndex = translatable.Count;

            translatable.Add(message);
        }

        if (anchorIndex < 0)
            return [];

        var start = Math.Max(0, anchorIndex - TranslationMessagesBefore);
        var end = Math.Min(translatable.Count - 1, anchorIndex + TranslationMessagesAfter);
        var count = end - start + 1;
        var window = new ChatMessage[count];
        for (var i = 0; i < count; i++)
            window[i] = translatable[start + i];

        return window;
    }

    internal static string BuildTranslationSystemPrompt(string? targetLanguageCode)
    {
        var targetLanguageName = Loc.PromptLanguageName(targetLanguageCode);
        return $$"""
            You are a translator for FINAL FANTASY XIV in-game chat messages.
            Translate each message naturally and concisely into {{targetLanguageName}}, using surrounding messages and speaker context to resolve omitted subjects, pronouns, FFXIV terms, slang, and tone.

            Mandatory output format constraints (always follow):
            - Return ONLY valid JSON in the exact shape: {"translations": [{"id": 1, "translation": "..."}]}.
            - Include one entry for each input message id.
            - Preserve line breaks inside multi-line messages when appropriate, and do not add markdown, speaker prefixes, timestamps, or commentary.
            - If a message is already written purely in {{targetLanguageName}} and needs no translation, set "translation" to null.
            - Do not include extra JSON keys outside "translations", "id", and "translation".
            """;
    }

    internal static string BuildTranslationUserPrompt(
        ConversationDescriptor conversation,
        string channelTag,
        IReadOnlyList<ChatMessage> messages,
        string? targetLanguageCode)
    {
        var targetLanguageName = Loc.PromptLanguageName(targetLanguageCode);
        var channelType = conversation.Key.Kind switch
        {
            ConversationKind.Tell => "Tell (Private Message)",
            ConversationKind.FreeCompany => "Free Company",
            ConversationKind.Linkshell => "Linkshell",
            ConversationKind.CrossWorldLinkshell => "Cross-World Linkshell",
            ConversationKind.Static => "Static Party",
            _ => "Chat",
        };

        var targetName = conversation.Key.Kind == ConversationKind.Tell && !string.IsNullOrWhiteSpace(conversation.WorldName)
            ? $"{conversation.DisplayName} ({conversation.WorldName})"
            : string.IsNullOrWhiteSpace(channelTag)
                ? conversation.DisplayName
                : $"{conversation.DisplayName} [{channelTag}]";

        var payload = new TranslationPromptItem[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var speaker = message.Outgoing
                ? "You"
                : !string.IsNullOrWhiteSpace(message.Sender.Name)
                    ? message.Sender.Name
                    : conversation.DisplayName;
            payload[i] = new TranslationPromptItem(i + 1, speaker, message.Body);
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Channel: {channelType}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Conversation: {targetName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Target language: {targetLanguageName}");
        sb.AppendLine();
        sb.AppendLine("Messages to translate (oldest to newest):");
        sb.Append(JsonSerializer.Serialize(payload, ExampleJsonOptions));
        return sb.ToString();
    }

    private void CancelAllTranslationStatesLocked()
    {
        foreach (var state in translationStates.Values)
        {
            state.Cts?.Cancel();
            state.Cts = null;
        }

        translationStates.Clear();
    }

    private async Task RunTranslateAsync(TranslationRequest request)
    {
        try
        {
            if (!TryBuildCompletionsUri(request.Endpoint, out var endpointUri) || endpointUri is null)
            {
                CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, InvalidEndpointMessage);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, request.Cts.Token);
            var systemPrompt = BuildTranslationSystemPrompt(request.TargetLanguage);
            var userPrompt = BuildTranslationUserPrompt(
                request.Conversation,
                request.ChannelTag,
                request.Messages,
                request.TargetLanguage);

            var rawContent = await RequestCompletionAsync(
                endpointUri,
                request.ApiKey,
                request.Model,
                systemPrompt,
                userPrompt,
                linked.Token).ConfigureAwait(false);

            if (!AiTranslationParser.TryParse(rawContent, request.Messages, out var parsed))
            {
                CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, new("AiNoTranslations"));
                return;
            }

            CommitTranslationSuccess(request, parsed);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || request.Cts.IsCancellationRequested)
        {
            // Superseded, cleared, switched character, or shutting down.
        }
        catch (OperationCanceledException)
        {
            CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, new("AiRequestTimedOut"));
        }
        catch (AiRequestException exception)
        {
            log.Warning("AI translation request failed: {Reason}", exception.Message);
            CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, exception.DisplayMessage);
        }
        catch (HttpRequestException exception)
        {
            log.Warning("AI translation HTTP request failed ({FailureType}).", exception.GetType().Name);
            CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, new("AiEndpointUnreachable"));
        }
        catch (Exception exception)
        {
            log.Error("Unexpected AI translation failure ({FailureType}).", exception.GetType().Name);
            CommitTranslationError(request.Session, request.Conversation.Key, request.GenerationId, new("AiTranslateFailed"));
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(request.GenerationId);
                if (translationStates.TryGetValue(request.Conversation.Key, out var state)
                    && state.GenerationId == request.GenerationId)
                {
                    state.Cts = null;
                }

                request.Cts.Dispose();
            }
        }
    }

    private void CommitTranslationSuccess(TranslationRequest request, Dictionary<long, string?> parsed)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(activeSession, request.Session))
                return;

            if (!translationStates.TryGetValue(request.Conversation.Key, out var state)
                || state.GenerationId != request.GenerationId)
            {
                return;
            }

            foreach (var message in request.Messages)
            {
                if (!parsed.TryGetValue(message.Id, out var translation))
                    continue;

                if (translation is not null)
                    translationCache.Set(request.Session.ContentId, message.Id, message.Body, translation);
                else
                    translationCache.Remove(request.Session.ContentId, message.Id);
            }

            Interlocked.Increment(ref translationRevision);
            translationStates.Remove(request.Conversation.Key);
        }
    }

    private void CommitTranslationError(
        PlayerSession capturedSession,
        ConversationKey key,
        long generationId,
        AiNotice errorMessage)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(activeSession, capturedSession))
                return;

            if (!translationStates.TryGetValue(key, out var state) || state.GenerationId != generationId)
                return;

            state.Status = AiTranslationStatus.Error;
            state.ErrorMessage = errorMessage;
            state.Cts = null;
        }
    }

    private sealed class ConversationTranslationState
    {
        public AiTranslationStatus Status { get; set; }

        public long? AnchorMessageId { get; set; }

        public AiNotice? ErrorMessage { get; set; }

        public long GenerationId { get; set; }

        public CancellationTokenSource? Cts { get; set; }
    }

    private sealed record TranslationRequest(
        PlayerSession Session,
        ConversationDescriptor Conversation,
        string ChannelTag,
        string Endpoint,
        string ApiKey,
        string Model,
        string TargetLanguage,
        ChatMessage[] Messages,
        long GenerationId,
        CancellationTokenSource Cts);

    private sealed record TranslationPromptItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("text")] string Text);
}
