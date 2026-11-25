using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void DrawContactProfile(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var contentPos = pos;
        var contentSize = size;
        if (narrow)
        {
            var subHeaderH = 42f * scale;
            DrawSubHeader(draw, pos, size.X, subHeaderH, inspectedFriend?.Name ?? Loc.Text("Friends"), null, showBack: true, scale);
            contentPos = pos + new Vector2(0, subHeaderH);
            contentSize = new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH));
        }

        if (inspectedFriend is not { } friend)
        {
            var prompt = snapshot.Friends is null
                ? Loc.Text("LoadFriends")
                : Loc.Text("SelectFriend");
            var fontSize = ImGui.GetFontSize();
            var maxW = MathF.Max(60f * scale, contentSize.X - 48f * scale);
            var textSize = ChatVisuals.CalcTextSize(prompt, fontSize, maxW);
            ChatVisuals.AddText(draw, contentPos + (contentSize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), prompt, maxW);
            return;
        }

        var avatarSize = MathF.Min(112f * scale, MathF.Min(contentSize.X * 0.42f, contentSize.Y * 0.36f));
        avatarSize = MathF.Max(64f * scale, avatarSize);

        var maxCardTextWidth = MathF.Max(60f * scale, contentSize.X - 36f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.28f;
        var nameText = ChatVisuals.TruncateSingleLine(friend.Name, nameFontSize, maxCardTextWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        var friendKey = ConversationKey.Tell(friend.ContentId);
        var noteLayout = MeasureProfileNote(friendKey, maxCardTextWidth, scale);

        var badgeFontSize = ImGui.GetFontSize() * 0.96f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var worldLabel = ChatVisuals.TruncateSingleLine($"@ {friend.World}", badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(worldLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var totalHeight = avatarSize + 18f * scale + nameSize.Y + 10f * scale + noteLayout.Height + 14f * scale + badgeSize.Y;
        var startY = contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.Avatar(draw, avatars, friend, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.19f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##profile-start-chat", actionCenter, actionRadius, scale))
        {
            CommitActiveNote();
            OpenConversation(ConversationDescriptor.FromFriend(friend));
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Text("OpenChat"));

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + 18f * scale);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var notePos = new Vector2(centerX - noteLayout.BoxSize.X * 0.5f, namePos.Y + nameSize.Y + 10f * scale);
        DrawProfileNote(draw, friendKey, notePos, noteLayout, scale);

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, notePos.Y + noteLayout.Height + 14f * scale);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), worldLabel);
    }

    private void DrawGroupDetail(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var contentPos = pos;
        var contentSize = size;
        if (narrow)
        {
            var subHeaderH = 42f * scale;
            var title = inspectedGroup is { } inspected
                ? LocalizeChannelName(snapshot, inspected).DisplayName
                : Loc.Text("Groups");
            DrawSubHeader(draw, pos, size.X, subHeaderH, title, null, showBack: true, scale);
            contentPos = pos + new Vector2(0, subHeaderH);
            contentSize = new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH));
        }

        if (inspectedGroup is not { } group)
        {
            var prompt = Loc.Text("SelectGroup");
            var fontSize = ImGui.GetFontSize();
            var maxW = MathF.Max(60f * scale, contentSize.X - 48f * scale);
            var textSize = ChatVisuals.CalcTextSize(prompt, fontSize, maxW);
            ChatVisuals.AddText(draw, contentPos + (contentSize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), prompt, maxW);
            return;
        }

        StaticGroup? staticInfo = null;
        if (group.Key.Kind == ConversationKind.Static && snapshot.Statics is not null)
            snapshot.Statics.TryGetValue(group.Key.StableKey, out staticInfo);

        var avatarSize = staticInfo is not null
            ? Math.Clamp(MathF.Min(contentSize.X * 0.30f, contentSize.Y * 0.24f), 60f * scale, 92f * scale)
            : Math.Clamp(MathF.Min(contentSize.X * 0.42f, contentSize.Y * 0.36f), 64f * scale, 112f * scale);

        var maxCardTextWidth = MathF.Max(60f * scale, contentSize.X - 36f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.24f;
        var nameText = ChatVisuals.TruncateSingleLine(LocalizeChannelName(snapshot, group).DisplayName, nameFontSize, maxCardTextWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        var noteLayout = MeasureProfileNote(group.Key, maxCardTextWidth, scale);

        var badgeFontSize = ImGui.GetFontSize() * 0.94f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var badgeLabel = group.Key.Kind switch
        {
            ConversationKind.FreeCompany => Loc.Text("FreeCompany"),
            ConversationKind.CrossWorldLinkshell => snapshot.FindActiveSlot(group.Key) is { } cwSlot
                ? Loc.Format("ChannelSlot", Loc.Text("CrossWorldLinkshell"), cwSlot)
                : Loc.Text("CrossWorldLinkshell"),
            ConversationKind.Linkshell => snapshot.FindActiveSlot(group.Key) is { } lsSlot
                ? Loc.Format("ChannelSlot", Loc.Text("Linkshell"), lsSlot)
                : Loc.Text("Linkshell"),
            ConversationKind.Static => staticInfo is not null
                ? Loc.Format("StaticMembers", staticInfo.Members.Count)
                : Loc.Text("Static"),
            _ => string.Empty,
        };
        badgeLabel = ChatVisuals.TruncateSingleLine(badgeLabel, badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(badgeLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var nameGap = staticInfo is not null ? 14f * scale : 18f * scale;
        var noteGap = staticInfo is not null ? 8f * scale : 10f * scale;
        var badgeGap = staticInfo is not null ? 10f * scale : 14f * scale;
        var totalHeight = avatarSize + nameGap + nameSize.Y + noteGap + noteLayout.Height + badgeGap + badgeSize.Y;
        var startY = staticInfo is not null
            ? contentPos.Y + 20f * scale
            : contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.GroupAvatar(draw, group.Key.Kind, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.20f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##group-start-chat", actionCenter, actionRadius, scale))
        {
            CommitActiveNote();
            OpenConversation(group);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Text("OpenChat"));

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + nameGap);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var notePos = new Vector2(centerX - noteLayout.BoxSize.X * 0.5f, namePos.Y + nameSize.Y + noteGap);
        DrawProfileNote(draw, group.Key, notePos, noteLayout, scale);

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, notePos.Y + noteLayout.Height + badgeGap);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), badgeLabel);

        if (staticInfo is null)
            return;

        var matched = snapshot.IsStaticMatched(staticInfo.StableKey);
        var statusLabel = matched ? Loc.Text("StaticPartyMatched") : Loc.Text("StaticPartyNotMatched");
        var statusFontSize = ImGui.GetFontSize() * 0.88f;
        var statusPad = new Vector2(12f, 4f) * scale;
        var statusText = ChatVisuals.TruncateSingleLine(statusLabel, statusFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - statusPad.X * 2f));
        var statusTextSize = ChatVisuals.CalcTextSize(statusText, statusFontSize);
        var statusSize = statusTextSize + statusPad * 2f;
        var statusPos = new Vector2(centerX - statusSize.X * 0.5f, badgePos.Y + badgeSize.Y + 10f * scale);
        draw.AddRectFilled(statusPos, statusPos + statusSize,
            ChatVisuals.Color(matched ? ChatVisuals.MatchSuccessBg : ChatVisuals.SystemBg), statusSize.Y * 0.5f);
        ChatVisuals.AddText(draw, statusPos + statusPad, statusFontSize,
            ChatVisuals.Color(matched ? ChatVisuals.MatchSuccessText : ChatVisuals.SystemText), statusText);

        var rosterTop = statusPos.Y + statusSize.Y + 16f * scale;
        var rosterW = MathF.Min(380f * scale, maxCardTextWidth);
        var rosterX = centerX - rosterW * 0.5f;
        var rosterAvailH = contentPos.Y + contentSize.Y - rosterTop - 16f * scale;
        if (rosterAvailH < 48f * scale)
            return;

        var rosterBoxPos = new Vector2(rosterX, rosterTop);
        var rosterBoxSize = new Vector2(rosterW, rosterAvailH);
        draw.AddRectFilled(rosterBoxPos, rosterBoxPos + rosterBoxSize, ChatVisuals.Color(ChatVisuals.ListBg), 10f * scale);

        ImGui.SetCursorScreenPos(rosterBoxPos + new Vector2(12f * scale, 10f * scale));
        using var rosterChild = ImRaii.Child("##group-detail-roster",
            new Vector2(MathF.Max(20f * scale, rosterW - 24f * scale), MathF.Max(20f * scale, rosterAvailH - 20f * scale)),
            false);
        if (rosterChild)
            DrawMemberGrid(staticInfo.Members, scale);
    }

    private ProfileNoteLayout MeasureProfileNote(ConversationKey key, float maxCardTextWidth, float scale)
    {
        var note = notes.GetValueOrDefault(key);
        var isEditing = editingNoteFor == key;
        var noteFontSize = ImGui.GetFontSize() * 0.98f;
        var maxNoteWidth = MathF.Min(280f * scale, maxCardTextWidth);
        var notePad = new Vector2(12f, 6f) * scale;
        var wrapWidth = MathF.Max(40f * scale, maxNoteWidth - notePad.X * 2f);
        var displayNote = string.IsNullOrEmpty(note) ? Loc.Text("AddNote") : note;
        var noteTextSize = ChatVisuals.CalcTextSize(displayNote, noteFontSize, wrapWidth);
        var noteBoxSize = isEditing
            ? new Vector2(MathF.Min(260f * scale, maxCardTextWidth), 32f * scale)
            : new Vector2(MathF.Min(maxNoteWidth, noteTextSize.X + notePad.X * 2f), noteTextSize.Y + notePad.Y * 2f);
        var edit = noteEdits.GetValueOrDefault(key);
        var status = edit?.Write is not null ? Loc.Text("Saving") : edit?.Failed == true ? Loc.Text("SaveFailed") : null;
        var height = noteBoxSize.Y + (status is null ? 0f : noteFontSize + 2f * scale);
        return new ProfileNoteLayout(note, displayNote, noteFontSize, wrapWidth, noteTextSize, noteBoxSize, isEditing,
            status, height);
    }

    private void DrawProfileNote(
        ImDrawListPtr draw,
        ConversationKey key,
        Vector2 notePos,
        ProfileNoteLayout layout,
        float scale)
    {
        var noteBoxSize = layout.BoxSize;
        var edit = noteEdits.GetValueOrDefault(key);
        if (layout.Status is { } status)
        {
            var statusSize = ChatVisuals.CalcTextSize(status, layout.FontSize);
            var statusPos = notePos + new Vector2((noteBoxSize.X - statusSize.X) * 0.5f, noteBoxSize.Y + 2f * scale);
            var color = edit?.Failed == true ? ChatVisuals.AlertText : ChatVisuals.Muted;
            ChatVisuals.AddText(draw, statusPos, layout.FontSize, ChatVisuals.Color(color), status);
        }

        if (layout.IsEditing)
        {
            var activeEdit = noteEdits[key];
            var saving = activeEdit.Write is not null;
            var inputRounding = 8f * scale;
            draw.AddRectFilled(notePos, notePos + noteBoxSize, ChatVisuals.Color(ChatVisuals.White), inputRounding);
            var inputPadY = MathF.Max(2f, (noteBoxSize.Y - ImGui.GetFontSize()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(notePos.X + 8f * scale, notePos.Y));
            var enter = false;
            using (ImRaii.Disabled(saving))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
            using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
            using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, inputPadY)))
            {
                if (focusNoteInput)
                {
                    ImGui.SetKeyboardFocusHere();
                    focusNoteInput = false;
                }

                ImGui.SetNextItemWidth(MathF.Max(40f * scale, noteBoxSize.X - 16f * scale));
                enter = ImGui.InputTextWithHint($"##note-input-{key.StorageId}", Loc.Text("AddNote"), ref activeEdit.Text, 160,
                    ImGuiInputTextFlags.EnterReturnsTrue);
            }

            var inputActive = ImGui.IsItemActive();
            var deactivated = ImGui.IsItemDeactivated();
            draw.AddRect(notePos, notePos + noteBoxSize,
                ChatVisuals.Color(inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder),
                inputRounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));

            if (activeEdit.Failed && ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Text("RetryNoteWithEnter"));

            if (!saving && ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                noteEdits.Remove(key);
                editingNoteFor = null;
            }
            else if (!saving && (enter || (deactivated && !activeEdit.Failed)))
            {
                SaveNote(key, activeEdit);
            }

            return;
        }

        ImGui.SetCursorScreenPos(notePos);
        if (ImGui.InvisibleButton($"##note-btn-{key.StorageId}", noteBoxSize))
        {
            CommitActiveNote();
            noteEdits.TryAdd(key, new NoteEdit(layout.Note ?? string.Empty));
            editingNoteFor = key;
            focusNoteInput = true;
        }

        if (ImGui.IsItemHovered())
        {
            draw.AddRectFilled(notePos, notePos + noteBoxSize, ChatVisuals.Color(ChatVisuals.ListBg), 8f * scale);
            ImGui.SetTooltip(edit?.Failed == true ? Loc.Text("RetryNoteByClick") : Loc.Text("EditNote"));
        }

        var noteColor = string.IsNullOrEmpty(layout.Note) ? ChatVisuals.Placeholder : ChatVisuals.Muted;
        var textOrigin = new Vector2(
            notePos.X + (noteBoxSize.X - layout.TextSize.X) * 0.5f,
            notePos.Y + (noteBoxSize.Y - layout.TextSize.Y) * 0.5f);
        ChatVisuals.AddText(draw, textOrigin, layout.FontSize, ChatVisuals.Color(noteColor),
            layout.DisplayNote, layout.WrapWidth);
    }

    private void UpdateNotes()
    {
        if (notesQuery is { IsCompleted: true } completed)
        {
            notesQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                notes.Clear();
                foreach (var (key, value) in completed.Result)
                    notes[key] = value;
            }
            else if (completed.Exception is { } exception)
            {
                log.Error(exception, "Failed to read notes.");
            }
        }

        if (notesQuery is not null)
            return;

        foreach (var (key, edit) in noteEdits)
        {
            if (edit.Write is not { IsCompleted: true } write)
                continue;

            edit.Write = null;
            if (!write.IsCompletedSuccessfully)
            {
                edit.Failed = true;
                if (editingNoteFor == key)
                    focusNoteInput = true;
                continue;
            }

            var saved = edit.Text.Trim();
            if (saved.Length == 0)
                notes.Remove(key);
            else
                notes[key] = saved;

            noteEdits.Remove(key);
            if (editingNoteFor == key)
                editingNoteFor = null;
        }

        if (notesRevision != notesStore.Revision)
        {
            notesRevision = notesStore.Revision;
            notesQuery = notesStore.ReadNotesAsync(session!.ContentId);
        }
    }

    private void CommitActiveNote()
    {
        if (editingNoteFor is not { } noteKey)
            return;

        var edit = noteEdits[noteKey];
        if (!edit.Failed)
            SaveNote(noteKey, edit);
        editingNoteFor = null;
    }

    private void SaveNote(ConversationKey key, NoteEdit edit)
    {
        if (session is null || edit.Write is not null)
            return;

        var trimmed = edit.Text.Trim();
        if (trimmed == (notes.GetValueOrDefault(key) ?? string.Empty))
        {
            noteEdits.Remove(key);
            if (editingNoteFor == key)
                editingNoteFor = null;
            return;
        }

        edit.Failed = false;
        edit.Write = notesStore.SaveNoteAsync(session.ContentId, key, trimmed);
        ObserveStoreTask(edit.Write, "Failed to save note.");
    }

    private readonly record struct ProfileNoteLayout(
        string? Note,
        string DisplayNote,
        float FontSize,
        float WrapWidth,
        Vector2 TextSize,
        Vector2 BoxSize,
        bool IsEditing,
        string? Status,
        float Height);

    private sealed class NoteEdit(string text)
    {
        public string Text = text;
        public Task? Write;
        public bool Failed;
    }
}
