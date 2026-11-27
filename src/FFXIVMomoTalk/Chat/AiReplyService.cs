using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
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

internal readonly record struct AiNotice(string Key, int? Number = null, string? Preview = null)
{
    public string Format(CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        var template = Loc.TextFor(culture, Key);
        var message = Number is int number ? string.Format(culture, template, number) : template;
        return Preview is null ? message : $"{message} {Preview}";
    }
}

internal sealed partial class AiReplyService(
    Configuration config,
    ChatCapture capture,
    MessageStore messageStore,
    IPluginLog log) : IAsyncDisposable
{
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

            var fallbackMessages = messages.Where(message => message.Rejection is null).TakeLast(maxMessages).ToArray();

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
                    .ReadAiContextAsync(request.Session.ContentId, request.Conversation.Key, request.MaxMessages)
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

            var candidates = AiReplyParser.ParseCandidates(rawContent);
            var valid = AiReplyParser.FilterValidReplies(request.Target, candidates);
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
}
