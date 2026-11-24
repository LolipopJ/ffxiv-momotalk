using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace FFXIVMomoTalk.Chat;

internal enum AiReplyStatus
{
    Idle,
    Loading,
    Ready,
    Error,
}

internal enum AiConnectionTestStatus
{
    Idle,
    Testing,
    Success,
    Error,
}

internal readonly record struct AiReplySnapshot(
    AiReplyStatus Status,
    IReadOnlyList<string> Options,
    AiNotice? ErrorMessage,
    long GenerationId)
{
    public static AiReplySnapshot Idle { get; } = new(AiReplyStatus.Idle, [], null, 0);
}

internal readonly record struct AiConnectionTestSnapshot(
    AiConnectionTestStatus Status,
    AiNotice? Message)
{
    public static AiConnectionTestSnapshot Idle { get; } = new(AiConnectionTestStatus.Idle, null);
}

internal readonly record struct AiNotice(string Key, int? Number = null)
{
    public string Format(CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        var message = Loc.TextFor(culture, Key);
        return Number is int number ? string.Format(culture, message, number) : message;
    }
}

internal sealed partial class AiReplyService(
    Configuration config,
    ChatCapture capture,
    MessageStore messageStore,
    IPluginLog log) : IAsyncDisposable
{
    private const int MaxReplyOptions = 3;
    private const int MaxErrorPreviewLength = 140;
    private static readonly AiNotice InvalidEndpointMessage = new("AiInvalidEndpoint");
    private static readonly JsonSerializerOptions ExampleJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    private readonly CancellationTokenSource shutdown = new();
    private readonly Lock gate = new();
    private readonly Dictionary<ConversationKey, ConversationReplyState> states = new();
    private readonly Dictionary<long, Task> inFlight = new();

    private PlayerSession? activeSession;
    private long nextGenerationId;
    private AiConnectionTestStatus connectionTestStatus;
    private AiNotice? connectionTestMessage;
    private long connectionTestGenerationId;
    private CancellationTokenSource? connectionTestCts;
    private bool attached;
    private bool disposed;

    public void Attach()
    {
        if (attached)
            return;

        capture.Captured += OnCaptured;
        attached = true;
    }

    public void Detach()
    {
        if (!attached)
            return;

        capture.Captured -= OnCaptured;
        attached = false;
    }

    public AiReplySnapshot GetState(ConversationKey key)
    {
        lock (gate)
        {
            return states.TryGetValue(key, out var state)
                ? new AiReplySnapshot(state.Status, state.Options, state.ErrorMessage, state.GenerationId)
                : AiReplySnapshot.Idle;
        }
    }

    public void Clear(ConversationKey key)
    {
        lock (gate)
        {
            if (states.Remove(key, out var existing))
            {
                existing.Cts?.Cancel();
                existing.Cts = null;
            }
        }
    }

    public void ClearAll()
    {
        lock (gate)
            CancelAllStatesLocked();
    }

    public void SyncSession(PlayerSession? session)
    {
        lock (gate)
        {
            if (ReferenceEquals(activeSession, session))
                return;

            SyncSessionLocked(session);
        }
    }

    public AiConnectionTestSnapshot GetConnectionTestState()
    {
        lock (gate)
            return new AiConnectionTestSnapshot(connectionTestStatus, connectionTestMessage);
    }

    public void ResetConnectionTest()
    {
        lock (gate)
            ResetConnectionTestLocked();
    }

    public bool TryTestConnection()
    {
        lock (gate)
        {
            if (disposed || !config.IsAiReplyConfigured || connectionTestStatus == AiConnectionTestStatus.Testing)
                return false;

            connectionTestCts?.Cancel();
            var generationId = ++nextGenerationId;
            var testCts = new CancellationTokenSource();
            connectionTestGenerationId = generationId;
            connectionTestCts = testCts;
            connectionTestStatus = AiConnectionTestStatus.Testing;
            connectionTestMessage = null;

            var endpoint = config.LlmEndpoint;
            var apiKey = config.LlmApiKey;
            var model = config.LlmModel;
            inFlight[generationId] = Task.Run(() => RunConnectionTestAsync(endpoint, apiKey, model, generationId, testCts));
            return true;
        }
    }

    public bool TryGenerate(
        PlayerSession session,
        ConversationDescriptor conversation,
        SendTarget target,
        string channelTag,
        IReadOnlyList<ChatMessage> messages,
        string? note)
    {
        lock (gate)
        {
            if (disposed || !config.IsAiReplyConfigured)
                return false;

            if (!ReferenceEquals(activeSession, session))
                SyncSessionLocked(session);

            if (states.TryGetValue(conversation.Key, out var existing) && existing.Status == AiReplyStatus.Loading)
                return false;

            var endpoint = config.LlmEndpoint;
            var apiKey = config.LlmApiKey;
            var model = config.LlmModel;
            var prompt = config.EffectiveLlmPrompt;
            var examples = SnapshotExamples(config.EffectiveLlmExamples);
            var windowMinutes = config.LlmContextWindowMinutes;
            var maxMessages = config.LlmContextMaxMessages;
            var minMessages = Math.Min(config.LlmContextMinMessages, maxMessages);

            var start = Math.Max(0, messages.Count - maxMessages);
            var count = messages.Count - start;
            var fallbackMessages = new ChatMessage[count];
            for (var i = 0; i < count; i++)
                fallbackMessages[i] = messages[start + i];

            var generationId = ++nextGenerationId;
            var requestCts = new CancellationTokenSource();
            var state = new ConversationReplyState
            {
                Status = AiReplyStatus.Loading,
                Options = [],
                ErrorMessage = null,
                GenerationId = generationId,
                Cts = requestCts,
            };
            states[conversation.Key] = state;

            var request = new GenerationRequest(
                session,
                conversation,
                target,
                channelTag,
                endpoint,
                apiKey,
                model,
                prompt,
                examples,
                windowMinutes,
                minMessages,
                maxMessages,
                fallbackMessages,
                note?.Trim(),
                generationId,
                requestCts);

            inFlight[generationId] = Task.Run(() => RunGenerateAsync(request));
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            activeSession = null;
            running = inFlight.Values.ToArray();
            CancelAllStatesLocked();
            ResetConnectionTestLocked();
        }

        Detach();
        await shutdown.CancelAsync().ConfigureAwait(false);

        if (running.Length > 0)
        {
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Background task exceptions are handled inside RunGenerateAsync.
            }
        }

        httpClient.Dispose();
        shutdown.Dispose();
    }

    internal static bool TryBuildCompletionsUri(string rawEndpoint, out Uri? uri)
    {
        uri = null;
        var trimmed = rawEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
            return false;

        var builder = new UriBuilder(parsed);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path += "/chat/completions";

        builder.Path = path;
        uri = builder.Uri;
        return true;
    }

    internal static AiReplyPromptExample[] SnapshotExamples(IReadOnlyList<IAiReplyExample> examples)
    {
        var snapshot = new List<AiReplyPromptExample>(examples.Count);
        foreach (var example in examples)
        {
            var context = CollapseSingleLine(example.Context);
            if (context.Length == 0)
                continue;

            var replies = example.Replies.Select(CollapseSingleLine).Where(reply => reply.Length > 0)
                .Take(Configuration.MaxExampleReplies).ToImmutableArray();
            if (!replies.IsEmpty)
                snapshot.Add(new AiReplyPromptExample(context, replies));
        }

        return [.. snapshot];
    }

    internal static string BuildSystemPrompt(string? userPrompt, SendTarget target, IReadOnlyList<AiReplyPromptExample> examples)
    {
        var stylePrompt = string.IsNullOrWhiteSpace(userPrompt) ? AiReplyPresets.DefaultPrompt : userPrompt.Trim();
        var prompt = new StringBuilder(stylePrompt).AppendLine().AppendLine();
        if (examples.Count > 0)
        {
            prompt.AppendLine("Dialogue examples (tone and option-variety reference only; always reply in the language used by the recent conversation messages):");
            for (var i = 0; i < examples.Count; i++)
            {
                var example = examples[i];
                prompt.Append("Example ").Append(i + 1).AppendLine(":")
                    .Append("Incoming: ").AppendLine(JsonSerializer.Serialize(example.Context, ExampleJsonOptions))
                    .Append("Expected JSON: ").AppendLine(JsonSerializer.Serialize(new { replies = example.Replies }, ExampleJsonOptions));
            }

            prompt.AppendLine();
        }

        var maxBodyBytes = Math.Max(64, ChatSender.MaxCommandBytes - Encoding.UTF8.GetByteCount(target.CommandPrefix) - 1);
        return prompt.Append($$"""
            Mandatory output format constraints (always follow):
            - Return ONLY valid JSON in the exact shape: {"replies": ["...", "..."]}.
            - Provide 1 to {{MaxReplyOptions}} distinct, ready-to-send reply options.
            - Each reply MUST be a single line with no line breaks, no bullet/number prefixes, and no markdown.
            - Match the language used in the recent conversation messages.
            - Keep each reply concise and strictly within {{maxBodyBytes}} UTF-8 bytes so it fits in-game chat.
            - Do not include explanations, commentary, or extra JSON keys outside "replies".
            """).ToString();
    }

    internal static ChatMessage[] SelectContextMessages(
        IReadOnlyList<ChatMessage> recentChronological,
        long nowMs,
        int windowMinutes,
        int minMessages,
        int maxMessages)
    {
        if (recentChronological.Count == 0 || maxMessages <= 0)
            return [];

        var cutoffMs = nowMs - (long)Math.Max(1, windowMinutes) * 60_000L;
        var withinWindow = 0;
        for (var i = recentChronological.Count - 1; i >= 0; i--)
        {
            if (recentChronological[i].SentAt < cutoffMs)
                break;

            withinWindow++;
        }

        var effectiveMax = Math.Min(maxMessages, recentChronological.Count);
        var effectiveMin = Math.Min(Math.Clamp(minMessages, 0, maxMessages), effectiveMax);
        var takeCount = Math.Clamp(withinWindow, effectiveMin, effectiveMax);
        if (takeCount == 0)
            return [];

        var start = recentChronological.Count - takeCount;
        var selected = new ChatMessage[takeCount];
        for (var i = 0; i < takeCount; i++)
            selected[i] = recentChronological[start + i];

        return selected;
    }

    internal static string BuildUserPrompt(
        PlayerSession session,
        ConversationDescriptor conversation,
        string channelTag,
        ChatMessage[] recentMessages,
        string? note,
        long nowMs)
    {
        var sb = new StringBuilder();
        var nowLocal = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToLocalTime();
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

        sb.AppendLine($"Channel: {channelType}");
        sb.AppendLine($"Conversation: {targetName}");
        sb.AppendLine($"Your character ([You]): {session.Player.Name}");
        sb.AppendLine($"Current time: {nowLocal:yyyy-MM-dd HH:mm}");

        if (!string.IsNullOrWhiteSpace(note))
            sb.AppendLine($"User note: {note}");

        sb.AppendLine();

        if (recentMessages.Length == 0)
        {
            sb.AppendLine("No prior messages exist in this conversation.");
            sb.Append("Suggest 1 to 3 natural opening greeting replies for [You] to start this conversation.");
        }
        else
        {
            sb.AppendLine("Recent messages (oldest to newest):");
            foreach (var message in recentMessages)
            {
                var speaker = message.Outgoing
                    ? "You"
                    : !string.IsNullOrWhiteSpace(message.Sender.Name)
                        ? message.Sender.Name
                        : conversation.DisplayName;
                var sentLocal = DateTimeOffset.FromUnixTimeMilliseconds(message.SentAt).ToLocalTime();
                var timestamp = sentLocal.Date == nowLocal.Date
                    ? sentLocal.ToString("HH:mm")
                    : sentLocal.ToString("yyyy-MM-dd HH:mm");
                sb.Append('[').Append(timestamp).Append("] [").Append(speaker).Append("]: ").AppendLine(message.Body);
            }

            sb.AppendLine();
            sb.Append("Suggest 1 to 3 natural next replies for [You] matching the recent conversation language.");
        }

        return sb.ToString();
    }

    private void OnCaptured(CapturedMessage captured, Task<long> _)
    {
        if (!captured.Outgoing)
            return;

        lock (gate)
        {
            if (disposed || !ReferenceEquals(activeSession, captured.Session))
                return;

            if (states.Remove(captured.Conversation.Key, out var existing))
            {
                existing.Cts?.Cancel();
                existing.Cts = null;
            }
        }
    }

    private void SyncSessionLocked(PlayerSession? session)
    {
        activeSession = session;
        CancelAllStatesLocked();
    }

    private void CancelAllStatesLocked()
    {
        foreach (var state in states.Values)
        {
            state.Cts?.Cancel();
            state.Cts = null;
        }

        states.Clear();
    }

    private void ResetConnectionTestLocked()
    {
        connectionTestCts?.Cancel();
        connectionTestCts = null;
        connectionTestGenerationId++;
        connectionTestStatus = AiConnectionTestStatus.Idle;
        connectionTestMessage = null;
    }

    private async Task RunConnectionTestAsync(
        string endpoint,
        string apiKey,
        string model,
        long generationId,
        CancellationTokenSource testCts)
    {
        try
        {
            if (!TryBuildCompletionsUri(endpoint, out var endpointUri) || endpointUri is null)
            {
                CommitConnectionTest(generationId, AiConnectionTestStatus.Error, InvalidEndpointMessage);
                return;
            }

            var trimmedApiKey = apiKey.Trim();
            AuthenticationHeaderValue authHeader;
            try
            {
                authHeader = new AuthenticationHeaderValue("Bearer", trimmedApiKey);
            }
            catch (FormatException)
            {
                CommitConnectionTest(generationId, AiConnectionTestStatus.Error, new("AiInvalidApiKeyFormat"));
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, testCts.Token);
            var startedAt = Stopwatch.GetTimestamp();
            var result = await SendOnceAsync(
                endpointUri,
                authHeader,
                model.Trim(),
                "Reply with OK.",
                "ping",
                includeResponseFormat: false,
                linked.Token).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw CreateHttpError(result.StatusCode, result.Body, trimmedApiKey);

            _ = ExtractAssistantContent(result.Body);
            var elapsedMs = (int)Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            CommitConnectionTest(generationId, AiConnectionTestStatus.Success, new("AiConnected", elapsedMs));
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || testCts.IsCancellationRequested)
        {
            // Cancelled by config edit, modal close, or plugin shutdown.
        }
        catch (OperationCanceledException)
        {
            CommitConnectionTest(generationId, AiConnectionTestStatus.Error, new("AiConnectionTimedOut"));
        }
        catch (AiRequestException exception)
        {
            log.Warning("AI endpoint connection test failed: {Reason}", exception.Message);
            CommitConnectionTest(generationId, AiConnectionTestStatus.Error, exception.DisplayMessage);
        }
        catch (HttpRequestException exception)
        {
            log.Warning("AI endpoint connection test HTTP failed ({FailureType}).", exception.GetType().Name);
            CommitConnectionTest(generationId, AiConnectionTestStatus.Error, new("AiEndpointUnreachable"));
        }
        catch (Exception exception)
        {
            log.Error("Unexpected AI endpoint connection test failure ({FailureType}).", exception.GetType().Name);
            CommitConnectionTest(generationId, AiConnectionTestStatus.Error, new("AiConnectionFailed"));
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(generationId);
                if (connectionTestGenerationId == generationId)
                    connectionTestCts = null;

                testCts.Dispose();
            }
        }
    }

    private void CommitConnectionTest(long generationId, AiConnectionTestStatus status, AiNotice message)
    {
        lock (gate)
        {
            if (disposed || connectionTestGenerationId != generationId)
                return;

            connectionTestStatus = status;
            connectionTestMessage = message;
            connectionTestCts = null;
        }
    }

    private async Task RunGenerateAsync(GenerationRequest request)
    {
        try
        {
            if (!TryBuildCompletionsUri(request.Endpoint, out var endpointUri) || endpointUri is null)
            {
                CommitResult(
                    request.Session,
                    request.Conversation.Key,
                    request.GenerationId,
                    AiReplyStatus.Error,
                    [],
                    InvalidEndpointMessage);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, request.Cts.Token);
            IReadOnlyList<ChatMessage> loadedMessages;
            try
            {
                loadedMessages = await messageStore
                    .ReadRecentAsync(request.Session.ContentId, request.Conversation.Key, request.MaxMessages)
                    .WaitAsync(linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                log.Debug("Failed to read extended AI context from message store ({FailureType}); using visible messages.",
                    exception.GetType().Name);
                loadedMessages = request.FallbackMessages;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var contextMessages = SelectContextMessages(
                loadedMessages,
                nowMs,
                request.WindowMinutes,
                request.MinMessages,
                request.MaxMessages);
            var systemPrompt = BuildSystemPrompt(request.Prompt, request.Target, request.Examples);
            var userPrompt = BuildUserPrompt(
                request.Session,
                request.Conversation,
                request.ChannelTag,
                contextMessages,
                request.Note,
                nowMs);

            var rawContent = await RequestCompletionAsync(
                endpointUri,
                request.ApiKey,
                request.Model,
                systemPrompt,
                userPrompt,
                linked.Token).ConfigureAwait(false);

            var candidates = ParseCandidates(rawContent);
            var valid = FilterValidReplies(request.Target, candidates);
            if (valid.Length == 0)
            {
                CommitResult(
                    request.Session,
                    request.Conversation.Key,
                    request.GenerationId,
                    AiReplyStatus.Error,
                    [],
                    new("AiNoReplies"));
                return;
            }

            CommitResult(
                request.Session,
                request.Conversation.Key,
                request.GenerationId,
                AiReplyStatus.Ready,
                valid,
                null);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || request.Cts.IsCancellationRequested)
        {
            // Superseded, cleared, switched character, or shutting down.
        }
        catch (OperationCanceledException)
        {
            CommitResult(
                request.Session,
                request.Conversation.Key,
                request.GenerationId,
                AiReplyStatus.Error,
                [],
                new("AiRequestTimedOut"));
        }
        catch (AiRequestException exception)
        {
            log.Warning("AI reply request failed: {Reason}", exception.Message);
            CommitResult(
                request.Session,
                request.Conversation.Key,
                request.GenerationId,
                AiReplyStatus.Error,
                [],
                exception.DisplayMessage);
        }
        catch (HttpRequestException exception)
        {
            log.Warning("AI reply HTTP request failed ({FailureType}).", exception.GetType().Name);
            CommitResult(
                request.Session,
                request.Conversation.Key,
                request.GenerationId,
                AiReplyStatus.Error,
                [],
                new("AiEndpointUnreachable"));
        }
        catch (Exception exception)
        {
            log.Error("Unexpected AI reply generation failure ({FailureType}).", exception.GetType().Name);
            CommitResult(
                request.Session,
                request.Conversation.Key,
                request.GenerationId,
                AiReplyStatus.Error,
                [],
                new("AiGenerateFailed"));
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(request.GenerationId);
                if (states.TryGetValue(request.Conversation.Key, out var state)
                    && state.GenerationId == request.GenerationId)
                {
                    state.Cts = null;
                }

                request.Cts.Dispose();
            }
        }
    }

    private async Task<string> RequestCompletionAsync(
        Uri endpointUri,
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken token)
    {
        var trimmedApiKey = apiKey.Trim();
        AuthenticationHeaderValue authHeader;
        try
        {
            authHeader = new AuthenticationHeaderValue("Bearer", trimmedApiKey);
        }
        catch (FormatException)
        {
            throw new AiRequestException(new("AiInvalidApiKeyFormat"));
        }

        var trimmedModel = model.Trim();
        var result = await SendOnceAsync(
            endpointUri,
            authHeader,
            trimmedModel,
            systemPrompt,
            userPrompt,
            includeResponseFormat: true,
            token).ConfigureAwait(false);

        if (!result.IsSuccess
            && result.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
            && ShouldRetryWithoutResponseFormat(result.Body))
        {
            log.Debug("LLM endpoint rejected response_format (HTTP {StatusCode}); retrying without it.", (int)result.StatusCode);
            result = await SendOnceAsync(
                endpointUri,
                authHeader,
                trimmedModel,
                systemPrompt,
                userPrompt,
                includeResponseFormat: false,
                token).ConfigureAwait(false);
        }

        if (!result.IsSuccess)
            throw CreateHttpError(result.StatusCode, result.Body, trimmedApiKey);

        return ExtractAssistantContent(result.Body);
    }

    private async Task<(bool IsSuccess, HttpStatusCode StatusCode, string Body)> SendOnceAsync(
        Uri endpointUri,
        AuthenticationHeaderValue authHeader,
        string model,
        string systemPrompt,
        string userPrompt,
        bool includeResponseFormat,
        CancellationToken token)
    {
        var payloadJson = BuildRequestJson(model, systemPrompt, userPrompt, includeResponseFormat);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = authHeader;

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, token)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, response.StatusCode, body);
    }

    private static bool ShouldRetryWithoutResponseFormat(string errorBody)
    {
        var error = ReadApiError(errorBody);
        if ((error.Parameter is "response_format" or "response_format.type")
            && (error.Code is "unsupported_parameter" or "unsupported_value" or "unknown_parameter"))
        {
            return true;
        }

        var message = error.Message ?? errorBody.TrimStart();
        return !message.StartsWith('{') && !message.StartsWith('[') && UnsupportedResponseFormatRegex().IsMatch(message);
    }

    private static AiRequestException CreateHttpError(HttpStatusCode statusCode, string responseBody, string trimmedApiKey)
        => new(DescribeHttpError(statusCode), FormatHttpError(statusCode, responseBody, trimmedApiKey));

    private static string FormatHttpError(HttpStatusCode statusCode, string responseBody, string trimmedApiKey)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
            return DescribeHttpError(statusCode).Format(CultureInfo.InvariantCulture);

        var message = ReadApiError(responseBody).Message;
        if (string.IsNullOrWhiteSpace(message))
            return DescribeHttpError(statusCode).Format(CultureInfo.InvariantCulture);

        if (trimmedApiKey.Length > 0)
            message = message.Replace(trimmedApiKey, "[redacted]", StringComparison.Ordinal);

        var detail = CollapseSingleLine(message);
        if (detail.Length > MaxErrorPreviewLength)
            detail = detail[..(MaxErrorPreviewLength - 3)].TrimEnd() + "...";

        if (detail.Length == 0)
            return DescribeHttpError(statusCode).Format(CultureInfo.InvariantCulture);

        return statusCode switch
        {
            HttpStatusCode.Forbidden => $"Authentication error (HTTP 403): {detail}",
            HttpStatusCode.TooManyRequests => $"Rate limit exceeded (HTTP 429): {detail}",
            _ => $"HTTP {(int)statusCode}: {detail}",
        };
    }

    private static AiNotice DescribeHttpError(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new("AiAuthenticationError", (int)statusCode),
            HttpStatusCode.TooManyRequests => new("AiRateLimitExceeded"),
            _ => new("AiHttpError", (int)statusCode),
        };
    }

    private static (string? Message, string? Parameter, string? Code) ReadApiError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return default;

            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            var message = ReadString(error, "message") ?? ReadString(root, "message") ?? ReadString(root, "error");
            return (message, ReadString(error, "param"), ReadString(error, "code"));
        }
        catch (JsonException)
        {
            return default;
        }

        static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string BuildRequestJson(string model, string systemPrompt, string userPrompt, bool includeResponseFormat)
    {
        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt },
        };

        if (includeResponseFormat)
        {
            return JsonSerializer.Serialize(new
            {
                model,
                messages,
                response_format = new { type = "json_object" },
            });
        }

        return JsonSerializer.Serialize(new
        {
            model,
            messages,
        });
    }

    private static string ExtractAssistantContent(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new AiRequestException(new("AiNoChoices"));
            }

            var firstChoice = choices[0];
            if (firstChoice.ValueKind != JsonValueKind.Object
                || !firstChoice.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                throw new AiRequestException(new("AiMissingMessage"));
            }

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textElement.GetString());
                    }
                }

                return sb.ToString();
            }

            throw new AiRequestException(new("AiUnsupportedContent"));
        }
        catch (JsonException)
        {
            throw new AiRequestException(new("AiInvalidJson"));
        }
    }

    internal static List<string> ParseCandidates(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return [];

        var cleaned = ThinkBlockRegex().Replace(rawContent, string.Empty).Trim();
        var unfenced = StripCodeFences(cleaned);

        var jsonCandidates = TryParseJsonCandidates(unfenced, out var stopFallback);
        if (stopFallback)
            return jsonCandidates;

        return ParseLineFallback(unfenced);
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0)
            return trimmed.Trim('`').Trim();

        var inner = trimmed[(firstNewLine + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0)
            inner = inner[..lastFence];

        return inner.Trim();
    }

    private static List<string> TryParseJsonCandidates(string text, out bool stopFallback)
    {
        if (TryExtractFromJson(text, out var isNonStringJson, out _, out var parsed))
        {
            stopFallback = true;
            return parsed;
        }

        if (isNonStringJson)
        {
            stopFallback = true;
            return [];
        }

        if (TrySliceJsonSpan(text, '{', '}', out var objectSlice))
        {
            if (TryExtractFromJson(objectSlice, out _, out var hasKnownKey, out parsed))
            {
                stopFallback = true;
                return parsed;
            }

            if (hasKnownKey)
            {
                stopFallback = true;
                return [];
            }
        }

        if (TrySliceJsonSpan(text, '[', ']', out var arraySlice)
            && TryExtractFromJson(arraySlice, out _, out _, out parsed))
        {
            stopFallback = true;
            return parsed;
        }

        stopFallback = false;
        return [];
    }

    private static bool TrySliceJsonSpan(string text, char open, char close, out string slice)
    {
        var start = text.IndexOf(open);
        var end = text.LastIndexOf(close);
        if (start >= 0 && end > start)
        {
            slice = text[start..(end + 1)];
            return true;
        }

        slice = string.Empty;
        return false;
    }

    private static bool TryExtractFromJson(
        string json,
        out bool isNonStringJson,
        out bool hasKnownReplyKey,
        out List<string> candidates)
    {
        isNonStringJson = false;
        hasKnownReplyKey = false;
        candidates = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return false;

            isNonStringJson = true;
            if (root.ValueKind == JsonValueKind.Array)
            {
                CollectStringArray(root, candidates);
                return candidates.Count > 0;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetPropertyIgnoreCase(root, "replies", out var replies))
            {
                hasKnownReplyKey = true;
                if (replies.ValueKind == JsonValueKind.Array)
                {
                    CollectStringArray(replies, candidates);
                    if (candidates.Count > 0)
                        return true;
                }
            }

            if (TryGetPropertyIgnoreCase(root, "options", out var options))
            {
                hasKnownReplyKey = true;
                if (options.ValueKind == JsonValueKind.Array)
                {
                    CollectStringArray(options, candidates);
                    if (candidates.Count > 0)
                        return true;
                }
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;

                CollectStringArray(prop.Value, candidates);
                if (candidates.Count > 0)
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(prop.Name, name))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void CollectStringArray(JsonElement array, List<string> target)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                target.Add(text);
        }
    }

    private static List<string> ParseLineFallback(string text)
    {
        var results = new List<string>();
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("```", StringComparison.Ordinal))
                continue;

            if (trimmed.EndsWith(':') || trimmed.EndsWith('：'))
                continue;

            var stripped = LeadingListPrefixRegex().Replace(trimmed, string.Empty).Trim();
            stripped = UnwrapQuotes(stripped);
            if (!string.IsNullOrWhiteSpace(stripped))
                results.Add(stripped);
        }

        return results;
    }

    private static string UnwrapQuotes(string value)
    {
        if (value.Length < 2)
            return value;

        var first = value[0];
        var last = value[^1];
        var isWrapped = (first == '"' && last == '"')
            || (first == '\'' && last == '\'')
            || (first == '“' && last == '”')
            || (first == '「' && last == '」')
            || (first == '『' && last == '』');

        return isWrapped ? value[1..^1].Trim() : value;
    }

    private static string[] FilterValidReplies(SendTarget target, IEnumerable<string> candidates)
    {
        var valid = new List<string>(MaxReplyOptions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var singleLine = CollapseSingleLine(candidate);
            if (singleLine.Length == 0)
                continue;

            if (!ChatSender.TryBuildCommand(target, singleLine, out var normalizedBody, out _, out _))
                continue;

            if (!seen.Add(normalizedBody))
                continue;

            valid.Add(normalizedBody);
            if (valid.Count == MaxReplyOptions)
                break;
        }

        return [.. valid];
    }

    private static string CollapseSingleLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or '\u2028' or '\u2029' || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (char.IsControl(ch))
                continue;

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    private void CommitResult(
        PlayerSession capturedSession,
        ConversationKey key,
        long generationId,
        AiReplyStatus status,
        string[] options,
        AiNotice? errorMessage)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(activeSession, capturedSession))
                return;

            if (!states.TryGetValue(key, out var state) || state.GenerationId != generationId)
                return;

            state.Status = status;
            state.Options = options;
            state.ErrorMessage = errorMessage;
            state.Cts = null;
        }
    }

    [GeneratedRegex(@"<think\b[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]+|\(\d+\)|\[\d+\]|\d+[.)、])\s*")]
    private static partial Regex LeadingListPrefixRegex();

    [GeneratedRegex(
        """
        \b(?:unsupported|unknown|unrecognized)\s+
            (?:(?:request\s+)?(?:parameter|argument)(?:\s+supplied)?(?:\s*:\s*|\s+))? ['"\x60]?\bresponse_format\b
        | \b(?:does\s+not\s+support|doesn't\s+support)\s+(?:the\s+)?['"\x60]?\bresponse_format\b
        | \bresponse_format\b['"\x60]?(?:\s+(?:parameter|argument))?
            (?:\s+of\s+type\s+['"\x60]?json_(?:object|schema)['"\x60]?)?
            \s+(?:is\s+)?(?:not\s+supported|unsupported)\b
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex UnsupportedResponseFormatRegex();

    private sealed class ConversationReplyState
    {
        public AiReplyStatus Status { get; set; }

        public IReadOnlyList<string> Options { get; set; } = [];

        public AiNotice? ErrorMessage { get; set; }

        public long GenerationId { get; set; }

        public CancellationTokenSource? Cts { get; set; }
    }

    private sealed record GenerationRequest(
        PlayerSession Session,
        ConversationDescriptor Conversation,
        SendTarget Target,
        string ChannelTag,
        string Endpoint,
        string ApiKey,
        string Model,
        string Prompt,
        AiReplyPromptExample[] Examples,
        int WindowMinutes,
        int MinMessages,
        int MaxMessages,
        ChatMessage[] FallbackMessages,
        string? Note,
        long GenerationId,
        CancellationTokenSource Cts);

    private sealed class AiRequestException(AiNotice displayMessage, string? logMessage = null)
        : Exception(logMessage ?? displayMessage.Format(CultureInfo.InvariantCulture))
    {
        public AiNotice DisplayMessage { get; } = displayMessage;
    }
}
