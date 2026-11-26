using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal static partial class ChatVisuals
{
    public static void Avatar(ImDrawListPtr draw, AvatarCache avatars, PlayerIdentity player, Vector2 position, float size,
        bool dropShadow = false)
    {
        var radius = size / 2f;
        var center = position + new Vector2(radius);
        if (dropShadow)
            draw.AddCircleFilled(center + new Vector2(0, 1.5f), radius + 1.5f, Color(Shadow), 36);

        if (avatars.GetTexture(player) is { } texture)
        {
            draw.AddImageRounded(texture.Handle, position, position + new Vector2(size), Vector2.Zero, Vector2.One,
                uint.MaxValue, radius);
            return;
        }

        draw.AddCircleFilled(center, radius, Color(AvatarFallbackBg), 32);
        var fg = Color(AvatarFallbackFg);
        draw.AddCircleFilled(center - new Vector2(0, size * 0.14f), size * 0.17f, fg, 24);
        draw.AddRectFilled(center + new Vector2(-size * 0.28f, size * 0.06f),
            center + new Vector2(size * 0.28f, size * 0.34f), fg, size * 0.15f);
    }

    public static void GroupAvatar(ImDrawListPtr draw, ConversationKind kind, Vector2 position, float size,
        bool dropShadow = false)
    {
        var radius = size / 2f;
        var center = position + new Vector2(radius);
        if (dropShadow)
            draw.AddCircleFilled(center + new Vector2(0, 1.5f), radius + 1.5f, Color(Shadow), 36);

        var bg = kind switch
        {
            ConversationKind.FreeCompany => GroupFcBg,
            ConversationKind.CrossWorldLinkshell => GroupCwlsBg,
            ConversationKind.Linkshell => GroupLsBg,
            ConversationKind.Static => GroupStaticBg,
            _ => AvatarFallbackBg,
        };
        draw.AddCircleFilled(center, radius, Color(bg), 36);

        var fg = Color(White);
        var iconSize = size * 0.54f;
        switch (kind)
        {
            case ConversationKind.FreeCompany:
                DrawShieldIcon(draw, center, iconSize, fg);
                break;
            case ConversationKind.CrossWorldLinkshell:
                DrawCrossWorldPearlIcon(draw, center, iconSize, fg);
                break;
            case ConversationKind.Linkshell:
                DrawLinkshellIcon(draw, center, iconSize, fg);
                break;
            case ConversationKind.Static:
                DrawGroupsIcon(draw, center, iconSize * 1.06f, fg, Color(bg));
                break;
        }
    }

    public static void DrawGroupsIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, uint bgCol)
    {
        // Side silhouettes
        var sideOffsetX = size * 0.31f;
        var sideHeadR = size * 0.145f;
        var sideHeadY = center.Y - size * 0.13f;
        var sideBodyTop = center.Y + size * 0.06f;
        var sideBodyBottom = center.Y + size * 0.34f;
        var sideHalfW = size * 0.20f;

        draw.AddCircleFilled(new Vector2(center.X - sideOffsetX, sideHeadY), sideHeadR, col, 18);
        draw.AddRectFilled(
            new Vector2(center.X - sideOffsetX - sideHalfW, sideBodyTop),
            new Vector2(center.X - sideOffsetX + sideHalfW, sideBodyBottom),
            col, sideHalfW * 0.7f, ImDrawFlags.RoundCornersTop);

        draw.AddCircleFilled(new Vector2(center.X + sideOffsetX, sideHeadY), sideHeadR, col, 18);
        draw.AddRectFilled(
            new Vector2(center.X + sideOffsetX - sideHalfW, sideBodyTop),
            new Vector2(center.X + sideOffsetX + sideHalfW, sideBodyBottom),
            col, sideHalfW * 0.7f, ImDrawFlags.RoundCornersTop);

        // Background cutout around foreground center silhouette so figures separate cleanly
        var gap = MathF.Max(1.2f, size * 0.065f);
        var mainHeadCenter = new Vector2(center.X, center.Y - size * 0.21f);
        var mainHeadR = size * 0.18f;
        var mainHalfW = size * 0.27f;
        var mainBodyTop = center.Y + size * 0.02f;
        var mainBodyBottom = center.Y + size * 0.38f;

        draw.AddCircleFilled(mainHeadCenter, mainHeadR + gap, bgCol, 22);
        draw.AddRectFilled(
            new Vector2(center.X - mainHalfW - gap, mainBodyTop - gap),
            new Vector2(center.X + mainHalfW + gap, mainBodyBottom),
            bgCol, (mainHalfW + gap) * 0.7f, ImDrawFlags.RoundCornersTop);

        // Center foreground silhouette
        draw.AddCircleFilled(mainHeadCenter, mainHeadR, col, 20);
        draw.AddRectFilled(
            new Vector2(center.X - mainHalfW, mainBodyTop),
            new Vector2(center.X + mainHalfW, mainBodyBottom),
            col, mainHalfW * 0.7f, ImDrawFlags.RoundCornersTop);
    }

    private static void DrawShieldIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var halfW = size * 0.38f;
        var topY = center.Y - size * 0.42f;
        var midY = center.Y + size * 0.06f;
        var botY = center.Y + size * 0.46f;
        draw.AddRectFilled(
            new Vector2(center.X - halfW, topY),
            new Vector2(center.X + halfW, midY),
            col, size * 0.10f, ImDrawFlags.RoundCornersTop);
        draw.AddTriangleFilled(
            new Vector2(center.X - halfW, midY - 1f),
            new Vector2(center.X + halfW, midY - 1f),
            new Vector2(center.X, botY),
            col);
    }

    private static void DrawCrossWorldPearlIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var r = size * 0.42f;
        var thickness = MathF.Max(1.5f, size * 0.11f);
        draw.AddCircle(center, r, col, 28, thickness);
        draw.AddLine(new Vector2(center.X - r, center.Y), new Vector2(center.X + r, center.Y), col, thickness * 0.85f);
        draw.AddLine(new Vector2(center.X, center.Y - r), new Vector2(center.X, center.Y + r), col, thickness * 0.85f);
        draw.AddCircleFilled(center, size * 0.14f, col, 18);
    }

    private static void DrawLinkshellIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var thickness = MathF.Max(1.5f, size * 0.11f);
        draw.AddCircle(center, size * 0.40f, col, 28, thickness);
        draw.AddCircle(center, size * 0.22f, col, 22, thickness * 0.85f);
        draw.AddCircleFilled(center, size * 0.09f, col, 14);
    }

    public static void DrawContactsIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var headRadius = size * 0.22f;
        draw.AddCircleFilled(center + new Vector2(0, -size * 0.20f), headRadius, col, 24);
        var bodyTop = center.Y + size * 0.06f;
        var bodyBottom = center.Y + size * 0.42f;
        var halfWidth = size * 0.36f;
        draw.AddRectFilled(
            new Vector2(center.X - halfWidth, bodyTop),
            new Vector2(center.X + halfWidth, bodyBottom),
            col,
            halfWidth * 0.65f,
            ImDrawFlags.RoundCornersTop);
    }

    public static void DrawChatIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, uint dotCol)
    {
        var halfW = size * 0.44f;
        var halfH = size * 0.32f;
        var topLeft = center + new Vector2(-halfW, -halfH - size * 0.05f);
        var bottomRight = center + new Vector2(halfW, halfH - size * 0.05f);
        draw.AddRectFilled(topLeft, bottomRight, col, size * 0.14f);
        draw.AddTriangleFilled(
            new Vector2(center.X - halfW * 0.55f, bottomRight.Y - 1f),
            new Vector2(center.X - halfW * 0.10f, bottomRight.Y - 1f),
            new Vector2(center.X - halfW * 0.58f, bottomRight.Y + size * 0.20f),
            col);

        var dotR = MathF.Max(1.4f, size * 0.065f);
        var dotY = (topLeft.Y + bottomRight.Y) * 0.5f;
        var spacing = size * 0.20f;
        draw.AddCircleFilled(new Vector2(center.X - spacing, dotY), dotR, dotCol, 12);
        draw.AddCircleFilled(new Vector2(center.X, dotY), dotR, dotCol, 12);
        draw.AddCircleFilled(new Vector2(center.X + spacing, dotY), dotR, dotCol, 12);
    }

    public static void DrawSearchIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, float thickness)
    {
        var r = size * 0.30f;
        var circleCenter = center + new Vector2(-size * 0.08f, -size * 0.08f);
        draw.AddCircle(circleCenter, r, col, 20, thickness);
        var dir = Vector2.Normalize(new Vector2(1f, 1f));
        draw.AddLine(circleCenter + dir * (r * 0.92f), center + new Vector2(size * 0.38f, size * 0.38f), col, thickness);
    }

    public static void DrawCloseIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, float thickness)
    {
        var half = size * 0.32f;
        draw.AddLine(center + new Vector2(-half, -half), center + new Vector2(half, half), col, thickness);
        draw.AddLine(center + new Vector2(half, -half), center + new Vector2(-half, half), col, thickness);
    }

    public static void DrawSettingsIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, float scale)
    {
        var ringRadius = size * 0.25f;
        var ringThickness = MathF.Max(1.8f, size * 0.13f);
        var toothInner = ringRadius + ringThickness * 0.30f;
        var toothOuter = size * 0.44f;
        var toothWidth = MathF.Max(2.2f, size * 0.16f);

        for (var i = 0; i < 6; i++)
        {
            var angle = i * (MathF.PI / 3f);
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(center + dir * toothInner, center + dir * toothOuter, col, toothWidth);
        }

        draw.AddCircle(center, ringRadius, col, 24, ringThickness);
    }

    public static void DrawBackIcon(ImDrawListPtr draw, Vector2 center, float size, uint col, float thickness)
    {
        var dx = size * 0.18f;
        var dy = size * 0.30f;
        draw.AddLine(center + new Vector2(dx, -dy), center + new Vector2(-dx, 0), col, thickness);
        draw.AddLine(center + new Vector2(-dx, 0), center + new Vector2(dx, dy), col, thickness);
    }

    public static void DrawSendIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var halfW = size * 0.42f;
        var halfH = size * 0.36f;
        var tip = center + new Vector2(halfW, 0);
        var topLeft = center + new Vector2(-halfW, -halfH);
        var bottomLeft = center + new Vector2(-halfW, halfH);
        var innerLeft = center + new Vector2(-halfW * 0.55f, 0);
        draw.AddTriangleFilled(topLeft, tip, innerLeft, col);
        draw.AddTriangleFilled(bottomLeft, innerLeft, tip, col);
    }

    public static void DrawCalendarIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        var halfW = size * 0.40f;
        var topY = center.Y - size * 0.34f;
        var bottomY = center.Y + size * 0.42f;
        var leftX = center.X - halfW;
        var rightX = center.X + halfW;
        var headerH = size * 0.24f;
        var thickness = MathF.Max(1.2f, size * 0.11f);
        var rounding = size * 0.12f;

        draw.AddRect(new Vector2(leftX, topY), new Vector2(rightX, bottomY), col, rounding, ImDrawFlags.None, thickness);
        draw.AddRectFilled(new Vector2(leftX, topY), new Vector2(rightX, topY + headerH), col, rounding, ImDrawFlags.RoundCornersTop);

        var ringOffsetX = halfW * 0.46f;
        var ringTop = topY - size * 0.12f;
        var ringBottom = topY + size * 0.08f;
        draw.AddLine(new Vector2(center.X - ringOffsetX, ringTop), new Vector2(center.X - ringOffsetX, ringBottom), col, thickness);
        draw.AddLine(new Vector2(center.X + ringOffsetX, ringTop), new Vector2(center.X + ringOffsetX, ringBottom), col, thickness);

        var dotSize = MathF.Max(2f, size * 0.16f);
        var dotCenter = new Vector2(center.X, (topY + headerH + bottomY) * 0.5f);
        draw.AddRectFilled(dotCenter - new Vector2(dotSize * 0.5f), dotCenter + new Vector2(dotSize * 0.5f), col, dotSize * 0.2f);
    }

    public static void DrawAiSparkleIcon(ImDrawListPtr draw, Vector2 center, float size, uint col)
    {
        DrawFourPointStar(draw, center + new Vector2(-size * 0.10f, size * 0.08f), size * 0.36f, col);
        DrawFourPointStar(draw, center + new Vector2(size * 0.28f, -size * 0.26f), size * 0.16f, col);
    }

    private static void DrawFourPointStar(ImDrawListPtr draw, Vector2 center, float outerRadius, uint col)
    {
        var innerRadius = outerRadius * 0.28f;
        var top = center + new Vector2(0, -outerRadius);
        var right = center + new Vector2(outerRadius, 0);
        var bottom = center + new Vector2(0, outerRadius);
        var left = center + new Vector2(-outerRadius, 0);
        var ne = center + new Vector2(innerRadius, -innerRadius);
        var se = center + new Vector2(innerRadius, innerRadius);
        var sw = center + new Vector2(-innerRadius, innerRadius);
        var nw = center + new Vector2(-innerRadius, -innerRadius);

        draw.AddQuadFilled(ne, se, sw, nw, col);
        draw.AddTriangleFilled(top, ne, nw, col);
        draw.AddTriangleFilled(right, se, ne, col);
        draw.AddTriangleFilled(bottom, sw, se, col);
        draw.AddTriangleFilled(left, nw, sw, col);
    }
}
