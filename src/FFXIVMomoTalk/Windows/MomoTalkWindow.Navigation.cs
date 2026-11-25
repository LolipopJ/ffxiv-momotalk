using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private void DrawWideLayout(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
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
        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactProfile(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
            case NavTab.Groups:
                DrawGroupDetail(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
            default:
                DrawConversationPane(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
        }
    }

    private void DrawNarrowLayout(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        if (!narrowShowingDetail)
        {
            var navHeight = 48f * scale;
            var listHeight = MathF.Max(1f, bodySize.Y - navHeight);
            DrawListPane(draw, snapshot, bodyPos, new Vector2(bodySize.X, listHeight), scale);
            DrawBottomNavBar(draw, bodyPos + new Vector2(0, listHeight), new Vector2(bodySize.X, navHeight), scale);
            return;
        }

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactProfile(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
            case NavTab.Groups:
                DrawGroupDetail(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
            default:
                DrawConversationPane(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
        }
    }

    private void DrawLeftNavRail(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottomLeft);

        var tabHeight = 54f * scale;
        DrawNavTabButton(draw, "##nav-contacts", NavTab.Contacts, pos, new Vector2(size.X, tabHeight),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-groups", NavTab.Groups, pos + new Vector2(0, tabHeight),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-chats", NavTab.Chats, pos + new Vector2(0, tabHeight * 2f),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
    }

    private void DrawBottomNavBar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottom);

        var third = MathF.Floor(size.X / 3f);
        DrawNavTabButton(draw, "##bnav-contacts", NavTab.Contacts, pos, new Vector2(third, size.Y),
            ImDrawFlags.RoundCornersBottomLeft, scale);
        DrawNavTabButton(draw, "##bnav-groups", NavTab.Groups, pos + new Vector2(third, 0), new Vector2(third, size.Y),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##bnav-chats", NavTab.Chats, pos + new Vector2(third * 2f, 0),
            new Vector2(size.X - third * 2f, size.Y), ImDrawFlags.RoundCornersBottomRight, scale);
    }

    private void DrawNavTabButton(ImDrawListPtr draw, string id, NavTab tab, Vector2 pos, Vector2 size,
        ImDrawFlags roundCorners, float scale)
    {
        ImGui.SetCursorScreenPos(pos);
        if (ImGui.InvisibleButton(id, size))
        {
            CommitActiveNote();
            activeTab = tab;
            narrowShowingDetail = false;
        }

        var selectedTab = activeTab == tab;
        var hovered = ImGui.IsItemHovered();
        var bg = selectedTab
            ? ChatVisuals.RailActive
            : hovered
                ? Vector4.Lerp(ChatVisuals.RailBg, ChatVisuals.RailActive, 0.55f)
                : ChatVisuals.RailBg;

        if (selectedTab || hovered)
        {
            var rounding = roundCorners == ImDrawFlags.None ? 0f : 10f * scale;
            draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(bg), rounding, roundCorners);
        }

        // Blend an opaque secondary color against bg instead of alpha transparency so overlapping icon primitives render uniformly.
        var iconStrength = selectedTab ? 1f : hovered ? 0.85f : 0.50f;
        var iconColor = ChatVisuals.Color(Vector4.Lerp(bg, ChatVisuals.White, iconStrength));
        var bgCol = ChatVisuals.Color(bg);
        var center = pos + size * 0.5f;
        var iconSize = 24f * scale;
        switch (tab)
        {
            case NavTab.Contacts:
                ChatVisuals.DrawContactsIcon(draw, center, iconSize, iconColor);
                break;
            case NavTab.Groups:
                ChatVisuals.DrawGroupsIcon(draw, center, iconSize, iconColor, bgCol);
                break;
            case NavTab.Chats:
                ChatVisuals.DrawChatIcon(draw, center, iconSize, iconColor, bgCol);
                break;
        }

        if (hovered)
        {
            ImGui.SetTooltip(tab switch
            {
                NavTab.Contacts => Loc.Text("Friends"),
                NavTab.Groups => Loc.Text("Groups"),
                NavTab.Chats => Loc.Text("Chats"),
                _ => string.Empty,
            });
        }
    }

    private enum NavTab
    {
        Contacts,
        Groups,
        Chats,
    }
}
