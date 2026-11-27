using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FFXIVMomoTalk.Chat;

internal static class AiTranslationParser
{
    private static readonly string[] TranslationPropertyNames = ["translation", "text", "translated"];
    internal static bool TryParse(
        string rawContent,
        IReadOnlyList<ChatMessage> sourceMessages,
        out Dictionary<long, string?> translations)
    {
        translations = new Dictionary<long, string?>();
        if (string.IsNullOrWhiteSpace(rawContent) || sourceMessages.Count == 0)
            return false;

        var cleaned = AiReplyParser.CleanResponseContent(rawContent);
        if (TryExtractFromJson(cleaned, sourceMessages, translations))
            return true;

        if (AiReplyParser.TrySliceJsonSpan(cleaned, '{', '}', out var objectSlice)
            && TryExtractFromJson(objectSlice, sourceMessages, translations))
        {
            return true;
        }

        if (AiReplyParser.TrySliceJsonSpan(cleaned, '[', ']', out var arraySlice)
            && TryExtractFromJson(arraySlice, sourceMessages, translations))
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractFromJson(
        string json,
        IReadOnlyList<ChatMessage> sourceMessages,
        Dictionary<long, string?> target)
    {
        target.Clear();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array)
            {
                items = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (!TryFindTranslationArray(root, out items))
                    return false;
            }
            else
            {
                return false;
            }

            var arrayLength = items.GetArrayLength();
            if (arrayLength == 0)
                return true;

            var matchedCount = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryReadItemId(item, out var promptId) || promptId < 1 || promptId > sourceMessages.Count)
                    continue;

                var source = sourceMessages[promptId - 1];
                string? normalized = null;
                if (TryReadTranslationText(item, out var rawText) && !string.IsNullOrWhiteSpace(rawText))
                {
                    var cleanedText = NormalizeMultiline(rawText);
                    var normalizedSource = NormalizeMultiline(source.Body);
                    if (cleanedText.Length > 0
                        && !StringComparer.OrdinalIgnoreCase.Equals(cleanedText, normalizedSource))
                    {
                        normalized = cleanedText;
                    }
                }

                target[source.Id] = normalized;
                matchedCount++;
            }

            return matchedCount > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindTranslationArray(JsonElement root, out JsonElement array)
    {
        if (AiReplyParser.TryGetPropertyIgnoreCase(root, "translations", out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (AiReplyParser.TryGetPropertyIgnoreCase(root, "messages", out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (AiReplyParser.TryGetPropertyIgnoreCase(root, "items", out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                array = property.Value;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static bool TryReadItemId(JsonElement item, out int id)
    {
        if (AiReplyParser.TryGetPropertyIgnoreCase(item, "id", out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out id))
                return true;

            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                return true;
            }
        }

        id = 0;
        return false;
    }

    private static bool TryReadTranslationText(JsonElement item, out string? text)
    {
        foreach (var name in TranslationPropertyNames)
        {
            if (!AiReplyParser.TryGetPropertyIgnoreCase(item, name, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString();
                return true;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                text = null;
                return true;
            }
        }

        text = null;
        return false;
    }

    internal static string NormalizeMultiline(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousWasCr = false;
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                sb.Append('\n');
                previousWasCr = true;
                continue;
            }

            if (ch == '\n')
            {
                if (!previousWasCr)
                    sb.Append('\n');
                previousWasCr = false;
                continue;
            }

            previousWasCr = false;
            if (ch is '\u2028' or '\u2029')
            {
                sb.Append('\n');
                continue;
            }

            if (ch == '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (char.IsControl(ch))
                continue;

            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}
