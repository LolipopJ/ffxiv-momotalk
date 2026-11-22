using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal static class ChatVisuals
{
    public const long TimeDividerGapMs = 5L * 60 * 1000;

    // Default MomoTalk palette extracted from u1805/momotalk
    public static readonly Vector4 HeaderPink = Hex(0xFC96AB);
    public static readonly Vector4 HeaderPinkHover = Hex(0xF27D96);
    public static readonly Vector4 HeaderPinkActive = Hex(0xE26A84);
    public static readonly Vector4 CoverPink = Hex(0xFB94A7);
    public static readonly Vector4 ModalBackdrop = Hex(0x000000, 0.50f);
    public static readonly Vector4 ModalDivider = Hex(0xE7EBEC);
    public static readonly Vector4 SwitchOffBg = Hex(0xE7EBEC);
    public static readonly Vector4 RailBg = Hex(0x4B5A6F);
    public static readonly Vector4 RailActive = Hex(0x68788F);
    public static readonly Vector4 ListBg = Hex(0xF3F7F8);
    public static readonly Vector4 ListHover = Hex(0xE1E7EC);
    public static readonly Vector4 Divider = Hex(0xE1E7EC);
    public static readonly Vector4 InputBorder = Hex(0xDCE5EC);
    public static readonly Vector4 InputFocus = Hex(0xFFB342);
    public static readonly Vector4 White = Hex(0xFFFFFF);
    public static readonly Vector4 Ink = Hex(0x2A323E);
    public static readonly Vector4 Muted = Hex(0x87929E);
    public static readonly Vector4 Placeholder = Hex(0x9E9EA7);
    public static readonly Vector4 IncomingBubble = Hex(0x4B5A6F);
    public static readonly Vector4 OutgoingBubble = Hex(0x4A8AC6);
    public static readonly Vector4 BubbleText = Hex(0xECF2FB);
    public static readonly Vector4 SystemBg = Hex(0xE1E7EC);
    public static readonly Vector4 SystemText = Hex(0x5D6B7A);
    public static readonly Vector4 AlertText = Hex(0xA6324C);
    public static readonly Vector4 SendBarBg = Hex(0xEEEEEE);
    public static readonly Vector4 SendIconIdle = Hex(0xBDBDBD);
    public static readonly Vector4 BadgeText = Hex(0x5D7C8C);
    public static readonly Vector4 BadgeBorder = Hex(0xC2CFD6);
    public static readonly Vector4 AvatarFallbackBg = Hex(0xD5DEE6);
    public static readonly Vector4 AvatarFallbackFg = Hex(0x7B8998);
    public static readonly Vector4 Shadow = Hex(0x2D2342, 0.16f);
    public static readonly Vector4 WindowBorder = Hex(0x2D2342, 0.20f);

    public static Vector4 Hex(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static Vector4 WithAlpha(Vector4 rgba, float alpha) => new(rgba.X, rgba.Y, rgba.Z, alpha);

    public static Vector4 ScaleAlpha(Vector4 rgba, float factor) => new(rgba.X, rgba.Y, rgba.Z, rgba.W * factor);

    public static uint Color(Vector4 rgba) => ImGui.ColorConvertFloat4ToU32(rgba);

    public static Vector2 CalcTextSize(string text, float fontSize, float wrapWidth = 0f)
    {
        if (string.IsNullOrEmpty(text))
            return new Vector2(0, fontSize);

        return ImGui.CalcTextSizeA(ImGui.GetFont(), fontSize, float.MaxValue, wrapWidth, text, out _);
    }

    public static void AddText(ImDrawListPtr draw, Vector2 pos, float fontSize, uint col, string text, float wrapWidth = 0f) =>
        draw.AddText(ImGui.GetFont(), fontSize, pos, col, text, wrapWidth);

    public static void AddBoldText(ImDrawListPtr draw, Vector2 pos, float fontSize, uint col, string text, float scale)
    {
        var font = ImGui.GetFont();
        var offset = MathF.Max(0.55f, 0.6f * scale);
        draw.AddText(font, fontSize, pos, col, text);
        draw.AddText(font, fontSize, pos + new Vector2(offset, 0), col, text);
    }

    public static string TruncateSingleLine(string text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return string.Empty;

        var singleLine = NormalizeSingleLine(text);
        if (singleLine.Length == 0)
            return string.Empty;

        if (CalcTextSize(singleLine, fontSize).X <= maxWidth)
            return singleLine;

        const string ellipsis = "...";
        var ellipsisWidth = CalcTextSize(ellipsis, fontSize).X;
        if (ellipsisWidth >= maxWidth)
            return ellipsis;

        var low = 0;
        var high = singleLine.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = singleLine[..mid];
            if (CalcTextSize(candidate, fontSize).X + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low == 0 ? ellipsis : singleLine[..low].TrimEnd() + ellipsis;
    }

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

    public static bool DrawToggleSwitch(ImDrawListPtr draw, string id, Vector2 pos, bool value, ref float anim, float scale,
        float alpha = 1f, bool interactive = true)
    {
        var width = 44f * scale;
        var height = 24f * scale;
        var size = new Vector2(width, height);

        var clicked = false;
        var hovered = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(pos);
            clicked = ImGui.InvisibleButton(id, size);
            hovered = ImGui.IsItemHovered();
        }

        var target = value ? 1f : 0f;
        var step = MathF.Min(1f, MathF.Max(0.001f, ImGui.GetIO().DeltaTime) * 12f);
        anim = anim < target ? MathF.Min(target, anim + step) : MathF.Max(target, anim - step);

        var trackColor = Vector4.Lerp(SwitchOffBg, HeaderPink, anim);
        if (hovered)
            trackColor = Vector4.Lerp(trackColor, value ? HeaderPinkHover : Divider, 0.25f);

        draw.AddRectFilled(pos, pos + size, Color(ScaleAlpha(trackColor, alpha)), height * 0.5f);

        var thumbRadius = 9f * scale;
        var inset = 3f * scale + thumbRadius;
        var thumbX = pos.X + inset + (width - inset * 2f) * anim;
        var thumbCenter = new Vector2(thumbX, pos.Y + height * 0.5f);
        draw.AddCircleFilled(thumbCenter + new Vector2(0, 1.5f * scale), thumbRadius,
            Color(ScaleAlpha(Hex(0x000000, 0.18f), alpha)), 24);
        draw.AddCircleFilled(thumbCenter, thumbRadius, Color(ScaleAlpha(White, alpha)), 24);
        return clicked;
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

    public static bool DrawProfileChatButton(ImDrawListPtr draw, string id, Vector2 center, float radius, float scale)
    {
        var topLeft = center - new Vector2(radius);
        ImGui.SetCursorScreenPos(topLeft);
        var clicked = ImGui.InvisibleButton(id, new Vector2(radius * 2f));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        draw.AddCircleFilled(center + new Vector2(0, 2f * scale), radius, Color(Shadow), 32);
        var fill = active ? RailBg : hovered ? RailActive : RailBg;
        draw.AddCircleFilled(center, radius, Color(fill), 32);
        DrawChatIcon(draw, center + new Vector2(0, 0.5f * scale), radius * 1.15f, Color(White), Color(fill));
        return clicked;
    }

    public static bool SkewButton(ImDrawListPtr draw, string id, string label, Vector2 position, Vector2 size, float scale,
        bool cornerIndicator = true)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        // Parallelogram matching MomoTalk CSS transform: skew(-15deg)
        var skew = size.Y * 0.24f;
        var p1 = position + new Vector2(skew, 0);
        var p2 = position + new Vector2(size.X, 0);
        var p3 = position + new Vector2(size.X - skew, size.Y);
        var p4 = position + new Vector2(0, size.Y);

        var shadowOffset = new Vector2(0, 2f * scale);
        draw.AddQuadFilled(p1 + shadowOffset, p2 + shadowOffset, p3 + shadowOffset, p4 + shadowOffset, Color(Shadow));

        var fill = active ? Divider : hovered ? ListHover : White;
        draw.AddQuadFilled(p1, p2, p3, p4, Color(fill));
        draw.AddQuad(p1, p2, p3, p4, Color(InputBorder), MathF.Max(1f, scale));

        if (cornerIndicator)
        {
            var tri = 7f * scale;
            draw.AddTriangleFilled(
                p2 + new Vector2(-tri, 0),
                p2,
                p2 + new Vector2(-tri * 0.24f, tri),
                Color(RailBg));
        }

        var fontSize = ImGui.GetFontSize() * 0.94f;
        var textSize = CalcTextSize(label, fontSize);
        var textPos = position + (size - textSize) * 0.5f;
        AddBoldText(draw, textPos, fontSize, Color(Ink), label, scale);
        return clicked;
    }

    public static void DrawBubble(
        ImDrawListPtr draw,
        Vector2 position,
        Vector2 size,
        Vector4 fill,
        string body,
        float fontSize,
        Vector2 padding,
        float maxTextWidth,
        bool outgoing,
        bool showTail,
        float scale)
    {
        var fillU32 = Color(fill);
        var rounding = 8f * scale;
        draw.AddRectFilled(position, position + size, fillU32, rounding);

        if (showTail)
        {
            var tailSize = 6f * scale;
            var tailY = position.Y + MathF.Min(12f * scale, size.Y * 0.42f);
            if (outgoing)
            {
                var right = position.X + size.X;
                draw.AddTriangleFilled(
                    new Vector2(right - 1f, tailY - tailSize * 0.55f),
                    new Vector2(right + tailSize, tailY),
                    new Vector2(right - 1f, tailY + tailSize * 0.55f),
                    fillU32);
            }
            else
            {
                var left = position.X;
                draw.AddTriangleFilled(
                    new Vector2(left + 1f, tailY - tailSize * 0.55f),
                    new Vector2(left - tailSize, tailY),
                    new Vector2(left + 1f, tailY + tailSize * 0.55f),
                    fillU32);
            }
        }

        AddText(draw, position + padding, fontSize, Color(BubbleText), body, maxTextWidth);
    }

    public static TimeDividerLayout MeasureTimeDivider(string label, float scale)
    {
        var fontSize = ImGui.GetFontSize() * 0.85f;
        var textSize = CalcTextSize(label, fontSize);
        var pad = new Vector2(12f, 3.5f) * scale;
        var pillSize = textSize + pad * 2f;
        return new TimeDividerLayout(pillSize.Y + 12f * scale, pillSize, textSize, fontSize);
    }

    public static void DrawTimeDivider(ImDrawListPtr draw, Vector2 origin, float availWidth, string label, float scale,
        TimeDividerLayout layout)
    {
        var pillPos = new Vector2(origin.X + (availWidth - layout.PillSize.X) * 0.5f, origin.Y + 6f * scale);
        draw.AddRectFilled(pillPos, pillPos + layout.PillSize, Color(SystemBg), layout.PillSize.Y * 0.5f);
        var textPos = pillPos + (layout.PillSize - layout.TextSize) * 0.5f;
        AddText(draw, textPos, layout.FontSize, Color(SystemText), label);
    }

    public static BannerLayout MeasureSystemBanner(string text, float maxBannerWidth, float scale)
    {
        var fontSize = ImGui.GetFontSize() * 0.92f;
        var padding = new Vector2(14f, 6f) * scale;
        var maxTextWidth = MathF.Max(40f * scale, maxBannerWidth - padding.X * 2f);
        var textSize = CalcTextSize(text, fontSize, maxTextWidth);
        var width = MathF.Min(maxBannerWidth, MathF.Max(textSize.X + padding.X * 2f, maxBannerWidth * 0.55f));
        var bannerSize = new Vector2(width, textSize.Y + padding.Y * 2f);
        return new BannerLayout(bannerSize.Y + 8f * scale, bannerSize, textSize, fontSize, padding, maxTextWidth);
    }

    public static void DrawSystemBanner(
        ImDrawListPtr draw,
        Vector2 origin,
        float availWidth,
        string text,
        bool isAlert,
        float scale,
        BannerLayout layout,
        float alpha = 1f)
    {
        var bannerPos = new Vector2(origin.X + (availWidth - layout.BannerSize.X) * 0.5f, origin.Y + 3f * scale);
        draw.AddRectFilled(bannerPos, bannerPos + layout.BannerSize, Color(ScaleAlpha(SystemBg, alpha)), 10f * scale);
        var textX = layout.TextSize.X < layout.MaxTextWidth
            ? bannerPos.X + (layout.BannerSize.X - layout.TextSize.X) * 0.5f
            : bannerPos.X + layout.Padding.X;
        var textPos = new Vector2(textX, bannerPos.Y + layout.Padding.Y);
        AddText(draw, textPos, layout.FontSize, Color(ScaleAlpha(isAlert ? AlertText : SystemText, alpha)), text,
            layout.MaxTextWidth);
    }

    public readonly record struct TimeDividerLayout(float Height, Vector2 PillSize, Vector2 TextSize, float FontSize);

    public readonly record struct BannerLayout(
        float Height,
        Vector2 BannerSize,
        Vector2 TextSize,
        float FontSize,
        Vector2 Padding,
        float MaxTextWidth);

    public static bool ShouldInsertTimeDivider(long? previousSentAt, long currentSentAt)
    {
        if (previousSentAt is not { } previous)
            return true;

        if (currentSentAt - previous >= TimeDividerGapMs)
            return true;

        var prevDate = DateTimeOffset.FromUnixTimeMilliseconds(previous).ToLocalTime().Date;
        var currDate = DateTimeOffset.FromUnixTimeMilliseconds(currentSentAt).ToLocalTime().Date;
        return prevDate != currDate;
    }

    public static string FormatTimeDivider(long sentAt)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(sentAt).ToLocalTime();
        var today = DateTimeOffset.Now.ToLocalTime().Date;
        if (local.Date == today)
            return local.ToString("HH:mm");

        return local.Year == today.Year
            ? local.ToString("MM-dd HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
    }

    public static string FormatFullTime(long sentAt) =>
        DateTimeOffset.FromUnixTimeMilliseconds(sentAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string NormalizeSingleLine(string text)
    {
        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        var lastWasSpace = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    buffer[length++] = ' ';
                    lastWasSpace = true;
                }
            }
            else
            {
                buffer[length++] = ch;
                lastWasSpace = false;
            }
        }

        while (length > 0 && buffer[length - 1] == ' ')
            length--;

        return new string(buffer[..length]);
    }
}
