using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVMomoTalk.Chat;

internal static partial class AiReplyParser
{
    internal const int MaxReplyOptions = 3;

    internal static List<AiReplyOption> ParseCandidates(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return [];

        var unfenced = CleanResponseContent(rawContent);

        var jsonCandidates = TryParseJsonCandidates(unfenced, out var stopFallback);
        if (stopFallback)
            return jsonCandidates;

        return ParseLineFallback(unfenced);
    }

    internal static string CleanResponseContent(string rawContent)
    {
        var cleaned = ThinkBlockRegex().Replace(rawContent, string.Empty).Trim();
        return StripCodeFences(cleaned);
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

    private static List<AiReplyOption> TryParseJsonCandidates(string text, out bool stopFallback)
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

    internal static bool TrySliceJsonSpan(string text, char open, char close, out string slice)
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
        out List<AiReplyOption> candidates)
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
                CollectReplyItems(root, candidates);
                return candidates.Count > 0;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetPropertyIgnoreCase(root, "replies", out var replies))
            {
                hasKnownReplyKey = true;
                if (replies.ValueKind == JsonValueKind.Array)
                {
                    CollectReplyItems(replies, candidates);
                    if (candidates.Count > 0)
                        return true;
                }
            }

            if (TryGetPropertyIgnoreCase(root, "options", out var options))
            {
                hasKnownReplyKey = true;
                if (options.ValueKind == JsonValueKind.Array)
                {
                    CollectReplyItems(options, candidates);
                    if (candidates.Count > 0)
                        return true;
                }
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;

                CollectReplyItems(prop.Value, candidates);
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

    internal static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
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

    private static bool TryGetStringPropertyIgnoreCase(JsonElement obj, string name, out string? value)
    {
        if (TryGetPropertyIgnoreCase(obj, name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static void CollectReplyItems(JsonElement array, List<AiReplyOption> target)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                    target.Add(new AiReplyOption(text));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var hasText = TryGetStringPropertyIgnoreCase(item, "text", out var text)
                    || TryGetStringPropertyIgnoreCase(item, "reply", out text);
                if (!hasText || string.IsNullOrWhiteSpace(text))
                    continue;

                var translation = TryGetStringPropertyIgnoreCase(item, "translation", out var rawTranslation)
                    && !string.IsNullOrWhiteSpace(rawTranslation)
                    ? rawTranslation
                    : null;
                target.Add(new AiReplyOption(text, translation));
            }
        }
    }

    private static List<AiReplyOption> ParseLineFallback(string text)
    {
        var results = new List<AiReplyOption>();
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
                results.Add(new AiReplyOption(stripped));
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

    internal static AiReplyOption[] FilterValidReplies(
        SendTarget target,
        IEnumerable<AiReplyOption> candidates,
        bool allowTranslation = true)
    {
        var valid = new List<AiReplyOption>(MaxReplyOptions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var singleLine = CollapseSingleLine(candidate.Text);
            if (singleLine.Length == 0)
                continue;

            if (!ChatSender.TryBuildCommand(target, singleLine, out var normalizedBody, out _, out _))
                continue;

            if (!seen.Add(normalizedBody))
                continue;

            string? translation = null;
            if (allowTranslation && !string.IsNullOrWhiteSpace(candidate.Translation))
            {
                var normalizedTranslation = CollapseSingleLine(candidate.Translation);
                if (normalizedTranslation.Length > 0
                    && !StringComparer.OrdinalIgnoreCase.Equals(normalizedTranslation, normalizedBody))
                {
                    translation = normalizedTranslation;
                }
            }

            valid.Add(new AiReplyOption(normalizedBody, translation));
            if (valid.Count == MaxReplyOptions)
                break;
        }

        return [.. valid];
    }

    internal static string CollapseSingleLine(string text)
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

    [GeneratedRegex(@"<think\b[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]+|\(\d+\)|\[\d+\]|\d+[.)、])\s*")]
    private static partial Regex LeadingListPrefixRegex();
}
