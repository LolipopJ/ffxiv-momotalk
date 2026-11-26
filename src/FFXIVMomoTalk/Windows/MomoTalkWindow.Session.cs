using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void SyncSession(PlayerSession? active)
    {
        if (ReferenceEquals(session, active))
            return;

        session = active;
        aiReply.SyncSession(active);
        lastReplyStateConv = null;
        lastReplyStatus = AiReplyStatus.Idle;
        lastReplyGenerationId = 0;
        inspectedFriend = null;
        inspectedGroup = null;
        selected = null;
        conversation = null;
        bubbleMeasurements.Clear();
        summaryQuery = null;
        notesQuery = null;
        friendProfilesQuery = null;
        hiddenQuery = null;
        summaryRevision = -1;
        notesRevision = -1;
        friendProfilesRevision = -1;
        hiddenRevision = -1;
        hiddenLoaded = false;
        summariesLoaded = false;
        summaryFailed = false;
        summaries = [];
        rows = [];
        notes.Clear();
        noteEdits.Clear();
        friendTags.Clear();
        friendColors.Clear();
        activeTagFilters.Clear();
        lastStatics = null;
        hidden.Clear();
        drafts.Clear();
        unassignedWarning = null;
        dismissedUnavailableStores = null;
        narrowShowingDetail = false;
        contactsSearch = string.Empty;
        chatsSearch = string.Empty;
        groupsSearch = string.Empty;
        editingNoteFor = null;
        focusNoteInput = false;
        tagDraft = string.Empty;
        focusTagInput = false;
        tagPopupFriendId = null;
        tagSaveFriendId = null;
        tagSaveTask = null;
        tagSaveFailed = false;
        colorPickerFriendId = null;
        colorPickerOpen = false;
        colorPickerLivePreview = false;
        colorPickerDirty = false;
        colorPickerRgb = default;
        colorPickerHex = string.Empty;
        colorPickerHexInvalid = false;
        colorSaveFriendId = null;
        colorSaveTask = null;
        colorSaveFailed = false;
        contextFriend = null;
        contextConversation = null;
        contextStatic = null;
        contextMessageBody = null;
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
        staticSave = null;
        CloseStaticModal();
    }

    private void SyncLiveState(SocialSnapshot snapshot)
    {
        if (selected is { } currentSelected)
            selected = snapshot.RefreshDescriptor(currentSelected);

        if (!ReferenceEquals(lastStatics, snapshot.Statics))
        {
            lastStatics = snapshot.Statics;
            Reorder(snapshot);
        }

        if (inspectedFriend is { } friend && snapshot.Friends is not null)
        {
            if (snapshot.Friends.TryGetValue(friend.ContentId, out var current))
            {
                inspectedFriend = current;
            }
            else
            {
                if (editingNoteFor == ConversationKey.Tell(friend.ContentId))
                    editingNoteFor = null;
                inspectedFriend = null;
                if (activeTab == NavTab.Contacts)
                    narrowShowingDetail = false;
            }
        }

        if (inspectedGroup is { } group)
        {
            var stillActive = group.Key.Kind switch
            {
                ConversationKind.FreeCompany => snapshot.FreeCompany?.Key == group.Key,
                ConversationKind.Linkshell => snapshot.FindActiveSlot(group.Key) is not null,
                ConversationKind.CrossWorldLinkshell => snapshot.FindActiveSlot(group.Key) is not null,
                ConversationKind.Static => snapshot.Statics is null || snapshot.Statics.ContainsKey(group.Key.StableKey),
                _ => false,
            };

            if (stillActive)
            {
                inspectedGroup = snapshot.RefreshDescriptor(group);
            }
            else
            {
                if (editingNoteFor == group.Key)
                    editingNoteFor = null;
                inspectedGroup = null;
                if (activeTab == NavTab.Groups)
                    narrowShowingDetail = false;
            }
        }
    }

    private void UpdateHidden(SocialSnapshot snapshot)
    {
        if (hiddenQuery is { IsCompleted: true } completed)
        {
            hiddenQuery = null;
            hiddenLoaded = true;
            if (completed.IsCompletedSuccessfully)
            {
                hidden.Clear();
                hidden.UnionWith(completed.Result);
            }
            else if (completed.Exception is { } exception)
            {
                log.Error(exception, "Failed to read hidden conversations.");
            }

            Reorder(snapshot);
        }

        if (hiddenQuery is null && hiddenRevision != cache.HiddenRevision)
        {
            hiddenRevision = cache.HiddenRevision;
            hiddenQuery = cache.ReadHiddenConversationsAsync(session!.ContentId);
        }
    }

    private void UpdateSummaries(SocialSnapshot snapshot)
    {
        if (summaryQuery is { IsCompleted: true } completed)
        {
            summaryQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                var previous = summaries;
                summaries = completed.Result;
                if (summariesLoaded && hidden.Count > 0)
                {
                    var previousLatest = new Dictionary<ConversationKey, ChatMessage>(previous.Count);
                    foreach (var row in previous)
                    {
                        if (row.Latest is { } prevLatest)
                            previousLatest[row.Conversation.Key] = prevLatest;
                    }

                    foreach (var row in summaries)
                    {
                        if (row.Latest is { } latest && hidden.Contains(row.Conversation.Key)
                            && (!previousLatest.TryGetValue(row.Conversation.Key, out var oldLatest)
                                || latest.Id > oldLatest.Id
                                || !StringComparer.Ordinal.Equals(latest.Body, oldLatest.Body)))
                        {
                            hidden.Remove(row.Conversation.Key);
                        }
                    }
                }

                summariesLoaded = true;
                Reorder(snapshot);
            }
            else
            {
                if (completed.Exception is { } exception)
                    log.Error(exception, "Failed to read conversations.");
                summaryFailed = true;
            }
        }

        if (summaryQuery is null && !summaryFailed && summaryRevision != messages.Revision)
        {
            summaryRevision = messages.Revision;
            summaryQuery = messages.ReadConversationsAsync(session!.ContentId);
        }
    }

    private void DeleteChatFromList(SocialSnapshot snapshot, ConversationDescriptor conversationDesc)
    {
        if (session is null)
            return;

        hidden.Add(conversationDesc.Key);
        if (cache.IncompatibleOwner != session.ContentId)
        {
            ObserveStoreTask(cache.HideConversationAsync(session.ContentId, conversationDesc.Key),
                "Failed to hide conversation.");
        }
        aiReply.Clear(conversationDesc.Key);
        if (selected?.Key == conversationDesc.Key)
        {
            selected = null;
            conversation = null;
            bubbleMeasurements.Clear();
            narrowShowingDetail = false;
            inputFocused = false;
            inputFocusedConversation = null;
            wasShowingTyping = false;
        }

        Reorder(snapshot);
    }

    private string? GetUnavailableStores(ulong owner)
    {
        var msgFail = messages.FailedOwner == owner;
        var notesFail = notesStore.FailedOwner == owner;
        var staticsFail = staticsStore.FailedOwner == owner;
        var cacheFail = cache.FailedOwner == owner;
        var profilesFail = friendProfileStore.FailedOwner == owner;
        if (!msgFail && !notesFail && !staticsFail && !cacheFail && !profilesFail)
            return null;

        var failed = new List<string>(5);
        if (msgFail)
            failed.Add(messages.FileName);
        if (notesFail)
            failed.Add(notesStore.FileName);
        if (staticsFail)
            failed.Add(staticsStore.FileName);
        if (cacheFail)
            failed.Add(cache.FileName);
        if (profilesFail)
            failed.Add(friendProfileStore.FileName);

        return string.Join(", ", failed);
    }

    private static float DrawStorageErrorBar(
        ImDrawListPtr draw,
        Vector2 pos,
        float width,
        string text,
        float scale,
        out bool dismissed)
    {
        var fontSize = ImGui.GetFontSize() * 0.90f;
        var pad = new Vector2(12f * scale, 6f * scale);
        var closeBtnSize = 18f * scale;
        var closeGap = 8f * scale;
        var maxTextWidth = MathF.Max(40f * scale, width - pad.X * 2f - closeBtnSize - closeGap);
        var textSize = ChatVisuals.CalcTextSize(text, fontSize, maxTextWidth);
        var height = MathF.Max(closeBtnSize + pad.Y, textSize.Y + pad.Y * 2f);

        draw.AddRectFilled(pos, pos + new Vector2(width, height), ChatVisuals.Color(ChatVisuals.SystemBg));
        draw.AddLine(new Vector2(pos.X, pos.Y + height - 1f), new Vector2(pos.X + width, pos.Y + height - 1f),
            ChatVisuals.Color(ChatVisuals.Divider), 1f);
        ChatVisuals.AddText(draw, new Vector2(pos.X + pad.X, pos.Y + (height - textSize.Y) * 0.5f), fontSize,
            ChatVisuals.Color(ChatVisuals.AlertText), text, maxTextWidth);

        var btnPos = new Vector2(pos.X + width - pad.X - closeBtnSize, pos.Y + (height - closeBtnSize) * 0.5f);
        var cursorBackup = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(btnPos);
        dismissed = ImGui.InvisibleButton("##dismiss-storage-error", new Vector2(closeBtnSize));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetCursorScreenPos(cursorBackup);

        if (hovered || active)
        {
            var hoverCol = ChatVisuals.WithAlpha(ChatVisuals.AlertText, active ? 0.20f : 0.12f);
            draw.AddRectFilled(btnPos, btnPos + new Vector2(closeBtnSize), ChatVisuals.Color(hoverCol), 5f * scale);
        }

        ChatVisuals.DrawCloseIcon(
            draw,
            btnPos + new Vector2(closeBtnSize * 0.5f),
            10f * scale,
            ChatVisuals.Color(ChatVisuals.WithAlpha(ChatVisuals.AlertText, hovered || active ? 1f : 0.72f)),
            MathF.Max(1.3f, 1.5f * scale));
        return height;
    }

    private void ObserveStoreTask(Task task, string message) =>
        _ = task.ContinueWith(
            completed => log.Error(completed.Exception, message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
