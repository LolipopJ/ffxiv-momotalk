using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal static partial class ChatVisuals
{
    public static void DrawAiExampleCard(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale, float alpha)
    {
        var rounding = 7f * scale;
        draw.AddRectFilled(pos, pos + size, Color(ScaleAlpha(ListBg, alpha)), rounding);
        draw.AddRect(pos, pos + size, Color(ScaleAlpha(InputBorder, alpha)), rounding);
    }

    public static AiExamplePreview MeasureAiExamplePreview(IAiReplyExample example, float width,
        float labelHeight, float fontSize, float scale)
    {
        var padding = 8f * scale;
        var gap = 4f * scale;
        var wrapWidth = width - 2f * padding;
        var rows = new (string Text, float Height)[1 + example.Replies.Count];
        rows[0] = (example.Context, CalcTextSize(example.Context, fontSize, wrapWidth).Y);
        for (var i = 0; i < example.Replies.Count; i++)
        {
            var text = $"{i + 1}. {example.Replies[i]}";
            rows[i + 1] = (text, CalcTextSize(text, fontSize, wrapWidth).Y);
        }

        var height = 2f * padding + labelHeight + rows.Sum(row => row.Height) + (rows.Length - 1) * gap;
        return new AiExamplePreview(height, padding, gap, wrapWidth, rows);
    }

    public static void DrawAiExamplePreview(ImDrawListPtr draw, Vector2 pos, float width, string title,
        float labelHeight, float fontSize, float scale, float alpha, AiExamplePreview preview)
    {
        DrawAiExampleCard(draw, pos, new Vector2(width, preview.Height), scale, alpha);
        var textPos = pos + new Vector2(preview.Padding);
        var ink = Color(ScaleAlpha(Ink, alpha));
        AddBoldText(draw, textPos, fontSize, ink, title, scale);
        textPos.Y += labelHeight;
        var secondary = Color(ScaleAlpha(SystemText, alpha));
        for (var i = 0; i < preview.Rows.Length; i++)
        {
            var (text, height) = preview.Rows[i];
            AddText(draw, textPos, fontSize, i == 0 ? ink : secondary, text, preview.WrapWidth);
            textPos.Y += height + preview.Gap;
        }
    }

    public readonly record struct AiExamplePreview(
        float Height, float Padding, float Gap, float WrapWidth, (string Text, float Height)[] Rows);

    public static int DrawReplyCard(
        ImDrawListPtr draw,
        Vector2 origin,
        float availWidth,
        float maxCardWidth,
        IReadOnlyList<AiReplyOption> options,
        bool enabled,
        float scale,
        out float totalHeight)
    {
        var m = ReplyCardMetrics.Create(maxCardWidth, scale);
        var buttonGap = 7f * scale;
        var buttonWidth = MathF.Max(40f * scale, m.CardWidth - m.PadX * 2f);
        var buttonPadX = 12f * scale;
        var buttonPadY = 7f * scale;
        var optionFontSize = ImGui.GetFontSize() * 0.95f;
        var textWrapWidth = MathF.Max(20f * scale, buttonWidth - buttonPadX * 2f);

        Span<float> buttonHeights = options.Count <= 8 ? stackalloc float[options.Count] : new float[options.Count];
        Span<Vector2> textSizes = options.Count <= 8 ? stackalloc Vector2[options.Count] : new Vector2[options.Count];
        var buttonsHeight = 0f;
        for (var i = 0; i < options.Count; i++)
        {
            var textSize = CalcTextSize(options[i].Text, optionFontSize, textWrapWidth);
            var buttonHeight = MathF.Max(34f * scale, textSize.Y + buttonPadY * 2f);
            textSizes[i] = textSize;
            buttonHeights[i] = buttonHeight;
            buttonsHeight += buttonHeight;
            if (i > 0)
                buttonsHeight += buttonGap;
        }

        var cardHeight = m.PadTop + m.HeaderRowH + m.HeaderToDividerGap + 1f + m.DividerToBodyGap + buttonsHeight + m.PadBottom;
        totalHeight = cardHeight + 14f * scale;

        var cardPos = new Vector2(origin.X + (availWidth - m.CardWidth) * 0.5f, origin.Y + 8f * scale);
        var dividerY = DrawReplyCardFrame(draw, cardPos, new Vector2(m.CardWidth, cardHeight), m, scale);

        var clickedIndex = -1;
        var cursorY = dividerY + 1f + m.DividerToBodyGap;
        var buttonX = cardPos.X + m.PadX;
        var btnRounding = 7f * scale;
        var borderThickness = MathF.Max(1f, scale);

        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var btnPos = new Vector2(buttonX, cursorY);
            var btnSize = new Vector2(buttonWidth, buttonHeights[i]);

            ImGui.SetCursorScreenPos(btnPos);
            bool clicked;
            var hovered = false;
            var hoveredAny = false;
            var active = false;
            using (ImRaii.Disabled(!enabled))
            {
                clicked = ImGui.InvisibleButton($"##ai-reply-opt-{i}", btnSize);
                hoveredAny = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                if (enabled)
                {
                    hovered = hoveredAny;
                    active = ImGui.IsItemActive();
                }
            }

            if (clicked && enabled)
                clickedIndex = i;

            var shadowOffset = new Vector2(0, 1.5f * scale);
            draw.AddRectFilled(btnPos + shadowOffset, btnPos + btnSize + shadowOffset, Color(ReplyOptionShadow), btnRounding);

            var fill = !enabled
                ? White
                : active
                    ? ReplyOptionActive
                    : hovered
                        ? ReplyOptionHover
                        : White;
            draw.AddRectFilled(btnPos, btnPos + btnSize, Color(fill), btnRounding);

            var borderCol = enabled && hovered ? ReplyCardAccent : ReplyOptionBorder;
            draw.AddRect(btnPos, btnPos + btnSize, Color(borderCol), btnRounding, ImDrawFlags.None, borderThickness);

            var textSize = textSizes[i];
            var textX = btnPos.X + MathF.Max(buttonPadX, (buttonWidth - textSize.X) * 0.5f);
            var textY = btnPos.Y + (btnSize.Y - textSize.Y) * 0.5f;
            var textCol = Color(enabled ? Ink : Muted);
            AddText(draw, new Vector2(textX, textY), optionFontSize, textCol, option.Text, textWrapWidth);

            if (hoveredAny && option.Translation is { Length: > 0 } translation)
            {
                using var tooltip = ImRaii.Tooltip();
                if (tooltip.Alive)
                {
                    ImGui.PushTextWrapPos(MathF.Max(160f * scale, MathF.Min(maxCardWidth, 360f * scale)));
                    ImGui.TextUnformatted(translation);
                    ImGui.PopTextWrapPos();
                }
            }

            cursorY += btnSize.Y + buttonGap;
        }

        ImGui.SetCursorScreenPos(origin);
        return clickedIndex;
    }

    public static float DrawReplyLoadingCard(
        ImDrawListPtr draw,
        Vector2 origin,
        float availWidth,
        float maxCardWidth,
        float timeSeconds,
        float scale)
    {
        var m = ReplyCardMetrics.Create(maxCardWidth, scale);
        var bodyHeight = 36f * scale;
        var cardHeight = m.PadTop + m.HeaderRowH + m.HeaderToDividerGap + 1f + m.DividerToBodyGap + bodyHeight + m.PadBottom;
        var totalHeight = cardHeight + 14f * scale;
        if (!ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
            return totalHeight;

        var cardPos = new Vector2(origin.X + (availWidth - m.CardWidth) * 0.5f, origin.Y + 8f * scale);
        var dividerY = DrawReplyCardFrame(draw, cardPos, new Vector2(m.CardWidth, cardHeight), m, scale);

        var pillPos = new Vector2(cardPos.X + m.PadX, dividerY + 1f + m.DividerToBodyGap);
        var pillSize = new Vector2(MathF.Max(40f * scale, m.CardWidth - m.PadX * 2f), bodyHeight);
        var rounding = 7f * scale;
        var shadowOffset = new Vector2(0, 1.5f * scale);
        draw.AddRectFilled(pillPos + shadowOffset, pillPos + pillSize + shadowOffset, Color(ReplyOptionShadow), rounding);
        draw.AddRectFilled(pillPos, pillPos + pillSize, Color(White), rounding);
        draw.AddRect(pillPos, pillPos + pillSize, Color(ReplyOptionBorder), rounding, ImDrawFlags.None, MathF.Max(1f, scale));

        var loadingLabel = Loc.Text("GeneratingReplies");
        var labelFontSize = ImGui.GetFontSize() * 0.92f;
        var labelSize = CalcTextSize(loadingLabel, labelFontSize);
        var dotRadius = 3.1f * scale;
        var dotSpacing = 9f * scale;
        var dotsWidth = dotSpacing * 2f + dotRadius * 2f;
        var gap = 9f * scale;
        var contentWidth = dotsWidth + gap + labelSize.X;
        var startX = pillPos.X + MathF.Max(10f * scale, (pillSize.X - contentWidth) * 0.5f);
        var centerY = pillPos.Y + pillSize.Y * 0.5f;

        const float period = 0.65f;
        for (var i = 0; i < 3; i++)
        {
            var phase = (timeSeconds + i * (period / 3f)) / period % 1f;
            var wave = 0.5f + 0.5f * MathF.Cos(phase * MathF.Tau);
            var dotAlpha = 0.28f + 0.72f * wave;
            var dotScale = 0.78f + 0.22f * wave;
            var dotCenter = new Vector2(startX + dotRadius + i * dotSpacing, centerY);
            draw.AddCircleFilled(dotCenter, dotRadius * dotScale, Color(WithAlpha(ReplyCardAccent, dotAlpha)), 16);
        }

        var textPos = new Vector2(startX + dotsWidth + gap, centerY - labelSize.Y * 0.5f);
        AddText(draw, textPos, labelFontSize, Color(Muted), loadingLabel);
        return totalHeight;
    }

    private static float DrawReplyCardFrame(
        ImDrawListPtr draw,
        Vector2 cardPos,
        Vector2 cardSize,
        ReplyCardMetrics m,
        float scale)
    {
        var cardMax = cardPos + cardSize;
        var rounding = 8f * scale;
        draw.AddRectFilled(cardPos, cardMax, Color(ReplyCardBg), rounding);

        draw.PushClipRect(cardPos + new Vector2(1f), cardMax - new Vector2(1f), true);
        var maxX = cardMax.X - 1f;
        var minY = cardPos.Y + 1f;
        var geoW = MathF.Min(cardSize.X * 0.46f, 190f * scale);
        var geoH = MathF.Min(cardSize.Y * 0.62f, 84f * scale);

        var pTopLeft = new Vector2(maxX - geoW, minY);
        var pTopMid = new Vector2(maxX - geoW * 0.48f, minY);
        var pCornerTop = new Vector2(maxX - rounding, minY);
        var pCornerRight = new Vector2(maxX, minY + rounding);
        var pRightMid = new Vector2(maxX, minY + geoH * 0.62f);
        var pRightBot = new Vector2(maxX, minY + geoH);
        var pInner1 = new Vector2(maxX - geoW * 0.62f, minY + geoH * 0.42f);
        var pInner2 = new Vector2(maxX - geoW * 0.28f, minY + geoH * 0.56f);
        var pInner3 = new Vector2(maxX - geoW * 0.36f, minY + geoH * 0.92f);

        draw.AddTriangleFilled(pTopLeft, pTopMid, pInner1, Color(ReplyCardGeoLight));
        draw.AddTriangleFilled(pTopMid, pCornerTop, pInner2, Color(ReplyCardGeoMid));
        draw.AddTriangleFilled(pTopMid, pInner2, pInner1, Color(ReplyCardGeoLight));
        draw.AddTriangleFilled(pCornerTop, pCornerRight, pInner2, Color(ReplyCardGeoDeep));
        draw.AddTriangleFilled(pCornerRight, pRightMid, pInner2, Color(ReplyCardGeoMid));
        draw.AddTriangleFilled(pInner2, pRightMid, pInner3, Color(ReplyCardGeoLight));
        draw.AddTriangleFilled(pRightMid, pRightBot, pInner3, Color(ReplyCardGeoLight));
        draw.PopClipRect();

        draw.AddRect(cardPos, cardMax, Color(ReplyCardBorder), rounding, ImDrawFlags.None, MathF.Max(1f, scale));

        var barWidth = MathF.Max(2.5f, 3.2f * scale);
        var barHeight = MathF.Max(14f * scale, m.TitleFontSize * 0.95f);
        var headerY = cardPos.Y + m.PadTop;
        var barPos = new Vector2(cardPos.X + m.PadX, headerY + (m.HeaderRowH - barHeight) * 0.5f);
        draw.AddRectFilled(barPos, barPos + new Vector2(barWidth, barHeight), Color(ReplyCardAccent), barWidth * 0.5f);

        var titleSize = CalcTextSize(Loc.Text("Reply"), m.TitleFontSize);
        var titlePos = new Vector2(barPos.X + barWidth + 7f * scale, headerY + (m.HeaderRowH - titleSize.Y) * 0.5f);
        AddBoldText(draw, titlePos, m.TitleFontSize, Color(Ink), Loc.Text("Reply"), scale);

        var dividerY = headerY + m.HeaderRowH + m.HeaderToDividerGap;
        draw.AddLine(
            new Vector2(cardPos.X + m.PadX, dividerY),
            new Vector2(cardMax.X - m.PadX, dividerY),
            Color(ReplyCardDivider),
            MathF.Max(1f, scale));
        return dividerY;
    }

    private readonly record struct ReplyCardMetrics(
        float CardWidth,
        float PadX,
        float PadTop,
        float PadBottom,
        float TitleFontSize,
        float HeaderRowH,
        float HeaderToDividerGap,
        float DividerToBodyGap)
    {
        public static ReplyCardMetrics Create(float maxCardWidth, float scale)
        {
            var titleFontSize = ImGui.GetFontSize() * 0.98f;
            return new ReplyCardMetrics(
                MathF.Min(460f * scale, MathF.Max(120f * scale, maxCardWidth)),
                14f * scale,
                10f * scale,
                12f * scale,
                titleFontSize,
                MathF.Max(18f * scale, titleFontSize),
                7f * scale,
                10f * scale);
        }
    }
}
