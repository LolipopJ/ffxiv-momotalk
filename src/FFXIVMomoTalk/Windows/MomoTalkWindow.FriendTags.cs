using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void UpdateFriendProfiles()
    {
        if (friendProfilesQuery is { IsCompleted: true } completed)
        {
            friendProfilesQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                friendTags.Clear();
                foreach (var (cid, tags) in completed.Result.Tags)
                    friendTags[cid] = [.. tags];

                friendColors.Clear();
                foreach (var (cid, rgb) in completed.Result.NameColors)
                    friendColors[cid] = rgb;
            }
            else if (completed.Exception is { } exception)
            {
                log.Error(exception, "Failed to read friend profiles.");
            }
        }

        if (tagSaveTask is { IsCompleted: true } tagDone)
        {
            var targetId = tagSaveFriendId;
            tagSaveTask = null;
            tagSaveFriendId = null;
            if (tagDone.IsCompletedSuccessfully && targetId is { } cid)
            {
                tagSaveFailed = false;
                if (tagDone.Result.Count == 0)
                    friendTags.Remove(cid);
                else
                    friendTags[cid] = [.. tagDone.Result];
            }
            else
            {
                tagSaveFailed = true;
            }
        }

        if (colorSaveTask is { IsCompleted: true } colorDone)
        {
            colorSaveTask = null;
            if (colorDone.IsCompletedSuccessfully)
            {
                colorSaveFailed = false;
            }
            else
            {
                colorSaveFailed = true;
                friendProfilesRevision = -1;
            }
        }

        if (friendProfilesQuery is null && tagSaveTask is null && colorSaveTask is null
            && friendProfilesRevision != friendProfileStore.Revision)
        {
            friendProfilesRevision = friendProfileStore.Revision;
            friendProfilesQuery = friendProfileStore.ReadAllAsync(session!.ContentId);
        }
    }

    private List<string>? GetFriendTags(ulong contentId) =>
        friendTags.GetValueOrDefault(contentId);

    private bool MatchesFriendTagFilter(ulong contentId)
    {
        if (activeTagFilters.Count == 0)
            return true;

        if (!friendTags.TryGetValue(contentId, out var tags) || tags.Count == 0)
            return false;

        foreach (var filter in activeTagFilters)
        {
            if (!tags.Any(tag => StringComparer.OrdinalIgnoreCase.Equals(tag, filter)))
                return false;
        }

        return true;
    }

    private FriendBadgeFlowLayout MeasureFriendBadgeFlow(ulong friendContentId, long? addedAt, float maxCardTextWidth, float scale)
    {
        var maxRowWidth = MathF.Min(360f * scale, maxCardTextWidth);
        var fontSize = ImGui.GetFontSize() * 0.90f;
        var padX = 11f * scale;
        var padY = 4.5f * scale;
        var itemHeight = ImGui.GetFontSize() * 0.96f + padY * 2f;
        var gap = 6f * scale;
        var items = new List<FriendBadgeItem>();

        if (addedAt is { } timestamp)
        {
            var addedDate = FriendDates.ToLocalDate(timestamp);
            var dateStr = addedDate.ToString("d", Loc.Culture);
            var durationTooltip = FormatFriendshipDuration(addedDate, DateOnly.FromDateTime(DateTime.Now));
            var iconSize = 13f * scale;
            var iconGap = 6f * scale;
            var textMaxW = MathF.Max(30f * scale, maxRowWidth - padX * 2f - iconSize - iconGap);
            var label = ChatVisuals.TruncateSingleLine(dateStr, fontSize, textMaxW);
            var textSize = ChatVisuals.CalcTextSize(label, fontSize);
            var width = MathF.Min(maxRowWidth, padX * 2f + iconSize + iconGap + textSize.X);
            items.Add(new FriendBadgeItem(FriendBadgeKind.AddedDate, label, durationTooltip, new Vector2(width, itemHeight)));
        }

        var tags = friendTags.GetValueOrDefault(friendContentId);
        if (tags is not null)
        {
            var removeSize = 14f * scale;
            var removeGap = 5f * scale;
            foreach (var tag in tags)
            {
                var textMaxW = MathF.Max(24f * scale, maxRowWidth - padX - 8f * scale - removeSize - removeGap);
                var label = ChatVisuals.TruncateSingleLine(tag, fontSize, textMaxW);
                var textSize = ChatVisuals.CalcTextSize(label, fontSize);
                var width = MathF.Min(maxRowWidth, padX + textSize.X + removeGap + removeSize + 7f * scale);
                items.Add(new FriendBadgeItem(FriendBadgeKind.Tag, label, tag, new Vector2(width, itemHeight)));
            }
        }

        var hasTags = tags is { Count: > 0 };
        var addLabel = hasTags ? "+" : $"+ {Loc.Text("AddTag")}";
        var addText = ChatVisuals.TruncateSingleLine(addLabel, fontSize, MathF.Max(20f * scale, maxRowWidth - padX * 2f));
        var addTextSize = ChatVisuals.CalcTextSize(addText, fontSize);
        var addWidth = hasTags
            ? MathF.Max(itemHeight, addTextSize.X + 16f * scale)
            : MathF.Min(maxRowWidth, addTextSize.X + padX * 2f);
        items.Add(new FriendBadgeItem(FriendBadgeKind.AddButton, addText, Loc.Text("AddTag"), new Vector2(addWidth, itemHeight)));

        var rowsList = new List<FriendBadgeRow>();
        var currentRow = new List<FriendBadgeItem>();
        var currentWidth = 0f;
        foreach (var item in items)
        {
            var needed = currentRow.Count == 0 ? item.Size.X : currentWidth + gap + item.Size.X;
            if (currentRow.Count > 0 && needed > maxRowWidth)
            {
                rowsList.Add(new FriendBadgeRow(currentRow, currentWidth));
                currentRow = [item];
                currentWidth = item.Size.X;
            }
            else
            {
                currentRow.Add(item);
                currentWidth = needed;
            }
        }

        if (currentRow.Count > 0)
            rowsList.Add(new FriendBadgeRow(currentRow, currentWidth));

        var flowHeight = rowsList.Count * itemHeight + Math.Max(0, rowsList.Count - 1) * gap;
        var errorHeight = tagSaveFailed ? fontSize + 6f * scale : 0f;
        return new FriendBadgeFlowLayout(rowsList, fontSize, padX, gap, flowHeight + errorHeight);
    }

    private void DrawFriendBadgeFlow(
        ImDrawListPtr draw,
        ulong friendContentId,
        float centerX,
        float topY,
        FriendBadgeFlowLayout layout,
        float scale)
    {
        var y = topY;
        var borderThickness = MathF.Max(1.4f, 1.5f * scale);
        var tagIndex = 0;

        foreach (var row in layout.Rows)
        {
            var x = centerX - row.Width * 0.5f;
            var rowHeight = 0f;

            foreach (var item in row.Items)
            {
                var pos = new Vector2(x, y);
                var size = item.Size;
                var rounding = size.Y * 0.5f;
                rowHeight = MathF.Max(rowHeight, size.Y);

                switch (item.Kind)
                {
                    case FriendBadgeKind.AddedDate:
                        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), rounding);
                        draw.AddRect(pos, pos + size, ChatVisuals.Color(ChatVisuals.BadgeBorder),
                            rounding, ImDrawFlags.None, borderThickness);

                        var iconSize = 13f * scale;
                        var iconCenter = new Vector2(pos.X + layout.PadX + iconSize * 0.5f, pos.Y + size.Y * 0.5f);
                        ChatVisuals.DrawCalendarIcon(draw, iconCenter, iconSize, ChatVisuals.Color(ChatVisuals.BadgeText));

                        var dateTextSize = ChatVisuals.CalcTextSize(item.Label, layout.FontSize);
                        var dateTextPos = new Vector2(pos.X + layout.PadX + iconSize + 6f * scale, pos.Y + (size.Y - dateTextSize.Y) * 0.5f);
                        ChatVisuals.AddText(draw, dateTextPos, layout.FontSize, ChatVisuals.Color(ChatVisuals.BadgeText), item.Label);

                        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(pos, pos + size))
                            ImGui.SetTooltip(item.FullValue);
                        break;

                    case FriendBadgeKind.Tag:
                        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.ReplyCardBg), rounding);
                        draw.AddRect(pos, pos + size, ChatVisuals.Color(ChatVisuals.BadgeBorder),
                            rounding, ImDrawFlags.None, MathF.Max(1f, 1.2f * scale));

                        var tagTextSize = ChatVisuals.CalcTextSize(item.Label, layout.FontSize);
                        var tagTextPos = new Vector2(pos.X + layout.PadX, pos.Y + (size.Y - tagTextSize.Y) * 0.5f);
                        ChatVisuals.AddText(draw, tagTextPos, layout.FontSize, ChatVisuals.Color(ChatVisuals.BadgeText), item.Label);

                        var removeSize = 14f * scale;
                        var removePos = new Vector2(pos.X + size.X - 7f * scale - removeSize, pos.Y + (size.Y - removeSize) * 0.5f);
                        ImGui.SetCursorScreenPos(removePos);
                        var removeClicked = false;
                        using (ImRaii.Disabled(tagSaveTask is not null))
                            removeClicked = ImGui.InvisibleButton($"##remove-friend-tag-{tagIndex}", new Vector2(removeSize));

                        var removeHovered = ImGui.IsItemHovered();
                        if (removeHovered)
                        {
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                            draw.AddCircleFilled(removePos + new Vector2(removeSize * 0.5f), removeSize * 0.5f,
                                ChatVisuals.Color(ChatVisuals.ListHover), 16);
                            ImGui.SetTooltip(Loc.Text("RemoveTag"));
                        }
                        else if (item.Label != item.FullValue && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(pos, pos + size))
                        {
                            ImGui.SetTooltip(item.FullValue);
                        }

                        ChatVisuals.DrawCloseIcon(draw, removePos + new Vector2(removeSize * 0.5f), 8.5f * scale,
                            ChatVisuals.Color(removeHovered ? ChatVisuals.AlertText : ChatVisuals.BadgeText),
                            MathF.Max(1.2f, 1.3f * scale));

                        if (removeClicked)
                            RemoveFriendTag(friendContentId, item.FullValue);

                        tagIndex++;
                        break;

                    case FriendBadgeKind.AddButton:
                        ImGui.SetCursorScreenPos(pos);
                        var addClicked = ImGui.InvisibleButton("##open-add-friend-tag", size);
                        var addHovered = ImGui.IsItemHovered();
                        var addActive = ImGui.IsItemActive();
                        if (addHovered)
                        {
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                            ImGui.SetTooltip(Loc.Text("AddTag"));
                        }

                        var btnFill = addActive ? ChatVisuals.ListHover : addHovered ? ChatVisuals.ListBg : ChatVisuals.White;
                        var btnBorder = addHovered || addActive ? ChatVisuals.ModalButtonHoverBorder : ChatVisuals.BadgeBorder;
                        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(btnFill), rounding);
                        draw.AddRect(pos, pos + size, ChatVisuals.Color(btnBorder),
                            rounding, ImDrawFlags.None, MathF.Max(1.2f, 1.3f * scale));

                        var addTextSize = ChatVisuals.CalcTextSize(item.Label, layout.FontSize);
                        ChatVisuals.AddText(draw, pos + (size - addTextSize) * 0.5f, layout.FontSize,
                            ChatVisuals.Color(ChatVisuals.BadgeText), item.Label);

                        if (addClicked)
                        {
                            CommitActiveNote();
                            tagDraft = string.Empty;
                            focusTagInput = true;
                            tagPopupFriendId = friendContentId;
                            tagPopupAnchor = new Vector2(pos.X, pos.Y + size.Y + 4f * scale);
                            ImGui.OpenPopup("##friend-add-tag-popup");
                        }
                        break;
                }

                x += size.X + layout.Gap;
            }

            y += rowHeight + layout.Gap;
        }

        if (tagSaveFailed)
        {
            var err = Loc.Text("SaveFailed");
            var errSize = ChatVisuals.CalcTextSize(err, layout.FontSize);
            ChatVisuals.AddText(draw, new Vector2(centerX - errSize.X * 0.5f, y), layout.FontSize,
                ChatVisuals.Color(ChatVisuals.AlertText), err);
        }
    }

    private void DrawAddTagPopup(ulong friendContentId, float scale)
    {
        if (tagPopupFriendId != friendContentId)
            return;

        var popupWidth = 236f * scale;
        ImGui.SetNextWindowPos(tagPopupAnchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f));

        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, ChatVisuals.White)
            .Push(ImGuiCol.Border, ChatVisuals.InputBorder);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 10f * scale)
            .Push(ImGuiStyleVar.PopupBorderSize, MathF.Max(1f, scale))
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale));
        using var popup = ImRaii.Popup("##friend-add-tag-popup");
        if (!popup)
            return;

        var draw = ImGui.GetWindowDrawList();
        var innerW = MathF.Max(80f * scale, ImGui.GetContentRegionAvail().X);
        var origin = ImGui.GetCursorScreenPos();
        var inputH = 30f * scale;
        var addBtnW = ChatVisuals.MeasureCompactModalButton(Loc.Text("AddTag"), scale, 74f * scale);
        var gap = 6f * scale;
        var inputW = MathF.Max(60f * scale, innerW - addBtnW - gap);

        draw.AddRectFilled(origin, origin + new Vector2(inputW, inputH), ChatVisuals.Color(ChatVisuals.White), 7f * scale);
        var padY = MathF.Max(2f, (inputH - ImGui.GetFontSize()) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + 8f * scale, origin.Y));
        bool enter;
        using (ImRaii.Disabled(tagSaveTask is not null))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, padY)))
        {
            if (focusTagInput)
            {
                ImGui.SetKeyboardFocusHere();
                focusTagInput = false;
            }

            ImGui.SetNextItemWidth(MathF.Max(30f * scale, inputW - 14f * scale));
            enter = ImGui.InputTextWithHint("##add-tag-input", Loc.Text("EnterTagName"), ref tagDraft,
                FriendProfileStore.MaxTagLength * 2, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var inputActive = ImGui.IsItemActive();
        draw.AddRect(origin, origin + new Vector2(inputW, inputH),
            ChatVisuals.Color(inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder),
            7f * scale, ImDrawFlags.None, MathF.Max(1.3f, 1.5f * scale));

        var normalized = FriendProfileStore.NormalizeTag(tagDraft);
        var canAdd = tagSaveTask is null && normalized.Length > 0;
        var addClicked = ChatVisuals.DrawModalButton(draw, "##confirm-add-tag", Loc.Text("AddTag"),
            new Vector2(origin.X + inputW + gap, origin.Y), new Vector2(addBtnW, inputH),
            primary: true, enabled: canAdd, scale);

        if (canAdd && (enter || addClicked))
        {
            AddFriendTag(friendContentId, normalized);
            tagDraft = string.Empty;
            focusTagInput = true;
        }

        var cursorY = origin.Y + inputH;
        var allTags = CollectAllUniqueTags();
        if (allTags.Count > 0)
        {
            cursorY += 8f * scale;
            draw.AddLine(new Vector2(origin.X, cursorY), new Vector2(origin.X + innerW, cursorY),
                ChatVisuals.Color(ChatVisuals.ModalDivider), 1f);
            cursorY += 8f * scale;

            var headerFontSize = ImGui.GetFontSize() * 0.84f;
            ChatVisuals.AddText(draw, new Vector2(origin.X, cursorY), headerFontSize,
                ChatVisuals.Color(ChatVisuals.Muted), Loc.Text("ExistingTags"));
            cursorY += headerFontSize + 6f * scale;

            var currentTags = friendTags.GetValueOrDefault(friendContentId);
            var chipFontSize = ImGui.GetFontSize() * 0.86f;
            var chipH = chipFontSize + 8f * scale;
            var chipX = origin.X;
            var chipGap = 5f * scale;

            foreach (var tag in allTags)
            {
                var assigned = currentTags is not null
                    && currentTags.Any(existing => StringComparer.OrdinalIgnoreCase.Equals(existing, tag));
                var display = ChatVisuals.TruncateSingleLine(assigned ? $"✓ {tag}" : tag, chipFontSize, innerW - 16f * scale);
                var chipW = MathF.Min(innerW, ChatVisuals.CalcTextSize(display, chipFontSize).X + 16f * scale);
                if (chipX > origin.X && chipX + chipW > origin.X + innerW)
                {
                    chipX = origin.X;
                    cursorY += chipH + chipGap;
                }

                var chipPos = new Vector2(chipX, cursorY);
                var chipSize = new Vector2(chipW, chipH);
                ImGui.SetCursorScreenPos(chipPos);
                bool clicked;
                using (ImRaii.Disabled(tagSaveTask is not null))
                    clicked = ImGui.InvisibleButton($"##quick-tag-{tag}", chipSize);

                var hovered = ImGui.IsItemHovered();
                if (hovered)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                var bg = assigned
                    ? ChatVisuals.MatchSuccessBg
                    : hovered
                        ? ChatVisuals.ListHover
                        : ChatVisuals.ListBg;
                var border = assigned ? ChatVisuals.MatchSuccessText : ChatVisuals.BadgeBorder;
                var textCol = assigned ? ChatVisuals.MatchSuccessText : ChatVisuals.BadgeText;
                draw.AddRectFilled(chipPos, chipPos + chipSize, ChatVisuals.Color(bg), chipH * 0.5f);
                draw.AddRect(chipPos, chipPos + chipSize, ChatVisuals.Color(border), chipH * 0.5f, ImDrawFlags.None, 1f);
                var textSize = ChatVisuals.CalcTextSize(display, chipFontSize);
                ChatVisuals.AddText(draw, chipPos + (chipSize - textSize) * 0.5f, chipFontSize, ChatVisuals.Color(textCol), display);

                if (clicked)
                {
                    if (assigned)
                        RemoveFriendTag(friendContentId, tag);
                    else
                        AddFriendTag(friendContentId, tag);
                }

                chipX += chipW + chipGap;
            }

            cursorY += chipH;
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(innerW, MathF.Max(1f, cursorY - origin.Y)));
    }

    private void DrawContactsTagFilterPopup(SocialSnapshot snapshot, float scale)
    {
        var popupWidth = 228f * scale;
        ImGui.SetNextWindowPos(filterPopupAnchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f));

        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, ChatVisuals.White)
            .Push(ImGuiCol.Border, ChatVisuals.InputBorder);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 10f * scale)
            .Push(ImGuiStyleVar.PopupBorderSize, MathF.Max(1f, scale))
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale));
        using var popup = ImRaii.Popup("##contacts-tag-filter-popup");
        if (!popup)
            return;

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var innerW = MathF.Max(80f * scale, ImGui.GetContentRegionAvail().X);
        var headerH = 24f * scale;

        var titleFontSize = ImGui.GetFontSize() * 0.94f;
        var clearFontSize = ImGui.GetFontSize() * 0.86f;
        var clearLabel = Loc.Text("Clear");
        var clearSize = ChatVisuals.CalcTextSize(clearLabel, clearFontSize);
        var clearBtnSize = new Vector2(clearSize.X + 12f * scale, headerH);
        var clearPos = new Vector2(origin.X + innerW - clearBtnSize.X, origin.Y);

        var titleText = ChatVisuals.TruncateSingleLine(Loc.Text("FilterByTags"), titleFontSize,
            MathF.Max(40f * scale, innerW - clearBtnSize.X - 6f * scale));
        var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
        ChatVisuals.AddBoldText(draw, new Vector2(origin.X, origin.Y + (headerH - titleSize.Y) * 0.5f),
            titleFontSize, ChatVisuals.Color(ChatVisuals.Ink), titleText, scale);

        var hasFilters = activeTagFilters.Count > 0;
        ImGui.SetCursorScreenPos(clearPos);
        bool clearClicked;
        using (ImRaii.Disabled(!hasFilters))
            clearClicked = ImGui.InvisibleButton("##filter-popup-clear", clearBtnSize);

        var clearHovered = hasFilters && ImGui.IsItemHovered();
        if (clearHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(clearPos, clearPos + clearBtnSize, ChatVisuals.Color(ChatVisuals.ListHover), 5f * scale);
        }

        ChatVisuals.AddText(draw, clearPos + (clearBtnSize - clearSize) * 0.5f, clearFontSize,
            ChatVisuals.Color(hasFilters ? ChatVisuals.HeaderPinkActive : ChatVisuals.Placeholder), clearLabel);
        if (clearClicked)
            activeTagFilters.Clear();

        var dividerY = origin.Y + headerH + 6f * scale;
        draw.AddLine(new Vector2(origin.X, dividerY), new Vector2(origin.X + innerW, dividerY),
            ChatVisuals.Color(ChatVisuals.ModalDivider), 1f);

        var tagCounts = CollectFriendTagCounts(snapshot);
        var bodyY = dividerY + 6f * scale;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, bodyY));

        if (tagCounts.Count == 0)
        {
            var emptyText = Loc.Text("NoTagsYet");
            var emptyFont = ImGui.GetFontSize() * 0.90f;
            var emptySize = ChatVisuals.CalcTextSize(emptyText, emptyFont, innerW);
            ChatVisuals.AddText(draw, new Vector2(origin.X, bodyY + 4f * scale), emptyFont,
                ChatVisuals.Color(ChatVisuals.Muted), emptyText, innerW);
            ImGui.Dummy(new Vector2(innerW, emptySize.Y + 10f * scale));
            return;
        }

        var rowH = 28f * scale;
        var listH = MathF.Min(210f * scale, tagCounts.Count * rowH);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var listChild = ImRaii.Child("##tag-filter-list", new Vector2(innerW, listH), false);
        if (!listChild)
            return;

        var listDraw = ImGui.GetWindowDrawList();
        var itemW = ImGui.GetContentRegionAvail().X;
        var itemFontSize = ImGui.GetFontSize() * 0.92f;
        var countFontSize = ImGui.GetFontSize() * 0.84f;

        foreach (var (tag, count) in tagCounts)
        {
            var rowOrigin = ImGui.GetCursorScreenPos();
            var rowSize = new Vector2(itemW, rowH);
            var clicked = ImGui.InvisibleButton($"##filter-tag-{tag}", rowSize);
            var hovered = ImGui.IsItemHovered();
            var isChecked = activeTagFilters.Contains(tag);

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                listDraw.AddRectFilled(rowOrigin, rowOrigin + rowSize, ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);
            }

            var boxSize = 14f * scale;
            var boxPos = new Vector2(rowOrigin.X + 6f * scale, rowOrigin.Y + (rowH - boxSize) * 0.5f);
            listDraw.AddRectFilled(boxPos, boxPos + new Vector2(boxSize),
                ChatVisuals.Color(isChecked ? ChatVisuals.HeaderPink : ChatVisuals.White), 3.5f * scale);
            listDraw.AddRect(boxPos, boxPos + new Vector2(boxSize),
                ChatVisuals.Color(isChecked ? ChatVisuals.HeaderPink : ChatVisuals.BadgeBorder), 3.5f * scale, ImDrawFlags.None, 1.3f);

            if (isChecked)
            {
                var p1 = boxPos + new Vector2(boxSize * 0.22f, boxSize * 0.54f);
                var p2 = boxPos + new Vector2(boxSize * 0.44f, boxSize * 0.74f);
                var p3 = boxPos + new Vector2(boxSize * 0.78f, boxSize * 0.28f);
                var tickCol = ChatVisuals.Color(ChatVisuals.White);
                var tickWidth = MathF.Max(1.6f, 1.8f * scale);
                listDraw.AddLine(p1, p2, tickCol, tickWidth);
                listDraw.AddLine(p2, p3, tickCol, tickWidth);
            }

            var countText = count.ToString(CultureInfo.InvariantCulture);
            var countSize = ChatVisuals.CalcTextSize(countText, countFontSize);
            var countX = rowOrigin.X + itemW - 8f * scale - countSize.X;
            listDraw.AddText(ImGui.GetFont(), countFontSize, new Vector2(countX, rowOrigin.Y + (rowH - countSize.Y) * 0.5f),
                ChatVisuals.Color(ChatVisuals.Muted), countText);

            var textX = boxPos.X + boxSize + 8f * scale;
            var textMaxW = MathF.Max(20f * scale, countX - 6f * scale - textX);
            var label = ChatVisuals.TruncateSingleLine(tag, itemFontSize, textMaxW);
            var labelSize = ChatVisuals.CalcTextSize(label, itemFontSize);
            ChatVisuals.AddText(listDraw, new Vector2(textX, rowOrigin.Y + (rowH - labelSize.Y) * 0.5f),
                itemFontSize, ChatVisuals.Color(ChatVisuals.Ink), label);

            if (clicked)
            {
                if (!activeTagFilters.Add(tag))
                    activeTagFilters.Remove(tag);
            }
        }
    }

    private float MeasureContactsTagFilterBar(float width, float scale)
    {
        var chips = BuildActiveFilterChips(width, scale, out var rowsCount, out var chipHeight, out var gapY);
        _ = chips;
        return 6f * scale * 2f + rowsCount * chipHeight + Math.Max(0, rowsCount - 1) * gapY + MathF.Max(1f, scale);
    }

    private void DrawContactsTagFilterBar(ImDrawListPtr draw, Vector2 pos, float width, float scale)
    {
        var chips = BuildActiveFilterChips(width, scale, out var rowsCount, out var chipHeight, out var gapY);
        var totalHeight = 6f * scale * 2f + rowsCount * chipHeight + Math.Max(0, rowsCount - 1) * gapY + MathF.Max(1f, scale);
        draw.AddRectFilled(pos, pos + new Vector2(width, totalHeight), ChatVisuals.Color(ChatVisuals.ListBg));

        var padX = 10f * scale;
        var chipFontSize = ImGui.GetFontSize() * 0.84f;
        var gapX = 5f * scale;
        var x = pos.X + padX;
        var y = pos.Y + 6f * scale;
        var maxX = pos.X + width - padX;

        string? removeFilter = null;
        var clearAll = false;
        var idx = 0;
        foreach (var (label, fullTag, isClear, chipWidth) in chips)
        {
            if (x > pos.X + padX && x + chipWidth > maxX)
            {
                x = pos.X + padX;
                y += chipHeight + gapY;
            }

            var chipPos = new Vector2(x, y);
            var chipSize = new Vector2(chipWidth, chipHeight);
            ImGui.SetCursorScreenPos(chipPos);
            var clicked = ImGui.InvisibleButton($"##active-filter-chip-{idx++}", chipSize);
            var hovered = ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (isClear)
            {
                if (hovered)
                    draw.AddRectFilled(chipPos, chipPos + chipSize, ChatVisuals.Color(ChatVisuals.ListHover), chipHeight * 0.5f);

                var clearSize = ChatVisuals.CalcTextSize(label, chipFontSize);
                ChatVisuals.AddText(draw, chipPos + (chipSize - clearSize) * 0.5f, chipFontSize,
                    ChatVisuals.Color(ChatVisuals.HeaderPinkActive), label);
                if (clicked)
                    clearAll = true;
            }
            else
            {
                var bg = hovered ? ChatVisuals.ListHover : ChatVisuals.White;
                draw.AddRectFilled(chipPos, chipPos + chipSize, ChatVisuals.Color(bg), chipHeight * 0.5f);
                draw.AddRect(chipPos, chipPos + chipSize, ChatVisuals.Color(ChatVisuals.HeaderPink),
                    chipHeight * 0.5f, ImDrawFlags.None, 1.2f);

                var labelSize = ChatVisuals.CalcTextSize(label, chipFontSize);
                var textPos = new Vector2(chipPos.X + 8f * scale, chipPos.Y + (chipHeight - labelSize.Y) * 0.5f);
                ChatVisuals.AddText(draw, textPos, chipFontSize, ChatVisuals.Color(ChatVisuals.Ink), label);

                var closeSize = 10f * scale;
                var closeCenter = new Vector2(chipPos.X + chipWidth - 7f * scale - closeSize * 0.5f, chipPos.Y + chipHeight * 0.5f);
                ChatVisuals.DrawCloseIcon(draw, closeCenter, closeSize * 0.75f,
                    ChatVisuals.Color(hovered ? ChatVisuals.AlertText : ChatVisuals.Muted), MathF.Max(1.1f, 1.2f * scale));

                if (hovered && label != fullTag)
                    ImGui.SetTooltip(fullTag);

                if (clicked)
                    removeFilter = fullTag;
            }

            x += chipWidth + gapX;
        }

        if (clearAll)
            activeTagFilters.Clear();
        else if (removeFilter is not null)
            activeTagFilters.Remove(removeFilter);

        var lineY = pos.Y + totalHeight - MathF.Max(1f, scale);
        draw.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + width, lineY),
            ChatVisuals.Color(ChatVisuals.Divider), MathF.Max(1f, scale));
    }

    private List<(string Label, string FullTag, bool IsClear, float Width)> BuildActiveFilterChips(
        float width,
        float scale,
        out int rowsCount,
        out float chipHeight,
        out float gapY)
    {
        var padX = 10f * scale;
        var innerW = MathF.Max(40f * scale, width - padX * 2f);
        var chipFontSize = ImGui.GetFontSize() * 0.84f;
        chipHeight = chipFontSize + 7f * scale;
        gapY = 4f * scale;
        var gapX = 5f * scale;
        var closeReserve = 16f * scale;

        var chips = new List<(string Label, string FullTag, bool IsClear, float Width)>(activeTagFilters.Count + 1);
        foreach (var tag in activeTagFilters.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var label = ChatVisuals.TruncateSingleLine(tag, chipFontSize, MathF.Max(20f * scale, innerW - 14f * scale - closeReserve));
            var chipW = MathF.Min(innerW, ChatVisuals.CalcTextSize(label, chipFontSize).X + 14f * scale + closeReserve);
            chips.Add((label, tag, false, chipW));
        }

        var clearLabel = Loc.Text("Clear");
        var clearW = MathF.Min(innerW, ChatVisuals.CalcTextSize(clearLabel, chipFontSize).X + 14f * scale);
        chips.Add((clearLabel, string.Empty, true, clearW));

        rowsCount = 1;
        var currentX = 0f;
        foreach (var chip in chips)
        {
            if (currentX > 0f && currentX + chip.Width > innerW)
            {
                rowsCount++;
                currentX = chip.Width + gapX;
            }
            else
            {
                currentX += chip.Width + gapX;
            }
        }

        return chips;
    }

    private List<string> CollectAllUniqueTags()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var list in friendTags.Values)
        {
            foreach (var tag in list)
            {
                if (seen.Add(tag))
                    result.Add(tag);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private List<(string Tag, int Count)> CollectFriendTagCounts(SocialSnapshot snapshot)
    {
        var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Friends is not null)
        {
            foreach (var cid in snapshot.Friends.Keys)
            {
                if (!friendTags.TryGetValue(cid, out var list))
                    continue;

                foreach (var tag in list)
                {
                    if (counts.TryGetValue(tag, out var existing))
                        counts[tag] = (existing.Display, existing.Count + 1);
                    else
                        counts[tag] = (tag, 1);
                }
            }
        }

        foreach (var active in activeTagFilters)
            counts.TryAdd(active, (active, 0));

        return counts.Values
            .OrderBy(entry => entry.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddFriendTag(ulong friendContentId, string rawTag)
    {
        if (session is null || tagSaveTask is not null)
            return;

        var normalized = FriendProfileStore.NormalizeTag(rawTag);
        if (normalized.Length == 0)
            return;

        tagSaveFailed = false;
        tagSaveFriendId = friendContentId;
        tagSaveTask = friendProfileStore.AddTagAsync(session.ContentId, friendContentId, normalized);
        ObserveStoreTask(tagSaveTask, "Failed to add friend tag.");
    }

    private void RemoveFriendTag(ulong friendContentId, string tag)
    {
        if (session is null || tagSaveTask is not null)
            return;

        tagSaveFailed = false;
        tagSaveFriendId = friendContentId;
        tagSaveTask = friendProfileStore.RemoveTagAsync(session.ContentId, friendContentId, tag);
        ObserveStoreTask(tagSaveTask, "Failed to remove friend tag.");
    }

    private enum FriendBadgeKind
    {
        AddedDate,
        Tag,
        AddButton,
    }

    private readonly record struct FriendBadgeItem(
        FriendBadgeKind Kind,
        string Label,
        string FullValue,
        Vector2 Size);

    private readonly record struct FriendBadgeRow(List<FriendBadgeItem> Items, float Width);

    private readonly record struct FriendBadgeFlowLayout(
        List<FriendBadgeRow> Rows,
        float FontSize,
        float PadX,
        float Gap,
        float TotalHeight);
}
