using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private float DrawAiPromptSettings(ImDrawListPtr draw, Vector2 pos, AiSettingsMetrics metrics,
        List<AiReplyExample>? customExamples, ChatVisuals.AiExamplePreview[] previewCards, float alpha, bool interactive)
    {
        var presetId = config.LlmPromptPreset;
        var isCustom = customExamples is not null;
        var ink = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha));
        ChatVisuals.AddBoldText(draw, pos, metrics.FontSize, ink, Loc.Text("SystemPrompt"), metrics.Scale);

        var stylePos = pos + new Vector2(0, metrics.LabelHeight);
        var copyLabel = isCustom ? null : Loc.Text("CopyToCustom");
        var copyWidth = copyLabel is null ? 0
            : ChatVisuals.MeasureCompactModalButton(copyLabel, metrics.Scale, metrics.Width * 0.60f);
        var comboWidth = copyLabel is null ? metrics.Width : metrics.Width - copyWidth - metrics.Gap;
        var selectedPreset = DrawAiPresetCombo(stylePos, presetId, comboWidth, metrics);
        var copy = copyLabel is not null && ChatVisuals.DrawCompactModalButton(draw, "##ai-copy-preset", copyLabel,
            stylePos + new Vector2(metrics.Width - copyWidth, 0), new Vector2(copyWidth, metrics.RowHeight),
            interactive, metrics.Scale, alpha);

        var promptPos = stylePos + new Vector2(0, metrics.RowHeight + metrics.Gap);
        var promptSize = new Vector2(metrics.Width, metrics.PromptHeight);
        var rounding = 7f * metrics.Scale;
        var background = isCustom ? ChatVisuals.White : ChatVisuals.ListBg;
        draw.AddRectFilled(promptPos, promptPos + promptSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(background, alpha)), rounding);

        var padding = new Vector2(metrics.Gap);
        var promptValue = isCustom ? config.LlmCustomPrompt : config.EffectiveLlmPrompt;
        ImGui.SetCursorScreenPos(promptPos + padding);
        bool promptChanged;
        using (ImRaii.PushColor(ImGuiCol.Text, ChatVisuals.Ink)
            .Push(ImGuiCol.FrameBg, Vector4.Zero)
            .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
            .Push(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f)
            .Push(ImGuiStyleVar.FramePadding, Vector2.Zero))
        {
            promptChanged = aiPromptEditor.Draw($"##ai-prompt-{presetId}", ref promptValue,
                promptSize - 2f * padding, readOnly: !isCustom || !interactive);
        }

        if (promptChanged && isCustom)
        {
            config.LlmCustomPrompt = promptValue;
            saveConfig();
        }

        DrawSettingFieldBorder(draw, promptPos, promptSize, rounding, metrics.Scale, alpha, interactive && isCustom);
        var examplesPos = promptPos + new Vector2(0, metrics.PromptHeight + metrics.SectionGap);
        var bottom = DrawAiExamples(draw, examplesPos, metrics, customExamples, previewCards, alpha, interactive);

        if (copy)
        {
            config.CopyPresetToCustom(presetId);
            aiExampleEditorRevision++;
            saveConfig();
        }
        else if (selectedPreset is not null && selectedPreset != presetId && interactive)
        {
            config.LlmPromptPreset = selectedPreset;
            saveConfig();
        }

        return bottom;
    }

    private static string? DrawAiPresetCombo(Vector2 pos, string presetId, float width, AiSettingsMetrics metrics)
    {
        var customLabel = Loc.Text("AiPresetCustom");
        var preview = presetId == AiReplyPresets.CustomId ? customLabel
            : Loc.Text((AiReplyPresets.Find(presetId) ?? AiReplyPresets.Default).DisplayNameKey);
        using var colors = ChatVisuals.PushSettingsComboColors();
        using var style = ChatVisuals.PushSettingsComboStyle(metrics.RowHeight, metrics.Scale);
        using var combo = ChatVisuals.BeginSettingsCombo("##ai-preset", preview, pos, width);
        if (!combo)
            return null;

        if (ChatVisuals.SelectSettingsOption(customLabel, AiReplyPresets.CustomId, presetId == AiReplyPresets.CustomId, metrics.RowHeight))
            return AiReplyPresets.CustomId;

        foreach (var preset in AiReplyPresets.All)
        {
            if (ChatVisuals.SelectSettingsOption(Loc.Text(preset.DisplayNameKey), preset.Id, presetId == preset.Id, metrics.RowHeight))
                return preset.Id;
        }

        return null;
    }

    private float DrawAiExamples(ImDrawListPtr draw, Vector2 pos, AiSettingsMetrics metrics,
        List<AiReplyExample>? customExamples, ChatVisuals.AiExamplePreview[] previewCards, float alpha, bool interactive)
    {
        var canAddExample = customExamples is { Count: < Configuration.MaxCustomExamples };
        var addLabel = Loc.Text("AddExample");
        var addWidth = canAddExample ? ChatVisuals.MeasureCompactModalButton(addLabel, metrics.Scale, metrics.Width * 0.42f) : 0;
        var titleWidth = canAddExample ? metrics.Width - addWidth - metrics.Gap : metrics.Width;
        var title = ChatVisuals.TruncateSingleLine(Loc.Text("ConversationExamples"), metrics.FontSize, titleWidth);
        var titleDelta = (metrics.RowHeight - metrics.FontSize) * 0.5f;
        var ink = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha));
        ChatVisuals.AddBoldText(draw, pos + new Vector2(0, titleDelta), metrics.FontSize, ink, title, metrics.Scale);
        var add = canAddExample && ChatVisuals.DrawCompactModalButton(draw, "##ai-example-add", addLabel,
            pos + new Vector2(metrics.Width - addWidth, 0), new Vector2(addWidth, metrics.RowHeight),
            interactive, metrics.Scale, alpha);
        pos.Y += metrics.RowHeight + metrics.Gap;

        if (customExamples is null)
        {
            for (var i = 0; i < previewCards.Length; i++)
            {
                var card = previewCards[i];
                ChatVisuals.DrawAiExamplePreview(draw, pos, metrics.Width, Loc.Format("ExampleItemTitle", i + 1),
                    metrics.LabelHeight, metrics.FontSize, metrics.Scale, alpha, card);
                pos.Y += card.Height + metrics.Gap;
            }

            return pos.Y;
        }

        if (customExamples.Count == 0)
        {
            var empty = Loc.Text("NoExamplesYet");
            ChatVisuals.AddText(draw, pos, metrics.FontSize,
                ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.SystemText, alpha)), empty, metrics.Width);
            pos.Y += ChatVisuals.CalcTextSize(empty, metrics.FontSize, metrics.Width).Y;
        }

        List<AiReplyExample>? edits = null;
        var removeAt = -1;
        var structureChanged = add;
        using var revisionId = ImRaii.PushId(aiExampleEditorRevision);
        for (var i = 0; i < customExamples.Count; i++)
        {
            using var exampleId = ImRaii.PushId(i);
            var example = customExamples[i];
            var edit = DrawCustomAiExample(draw, pos, metrics, example, Loc.Format("ExampleItemTitle", i + 1),
                alpha, interactive, out var remove, out var repliesChanged);
            if (remove)
                removeAt = i;
            if (edit is not null)
                (edits ??= [.. customExamples])[i] = edit;
            structureChanged |= remove || repliesChanged;
            pos.Y += metrics.EditorHeight(example.Replies.Count) + metrics.Gap;
        }

        if (removeAt >= 0)
            (edits ??= [.. customExamples]).RemoveAt(removeAt);
        if (add)
            (edits ??= [.. customExamples]).Add(new AiReplyExample());
        if (structureChanged)
            aiExampleEditorRevision++;
        if (edits is not null)
        {
            config.LlmCustomExamples = edits;
            saveConfig();
        }

        return pos.Y;
    }

    private static AiReplyExample? DrawCustomAiExample(ImDrawListPtr draw, Vector2 pos, AiSettingsMetrics metrics,
        AiReplyExample example, string title, float alpha, bool interactive, out bool remove, out bool repliesChanged)
    {
        var count = example.Replies.Count;
        var cardSize = new Vector2(metrics.Width, metrics.EditorHeight(count));
        ChatVisuals.DrawAiExampleCard(draw, pos, cardSize, metrics.Scale, alpha);
        pos += new Vector2(metrics.Gap);
        var width = metrics.Width - 2f * metrics.Gap;
        var height = metrics.RowHeight;
        var ink = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha));
        var removeLabel = Loc.Text("Remove");
        var removeWidth = ChatVisuals.MeasureCompactModalButton(removeLabel, metrics.Scale, width * 0.40f);
        var removeSize = new Vector2(removeWidth, height);
        var titleDelta = (height - metrics.FontSize) * 0.5f;
        ChatVisuals.AddBoldText(draw, pos + new Vector2(0, titleDelta), metrics.FontSize, ink, title, metrics.Scale);
        remove = ChatVisuals.DrawCompactModalButton(draw, "##remove-example", removeLabel,
            pos + new Vector2(width - removeWidth, 0), removeSize, interactive, metrics.Scale, alpha);

        pos.Y += height + metrics.FieldGap;
        ChatVisuals.AddText(draw, pos, metrics.FontSize, ink, Loc.Text("ExampleContext"));
        pos.Y += metrics.LabelHeight;
        var context = example.Context;
        var contextChanged = DrawSettingTextField(draw, "##context", Loc.Text("ExampleContextHint"), pos,
            width, height, ref context, 1024, ImGuiInputTextFlags.None, metrics.Scale, alpha, interactive);

        pos.Y += height + metrics.FieldGap;
        ChatVisuals.AddText(draw, pos, metrics.FontSize, ink, Loc.Text("ExampleReplies"));
        pos.Y += metrics.LabelHeight;
        var replyHint = Loc.Text("ExampleReplyHint");
        var replyWidth = count > 1 ? width - removeWidth - metrics.Gap : width;
        List<string>? edits = null;
        var removeAt = -1;
        for (var i = 0; i < count; i++)
        {
            using var replyId = ImRaii.PushId(i);
            var reply = example.Replies[i];
            if (DrawSettingTextField(draw, "##reply", replyHint, pos, replyWidth, height, ref reply, 1024,
                ImGuiInputTextFlags.None, metrics.Scale, alpha, interactive))
            {
                (edits ??= [.. example.Replies])[i] = reply;
            }

            if (count > 1 && ChatVisuals.DrawCompactModalButton(draw, "##remove-reply", removeLabel,
                pos + new Vector2(width - removeWidth, 0), removeSize, interactive, metrics.Scale, alpha))
            {
                removeAt = i;
            }

            pos.Y += height + metrics.FieldGap;
        }

        var addReply = false;
        if (count < Configuration.MaxExampleReplies)
        {
            var addLabel = Loc.Text("AddReply");
            var addWidth = ChatVisuals.MeasureCompactModalButton(addLabel, metrics.Scale, width);
            addReply = ChatVisuals.DrawCompactModalButton(draw, "##add-reply", addLabel, pos,
                new Vector2(addWidth, height), interactive, metrics.Scale, alpha);
        }

        if (removeAt >= 0)
            (edits ??= [.. example.Replies]).RemoveAt(removeAt);
        if (addReply)
            (edits ??= [.. example.Replies]).Add(string.Empty);
        repliesChanged = removeAt >= 0 || addReply;

        return contextChanged || edits is not null
            ? new AiReplyExample { Context = context, Replies = edits ?? example.Replies }
            : null;
    }

    private readonly record struct AiSettingsMetrics(float Width, float RowHeight, float FontSize, float LabelHeight, float Scale)
    {
        public float Gap => 8f * Scale;
        public float SectionGap => 12f * Scale;
        public float FieldGap => 5f * Scale;
        public float PromptHeight => 96f * Scale;
        public float HeaderHeight => LabelHeight + RowHeight + Gap + PromptHeight + SectionGap + RowHeight + Gap;

        public float EditorHeight(int replyCount) =>
            2f * Gap + 2f * LabelHeight + (2 + replyCount) * RowHeight + (replyCount + 1) * FieldGap
            + (replyCount < Configuration.MaxExampleReplies ? RowHeight + FieldGap : 0);
    }
}
