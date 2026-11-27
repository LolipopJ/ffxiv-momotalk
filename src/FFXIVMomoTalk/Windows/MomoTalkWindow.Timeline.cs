using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private unsafe string? DrawTimeline(SocialSnapshot snapshot, ConversationHistory view, ConversationDescriptor currentConv,
        float contentWidth, bool showTyping, AiReplySnapshot replyState, AiTranslationSnapshot translateState,
        bool canSend, float scale)
    {
        var hasReplyCard = replyState.Status == AiReplyStatus.Loading
            || (replyState.Status == AiReplyStatus.Ready && replyState.Options.Count > 0);
        var replyCardAppeared = hasReplyCard
            && (lastReplyStateConv != currentConv.Key
                || lastReplyStatus != replyState.Status
                || lastReplyGenerationId != replyState.GenerationId);
        var currentTranslationRevision = aiReply.TranslationRevision;
        var translationsUpdated = currentTranslationRevision != lastTranslationRevision;
        lastTranslationRevision = currentTranslationRevision;

        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f * scale;
        var followLatest = view.RevealLatest
            || (view.AddedMessages && view.PrependAnchor is null && atBottom)
            || (showTyping && !wasShowingTyping && atBottom)
            || (replyCardAppeared && atBottom)
            || (translationsUpdated && atBottom);

        ImGui.Dummy(new Vector2(0, 6f * scale));
        var availWidth = MathF.Min(ImGui.GetContentRegionAvail().X, contentWidth);

        if (view.ReadFailed)
        {
            DrawTimelineBanner(Loc.Text("ReadHistoryFailed"), isAlert: true, availWidth, scale);
            DrawCenteredPillButton(Loc.Text("Retry"), "##retry-history", availWidth, scale, view.Retry);
        }
        else if (view.HasOlder)
        {
            using var disabled = ImRaii.Disabled(view.IsLoading);
            DrawCenteredPillButton(view.IsLoading ? Loc.Text("LoadingEarlierMessages") : Loc.Text("LoadEarlierMessages"),
                "##load-older", availWidth, scale, view.LoadPage);
        }
        else if (view.Messages.Count == 0 && !showTyping && !hasReplyCard)
        {
            var label = view.IsLoading ? Loc.Text("LoadingHistory") : Loc.Text("NoMessages");
            var placeholderFontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(label, placeholderFontSize);
            var origin = ImGui.GetCursorScreenPos();
            ChatVisuals.AddText(ImGui.GetWindowDrawList(), origin + new Vector2((availWidth - textSize.X) * 0.5f, 12f * scale),
                placeholderFontSize, ChatVisuals.Color(ChatVisuals.Muted), label);
            ImGui.Dummy(new Vector2(availWidth, textSize.Y + 24f * scale));
        }

        var previousFirstY = firstMessageY;
        var first = true;
        long? previousSentAt = null;
        SenderGroupKey? previousSpeaker = null;
        var previousHadRejection = false;
        var tellPeer = currentConv.ToTellPeer();
        var staticGroup = currentConv.Key.Kind == ConversationKind.Static && snapshot.Statics is not null
            && snapshot.Statics.TryGetValue(currentConv.Key.StableKey, out var foundStatic)
                ? foundStatic
                : null;
        var font = (nint)ImGui.GetFont().Handle;
        var fontSize = ImGui.GetFontSize();
        if (font != bubbleFont || fontSize != bubbleFontSize)
        {
            bubbleFont = font;
            bubbleFontSize = fontSize;
            bubbleMeasurements.Clear();
        }

        foreach (var message in view.Messages)
        {
            var author = message.Outgoing
                ? session!.Player
                : tellPeer
                    ?? (staticGroup is not null && message.Sender.ContentId != 0
                        ? staticGroup.Members.FirstOrDefault(member => member.ContentId == message.Sender.ContentId) ?? message.Sender
                        : message.Sender);
            var speaker = SenderGroupKey.From(message.Outgoing, author);
            var showTimeDivider = ChatVisuals.ShouldInsertTimeDivider(previousSentAt, message.SentAt);
            var isGroupStart = previousSpeaker is null
                || !previousSpeaker.Value.Equals(speaker)
                || showTimeDivider
                || previousHadRejection;

            if (showTimeDivider)
            {
                var label = ChatVisuals.FormatTimeDivider(message.SentAt);
                var divider = ChatVisuals.MeasureTimeDivider(label, scale);
                var origin = ImGui.GetCursorScreenPos();
                if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, divider.Height)))
                    ChatVisuals.DrawTimeDivider(ImGui.GetWindowDrawList(), origin, availWidth, label, scale, divider);

                ImGui.Dummy(new Vector2(availWidth, divider.Height));
            }
            else if (isGroupStart)
            {
                ImGui.Dummy(new Vector2(availWidth, 6f * scale));
            }

            var y = ImGui.GetCursorPosY();
            if (first)
            {
                firstMessageY = y;
                first = false;
            }

            if (view.PrependAnchor == message.Id)
                ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() + y - previousFirstY));

            DrawMessageBubble(snapshot, message, author, currentConv.Key.IsGroup, isGroupStart, translateState,
                availWidth, fontSize, scale);

            if (message.Rejection is { } rejection)
                DrawTimelineBanner(Loc.Reason(rejection), isAlert: true, availWidth, scale);

            previousSentAt = message.SentAt;
            previousSpeaker = speaker;
            previousHadRejection = message.Rejection is not null;
        }

        string? selectedReplyOption = null;
        if (replyState.Status == AiReplyStatus.Loading)
        {
            var maxCardWidth = MathF.Max(120f * scale, availWidth - 32f * scale);
            var loadingHeight = ChatVisuals.DrawReplyLoadingCard(ImGui.GetWindowDrawList(),
                ImGui.GetCursorScreenPos(), availWidth, maxCardWidth, (float)ImGui.GetTime(), scale);
            ImGui.Dummy(new Vector2(availWidth, loadingHeight));
        }
        else if (replyState.Status == AiReplyStatus.Ready && replyState.Options.Count > 0)
        {
            var maxCardWidth = MathF.Max(120f * scale, availWidth - 32f * scale);
            var clickedIndex = ChatVisuals.DrawReplyCard(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(),
                availWidth, maxCardWidth, replyState.Options, canSend, scale, out var cardHeight);
            if (clickedIndex >= 0 && clickedIndex < replyState.Options.Count)
                selectedReplyOption = replyState.Options[clickedIndex].Text;

            ImGui.Dummy(new Vector2(availWidth, cardHeight));
        }

        if (showTyping)
        {
            var typingGroupStart = previousSpeaker is null || !previousSpeaker.Value.Outgoing || previousHadRejection || hasReplyCard;
            if (typingGroupStart && view.Messages.Count > 0 && !hasReplyCard)
                ImGui.Dummy(new Vector2(availWidth, 6f * scale));

            DrawTypingBubbleRow(session!.Player, typingGroupStart, availWidth, scale);
        }

        ImGui.Dummy(new Vector2(0, 8f * scale));
        if (followLatest && (view.Messages.Count > 0 || showTyping || hasReplyCard))
            ImGui.SetScrollHereY(1f);

        view.ClearScrollRequests();

        using var msgMenu = ChatVisuals.BeginContextMenu("##message-context-menu", scale);
        if (msgMenu.IsOpen && contextMessageBody is { } copyBody)
        {
            var cachedTranslation = contextMessageId is { } msgId && session is not null
                ? aiReply.GetTranslation(session.ContentId, msgId, copyBody)
                : null;
            var showTranslate = config.IsAiReplyConfigured && contextMessageId is not null;
            var isTranslating = translateState.Status == AiTranslationStatus.Loading;
            var copyLabel = Loc.Text("CopyText");
            var copyTransLabel = cachedTranslation is { Length: > 0 } ? Loc.Text("CopyTranslation") : null;
            var translateLabel = showTranslate ? (isTranslating ? Loc.Text("Translating") : Loc.Text("TranslateChat")) : null;

            var itemFontSize = ImGui.GetFontSize() * 0.96f;
            var menuWidth = 140f * scale;
            menuWidth = MathF.Max(menuWidth, ChatVisuals.CalcTextSize(copyLabel, itemFontSize).X + 24f * scale);
            if (copyTransLabel is not null)
                menuWidth = MathF.Max(menuWidth, ChatVisuals.CalcTextSize(copyTransLabel, itemFontSize).X + 24f * scale);
            if (translateLabel is not null)
                menuWidth = MathF.Max(menuWidth, ChatVisuals.CalcTextSize(translateLabel, itemFontSize).X + 24f * scale);

            if (ChatVisuals.ContextMenuItem("##ctx-copy-text", copyLabel, menuWidth, scale))
                ImGui.SetClipboardText(copyBody);

            if (copyTransLabel is not null && cachedTranslation is not null
                && ChatVisuals.ContextMenuItem("##ctx-copy-translation", copyTransLabel, menuWidth, scale))
            {
                ImGui.SetClipboardText(cachedTranslation);
            }

            if (translateLabel is not null && contextMessageId is { } translateId
                && ChatVisuals.ContextMenuItem("##ctx-translate-chat", translateLabel, menuWidth, scale, enabled: !isTranslating))
            {
                aiReply.TryTranslateMessages(session!, currentConv, snapshot.FormatChannelTag(currentConv), view.Messages, translateId);
            }
        }

        return selectedReplyOption;
    }

    private static void DrawCenteredPillButton(string label, string id, float availWidth, float scale, Action onClick)
    {
        var fontSize = ImGui.GetFontSize() * 0.88f;
        var textSize = ChatVisuals.CalcTextSize(label, fontSize);
        var btnSize = textSize + new Vector2(22f, 10f) * scale;
        var origin = ImGui.GetCursorScreenPos();
        var btnPos = new Vector2(origin.X + (availWidth - btnSize.X) * 0.5f, origin.Y + 4f * scale);

        ImGui.SetCursorScreenPos(btnPos);
        if (ImGui.InvisibleButton(id, btnSize))
            onClick();

        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(btnPos, btnPos + btnSize,
            ChatVisuals.Color(hovered ? ChatVisuals.InputBorder : ChatVisuals.SystemBg), btnSize.Y * 0.5f);
        ChatVisuals.AddText(draw, btnPos + (btnSize - textSize) * 0.5f, fontSize,
            ChatVisuals.Color(ChatVisuals.SystemText), label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(availWidth, btnSize.Y + 10f * scale));
    }

    private static void DrawTimelineBanner(string text, bool isAlert, float availWidth, float scale)
    {
        var maxWidth = MathF.Max(80f * scale, availWidth - 44f * scale);
        var layout = ChatVisuals.MeasureSystemBanner(text, maxWidth, scale);
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, layout.Height)))
            ChatVisuals.DrawSystemBanner(ImGui.GetWindowDrawList(), origin, availWidth, text, isAlert, scale, layout);

        ImGui.Dummy(new Vector2(availWidth, layout.Height));
    }

    private void DrawMessageBubble(SocialSnapshot snapshot, ChatMessage message, PlayerIdentity author, bool isGroup,
        bool isGroupStart, AiTranslationSnapshot translateState, float availWidth, float fontSize, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var sideMargin = 16f * scale;
        var padding = new Vector2(11f, 7f) * scale;
        var nameFontSize = fontSize * 0.88f;
        var nameTopInset = MathF.Round(nameFontSize * 0.14f);
        var groupNameOffset = nameFontSize - nameTopInset + 4f * scale;
        var singleLineBubbleHeight = fontSize + padding.Y * 2f;
        var avatarSize = groupNameOffset + singleLineBubbleHeight;
        var avatarGap = 11f * scale;
        var showAvatarSlot = !message.Outgoing || config.ShowSelfAvatar;
        var avatarOffset = showAvatarSlot ? avatarSize + avatarGap : 0f;
        var innerWidth = MathF.Max(80f * scale, availWidth - sideMargin * 2f);
        var maxBody = MathF.Max(36f * scale,
            MathF.Min(420f * scale, innerWidth * 0.78f - avatarOffset - padding.X * 2f));
        var cachedTranslation = aiReply.GetTranslation(session!.ContentId, message.Id, message.Body);
        var translationPending = translateState.Status == AiTranslationStatus.Loading
            && translateState.AnchorMessageId == message.Id;
        var footerText = translationPending ? Loc.Text("Translating") : cachedTranslation;
        if (!bubbleMeasurements.TryGetValue(message.Id, out var measured)
            || measured.WrapWidth != maxBody
            || measured.Body != message.Body
            || measured.Footer != footerText)
        {
            var bodyMeasured = ChatVisuals.CalcTextSize(message.Body, fontSize, maxBody);
            var footerMeasured = string.IsNullOrEmpty(footerText)
                ? Vector2.Zero
                : ChatVisuals.CalcTextSize(footerText, ChatVisuals.TranslationFontSize(fontSize), maxBody);
            measured = (message.Body, footerText, maxBody, bodyMeasured, footerMeasured);
            bubbleMeasurements[message.Id] = measured;
        }

        var textSize = measured.TextSize;
        var hasFooter = !string.IsNullOrEmpty(footerText);
        var bubbleContentWidth = hasFooter ? MathF.Max(textSize.X, measured.FooterSize.X) : textSize.X;
        var bubbleContentHeight = hasFooter
            ? textSize.Y
                + ChatVisuals.TranslationDividerGap(scale) * 2f
                + ChatVisuals.TranslationDividerThickness(scale)
                + measured.FooterSize.Y
            : textSize.Y;
        var bubbleSize = new Vector2(MathF.Min(maxBody, bubbleContentWidth) + padding.X * 2f, bubbleContentHeight + padding.Y * 2f);

        var showName = isGroupStart && showAvatarSlot;
        var nameOffset = showName ? groupNameOffset : 0f;
        var bubbleTop = origin.Y + nameOffset;
        var contentHeight = isGroupStart && showAvatarSlot
            ? MathF.Max(avatarSize, nameOffset + bubbleSize.Y)
            : nameOffset + bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            var bubbleX = message.Outgoing
                ? origin.X + availWidth - sideMargin - avatarOffset - bubbleSize.X
                : origin.X + sideMargin + avatarOffset;
            var bubblePos = new Vector2(bubbleX, bubbleTop);

            if (isGroupStart && showAvatarSlot)
            {
                var avatarX = message.Outgoing
                    ? origin.X + availWidth - sideMargin - avatarSize
                    : origin.X + sideMargin;
                ChatVisuals.Avatar(draw, avatars, author, new Vector2(avatarX, origin.Y), avatarSize);
            }

            if (showName)
            {
                var displayName = message.Outgoing ? Loc.Text("You") : author.Name;
                var nameText = ChatVisuals.TruncateSingleLine(displayName, nameFontSize,
                    MathF.Max(40f * scale, innerWidth - avatarOffset));
                var nameX = message.Outgoing
                    ? origin.X + availWidth - sideMargin - avatarOffset - ChatVisuals.CalcTextSize(nameText, nameFontSize).X
                    : bubbleX;
                var nameColor = !message.Outgoing && isGroup ? GetGroupSpeakerNameColor(snapshot, author) : ChatVisuals.Ink;
                ChatVisuals.AddText(draw, new Vector2(nameX, origin.Y - nameTopInset), nameFontSize,
                    ChatVisuals.Color(nameColor), nameText);
            }

            ChatVisuals.DrawBubble(
                draw,
                bubblePos,
                bubbleSize,
                message.Outgoing ? ChatVisuals.OutgoingBubble : ChatVisuals.IncomingBubble,
                message.Body,
                fontSize,
                padding,
                maxBody,
                message.Outgoing,
                isGroupStart,
                scale,
                textSize,
                footerText,
                translationPending);

            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(bubblePos, bubblePos + bubbleSize))
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextMessageId = message.Id;
                    contextMessageBody = message.Body;
                    ImGui.OpenPopup("##message-context-menu");
                }
                else if (!ImGui.IsPopupOpen("##message-context-menu"))
                {
                    ImGui.SetTooltip(ChatVisuals.FormatFullTime(message.SentAt));
                }
            }
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private void DrawTypingBubbleRow(PlayerIdentity self, bool isGroupStart, float availWidth, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var sideMargin = 16f * scale;
        var fontSize = ImGui.GetFontSize();
        var nameFontSize = fontSize * 0.88f;
        var nameTopInset = MathF.Round(nameFontSize * 0.14f);
        var groupNameOffset = nameFontSize - nameTopInset + 4f * scale;
        var singleLineBubbleHeight = fontSize + 14f * scale;
        var avatarSize = groupNameOffset + singleLineBubbleHeight;
        var avatarGap = 11f * scale;
        var showAvatarSlot = config.ShowSelfAvatar;
        var avatarOffset = showAvatarSlot ? avatarSize + avatarGap : 0f;
        var showName = isGroupStart && showAvatarSlot;
        var nameOffset = showName ? groupNameOffset : 0f;
        var bubbleSize = new Vector2(62f * scale, singleLineBubbleHeight);
        var bubbleTop = origin.Y + nameOffset;
        var contentHeight = isGroupStart && showAvatarSlot
            ? MathF.Max(avatarSize, nameOffset + bubbleSize.Y)
            : nameOffset + bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            if (isGroupStart && showAvatarSlot)
            {
                var avatarX = origin.X + availWidth - sideMargin - avatarSize;
                ChatVisuals.Avatar(draw, avatars, self, new Vector2(avatarX, origin.Y), avatarSize);
            }

            if (showName)
            {
                var nameX = origin.X + availWidth - sideMargin - avatarOffset - ChatVisuals.CalcTextSize(Loc.Text("You"), nameFontSize).X;
                ChatVisuals.AddText(draw, new Vector2(nameX, origin.Y - nameTopInset), nameFontSize,
                    ChatVisuals.Color(ChatVisuals.Ink), Loc.Text("You"));
            }

            var bubbleX = origin.X + availWidth - sideMargin - avatarOffset - bubbleSize.X;
            ChatVisuals.DrawTypingBubble(draw, new Vector2(bubbleX, bubbleTop), bubbleSize,
                (float)ImGui.GetTime(), isGroupStart, scale);
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private readonly record struct SenderGroupKey(bool Outgoing, ulong ContentId, uint WorldId, string Name)
    {
        public static SenderGroupKey From(bool outgoing, PlayerIdentity author) =>
            outgoing
                ? new SenderGroupKey(true, 0, 0, string.Empty)
                : author.ContentId != 0
                    ? new SenderGroupKey(false, author.ContentId, 0, string.Empty)
                    : new SenderGroupKey(false, 0, author.WorldId, author.Name.Trim().ToLowerInvariant());
    }
}
