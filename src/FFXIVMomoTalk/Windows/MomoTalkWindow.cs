using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed class MomoTalkWindow : Window
{
    private const float NarrowThreshold = 640f;

    private readonly FriendDirectory directory;
    private readonly HistoryStore history;
    private readonly ChatCapture capture;
    private readonly TellSender sender;
    private readonly AvatarCache avatars;
    private readonly Dictionary<PlayerKey, Draft> drafts = new();
    private readonly Dictionary<PlayerKey, TellMessage> latestByPeer = new();

    private PlayerSession? session;
    private NavTab activeTab = NavTab.Chats;
    private PlayerIdentity? inspectedFriend;
    private PlayerIdentity? selected;
    private ConversationHistory? conversation;
    private IReadOnlyList<ConversationSummary> summaries = [];
    private IReadOnlyList<ConversationSummary> rows = [];
    private Task<IReadOnlyList<ConversationSummary>>? summaryQuery;
    private long summaryRevision = -1;
    private bool summaryFailed;
    private bool sortByRecent = true;
    private bool narrowShowingDetail;
    private string contactsSearch = string.Empty;
    private string chatsSearch = string.Empty;
    private float firstMessageY;
    private TellWarning? unassignedWarning;
    private Vector2 dragWindowOrigin;
    private Vector2 dragMouseOrigin;

    public MomoTalkWindow(FriendDirectory directory, HistoryStore history, ChatCapture capture, TellSender sender,
        AvatarCache avatars) : base("FFXIV MomoTalk")
    {
        this.directory = directory;
        this.history = history;
        this.capture = capture;
        this.sender = sender;
        this.avatars = avatars;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(840, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 360),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleColor(ImGuiCol.Text, ChatVisuals.Ink);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, ChatVisuals.Placeholder);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.Border, ChatVisuals.WindowBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, ChatVisuals.Divider);
        ImGui.PushStyleColor(ImGuiCol.Header, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.Button, ChatVisuals.SystemBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ChatVisuals.InputBorder);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ChatVisuals.BadgeBorder);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.48f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.68f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.70f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, MathF.Max(1f, scale));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f * scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(6);
        ImGui.PopStyleColor(23);
    }

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        var headerHeight = 46f * scale;

        DrawHeader(draw, winPos, winSize.X, headerHeight, scale);

        var bodyPos = winPos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(winSize.X, MathF.Max(1f, winSize.Y - headerHeight));

        var snapshot = directory.Current;
        SyncSession(snapshot.Session);
        ApplySendWarnings();

        if (session is null)
        {
            DrawLoggedOut(draw, bodyPos, bodySize, scale);
            return;
        }

        SyncInspectedFriend(snapshot);
        UpdateSummaries();

        var narrow = bodySize.X < NarrowThreshold * scale;
        if (narrow)
            DrawNarrowLayout(draw, snapshot, bodyPos, bodySize, scale);
        else
            DrawWideLayout(draw, snapshot, bodyPos, bodySize, scale);
    }

    private void DrawHeader(ImDrawListPtr draw, Vector2 winPos, float width, float height, float scale)
    {
        var rounding = 10f * scale;
        draw.AddRectFilled(winPos, winPos + new Vector2(width, height), ChatVisuals.Color(ChatVisuals.HeaderPink),
            rounding, ImDrawFlags.RoundCornersTop);

        var closeSize = 30f * scale;
        var closePos = new Vector2(winPos.X + width - closeSize - 10f * scale, winPos.Y + (height - closeSize) * 0.5f);
        var dragWidth = MathF.Max(1f, closePos.X - winPos.X - 6f * scale);

        ImGui.SetCursorScreenPos(winPos);
        ImGui.InvisibleButton("##header-drag", new Vector2(dragWidth, height));
        if (ImGui.IsItemActivated())
        {
            dragWindowOrigin = ImGui.GetWindowPos();
            dragMouseOrigin = ImGui.GetIO().MousePos;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            ImGui.SetWindowPos(dragWindowOrigin + (ImGui.GetIO().MousePos - dragMouseOrigin));

        var logoSize = 22f * scale;
        var logoCenter = new Vector2(winPos.X + 24f * scale, winPos.Y + height * 0.5f);
        ChatVisuals.DrawMomoLogo(draw, logoCenter, logoSize, ChatVisuals.Color(ChatVisuals.White));

        var titleFontSize = ImGui.GetFontSize() * 1.42f;
        var titleSize = ChatVisuals.CalcTextSize("MomoTalk", titleFontSize);
        var titlePos = new Vector2(winPos.X + 42f * scale, winPos.Y + (height - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize, ChatVisuals.Color(ChatVisuals.White), "MomoTalk", scale);

        ImGui.SetCursorScreenPos(closePos);
        if (ImGui.InvisibleButton("##window-close", new Vector2(closeSize)))
            IsOpen = false;

        var closeHovered = ImGui.IsItemHovered();
        var closeActive = ImGui.IsItemActive();
        if (closeHovered || closeActive)
        {
            var bg = closeActive ? ChatVisuals.HeaderPinkActive : ChatVisuals.HeaderPinkHover;
            draw.AddRectFilled(closePos, closePos + new Vector2(closeSize), ChatVisuals.Color(bg), 6f * scale);
        }

        ChatVisuals.DrawCloseIcon(draw, closePos + new Vector2(closeSize * 0.5f), 14f * scale,
            ChatVisuals.Color(ChatVisuals.White), MathF.Max(1.6f, 1.8f * scale));
    }

    private static void DrawLoggedOut(ImDrawListPtr draw, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        draw.AddRectFilled(bodyPos, bodyPos + bodySize, ChatVisuals.Color(ChatVisuals.White), 10f * scale,
            ImDrawFlags.RoundCornersBottom);
        const string message = "Log in to view your friend conversations.";
        var fontSize = ImGui.GetFontSize();
        var maxTextWidth = MathF.Max(60f * scale, bodySize.X - 48f * scale);
        var textSize = ChatVisuals.CalcTextSize(message, fontSize, maxTextWidth);
        var pos = bodyPos + (bodySize - textSize) * 0.5f;
        ChatVisuals.AddText(draw, pos, fontSize, ChatVisuals.Color(ChatVisuals.Muted), message, maxTextWidth);
    }

    private void DrawWideLayout(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        var railWidth = 54f * scale;
        var remaining = MathF.Max(1f, bodySize.X - railWidth);
        var listWidth = Math.Clamp(remaining * 0.42f, 236f * scale, 340f * scale);
        if (listWidth > remaining * 0.58f)
            listWidth = remaining * 0.58f;

        var rightWidth = MathF.Max(1f, remaining - listWidth);

        DrawLeftNavRail(draw, bodyPos, new Vector2(railWidth, bodySize.Y), scale);

        var listPos = bodyPos + new Vector2(railWidth, 0);
        DrawListPane(draw, snapshot, listPos, new Vector2(listWidth, bodySize.Y), scale);

        var dividerX = listPos.X + listWidth;
        draw.AddLine(new Vector2(dividerX, bodyPos.Y), new Vector2(dividerX, bodyPos.Y + bodySize.Y),
            ChatVisuals.Color(ChatVisuals.Divider), MathF.Max(1f, scale));

        var rightPos = new Vector2(dividerX, bodyPos.Y);
        var rightSize = new Vector2(rightWidth, bodySize.Y);
        if (activeTab == NavTab.Contacts)
            DrawContactProfile(draw, snapshot, rightPos, rightSize, narrow: false, scale);
        else
            DrawConversationPane(draw, snapshot, rightPos, rightSize, narrow: false, scale);
    }

    private void DrawNarrowLayout(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        if (!narrowShowingDetail)
        {
            var navHeight = 48f * scale;
            var listHeight = MathF.Max(1f, bodySize.Y - navHeight);
            DrawListPane(draw, snapshot, bodyPos, new Vector2(bodySize.X, listHeight), scale);
            DrawBottomNavBar(draw, bodyPos + new Vector2(0, listHeight), new Vector2(bodySize.X, navHeight), scale);
            return;
        }

        if (activeTab == NavTab.Contacts)
            DrawContactProfile(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
        else
            DrawConversationPane(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
    }

    private void DrawLeftNavRail(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottomLeft);

        var tabHeight = 54f * scale;
        DrawNavTabButton(draw, "##nav-contacts", NavTab.Contacts, pos, new Vector2(size.X, tabHeight),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-chats", NavTab.Chats, pos + new Vector2(0, tabHeight),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
    }

    private void DrawBottomNavBar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottom);

        var half = size.X * 0.5f;
        DrawNavTabButton(draw, "##bnav-contacts", NavTab.Contacts, pos, new Vector2(half, size.Y),
            ImDrawFlags.RoundCornersBottomLeft, scale);
        DrawNavTabButton(draw, "##bnav-chats", NavTab.Chats, pos + new Vector2(half, 0), new Vector2(size.X - half, size.Y),
            ImDrawFlags.RoundCornersBottomRight, scale);
    }

    private void DrawNavTabButton(ImDrawListPtr draw, string id, NavTab tab, Vector2 pos, Vector2 size,
        ImDrawFlags roundCorners, float scale)
    {
        ImGui.SetCursorScreenPos(pos);
        if (ImGui.InvisibleButton(id, size))
        {
            activeTab = tab;
            narrowShowingDetail = false;
        }

        var selectedTab = activeTab == tab;
        var hovered = ImGui.IsItemHovered();
        var bg = selectedTab
            ? ChatVisuals.RailActive
            : hovered
                ? ChatVisuals.WithAlpha(ChatVisuals.RailActive, 0.55f)
                : ChatVisuals.RailBg;

        if (selectedTab || hovered)
        {
            var rounding = roundCorners == ImDrawFlags.None ? 0f : 10f * scale;
            draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(bg), rounding, roundCorners);
        }

        var iconAlpha = selectedTab ? 1f : hovered ? 0.85f : 0.50f;
        var iconColor = ChatVisuals.Color(ChatVisuals.WithAlpha(ChatVisuals.White, iconAlpha));
        var center = pos + size * 0.5f;
        var iconSize = 24f * scale;
        if (tab == NavTab.Contacts)
            ChatVisuals.DrawContactsIcon(draw, center, iconSize, iconColor);
        else
            ChatVisuals.DrawChatIcon(draw, center, iconSize, iconColor, ChatVisuals.Color(bg));

        if (hovered)
            ImGui.SetTooltip(tab == NavTab.Contacts ? "Friends" : "Chats");
    }

    private void DrawListPane(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.ListBg));

        var headerHeight = 52f * scale;
        DrawListToolbar(draw, pos, new Vector2(size.X, headerHeight), scale);

        var bodyPos = pos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(size.X, MathF.Max(1f, size.Y - headerHeight));
        ImGui.SetCursorScreenPos(bodyPos);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var body = ImRaii.Child("##list-body", bodySize, false);
        if (!body)
            return;

        if (activeTab == NavTab.Contacts)
            DrawContactsList(snapshot, scale);
        else
            DrawChatsList(scale);
    }

    private void DrawListToolbar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        var padX = 10f * scale;
        var controlH = 32f * scale;
        var controlY = pos.Y + (size.Y - controlH) * 0.5f;

        var showSort = activeTab == NavTab.Chats;
        var sortWidth = showSort ? 82f * scale : 0f;
        var gap = showSort ? 8f * scale : 0f;
        var searchWidth = MathF.Max(60f * scale, size.X - padX * 2f - sortWidth - gap);

        if (activeTab == NavTab.Contacts)
            DrawSearchPill(draw, "##contacts-search", "Search friends...", ref contactsSearch,
                new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
        else
            DrawSearchPill(draw, "##chats-search", "Search chats...", ref chatsSearch,
                new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);

        if (showSort)
        {
            var sortPos = new Vector2(pos.X + padX + searchWidth + gap, controlY + 1f * scale);
            var sortSize = new Vector2(sortWidth, controlH - 2f * scale);
            if (ChatVisuals.SkewButton(draw, "##sort-toggle", sortByRecent ? "Recent" : "Name", sortPos, sortSize, scale))
            {
                sortByRecent = !sortByRecent;
                Reorder();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortByRecent ? "Sorted by recent message (click for Name)" : "Sorted by name (click for Recent)");
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

    private void DrawContactsList(FriendSnapshot snapshot, float scale)
    {
        if (snapshot.Friends is null)
        {
            DrawListNotice("Open the in-game Friend List to load your friends.", scale);
            return;
        }

        if (snapshot.Friends.Count == 0)
        {
            DrawListNotice("Your in-game Friend List is empty.", scale);
            return;
        }

        var filtered = snapshot.Friends.Values
            .Where(friend => MatchesSearch(friend, contactsSearch))
            .OrderBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.WorldId)
            .ToArray();

        if (filtered.Length == 0)
        {
            DrawListNotice("No matching friends.", scale);
            return;
        }

        foreach (var friend in filtered)
        {
            var isSelected = inspectedFriend?.Key == friend.Key;
            var latestPreview = FindLatestPreview(friend.Key);
            DrawListRow(friend, latestPreview, isSelected, $"##contact-{friend.Key}", out var clicked, out var doubleClicked, scale);
            if (doubleClicked)
            {
                OpenConversation(friend);
            }
            else if (clicked)
            {
                inspectedFriend = friend;
                narrowShowingDetail = true;
            }
        }
    }

    private void DrawChatsList(float scale)
    {
        if (summaryFailed)
        {
            ImGui.SetCursorPos(new Vector2(12f * scale, 10f * scale));
            ImGui.TextColored(ChatVisuals.AlertText, "Failed to read conversations.");
            ImGui.SetCursorPosX(12f * scale);
            if (ImGui.SmallButton("Retry"))
            {
                summaryFailed = false;
                summaryRevision = -1;
            }

            ImGui.Spacing();
        }

        if (rows.Count == 0)
        {
            DrawListNotice(summaryQuery is null
                ? "No conversations yet. Pick a friend in Friends to start chatting."
                : "Loading conversations...", scale);
            return;
        }

        var anyMatch = false;
        foreach (var row in rows)
        {
            if (!MatchesSearch(row.Peer, chatsSearch))
                continue;

            anyMatch = true;
            var isSelected = selected?.Key == row.Peer.Key;
            var subtitle = row.Latest is { } latest ? FormatPreview(latest) : "No messages yet";
            DrawListRow(row.Peer, subtitle, isSelected, $"##chat-{row.Peer.Key}", out var clicked, out _, scale);
            if (clicked)
                OpenConversation(row.Peer);
        }

        if (!anyMatch)
            DrawListNotice("No matching conversations.", scale);
    }

    private static void DrawListNotice(string message, float scale)
    {
        var pad = 16f * scale;
        ImGui.SetCursorPos(new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X - pad);
        ImGui.TextColored(ChatVisuals.Muted, message);
        ImGui.PopTextWrapPos();
    }

    private void DrawListRow(PlayerIdentity peer, string? subtitle, bool isSelected, string id,
        out bool clicked, out bool doubleClicked, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = 60f * scale;
        var size = new Vector2(width, rowHeight);

        clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

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
        ChatVisuals.Avatar(draw, avatars, peer, avatarPos, avatarSize);

        var worldFontSize = ImGui.GetFontSize() * 0.84f;
        var maxWorldWidth = MathF.Min(96f * scale, width * 0.30f);
        var worldText = ChatVisuals.TruncateSingleLine(peer.World, worldFontSize, maxWorldWidth);
        var worldSize = ChatVisuals.CalcTextSize(worldText, worldFontSize);
        var worldX = origin.X + width - padX - worldSize.X;
        var worldY = origin.Y + (rowHeight - worldSize.Y) * 0.5f;
        ChatVisuals.AddText(draw, new Vector2(worldX, worldY), worldFontSize, ChatVisuals.Color(ChatVisuals.Muted), worldText);

        var textLeft = avatarPos.X + avatarSize + 10f * scale;
        var textWidth = MathF.Max(20f * scale, worldX - textLeft - 10f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.04f;
        var nameText = ChatVisuals.TruncateSingleLine(peer.Name, nameFontSize, textWidth);
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

    private void DrawContactProfile(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var contentPos = pos;
        var contentSize = size;
        if (narrow)
        {
            var subHeaderH = 42f * scale;
            DrawSubHeader(draw, pos, size.X, subHeaderH, inspectedFriend?.Name ?? "Friends", null, showBack: true, scale);
            contentPos = pos + new Vector2(0, subHeaderH);
            contentSize = new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH));
        }

        if (inspectedFriend is not { } friend)
        {
            var prompt = snapshot.Friends is null
                ? "Open the in-game Friend List to load your friends."
                : "Select a friend to view profile.";
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

        var badgeFontSize = ImGui.GetFontSize() * 0.96f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var worldLabel = ChatVisuals.TruncateSingleLine($"@ {friend.World}", badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(worldLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var totalHeight = avatarSize + 18f * scale + nameSize.Y + 12f * scale + badgeSize.Y;
        var startY = contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.Avatar(draw, avatars, friend, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.19f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##profile-start-chat", actionCenter, actionRadius, scale))
            OpenConversation(friend);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open chat");

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + 18f * scale);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, namePos.Y + nameSize.Y + 12f * scale);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), worldLabel);
    }

    private void DrawConversationPane(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var subHeaderH = 42f * scale;
        if (selected is not { } peer || conversation is not { } view)
        {
            if (narrow)
                DrawSubHeader(draw, pos, size.X, subHeaderH, "Chats", null, showBack: true, scale);

            var emptyPos = narrow ? pos + new Vector2(0, subHeaderH) : pos;
            var emptySize = narrow ? new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH)) : size;
            const string emptyPrompt = "Choose a conversation.";
            var fontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(emptyPrompt, fontSize);
            ChatVisuals.AddText(draw, emptyPos + (emptySize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), emptyPrompt);
            return;
        }

        DrawSubHeader(draw, pos, size.X, subHeaderH, peer.Name, peer.World, showBack: narrow, scale);
        view.Update();

        var draft = drafts[peer.Key];
        var friend = snapshot.FindRecipient(peer.Key, out var unavailable);
        var warning = draft.Warning is { AttachedToMessage: false } local ? local : unassignedWarning;
        var status = unavailable ?? (warning is { AttachedToMessage: false } visible ? visible.Message : null);
        var statusIsAlert = unavailable is null;
        var writeFailed = capture.FailedWriteOwner == session!.ContentId;

        var bannerMaxWidth = MathF.Max(80f * scale, size.X - 36f * scale);
        ChatVisuals.BannerLayout? statusBanner = status is not null
            ? ChatVisuals.MeasureSystemBanner(status, bannerMaxWidth, scale)
            : null;
        const string writeErrorText = "A Tell could not be saved. See /xllog for details.";
        ChatVisuals.BannerLayout? writeErrorBanner = writeFailed
            ? ChatVisuals.MeasureSystemBanner(writeErrorText, bannerMaxWidth, scale)
            : null;

        var statusHeight = statusBanner?.Height ?? 0f;
        var writeErrHeight = writeErrorBanner?.Height ?? 0f;
        var sendBarHeight = 52f * scale;
        var composerHeight = sendBarHeight + statusHeight + writeErrHeight + (statusHeight + writeErrHeight > 0 ? 4f * scale : 0f);
        var timelinePos = pos + new Vector2(0, subHeaderH);
        var timelineHeight = MathF.Max(1f, size.Y - subHeaderH - composerHeight);

        using var peerId = ImRaii.PushId(peer.Key.ToString());
        ImGui.SetCursorScreenPos(timelinePos);
        using (var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (var timeline = ImRaii.Child("##messages", new Vector2(size.X, timelineHeight), false))
        {
            if (timeline)
                DrawTimeline(view, peer, scale);
        }

        var footerPos = timelinePos + new Vector2(0, timelineHeight);
        if (status is not null && statusBanner is { } sb)
        {
            ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, status, statusIsAlert, scale, sb);
            footerPos.Y += sb.Height;
        }

        if (writeErrorBanner is { } wb)
        {
            ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, writeErrorText, isAlert: true, scale, wb);
            footerPos.Y += wb.Height;
        }

        var sendBarPos = pos + new Vector2(0, size.Y - sendBarHeight);
        DrawSendBar(draw, peer, friend, draft, sendBarPos, new Vector2(size.X, sendBarHeight), bottomCorners, scale);
    }

    private void DrawSubHeader(ImDrawListPtr draw, Vector2 pos, float width, float height, string title, string? world,
        bool showBack, float scale)
    {
        var left = pos.X + 14f * scale;
        if (showBack)
        {
            var btnSize = 28f * scale;
            var btnPos = new Vector2(pos.X + 8f * scale, pos.Y + (height - btnSize) * 0.5f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##narrow-back", new Vector2(btnSize)))
                narrowShowingDetail = false;

            if (ImGui.IsItemHovered())
                draw.AddRectFilled(btnPos, btnPos + new Vector2(btnSize), ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);

            ChatVisuals.DrawBackIcon(draw, btnPos + new Vector2(btnSize * 0.5f), 14f * scale,
                ChatVisuals.Color(ChatVisuals.Ink), MathF.Max(1.6f, 1.8f * scale));
            left = btnPos.X + btnSize + 8f * scale;
        }

        var rightLimit = pos.X + width - 14f * scale;
        var avail = MathF.Max(30f * scale, rightLimit - left);
        var titleFontSize = ImGui.GetFontSize() * 1.06f;

        if (!string.IsNullOrEmpty(world))
        {
            var worldFontSize = ImGui.GetFontSize() * 0.90f;
            var worldLabel = $"@ {world}";
            var worldSize = ChatVisuals.CalcTextSize(worldLabel, worldFontSize);
            var titleMax = MathF.Max(30f * scale, avail - worldSize.X - 8f * scale);
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, titleMax);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            var titleY = pos.Y + (height - titleSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(left, titleY), titleFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                titleText, scale);

            var worldX = left + titleSize.X + 8f * scale;
            var worldY = pos.Y + (height - worldSize.Y) * 0.5f;
            var clampedWorld = ChatVisuals.TruncateSingleLine(worldLabel, worldFontSize, MathF.Max(0, rightLimit - worldX));
            ChatVisuals.AddText(draw, new Vector2(worldX, worldY), worldFontSize, ChatVisuals.Color(ChatVisuals.Muted),
                clampedWorld);
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

    private void DrawTimeline(ConversationHistory view, PlayerIdentity peer, float scale)
    {
        var followLatest = view.RevealLatest || (view.AddedMessages && view.PrependAnchor is null
            && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f * scale);

        ImGui.Dummy(new Vector2(0, 6f * scale));
        var availWidth = ImGui.GetContentRegionAvail().X;

        if (view.ReadFailed)
        {
            DrawTimelineBanner("History could not be read.", isAlert: true, availWidth, scale);
            DrawCenteredPillButton("Retry", "##retry-history", availWidth, scale, view.Retry);
        }
        else if (view.HasOlder)
        {
            using var disabled = ImRaii.Disabled(view.IsLoading);
            DrawCenteredPillButton(view.IsLoading ? "Loading earlier messages..." : "Load earlier messages",
                "##load-older", availWidth, scale, view.LoadPage);
        }
        else if (view.Messages.Count == 0)
        {
            var label = view.IsLoading ? "Loading history..." : "No messages yet.";
            var fontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(label, fontSize);
            var origin = ImGui.GetCursorScreenPos();
            ChatVisuals.AddText(ImGui.GetWindowDrawList(), origin + new Vector2((availWidth - textSize.X) * 0.5f, 12f * scale),
                fontSize, ChatVisuals.Color(ChatVisuals.Muted), label);
            ImGui.Dummy(new Vector2(availWidth, textSize.Y + 24f * scale));
        }

        var previousFirstY = firstMessageY;
        var first = true;
        long? previousSentAt = null;
        bool? previousOutgoing = null;
        var previousHadRejection = false;

        foreach (var message in view.Messages)
        {
            var showTimeDivider = ChatVisuals.ShouldInsertTimeDivider(previousSentAt, message.SentAt);
            var isGroupStart = previousOutgoing is null || previousOutgoing.Value != message.Outgoing
                || showTimeDivider || previousHadRejection;

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

            DrawMessageBubble(message, message.Outgoing ? session!.Player : peer, isGroupStart, scale);

            if (message.Rejection is { } rejection)
                DrawTimelineBanner(rejection, isAlert: true, availWidth, scale);

            previousSentAt = message.SentAt;
            previousOutgoing = message.Outgoing;
            previousHadRejection = message.Rejection is not null;
        }

        ImGui.Dummy(new Vector2(0, 8f * scale));
        if (followLatest && view.Messages.Count > 0)
            ImGui.SetScrollHereY(1f);

        view.ClearScrollRequests();
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

    private void DrawMessageBubble(TellMessage message, PlayerIdentity author, bool isGroupStart, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var sideMargin = 16f * scale;
        var avatarSize = 36f * scale;
        var avatarGap = 11f * scale;
        var fontSize = ImGui.GetFontSize();
        var padding = new Vector2(11f, 7f) * scale;
        var innerWidth = MathF.Max(80f * scale, availWidth - sideMargin * 2f);
        var maxBody = MathF.Max(36f * scale,
            MathF.Min(420f * scale, innerWidth * 0.78f - avatarSize - avatarGap - padding.X * 2f));
        var textSize = ChatVisuals.CalcTextSize(message.Body, fontSize, maxBody);
        var bubbleSize = new Vector2(MathF.Min(maxBody, textSize.X) + padding.X * 2f, textSize.Y + padding.Y * 2f);

        var showName = isGroupStart && !message.Outgoing;
        var nameFontSize = fontSize * 0.88f;
        var nameOffset = showName ? nameFontSize + 4f * scale : 0f;
        var bubbleTop = origin.Y + nameOffset;
        var contentHeight = isGroupStart
            ? MathF.Max(avatarSize, nameOffset + bubbleSize.Y)
            : bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            var bubbleX = message.Outgoing
                ? origin.X + availWidth - sideMargin - avatarSize - avatarGap - bubbleSize.X
                : origin.X + sideMargin + avatarSize + avatarGap;
            var bubblePos = new Vector2(bubbleX, bubbleTop);

            if (isGroupStart)
            {
                var avatarX = message.Outgoing
                    ? origin.X + availWidth - sideMargin - avatarSize
                    : origin.X + sideMargin;
                ChatVisuals.Avatar(draw, avatars, author, new Vector2(avatarX, origin.Y), avatarSize);
            }

            if (showName)
            {
                var nameText = ChatVisuals.TruncateSingleLine(author.Name, nameFontSize,
                    MathF.Max(40f * scale, innerWidth - avatarSize - avatarGap));
                ChatVisuals.AddText(draw, new Vector2(bubbleX, origin.Y), nameFontSize,
                    ChatVisuals.Color(ChatVisuals.Ink), nameText);
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
                scale);

            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(bubblePos, bubblePos + bubbleSize))
                ImGui.SetTooltip(ChatVisuals.FormatFullTime(message.SentAt));
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private void DrawSendBar(ImDrawListPtr draw, PlayerIdentity peer, PlayerIdentity? friend, Draft draft, Vector2 pos,
        Vector2 size, ImDrawFlags bottomCorners, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.SendBarBg), 10f * scale, bottomCorners);
        draw.AddLine(pos, pos + new Vector2(size.X, 0), ChatVisuals.Color(ChatVisuals.Divider), 1f);

        var padX = 12f * scale;
        var pillH = 34f * scale;
        var pillY = pos.Y + (size.Y - pillH) * 0.5f;
        var sendSize = 34f * scale;
        var sendGap = 8f * scale;
        var pillWidth = MathF.Max(60f * scale, size.X - padX * 2f - sendSize - sendGap);
        var pillPos = new Vector2(pos.X + padX, pillY);
        var pillRounding = pillH * 0.5f;

        draw.AddRectFilled(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(ChatVisuals.White), pillRounding);

        var inputPadX = 14f * scale;
        var inputPadY = MathF.Max(2f, (pillH - ImGui.GetFontSize()) * 0.5f);
        var enter = false;
        ImGui.SetCursorScreenPos(new Vector2(pillPos.X + inputPadX, pillPos.Y));
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, inputPadY)))
        {
            ImGui.SetNextItemWidth(MathF.Max(20f * scale, pillWidth - inputPadX * 2f));
            enter = ImGui.InputTextWithHint("##tell", "Write a Tell...", ref draft.Text, 2048,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var canSend = friend is not null;
        if (enter && canSend)
        {
            SubmitDraft(peer, draft);
            ImGui.SetKeyboardFocusHere(-1);
        }

        var borderCol = ImGui.IsItemActive() ? ChatVisuals.InputFocus : ChatVisuals.InputBorder;
        draw.AddRect(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(borderCol), pillRounding,
            ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));

        var sendPos = new Vector2(pillPos.X + pillWidth + sendGap, pillY);
        ImGui.SetCursorScreenPos(sendPos);
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

        if (clicked && canSend)
            SubmitDraft(peer, draft);
    }

    private void SyncSession(PlayerSession? active)
    {
        if (ReferenceEquals(session, active))
            return;

        session = active;
        inspectedFriend = null;
        selected = null;
        conversation = null;
        summaryQuery = null;
        summaryRevision = -1;
        summaryFailed = false;
        summaries = [];
        rows = [];
        latestByPeer.Clear();
        drafts.Clear();
        unassignedWarning = null;
        narrowShowingDetail = false;
        contactsSearch = string.Empty;
        chatsSearch = string.Empty;
    }

    private void SyncInspectedFriend(FriendSnapshot snapshot)
    {
        if (inspectedFriend is not { } friend)
            return;

        if (snapshot.Friends is null)
            return;

        if (snapshot.Friends.TryGetValue(friend.Key, out var current))
        {
            inspectedFriend = current;
        }
        else
        {
            inspectedFriend = null;
            if (activeTab == NavTab.Contacts)
                narrowShowingDetail = false;
        }
    }

    private void UpdateSummaries()
    {
        if (summaryQuery is { IsCompleted: true } completed)
        {
            summaryQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                summaries = completed.Result;
                Reorder();
            }
            else
            {
                _ = completed.Exception;
                summaryFailed = true;
            }
        }

        if (summaryQuery is null && !summaryFailed && summaryRevision != history.Revision)
        {
            summaryRevision = history.Revision;
            summaryQuery = history.ReadConversationsAsync(session!.ContentId);
        }
    }

    private void OpenConversation(PlayerIdentity peer)
    {
        activeTab = NavTab.Chats;
        narrowShowingDetail = true;
        if (selected?.Key == peer.Key)
            return;

        selected = peer;
        conversation = new ConversationHistory(history, session!.ContentId, peer.Key);
        firstMessageY = 0;
        drafts.TryAdd(peer.Key, new Draft());
        Reorder();
    }

    private void Reorder()
    {
        IEnumerable<ConversationSummary> visible = summaries;
        if (selected is { } peer && !summaries.Any(row => row.Peer.Key == peer.Key))
            visible = visible.Append(new ConversationSummary(peer, null));

        var ranked = sortByRecent
            ? visible.OrderByDescending(row => row.Latest?.SentAt).ThenByDescending(row => row.Latest?.Id)
                .ThenBy(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase)
            : visible.OrderBy(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase);
        rows = ranked
            .ThenBy(row => row.Peer.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Peer.WorldId)
            .ToArray();

        latestByPeer.Clear();
        foreach (var summary in summaries)
        {
            if (summary.Latest is { } latest)
                latestByPeer[summary.Peer.Key] = latest;
        }
    }

    private string? FindLatestPreview(PlayerKey key) =>
        latestByPeer.TryGetValue(key, out var latest) ? FormatPreview(latest) : null;

    private static string FormatPreview(TellMessage latest) =>
        latest.Outgoing ? $"You: {latest.Body}" : latest.Body;

    private static bool MatchesSearch(PlayerIdentity peer, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        return peer.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || peer.World.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private void SubmitDraft(PlayerIdentity peer, Draft draft)
    {
        if (!sender.TrySubmit(session!, peer, draft.Text, out var error))
        {
            draft.Warning = new TellWarning(session!, peer.Key, error);
            return;
        }

        draft.Text = string.Empty;
        draft.Warning = null;
        unassignedWarning = null;
    }

    private void ApplySendWarnings()
    {
        while (sender.TakeWarning() is { } warning)
        {
            if (!ReferenceEquals(warning.Session, session) || warning.AttachedToMessage)
                continue;

            if (warning.Peer is { } peer && drafts.TryGetValue(peer, out var draft))
                draft.Warning = warning;
            else
                unassignedWarning = warning;
        }
    }

    private enum NavTab
    {
        Contacts,
        Chats,
    }

    private sealed class Draft
    {
        public string Text = string.Empty;
        public TellWarning? Warning;
    }
}
