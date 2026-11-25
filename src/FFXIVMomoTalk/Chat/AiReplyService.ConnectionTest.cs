using System.Diagnostics;
using System.Net.Http.Headers;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class AiReplyService
{
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
}
