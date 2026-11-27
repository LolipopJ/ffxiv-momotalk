using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void DrawConversationPane(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var subHeaderH = 42f * scale;
        if (selected is not { } currentConv || conversation is not { } view)
        {
            if (narrow)
                DrawSubHeader(draw, pos, size.X, subHeaderH, Loc.Text("Chats"), null, showBack: true, scale);

            var emptyPos = narrow ? pos + new Vector2(0, subHeaderH) : pos;
            var emptySize = narrow ? new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH)) : size;
            var emptyPrompt = Loc.Text("ChooseConversation");
            var fontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(emptyPrompt, fontSize);
            ChatVisuals.AddText(draw, emptyPos + (emptySize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), emptyPrompt);
            return;
        }

        var subLabel = currentConv.Key.Kind == ConversationKind.Tell
            ? (string.IsNullOrEmpty(currentConv.WorldName) ? null : $"@ {currentConv.WorldName}")
            : FormatChannelTag(snapshot, currentConv);
        DrawSubHeader(draw, pos, size.X, subHeaderH, LocalizeChannelName(snapshot, currentConv).DisplayName,
            subLabel, showBack: narrow, scale);
        view.Update();

        var draft = drafts[currentConv.Key];
        if (draft.Pending is { Submission.IsCompleted: true } pending)
        {
            if (pending.Submission.IsCompletedSuccessfully && pending.Submission.Result
                && draft.Revision == pending.Revision)
                draft.Text = string.Empty;

            draft.Pending = null;
        }

        var sendTarget = snapshot.FindSendTarget(currentConv.Key, out var unavailableReason);
        var unavailable = unavailableReason is not null ? Loc.Reason(unavailableReason) : null;
        var needsNativeList = unavailableReason is "OpenFriendsToSendTell"
            or "FreeCompanyUnavailable" or "FreeCompanyLeft"
            or "LinkshellUnavailable" or "LinkshellLeft"
            or "CrossWorldLinkshellUnavailable" or "CrossWorldLinkshellLeft";
        var replyState = config.IsAiReplyConfigured ? aiReply.GetState(currentConv.Key) : AiReplySnapshot.Idle;
        var translateState = config.IsAiReplyConfigured ? aiReply.GetTranslationState(currentConv.Key) : AiTranslationSnapshot.Idle;
        var warning = draft.Warning is { AttachedToMessage: false } local ? local : unassignedWarning;
        var warningText = warning is { AttachedToMessage: false } visible ? Loc.Reason(visible.Message) : null;
        var aiError = replyState.Status == AiReplyStatus.Error ? replyState.ErrorMessage?.Format() : null;
        var translateError = translateState.Status == AiTranslationStatus.Error ? translateState.ErrorMessage?.Format() : null;
        var errorStatus = warningText ?? aiError ?? translateError;
        var writeFailed = capture.FailedWriteOwner == session!.ContentId;
        var showTyping = sendTarget is not null
            && inputFocused
            && inputFocusedConversation == currentConv.Key
            && !string.IsNullOrWhiteSpace(draft.Text);

        var bannerMaxWidth = MathF.Max(80f * scale, size.X - 36f * scale);
        ChatVisuals.BannerLayout? infoBanner = unavailable is not null
            ? ChatVisuals.MeasureSystemBanner(unavailable, bannerMaxWidth, scale, closable: false)
            : null;
        ChatVisuals.BannerLayout? errorBanner = errorStatus is not null
            ? ChatVisuals.MeasureSystemBanner(errorStatus, bannerMaxWidth, scale, closable: true)
            : null;
        var writeErrorText = Loc.Text("MessageSaveFailed");
        ChatVisuals.BannerLayout? writeErrorBanner = writeFailed
            ? ChatVisuals.MeasureSystemBanner(writeErrorText, bannerMaxWidth, scale, closable: true)
            : null;

        var nativeListHeight = needsNativeList ? (NativeListButtonHeight + 4f) * scale : 0f;
        var infoHeight = (infoBanner?.Height ?? 0f) + nativeListHeight;
        var errorHeight = errorBanner?.Height ?? 0f;
        var writeErrHeight = writeErrorBanner?.Height ?? 0f;
        var bannersHeight = infoHeight + errorHeight + writeErrHeight;
        var sendBarHeight = 52f * scale;
        var composerHeight = sendBarHeight + bannersHeight + (bannersHeight > 0 ? 4f * scale : 0f);
        var timelinePos = pos + new Vector2(0, subHeaderH);
        var timelineHeight = MathF.Max(1f, size.Y - subHeaderH - composerHeight);

        var timelineContentWidth = MathF.Max(1f, size.X - ImGui.GetStyle().ScrollbarSize);

        using var convId = ImRaii.PushId(currentConv.Key.StorageId);
        ImGui.SetCursorScreenPos(timelinePos);
        string? selectedReplyOption = null;
        using (var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (var timeline = ImRaii.Child("##messages", new Vector2(size.X, timelineHeight), false))
        {
            if (timeline)
                selectedReplyOption = DrawTimeline(snapshot, view, currentConv, timelineContentWidth, showTyping, replyState,
                    translateState, sendTarget is not null && draft.Pending is null, scale);
        }

        wasShowingTyping = showTyping;
        lastReplyStateConv = currentConv.Key;
        lastReplyStatus = replyState.Status;
        lastReplyGenerationId = replyState.GenerationId;

        if (selectedReplyOption is not null && sendTarget is not null)
            SubmitReplyOption(sendTarget, draft, selectedReplyOption);

        var footerPos = timelinePos + new Vector2(0, timelineHeight);
        if (unavailable is not null && infoBanner is { } ib)
        {
            ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, unavailable, isAlert: false, scale, ib);
            footerPos.Y += ib.Height;
            if (needsNativeList && snapshot.Session is { } owner)
            {
                var buttonPos = footerPos + new Vector2((size.X - ib.BannerSize.X) * 0.5f, 0);
                DrawNativeListButton(owner, currentConv.Key.Kind, buttonPos, ib.BannerSize.X, scale);
                footerPos.Y += nativeListHeight;
            }
        }

        if (errorStatus is not null && errorBanner is { } eb)
        {
            if (ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, errorStatus, isAlert: true, scale, eb,
                closeButtonId: "##dismiss-chat-error"))
            {
                if (warningText is not null)
                {
                    if (ReferenceEquals(draft.Warning, warning))
                        draft.Warning = null;
                    if (ReferenceEquals(unassignedWarning, warning))
                        unassignedWarning = null;
                }
                else if (aiError is not null)
                {
                    aiReply.Clear(currentConv.Key);
                }
                else
                {
                    aiReply.ClearTranslationState(currentConv.Key);
                }
            }

            footerPos.Y += eb.Height;
        }

        if (writeErrorBanner is { } wb)
        {
            if (ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, writeErrorText, isAlert: true, scale, wb,
                closeButtonId: "##dismiss-write-error"))
            {
                capture.ClearFailedWrite(session!.ContentId);
            }

            footerPos.Y += wb.Height;
        }

        var sendBarPos = pos + new Vector2(0, size.Y - sendBarHeight);
        DrawSendBar(draw, snapshot, currentConv, view, sendTarget, draft, replyState, sendBarPos,
            new Vector2(size.X, sendBarHeight), bottomCorners, scale);
    }

    private void DrawSubHeader(ImDrawListPtr draw, Vector2 pos, float width, float height, string title, string? subtitle,
        bool showBack, float scale)
    {
        var left = pos.X + 14f * scale;
        if (showBack)
        {
            var btnSize = 28f * scale;
            var btnPos = new Vector2(pos.X + 8f * scale, pos.Y + (height - btnSize) * 0.5f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##narrow-back", new Vector2(btnSize)))
            {
                CommitActiveNote();
                narrowShowingDetail = false;
            }

            if (ImGui.IsItemHovered())
                draw.AddRectFilled(btnPos, btnPos + new Vector2(btnSize), ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);

            ChatVisuals.DrawBackIcon(draw, btnPos + new Vector2(btnSize * 0.5f), 14f * scale,
                ChatVisuals.Color(ChatVisuals.Ink), MathF.Max(1.6f, 1.8f * scale));
            left = btnPos.X + btnSize + 8f * scale;
        }

        var rightLimit = pos.X + width - 14f * scale;
        var avail = MathF.Max(30f * scale, rightLimit - left);
        var titleFontSize = ImGui.GetFontSize() * 1.06f;

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subFontSize = ImGui.GetFontSize() * 0.90f;
            var subSize = ChatVisuals.CalcTextSize(subtitle, subFontSize);
            var titleMax = MathF.Max(30f * scale, avail - subSize.X - 8f * scale);
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, titleMax);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            var titleY = pos.Y + (height - titleSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(left, titleY), titleFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                titleText, scale);

            var subX = left + titleSize.X + 8f * scale;
            var subY = pos.Y + (height - subSize.Y) * 0.5f;
            var clampedSub = ChatVisuals.TruncateSingleLine(subtitle, subFontSize, MathF.Max(0, rightLimit - subX));
            ChatVisuals.AddText(draw, new Vector2(subX, subY), subFontSize, ChatVisuals.Color(ChatVisuals.Muted),
                clampedSub);
        }
        else
        {
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, avail);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            ChatVisuals.AddBoldText(draw, new Vector2(left, pos.Y + (height - titleSize.Y) * 0.5f), titleFontSize,
                ChatVisuals.Color(ChatVisuals.Ink), titleText, scale);
        }

        var lineY = pos.Y + height - 1f;
        draw.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + width, lineY),
            ChatVisuals.Color(ChatVisuals.Divider), 1f);
    }

    private void DrawSendBar(ImDrawListPtr draw, SocialSnapshot snapshot, ConversationDescriptor currentConv,
        ConversationHistory view, SendTarget? target, Draft draft, AiReplySnapshot replyState, Vector2 pos, Vector2 size,
        ImDrawFlags bottomCorners, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.SendBarBg), 10f * scale, bottomCorners);
        draw.AddLine(pos, pos + new Vector2(size.X, 0), ChatVisuals.Color(ChatVisuals.Divider), 1f);

        var padX = 12f * scale;
        var pillH = 34f * scale;
        var pillY = pos.Y + (size.Y - pillH) * 0.5f;
        var sendSize = 34f * scale;
        var sendGap = 8f * scale;
        var showAiButton = config.IsAiReplyConfigured;
        var aiSize = showAiButton ? sendSize : 0f;
        var aiGap = showAiButton ? 6f * scale : 0f;
        var pillWidth = MathF.Max(60f * scale, size.X - padX * 2f - sendSize - sendGap - aiSize - aiGap);
        var pillPos = new Vector2(pos.X + padX, pillY);
        var pillRounding = pillH * 0.5f;

        draw.AddRectFilled(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(ChatVisuals.White), pillRounding);

        var hint = currentConv.Key.Kind switch
        {
            ConversationKind.Tell => Loc.Text("WriteTell"),
            ConversationKind.FreeCompany => Loc.Text("WriteFreeCompany"),
            ConversationKind.CrossWorldLinkshell => Loc.Text("WriteCrossWorldLinkshell"),
            ConversationKind.Linkshell => Loc.Text("WriteLinkshell"),
            ConversationKind.Static => Loc.Text("WriteStatic"),
            _ => Loc.Text("WriteMessage"),
        };

        var inputPadX = 14f * scale;
        var inputPadY = MathF.Max(2f, (pillH - ImGui.GetFontSize()) * 0.5f);
        var previousText = draft.Text;
        var enter = false;
        ImGui.SetCursorScreenPos(new Vector2(pillPos.X + inputPadX, pillPos.Y));
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, inputPadY)))
        {
            ImGui.SetNextItemWidth(MathF.Max(20f * scale, pillWidth - inputPadX * 2f));
            enter = ImGui.InputTextWithHint("##chat-input", hint, ref draft.Text, 2048,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (draft.Text != previousText)
            draft.Revision++;

        var inputActive = ImGui.IsItemActive();
        if (enter && target is not null && draft.Pending is null)
        {
            SubmitDraft(target, draft);
            ImGui.SetKeyboardFocusHere(-1);
        }

        inputFocused = inputActive;
        inputFocusedConversation = inputActive ? currentConv.Key : null;

        var borderCol = inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder;
        draw.AddRect(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(borderCol), pillRounding,
            ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));

        var sendPos = new Vector2(pillPos.X + pillWidth + sendGap, pillY);
        if (showAiButton)
        {
            var aiPos = sendPos;
            sendPos = new Vector2(aiPos.X + aiSize + aiGap, pillY);
            var aiLoading = replyState.Status == AiReplyStatus.Loading;
            var canGenerate = target is not null && draft.Pending is null && !aiLoading;

            ImGui.SetCursorScreenPos(aiPos);
            bool aiClicked;
            using (ImRaii.Disabled(!canGenerate))
                aiClicked = ImGui.InvisibleButton("##ai-reply", new Vector2(aiSize));

            var aiHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            var aiActive = canGenerate && ImGui.IsItemActive();
            var aiMenuOpen = ImGui.IsPopupOpen("##ai-reply-lang-menu");
            if (canGenerate && aiHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                ImGui.OpenPopup("##ai-reply-lang-menu");
                aiMenuOpen = true;
            }

            if (canGenerate && (aiHovered || aiActive || aiMenuOpen))
            {
                var bg = aiActive ? ChatVisuals.Divider : ChatVisuals.ListHover;
                draw.AddRectFilled(aiPos, aiPos + new Vector2(aiSize), ChatVisuals.Color(bg), 6f * scale);
            }

            var aiIconCol = !canGenerate
                ? ChatVisuals.SendIconIdle
                : aiHovered || aiActive || aiMenuOpen
                    ? ChatVisuals.RailBg
                    : ChatVisuals.OutgoingBubble;
            ChatVisuals.DrawAiSparkleIcon(draw, aiPos + new Vector2(aiSize * 0.5f), 20f * scale,
                ChatVisuals.Color(aiIconCol));

            if (aiHovered && !aiMenuOpen)
            {
                ImGui.SetTooltip(aiLoading
                    ? Loc.Text("GeneratingReplies")
                    : !canGenerate
                        ? Loc.Text("CannotGenerateReplies")
                        : Loc.Text("GenerateAiReplies"));
            }

            string? requestedLanguage = null;
            var triggerGenerate = aiClicked && canGenerate;
            using (var langMenu = ChatVisuals.BeginContextMenu("##ai-reply-lang-menu", scale))
            {
                if (langMenu.IsOpen)
                {
                    if (!canGenerate)
                    {
                        ImGui.CloseCurrentPopup();
                    }
                    else
                    {
                        foreach (var (code, name) in Loc.Languages)
                        {
                            if (ChatVisuals.ContextMenuItem($"##ai-lang-{code}", name, 132f * scale, scale))
                            {
                                requestedLanguage = code;
                                triggerGenerate = true;
                            }
                        }
                    }
                }
            }

            if (triggerGenerate && target is not null && canGenerate)
            {
                if (aiReply.TryGenerate(session!, currentConv, target, snapshot.FormatChannelTag(currentConv),
                    view.Messages, notes.GetValueOrDefault(currentConv.Key), requestedLanguage))
                {
                    draft.Warning = null;
                    unassignedWarning = null;
                }
            }
        }

        ImGui.SetCursorScreenPos(sendPos);
        var canSend = target is not null && draft.Pending is null;
        var clicked = false;
        using (ImRaii.Disabled(!canSend))
            clicked = ImGui.InvisibleButton("##send", new Vector2(sendSize));

        var hasText = !string.IsNullOrWhiteSpace(draft.Text);
        var sendHovered = canSend && ImGui.IsItemHovered();
        var iconCol = !canSend || !hasText
            ? ChatVisuals.SendIconIdle
            : sendHovered
                ? ChatVisuals.RailBg
                : ChatVisuals.OutgoingBubble;
        ChatVisuals.DrawSendIcon(draw, sendPos + new Vector2(sendSize * 0.5f), 20f * scale, ChatVisuals.Color(iconCol));

        if (clicked && target is not null)
            SubmitDraft(target, draft);
    }

    private void OpenConversation(ConversationDescriptor conversationDesc)
    {
        activeTab = NavTab.Chats;
        narrowShowingDetail = true;
        hidden.Remove(conversationDesc.Key);
        if (cache.IncompatibleOwner != session!.ContentId)
        {
            ObserveStoreTask(cache.UnhideConversationAsync(session.ContentId, conversationDesc.Key),
                "Failed to unhide conversation.");
        }
        if (selected?.Key == conversationDesc.Key)
        {
            selected = conversationDesc;
            return;
        }

        selected = conversationDesc;
        conversation = new ConversationHistory(messages, session!.ContentId, conversationDesc.Key);
        bubbleMeasurements.Clear();
        contextMessageId = null;
        contextMessageBody = null;
        contextMessageIsSystem = false;
        lastTranslationRevision = aiReply.TranslationRevision;
        firstMessageY = 0;
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
        drafts.TryAdd(conversationDesc.Key, new Draft());
        Reorder(directory.Current);
    }

    private void SubmitDraft(SendTarget target, Draft draft)
    {
        if (draft.Pending is null && SubmitText(target, draft, draft.Text) is { } submission)
            draft.Pending = (draft.Revision, submission);
    }

    private void SubmitReplyOption(SendTarget target, Draft draft, string replyText) =>
        SubmitText(target, draft, replyText);

    private Task<bool>? SubmitText(SendTarget target, Draft draft, string text)
    {
        if (!sender.TrySubmit(session!, target, text, out var submission, out var error))
        {
            draft.Warning = new SendWarning(session!, target.Key, error);
            return null;
        }

        aiReply.Clear(target.Key);
        draft.Warning = null;
        unassignedWarning = null;
        return submission;
    }

    private void ApplySendWarnings()
    {
        while (sender.TakeWarning() is { } warning)
        {
            if (!ReferenceEquals(warning.Session, session) || warning.AttachedToMessage)
                continue;

            if (warning.Conversation is { } key)
            {
                if (!drafts.TryGetValue(key, out var draft))
                {
                    draft = new Draft();
                    drafts.Add(key, draft);
                }

                draft.Warning = warning;
            }
            else
            {
                unassignedWarning = warning;
            }
        }
    }

    private sealed class Draft
    {
        public string Text = string.Empty;
        public long Revision;
        public (long Revision, Task<bool> Submission)? Pending;
        public SendWarning? Warning;
    }
}
