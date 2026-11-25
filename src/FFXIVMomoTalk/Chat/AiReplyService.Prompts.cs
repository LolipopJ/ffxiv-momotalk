using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class AiReplyService
{
    internal static AiReplyPromptExample[] SnapshotExamples(IReadOnlyList<IAiReplyExample> examples)
    {
        var snapshot = new List<AiReplyPromptExample>(examples.Count);
        foreach (var example in examples)
        {
            var context = AiReplyParser.CollapseSingleLine(example.Context);
            if (context.Length == 0)
                continue;

            var replies = example.Replies.Select(AiReplyParser.CollapseSingleLine).Where(reply => reply.Length > 0)
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
        return prompt.Append(CultureInfo.InvariantCulture, $$"""
            Mandatory output format constraints (always follow):
            - Return ONLY valid JSON in the exact shape: {"replies": ["...", "..."]}.
            - Provide 1 to {{AiReplyParser.MaxReplyOptions}} distinct, ready-to-send reply options.
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

        sb.AppendLine(CultureInfo.InvariantCulture, $"Channel: {channelType}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Conversation: {targetName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Your character ([You]): {session.Player.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Current time: {nowLocal:yyyy-MM-dd HH:mm}");

        if (!string.IsNullOrWhiteSpace(note))
            sb.AppendLine(CultureInfo.InvariantCulture, $"User note: {note}");

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
                    ? sentLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : sentLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                sb.Append('[').Append(timestamp).Append("] [").Append(speaker).Append("]: ").AppendLine(message.Body);
            }

            sb.AppendLine();
            sb.Append("Suggest 1 to 3 natural next replies for [You] matching the recent conversation language.");
        }

        return sb.ToString();
    }
}
