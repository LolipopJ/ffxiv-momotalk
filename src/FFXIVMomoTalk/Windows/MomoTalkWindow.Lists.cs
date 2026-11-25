using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void DrawListPane(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.ListBg));

        var headerHeight = 52f * scale;
        DrawListToolbar(draw, snapshot, pos, new Vector2(size.X, headerHeight), scale);

        var bodyPos = pos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(size.X, MathF.Max(1f, size.Y - headerHeight));
        ImGui.SetCursorScreenPos(bodyPos);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var body = ImRaii.Child("##list-body", bodySize, false);
        if (!body)
            return;

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactsList(snapshot, scale);
                break;
            case NavTab.Groups:
                DrawGroupsList(snapshot, scale);
                break;
            default:
                DrawChatsList(snapshot, scale);
                break;
        }
    }

    private void DrawListToolbar(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        var padX = 10f * scale;
        var controlH = 32f * scale;
        var controlY = pos.Y + (size.Y - controlH) * 0.5f;

        var showSort = activeTab == NavTab.Chats;
        var sortWidth = showSort ? 74f * scale : 0f;
        var dirWidth = showSort ? 42f * scale : 0f;
        var gap = showSort ? 8f * scale : 0f;
        var dirGap = showSort ? 5f * scale : 0f;
        var searchWidth = MathF.Max(60f * scale, size.X - padX * 2f - sortWidth - dirWidth - gap - dirGap);

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawSearchPill(draw, "##contacts-search", Loc.Text("SearchFriends"), ref contactsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
            case NavTab.Groups:
                DrawSearchPill(draw, "##groups-search", Loc.Text("SearchGroups"), ref groupsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
            default:
                DrawSearchPill(draw, "##chats-search", Loc.Text("SearchChats"), ref chatsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
        }

        if (showSort)
        {
            var sortPos = new Vector2(pos.X + padX + searchWidth + gap, controlY + 1f * scale);
            var sortSize = new Vector2(sortWidth, controlH - 2f * scale);
            if (ChatVisuals.SkewButton(draw, "##sort-toggle", sortByRecent ? Loc.Text("SortRecent") : Loc.Text("SortName"), sortPos, sortSize, scale))
            {
                sortByRecent = !sortByRecent;
                Reorder(snapshot);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortByRecent ? Loc.Text("SortRecentHint") : Loc.Text("SortNameHint"));

            var dirPos = new Vector2(sortPos.X + sortWidth + dirGap, sortPos.Y);
            var dirSize = new Vector2(dirWidth, sortSize.Y);
            if (ChatVisuals.SkewSortOrderButton(draw, "##sort-order", sortAscending, dirPos, dirSize, scale))
            {
                sortAscending = !sortAscending;
                Reorder(snapshot);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortAscending ? Loc.Text("SortAscendingHint") : Loc.Text("SortDescendingHint"));
        }

        var lineY = pos.Y + size.Y - MathF.Max(1f, scale);
        draw.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + size.X, lineY),
            ChatVisuals.Color(ChatVisuals.Divider), MathF.Max(1.5f, 1.8f * scale));
    }

    private static void DrawSearchPill(ImDrawListPtr draw, string id, string hint, ref string query, Vector2 pos, Vector2 size,
        float scale)
    {
        var rounding = size.Y * 0.5f;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), rounding);

        var iconCenter = new Vector2(pos.X + 15f * scale, pos.Y + size.Y * 0.5f);
        ChatVisuals.DrawSearchIcon(draw, iconCenter, 13f * scale, ChatVisuals.Color(ChatVisuals.Placeholder),
            MathF.Max(1.4f, 1.5f * scale));

        var inputLeft = pos.X + 26f * scale;
        var inputWidth = MathF.Max(20f * scale, size.X - 36f * scale);
        var padY = MathF.Max(2f, (size.Y - ImGui.GetFontSize()) * 0.5f);

        ImGui.SetCursorScreenPos(new Vector2(inputLeft, pos.Y));
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, padY)))
        {
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputTextWithHint(id, hint, ref query, 128);
        }

        var borderCol = ImGui.IsItemActive() ? ChatVisuals.InputFocus : ChatVisuals.InputBorder;
        draw.AddRect(pos, pos + size, ChatVisuals.Color(borderCol), rounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));
    }

    private void DrawContactsList(SocialSnapshot snapshot, float scale)
    {
        if (snapshot.Friends is null)
        {
            DrawListNotice(Loc.Text("LoadFriends"), scale);
            DrawListSyncButtons(snapshot.Session, scale, [ConversationKind.Tell]);
            return;
        }

        if (snapshot.Friends.Count == 0)
        {
            DrawListNotice(Loc.Text("FriendsEmpty"), scale);
            return;
        }

        var filtered = snapshot.Friends.Values
            .Where(friend => MatchesFriendSearch(friend, contactsSearch, notes.GetValueOrDefault(ConversationKey.Tell(friend.ContentId))))
            .OrderBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.WorldId)
            .ThenBy(friend => friend.ContentId)
            .ToArray();

        if (filtered.Length == 0)
        {
            DrawListNotice(Loc.Text("NoMatchingFriends"), scale);
            return;
        }

        foreach (var friend in filtered)
        {
            var isSelected = inspectedFriend?.ContentId == friend.ContentId;
            var note = notes.GetValueOrDefault(ConversationKey.Tell(friend.ContentId));
            DrawListRow(ConversationDescriptor.FromFriend(friend), note, friend.World, isSelected,
                $"##contact-{friend.ContentId:X16}", out var clicked, out var doubleClicked, out var rightClicked, scale);
            if (rightClicked)
            {
                contextFriend = friend;
                ImGui.OpenPopup("##contact-row-menu");
            }
            else if (doubleClicked)
            {
                CommitActiveNote();
                OpenConversation(ConversationDescriptor.FromFriend(friend));
            }
            else if (clicked)
            {
                if (inspectedFriend?.ContentId != friend.ContentId)
                    CommitActiveNote();

                inspectedFriend = friend;
                narrowShowingDetail = true;
            }
        }

        using var contactMenu = ChatVisuals.BeginContextMenu("##contact-row-menu", scale);
        if (contactMenu.IsOpen && contextFriend is { } menuPeer)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-contact-lodestone", Loc.Text("OpenLodestone"), 156f * scale, scale))
                Util.OpenLink(avatars.GetLodestoneUrl(menuPeer));
        }
    }

    private void DrawGroupsList(SocialSnapshot snapshot, float scale)
    {
        var items = BuildActiveGroupItems(snapshot);
        if (items.Count == 0)
        {
            DrawListNotice(Loc.Text("GroupsEmpty"), scale);
            DrawListSyncButtons(snapshot.Session, scale,
                [ConversationKind.FreeCompany, ConversationKind.Linkshell, ConversationKind.CrossWorldLinkshell]);
            return;
        }

        var anyMatch = false;
        foreach (var item in items)
        {
            var display = LocalizeChannelName(snapshot, item.Conversation);
            var note = notes.GetValueOrDefault(item.Conversation.Key);
            if (!MatchesGroupSearch(item, display.DisplayName, groupsSearch, note))
                continue;

            anyMatch = true;
            var isSelected = inspectedGroup?.Key == item.Conversation.Key;
            var subtitle = string.IsNullOrEmpty(note) ? item.Subtitle : note;
            DrawListRow(display, subtitle, item.Tag, isSelected,
                $"##group-{item.Conversation.Key.StorageId}", out var clicked, out var doubleClicked, out var rightClicked, scale);

            if (rightClicked && item.Static is { } rightClickedStatic)
            {
                contextStatic = rightClickedStatic;
                ImGui.OpenPopup("##group-row-menu");
            }
            else if (doubleClicked)
            {
                CommitActiveNote();
                OpenConversation(item.Conversation);
            }
            else if (clicked)
            {
                if (inspectedGroup?.Key != item.Conversation.Key)
                    CommitActiveNote();

                inspectedGroup = item.Conversation;
                narrowShowingDetail = true;
            }
        }

        if (!anyMatch)
            DrawListNotice(Loc.Text("NoMatchingGroups"), scale);

        using var groupMenu = ChatVisuals.BeginContextMenu("##group-row-menu", scale);
        if (groupMenu.IsOpen && contextStatic is { } menuStatic)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-group-rename", Loc.Text("RenameGroup"), 156f * scale, scale))
                OpenRenameStaticModal(menuStatic.StableKey, menuStatic.Name);
        }
    }

    private void DrawChatsList(SocialSnapshot snapshot, float scale)
    {
        if (summaryFailed)
        {
            ImGui.SetCursorPos(new Vector2(12f * scale, 10f * scale));
            ImGui.TextColored(ChatVisuals.AlertText, Loc.Text("ReadConversationsFailed"));
            ImGui.SetCursorPosX(12f * scale);
            if (ImGui.SmallButton($"{Loc.Text("Retry")}###retry-conversations"))
            {
                summaryFailed = false;
                summaryRevision = -1;
            }

            ImGui.Spacing();
        }

        if (rows.Length == 0)
        {
            var loading = summaryQuery is not null || (!hiddenLoaded && hiddenQuery is not null);
            DrawListNotice(loading
                ? Loc.Text("LoadingConversations")
                : Loc.Text("ChatsEmpty"), scale);
            return;
        }

        var anyMatch = false;
        foreach (var row in rows)
        {
            var conversationDesc = snapshot.RefreshDescriptor(row.Conversation);
            var display = LocalizeChannelName(snapshot, conversationDesc);
            var tag = FormatChannelTag(snapshot, conversationDesc);
            var note = notes.GetValueOrDefault(conversationDesc.Key);
            if (!MatchesChatSearch(snapshot, display, tag, chatsSearch, note))
                continue;

            anyMatch = true;
            var isSelected = selected?.Key == conversationDesc.Key;
            var subtitle = row.Latest is { } latest
                ? FormatPreview(conversationDesc.Key, latest)
                : Loc.Text("NoMessagesPreview");
            DrawListRow(display, subtitle, tag, isSelected, $"##chat-{conversationDesc.Key.StorageId}",
                out var clicked, out _, out var rightClicked, scale);
            if (rightClicked)
            {
                contextConversation = conversationDesc;
                ImGui.OpenPopup("##chat-row-menu");
            }
            else if (clicked)
            {
                OpenConversation(conversationDesc);
            }
        }

        if (!anyMatch)
            DrawListNotice(Loc.Text("NoMatchingChats"), scale);

        using var chatMenu = ChatVisuals.BeginContextMenu("##chat-row-menu", scale);
        if (chatMenu.IsOpen && contextConversation is { } menuConv)
        {
            if (menuConv.ToTellPeer() is { } tellPeer)
            {
                if (ChatVisuals.ContextMenuItem("##ctx-chat-lodestone", Loc.Text("OpenLodestone"), 156f * scale, scale))
                    Util.OpenLink(avatars.GetLodestoneUrl(tellPeer));
            }
            else if (menuConv.Key.Kind == ConversationKind.Static)
            {
                if (ChatVisuals.ContextMenuItem("##ctx-chat-rename", Loc.Text("RenameGroup"), 156f * scale, scale))
                    OpenRenameStaticModal(menuConv.Key.StableKey, menuConv.DisplayName);
            }

            if (ChatVisuals.ContextMenuItem("##ctx-chat-delete", Loc.Text("DeleteChat"), 156f * scale, scale, danger: true))
                DeleteChatFromList(snapshot, menuConv);
        }
    }

    private static void DrawListNotice(string message, float scale)
    {
        var pad = 16f * scale;
        ImGui.SetCursorPos(new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X - pad);
        ImGui.TextColored(ChatVisuals.Muted, message);
        ImGui.PopTextWrapPos();
    }

    private void DrawListSyncButtons(PlayerSession? owner, float scale, ReadOnlySpan<ConversationKind> kinds)
    {
        if (owner is null)
            return;

        var pad = 16f * scale;
        var gap = 4f * scale;
        ImGui.SetCursorPosX(pad);
        var maxWidth = (ImGui.GetContentRegionAvail().X - pad - gap * (kinds.Length - 1)) / kinds.Length;
        for (var index = 0; index < kinds.Length; index++)
        {
            if (index > 0)
                ImGui.SameLine(0f, gap);
            DrawNativeListButton(owner, kinds[index], ImGui.GetCursorScreenPos(), maxWidth, scale);
        }
    }

    private void DrawNativeListButton(PlayerSession owner, ConversationKind kind, Vector2 pos, float maxWidth, float scale)
    {
        var label = Loc.Text(kind switch
        {
            ConversationKind.Tell => "FriendList",
            ConversationKind.FreeCompany => "FreeCompanyTag",
            ConversationKind.Linkshell => "LinkshellTag",
            ConversationKind.CrossWorldLinkshell => "CrossWorldLinkshellTag",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        var width = ChatVisuals.MeasureCompactModalButton(label, scale, maxWidth);
        var size = new Vector2(width, NativeListButtonHeight * scale);
        if (ChatVisuals.DrawCompactModalButton(ImGui.GetWindowDrawList(), $"##open-native-{kind}", label, pos,
            size, enabled: true, scale, alpha: 1f))
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() => directory.OpenNativeList(owner, kind));
        }
    }

    private void DrawListRow(ConversationDescriptor conversationDesc, string? subtitle, string trailingTag,
        bool isSelected, string id, out bool clicked, out bool doubleClicked, out bool rightClicked, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = 60f * scale;
        var size = new Vector2(width, rowHeight);

        clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        rightClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        if (!ImGui.IsRectVisible(origin, origin + size))
            return;

        var draw = ImGui.GetWindowDrawList();
        if (isSelected || hovered)
            draw.AddRectFilled(origin, origin + size, ChatVisuals.Color(ChatVisuals.ListHover));

        draw.AddLine(new Vector2(origin.X, origin.Y + rowHeight - 1f),
            new Vector2(origin.X + width, origin.Y + rowHeight - 1f),
            ChatVisuals.Color(ChatVisuals.Divider), 1f);

        draw.PushClipRect(origin, origin + size, true);

        var padX = 12f * scale;
        var avatarSize = 40f * scale;
        var avatarPos = origin + new Vector2(padX, (rowHeight - avatarSize) * 0.5f);
        if (conversationDesc.ToTellPeer() is { } peer)
            ChatVisuals.Avatar(draw, avatars, peer, avatarPos, avatarSize);
        else
            ChatVisuals.GroupAvatar(draw, conversationDesc.Key.Kind, avatarPos, avatarSize);

        var tagFontSize = ImGui.GetFontSize() * 0.84f;
        var maxTagWidth = MathF.Min(96f * scale, width * 0.30f);
        var tagText = ChatVisuals.TruncateSingleLine(trailingTag, tagFontSize, maxTagWidth);
        var tagSize = ChatVisuals.CalcTextSize(tagText, tagFontSize);
        var tagX = origin.X + width - padX - tagSize.X;
        var tagY = origin.Y + (rowHeight - tagSize.Y) * 0.5f;
        if (tagText.Length > 0)
            ChatVisuals.AddText(draw, new Vector2(tagX, tagY), tagFontSize, ChatVisuals.Color(ChatVisuals.Muted), tagText);

        var textLeft = avatarPos.X + avatarSize + 10f * scale;
        var rightEdge = tagText.Length > 0 ? tagX - 10f * scale : origin.X + width - padX;
        var textWidth = MathF.Max(20f * scale, rightEdge - textLeft);
        var nameFontSize = ImGui.GetFontSize() * 1.04f;
        var nameText = ChatVisuals.TruncateSingleLine(conversationDesc.DisplayName, nameFontSize, textWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subFontSize = ImGui.GetFontSize() * 0.88f;
            var subText = ChatVisuals.TruncateSingleLine(subtitle, subFontSize, textWidth);
            var subSize = ChatVisuals.CalcTextSize(subText, subFontSize);
            var lineGap = 3f * scale;
            var blockY = origin.Y + (rowHeight - nameSize.Y - subSize.Y - lineGap) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(textLeft, blockY), nameFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                nameText, scale);
            ChatVisuals.AddText(draw, new Vector2(textLeft, blockY + nameSize.Y + lineGap), subFontSize,
                ChatVisuals.Color(ChatVisuals.Muted), subText);
        }
        else
        {
            var nameY = origin.Y + (rowHeight - nameSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(textLeft, nameY), nameFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                nameText, scale);
        }

        draw.PopClipRect();
    }

    private void Reorder(SocialSnapshot snapshot)
    {
        if (!hiddenLoaded)
        {
            rows = selected is { } activeSelected
                ? [new ConversationSummary(snapshot.RefreshDescriptor(activeSelected), null, null)]
                : [];
            return;
        }

        IEnumerable<ConversationSummary> visible = summaries
            .Where(row => !hidden.Contains(row.Conversation.Key))
            .Select(row => row with { Conversation = snapshot.RefreshDescriptor(row.Conversation) });
        if (selected is { } current && !visible.Any(row => row.Conversation.Key == current.Key))
            visible = visible.Append(new ConversationSummary(current, null, null));

        IOrderedEnumerable<ConversationSummary> ranked;
        if (sortByRecent)
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.LastSentAt is null ? 1 : 0)
                    .ThenBy(row => row.LastSentAt)
                    .ThenBy(row => row.Latest?.Id)
                    .ThenBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.LastSentAt)
                    .ThenByDescending(row => row.Latest?.Id)
                    .ThenBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase);
        }

        rows = ranked
            .ThenBy(row => row.Conversation.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Conversation.Key.StorageId, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<GroupListItem> BuildActiveGroupItems(SocialSnapshot snapshot)
    {
        var items = new List<GroupListItem>();
        if (snapshot.FreeCompany is { } fc)
            items.Add(new GroupListItem(fc.ToDescriptor(), Loc.Text("FreeCompany"), Loc.Text("FreeCompanyTag"), null));

        foreach (var (slot, cwls) in snapshot.CrossWorldLinkshells.OrderBy(pair => pair.Key))
            items.Add(new GroupListItem(cwls.ToDescriptor(), Loc.Text("CrossWorldLinkshell"), $"{Loc.Text("CrossWorldLinkshellTag")} {slot}", null));

        foreach (var (slot, ls) in snapshot.Linkshells.OrderBy(pair => pair.Key))
            items.Add(new GroupListItem(ls.ToDescriptor(), Loc.Text("Linkshell"), $"{Loc.Text("LinkshellTag")} {slot}", null));

        if (snapshot.Statics is not null)
        {
            foreach (var group in snapshot.Statics.Values
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.CreatedAt))
            {
                var subtitle = snapshot.IsStaticMatched(group.StableKey)
                    ? Loc.Format("MatchedMemberCount", group.Members.Count)
                    : Loc.Format("MemberCount", group.Members.Count);
                items.Add(new GroupListItem(group.ToDescriptor(), subtitle, Loc.Text("Static"), group));
            }
        }

        return items;
    }

    private static string FormatPreview(ConversationKey key, ChatMessage latest)
    {
        if (latest.Outgoing)
            return Loc.Format("OutgoingPreview", latest.Body);

        return key.IsGroup && !string.IsNullOrWhiteSpace(latest.Sender.Name)
            ? $"{latest.Sender.Name}: {latest.Body}"
            : latest.Body;
    }

    private static bool MatchesFriendSearch(PlayerIdentity peer, string query, string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        return peer.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || peer.World.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesGroupSearch(GroupListItem item, string displayName, string query, string? note)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        if (displayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || item.Tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return item.Static is { } group
            && group.Members.Any(member => member.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesChatSearch(
        SocialSnapshot snapshot,
        ConversationDescriptor conversationDesc,
        string tag,
        string query,
        string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        if (conversationDesc.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || conversationDesc.WorldName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return conversationDesc.Key.Kind == ConversationKind.Static
            && snapshot.Statics is not null
            && snapshot.Statics.TryGetValue(conversationDesc.Key.StableKey, out var group)
            && group.Members.Any(member => member.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record GroupListItem(
        ConversationDescriptor Conversation,
        string Subtitle,
        string Tag,
        StaticGroup? Static);
}
