using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void DrawFriendAnniversariesSection(SocialSnapshot snapshot, IReadOnlyList<PlayerIdentity> filtered, float scale)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var items = CollectUpcomingAnniversaries(snapshot, filtered, today);
        if (items.Count == 0)
            return;

        var draw = ImGui.GetWindowDrawList();
        var headerOrigin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var headerHeight = 30f * scale;
        var headerSize = new Vector2(width, headerHeight);
        ImGui.Dummy(headerSize);

        if (ImGui.IsRectVisible(headerOrigin, headerOrigin + headerSize))
        {
            draw.AddRectFilled(headerOrigin, headerOrigin + headerSize, ChatVisuals.Color(ChatVisuals.AnniversaryHeaderBg));
            draw.AddRectFilled(headerOrigin, new Vector2(headerOrigin.X + 3f * scale, headerOrigin.Y + headerHeight),
                ChatVisuals.Color(ChatVisuals.HeaderPink));
            draw.AddLine(
                new Vector2(headerOrigin.X, headerOrigin.Y + headerHeight - 1f),
                new Vector2(headerOrigin.X + width, headerOrigin.Y + headerHeight - 1f),
                ChatVisuals.Color(ChatVisuals.AnniversaryDivider),
                1f);

            var padX = 12f * scale;
            var countText = items.Count.ToString(CultureInfo.InvariantCulture);
            var countFontSize = ImGui.GetFontSize() * 0.80f;
            var countSize = ChatVisuals.CalcTextSize(countText, countFontSize);
            var pillH = 17f * scale;
            var pillW = MathF.Max(pillH, countSize.X + 10f * scale);
            var pillPos = new Vector2(headerOrigin.X + width - padX - pillW, headerOrigin.Y + (headerHeight - pillH) * 0.5f);
            draw.AddRectFilled(pillPos, pillPos + new Vector2(pillW, pillH), ChatVisuals.Color(ChatVisuals.HeaderPink), pillH * 0.5f);
            ChatVisuals.AddBoldText(draw, pillPos + (new Vector2(pillW, pillH) - countSize) * 0.5f,
                countFontSize, ChatVisuals.Color(ChatVisuals.White), countText, scale);

            var iconSize = 14f * scale;
            var iconCenter = new Vector2(headerOrigin.X + padX + iconSize * 0.5f, headerOrigin.Y + headerHeight * 0.5f);
            ChatVisuals.DrawCakeIcon(draw, iconCenter, iconSize, ChatVisuals.Color(ChatVisuals.AnniversaryAccent));

            var titleLeft = iconCenter.X + iconSize * 0.5f + 7f * scale;
            var titleMaxW = MathF.Max(20f * scale, pillPos.X - 8f * scale - titleLeft);
            var titleFontSize = ImGui.GetFontSize() * 0.88f;
            var titleText = ChatVisuals.TruncateSingleLine(Loc.Text("FriendAnniversaries"), titleFontSize, titleMaxW);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            ChatVisuals.AddBoldText(draw, new Vector2(titleLeft, headerOrigin.Y + (headerHeight - titleSize.Y) * 0.5f),
                titleFontSize, ChatVisuals.Color(ChatVisuals.AnniversaryAccent), titleText, scale);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            var friend = entry.Friend;
            var isSelected = inspectedFriend?.ContentId == friend.ContentId;
            DrawFriendAnniversaryRow(
                friend,
                entry.Anniversary,
                isSelected,
                isLast: i == items.Count - 1,
                scale,
                out var clicked,
                out var doubleClicked,
                out var rightClicked);

            HandleFriendRowInteraction(friend, clicked, doubleClicked, rightClicked);
        }
    }

    private void DrawFriendAnniversaryRow(
        PlayerIdentity friend,
        UpcomingAnniversary anniversary,
        bool isSelected,
        bool isLast,
        float scale,
        out bool clicked,
        out bool doubleClicked,
        out bool rightClicked)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = 60f * scale;
        var size = new Vector2(width, rowHeight);

        clicked = ImGui.InvisibleButton($"##anniv-contact-{friend.ContentId:X16}", size);
        var hovered = ImGui.IsItemHovered();
        doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        rightClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        var countdown = FormatAnniversaryCountdown(anniversary);
        if (!ImGui.IsRectVisible(origin, origin + size))
            return;

        var draw = ImGui.GetWindowDrawList();
        var bg = isSelected || hovered ? ChatVisuals.AnniversaryRowHover : ChatVisuals.AnniversaryRowBg;
        draw.AddRectFilled(origin, origin + size, ChatVisuals.Color(bg));

        draw.AddRectFilled(
            origin,
            new Vector2(origin.X + 3f * scale, origin.Y + rowHeight),
            ChatVisuals.Color(anniversary.IsToday ? ChatVisuals.HeaderPink : ChatVisuals.AnniversaryDivider));

        var borderThickness = isLast ? MathF.Max(1.5f, 1.8f * scale) : 1f;
        draw.AddLine(
            new Vector2(origin.X, origin.Y + rowHeight - 1f),
            new Vector2(origin.X + width, origin.Y + rowHeight - 1f),
            ChatVisuals.Color(ChatVisuals.AnniversaryDivider),
            borderThickness);

        draw.PushClipRect(origin, origin + size, true);

        var padX = 12f * scale;
        var avatarSize = 40f * scale;
        var avatarPos = origin + new Vector2(padX, (rowHeight - avatarSize) * 0.5f);
        ChatVisuals.Avatar(draw, avatars, friend, avatarPos, avatarSize);
        if (anniversary.IsToday)
        {
            var radius = avatarSize * 0.5f;
            draw.AddCircle(
                avatarPos + new Vector2(radius),
                radius + 1.5f * scale,
                ChatVisuals.Color(ChatVisuals.HeaderPink),
                36,
                MathF.Max(1.6f, 2f * scale));
        }

        var badgeSize = 24f * scale;
        var badgePos = new Vector2(origin.X + width - padX - badgeSize, origin.Y + (rowHeight - badgeSize) * 0.5f);
        var badgeCenter = badgePos + new Vector2(badgeSize * 0.5f);
        draw.AddCircleFilled(
            badgeCenter,
            badgeSize * 0.5f,
            ChatVisuals.Color(anniversary.IsToday ? ChatVisuals.HeaderPink : ChatVisuals.AnniversaryBadgeBg),
            24);
        ChatVisuals.DrawCakeIcon(
            draw,
            badgeCenter,
            13.5f * scale,
            ChatVisuals.Color(anniversary.IsToday ? ChatVisuals.White : ChatVisuals.AnniversaryAccent));

        var textLeft = avatarPos.X + avatarSize + 10f * scale;
        var textWidth = MathF.Max(20f * scale, badgePos.X - 8f * scale - textLeft);

        var nameFontSize = ImGui.GetFontSize() * 1.04f;
        var nameText = ChatVisuals.TruncateSingleLine(friend.Name, nameFontSize, textWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        var subFontSize = ImGui.GetFontSize() * 0.86f;
        var subText = ChatVisuals.TruncateSingleLine(countdown, subFontSize, textWidth);
        var subSize = ChatVisuals.CalcTextSize(subText, subFontSize);

        var lineGap = 3f * scale;
        var blockY = origin.Y + (rowHeight - nameSize.Y - subSize.Y - lineGap) * 0.5f;
        var titleColor = ChatVisuals.Color(GetFriendNameColor(friend.ContentId));
        ChatVisuals.AddBoldText(draw, new Vector2(textLeft, blockY), nameFontSize, titleColor, nameText, scale);

        var subPos = new Vector2(textLeft, blockY + nameSize.Y + lineGap);
        var subColor = ChatVisuals.Color(ChatVisuals.AnniversaryAccent);
        if (anniversary.IsToday)
            ChatVisuals.AddBoldText(draw, subPos, subFontSize, subColor, subText, scale);
        else
            ChatVisuals.AddText(draw, subPos, subFontSize, subColor, subText);

        draw.PopClipRect();

        if (hovered && subText != countdown)
            ImGui.SetTooltip(countdown);
    }

    private static List<FriendAnniversaryEntry> CollectUpcomingAnniversaries(
        SocialSnapshot snapshot,
        IReadOnlyList<PlayerIdentity> filtered,
        DateOnly today)
    {
        var items = new List<FriendAnniversaryEntry>();
        if (snapshot.FriendAddedAt is null or { Count: 0 })
            return items;

        foreach (var friend in filtered)
        {
            if (!snapshot.FriendAddedAt.TryGetValue(friend.ContentId, out var addedAt) || addedAt <= 0)
                continue;

            var addedDate = FriendDates.ToLocalDate(addedAt);
            if (FriendDates.TryGetUpcomingAnniversary(addedDate, today) is { } anniversary)
                items.Add(new FriendAnniversaryEntry(friend, anniversary));
        }

        items.Sort((a, b) =>
        {
            var cmp = a.Anniversary.DaysRemaining.CompareTo(b.Anniversary.DaysRemaining);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Friend.Name, b.Friend.Name);
            if (cmp != 0)
                return cmp;

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Friend.World, b.Friend.World);
            if (cmp != 0)
                return cmp;

            cmp = a.Friend.WorldId.CompareTo(b.Friend.WorldId);
            return cmp != 0 ? cmp : a.Friend.ContentId.CompareTo(b.Friend.ContentId);
        });

        return items;
    }

    private static string FormatAnniversaryCountdown(UpcomingAnniversary anniversary)
    {
        if (anniversary.IsToday)
            return Loc.Format("FriendAnniversaryToday", anniversary.Years);

        return anniversary.DaysRemaining == 1
            ? Loc.Format("FriendAnniversaryCountdownOne", anniversary.Years, anniversary.DaysRemaining)
            : Loc.Format("FriendAnniversaryCountdown", anniversary.Years, anniversary.DaysRemaining);
    }

    private static string FormatFriendshipDuration(DateOnly addedDate, DateOnly today)
    {
        var duration = FriendDates.CalculateDuration(addedDate, today);
        if (duration.IsZero)
            return Loc.Text("FriendAddedToday");

        var parts = new List<string>(3);
        if (duration.Years > 0)
            parts.Add(Loc.Format(duration.Years == 1 ? "DurationYearOne" : "DurationYears", duration.Years));
        if (duration.Months > 0)
            parts.Add(Loc.Format(duration.Months == 1 ? "DurationMonthOne" : "DurationMonths", duration.Months));
        if (duration.Days > 0)
            parts.Add(Loc.Format(duration.Days == 1 ? "DurationDayOne" : "DurationDays", duration.Days));

        var joined = parts.Count switch
        {
            1 => parts[0],
            2 => Loc.Format("DurationJoinTwo", parts[0], parts[1]),
            _ => Loc.Format("DurationJoinThree", parts[0], parts[1], parts[2]),
        };
        return Loc.Format("FriendDuration", joined);
    }

    private readonly record struct FriendAnniversaryEntry(PlayerIdentity Friend, UpcomingAnniversary Anniversary);
}
