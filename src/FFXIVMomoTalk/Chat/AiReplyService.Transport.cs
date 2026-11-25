using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class AiReplyService
{
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

        var detail = AiReplyParser.CollapseSingleLine(message);
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

    private sealed class AiRequestException(AiNotice displayMessage, string? logMessage = null)
        : Exception(logMessage ?? displayMessage.Format(CultureInfo.InvariantCulture))
    {
        public AiNotice DisplayMessage { get; } = displayMessage;
    }
}
