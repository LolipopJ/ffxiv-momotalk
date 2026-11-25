using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVMomoTalk.Windows;

internal static partial class ChatVisuals
{
    public static void DrawTypingBubble(
        ImDrawListPtr draw,
        Vector2 position,
        Vector2 size,
        float timeSeconds,
        bool showTail,
        float scale)
    {
        var fillU32 = Color(OutgoingBubble);
        var rounding = 8f * scale;
        draw.AddRectFilled(position, position + size, fillU32, rounding);

        if (showTail)
        {
            var tailSize = 6f * scale;
            var tailY = position.Y + MathF.Min(12f * scale, size.Y * 0.42f);
            var right = position.X + size.X;
            draw.AddTriangleFilled(
                new Vector2(right - 1f, tailY - tailSize * 0.55f),
                new Vector2(right + tailSize, tailY),
                new Vector2(right - 1f, tailY + tailSize * 0.55f),
                fillU32);
        }

        const float period = 0.55f;
        var radius = 4.1f * scale;
        var spacing = 13f * scale;
        var center = position + size * 0.5f;
        for (var i = 0; i < 3; i++)
        {
            var offset = i % 2 == 0 ? period * 0.5f : 0f;
            var phase = (timeSeconds + offset) / period % 1f;
            var wave = 0.5f + 0.5f * MathF.Cos(phase * MathF.Tau);
            var dotScale = 0.75f + 0.25f * wave;
            var dotAlpha = 0.20f + 0.80f * wave;
            var dotCenter = new Vector2(center.X + (i - 1) * spacing, center.Y);
            draw.AddCircleFilled(dotCenter, radius * dotScale, Color(WithAlpha(White, dotAlpha)), 18);
        }
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

    public static BannerLayout MeasureSystemBanner(string text, float maxBannerWidth, float scale, bool closable = false)
    {
        var fontSize = ImGui.GetFontSize() * 0.92f;
        var padding = new Vector2(14f, 6f) * scale;
        var closeSlot = closable ? 22f * scale : 0f;
        var maxTextWidth = MathF.Max(40f * scale, maxBannerWidth - padding.X * 2f - closeSlot);
        var textSize = CalcTextSize(text, fontSize, maxTextWidth);
        var width = MathF.Min(maxBannerWidth, MathF.Max(textSize.X + padding.X * 2f + closeSlot, maxBannerWidth * 0.55f));
        var bannerSize = new Vector2(width, textSize.Y + padding.Y * 2f);
        return new BannerLayout(bannerSize.Y + 8f * scale, bannerSize, textSize, fontSize, padding, maxTextWidth, closable);
    }

    public static bool DrawSystemBanner(
        ImDrawListPtr draw,
        Vector2 origin,
        float availWidth,
        string text,
        bool isAlert,
        float scale,
        BannerLayout layout,
        float alpha = 1f,
        Vector4? bgOverride = null,
        Vector4? textOverride = null,
        string? closeButtonId = null)
    {
        var bannerPos = new Vector2(origin.X + (availWidth - layout.BannerSize.X) * 0.5f, origin.Y + 3f * scale);
        draw.AddRectFilled(bannerPos, bannerPos + layout.BannerSize,
            Color(ScaleAlpha(bgOverride ?? SystemBg, alpha)), 10f * scale);
        var closeSlot = layout.Closable ? 22f * scale : 0f;
        var textAreaWidth = layout.BannerSize.X - closeSlot;
        var textX = layout.TextSize.X < layout.MaxTextWidth
            ? bannerPos.X + (textAreaWidth - layout.TextSize.X) * 0.5f
            : bannerPos.X + layout.Padding.X;
        var textPos = new Vector2(textX, bannerPos.Y + layout.Padding.Y);
        var fg = textOverride ?? (isAlert ? AlertText : SystemText);
        AddText(draw, textPos, layout.FontSize, Color(ScaleAlpha(fg, alpha)), text, layout.MaxTextWidth);

        if (!layout.Closable || string.IsNullOrEmpty(closeButtonId))
            return false;

        var btnSize = 18f * scale;
        var btnPos = new Vector2(
            bannerPos.X + layout.BannerSize.X - layout.Padding.X * 0.6f - btnSize,
            bannerPos.Y + (layout.BannerSize.Y - btnSize) * 0.5f);
        var cursorBackup = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(btnPos);
        var clicked = ImGui.InvisibleButton(closeButtonId, new Vector2(btnSize));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetCursorScreenPos(cursorBackup);

        if (hovered || active)
        {
            var hoverCol = WithAlpha(fg, active ? 0.20f : 0.12f);
            draw.AddRectFilled(btnPos, btnPos + new Vector2(btnSize), Color(ScaleAlpha(hoverCol, alpha)), 5f * scale);
        }

        DrawCloseIcon(
            draw,
            btnPos + new Vector2(btnSize * 0.5f),
            10f * scale,
            Color(ScaleAlpha(fg, hovered || active ? 1f : 0.72f)),
            MathF.Max(1.3f, 1.5f * scale));
        return clicked;
    }

    public readonly record struct TimeDividerLayout(float Height, Vector2 PillSize, Vector2 TextSize, float FontSize);

    public readonly record struct BannerLayout(
        float Height,
        Vector2 BannerSize,
        Vector2 TextSize,
        float FontSize,
        Vector2 Padding,
        float MaxTextWidth,
        bool Closable = false);

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
            return local.ToString("t", Loc.Culture);

        return local.Year == today.Year
            ? $"{local.ToString("M", Loc.Culture)} {local.ToString("t", Loc.Culture)}"
            : local.ToString("g", Loc.Culture);
    }

    public static string FormatFullTime(long sentAt) =>
        DateTimeOffset.FromUnixTimeMilliseconds(sentAt).ToLocalTime().ToString("G", Loc.Culture);
}
