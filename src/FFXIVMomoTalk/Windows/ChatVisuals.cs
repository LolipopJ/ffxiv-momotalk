using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
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
    public static readonly Vector4 ModalButtonBg = Hex(0xE6ECF1);
    public static readonly Vector4 ModalButtonHover = Hex(0xD5E0E9);
    public static readonly Vector4 ModalButtonActive = Hex(0xC0D0DD);
    public static readonly Vector4 ModalButtonBorder = Hex(0xCED8E0);
    public static readonly Vector4 ModalButtonHoverBorder = Hex(0x9BB1C4);
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
    public static readonly Vector4 DangerHoverBg = Hex(0xFDECEF);
    public static readonly Vector4 SendBarBg = Hex(0xEEEEEE);
    public static readonly Vector4 SendIconIdle = Hex(0xBDBDBD);
    public static readonly Vector4 BadgeText = Hex(0x5D7C8C);
    public static readonly Vector4 BadgeBorder = Hex(0xC2CFD6);
    public static readonly Vector4 AvatarFallbackBg = Hex(0xD5DEE6);
    public static readonly Vector4 AvatarFallbackFg = Hex(0x7B8998);
    public static readonly Vector4 GroupFcBg = Hex(0xE09258);
    public static readonly Vector4 GroupCwlsBg = Hex(0x4AA3A8);
    public static readonly Vector4 GroupLsBg = Hex(0x5EA47C);
    public static readonly Vector4 GroupStaticBg = Hex(0x4A8AC6);
    public static readonly Vector4 MatchSuccessBg = Hex(0xE4F2EC);
    public static readonly Vector4 MatchSuccessText = Hex(0x2F7B58);
    public static readonly Vector4 ReplyCardBg = Hex(0xEFF5F8);
    public static readonly Vector4 ReplyCardBorder = Hex(0xD9E2E8);
    public static readonly Vector4 ReplyCardAccent = Hex(0x3E95E8);
    public static readonly Vector4 ReplyCardDivider = Hex(0xCBD5E1);
    public static readonly Vector4 ReplyCardGeoLight = Hex(0xDCEEF9, 0.72f);
    public static readonly Vector4 ReplyCardGeoMid = Hex(0xCBE2F0, 0.55f);
    public static readonly Vector4 ReplyCardGeoDeep = Hex(0xB9D8EC, 0.40f);
    public static readonly Vector4 ReplyOptionHover = Hex(0xF4F8FB);
    public static readonly Vector4 ReplyOptionActive = Hex(0xE6F0F8);
    public static readonly Vector4 ReplyOptionBorder = Hex(0xDCE5EC);
    public static readonly Vector4 ReplyOptionShadow = Hex(0x2D3A4B, 0.10f);
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

    public static bool DrawModalButton(
        ImDrawListPtr draw,
        string id,
        string label,
        Vector2 pos,
        Vector2 size,
        bool primary,
        bool enabled,
        float scale,
        float alpha = 1f)
    {
        var clicked = false;
        var hovered = false;
        var active = false;
        if (enabled)
        {
            ImGui.SetCursorScreenPos(pos);
            clicked = ImGui.InvisibleButton(id, size);
            hovered = ImGui.IsItemHovered();
            active = ImGui.IsItemActive();
            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        Vector4 fill;
        Vector4 textCol;
        Vector4? borderCol = null;
        if (primary)
        {
            fill = !enabled
                ? SwitchOffBg
                : active
                    ? HeaderPinkActive
                    : hovered
                        ? HeaderPinkHover
                        : HeaderPink;
            textCol = enabled ? White : Muted;
        }
        else
        {
            fill = !enabled
                ? ListBg
                : active
                    ? ModalButtonActive
                    : hovered
                        ? ModalButtonHover
                        : ModalButtonBg;
            borderCol = !enabled
                ? ModalDivider
                : hovered || active
                    ? ModalButtonHoverBorder
                    : ModalButtonBorder;
            textCol = enabled ? Ink : Muted;
        }

        var rounding = 7f * scale;
        draw.AddRectFilled(pos, pos + size, Color(ScaleAlpha(fill, alpha)), rounding);
        if (borderCol is { } border)
            draw.AddRect(pos, pos + size, Color(ScaleAlpha(border, alpha)), rounding, ImDrawFlags.None, MathF.Max(1f, scale));

        var pressOffsetY = enabled && active ? 1f * scale : 0f;
        var fontSize = ImGui.GetFontSize() * 0.96f;
        var textSize = CalcTextSize(label, fontSize);
        var textPos = pos + (size - textSize) * 0.5f + new Vector2(0, pressOffsetY);
        if (primary)
            AddBoldText(draw, textPos, fontSize, Color(ScaleAlpha(textCol, alpha)), label, scale);
        else
            AddText(draw, textPos, fontSize, Color(ScaleAlpha(textCol, alpha)), label);

        return clicked;
    }

    public static ImRaii.ColorDisposable PushSettingsComboColors() => ImRaii.PushColor(ImGuiCol.Text, Ink)
        .Push(ImGuiCol.FrameBg, ModalButtonBg)
        .Push(ImGuiCol.FrameBgHovered, ModalButtonHover)
        .Push(ImGuiCol.Button, ModalButtonBg)
        .Push(ImGuiCol.ButtonHovered, ModalButtonHover)
        .Push(ImGuiCol.ButtonActive, ModalButtonActive)
        .Push(ImGuiCol.Border, ModalButtonBorder)
        .Push(ImGuiCol.PopupBg, White)
        .Push(ImGuiCol.Header, ModalButtonBg)
        .Push(ImGuiCol.HeaderHovered, ModalButtonHover)
        .Push(ImGuiCol.HeaderActive, ModalButtonActive);

    public static ImRaii.StyleDisposable PushSettingsComboStyle(float height, float scale)
    {
        var paddingY = MathF.Max(0f, (height - ImGui.GetFontSize()) * 0.5f);
        return ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10f * scale, paddingY))
            .Push(ImGuiStyleVar.FrameRounding, 7f * scale)
            .Push(ImGuiStyleVar.FrameBorderSize, MathF.Max(1f, scale))
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(8f * scale))
            .Push(ImGuiStyleVar.PopupRounding, 7f * scale)
            .Push(ImGuiStyleVar.PopupBorderSize, MathF.Max(1f, scale))
            .Push(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
    }

    public static ImRaii.ComboDisposable BeginSettingsCombo(string id, string label, Vector2 pos, float width)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.SetNextItemWidth(width);
        var frameWidth = ImGui.CalcItemWidth();
        ImGui.SetNextWindowSizeConstraints(new Vector2(frameWidth, 0), new Vector2(frameWidth, float.MaxValue));
        var labelWidth = frameWidth - ImGui.GetFrameHeight() - 2f * ImGui.GetStyle().FramePadding.X;
        var preview = TruncateSingleLine(label, ImGui.GetFontSize(), labelWidth);
        var combo = ImRaii.Combo(id, preview);
        if (!combo && ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (preview != label)
                ImGui.SetTooltip(label);
        }

        return combo;
    }

    public static bool SelectSettingsOption(string label, string id, bool selected, float height)
    {
        var visibleLabel = TruncateSingleLine(label, ImGui.GetFontSize(), ImGui.GetContentRegionAvail().X);
        var clicked = ImGui.Selectable($"{visibleLabel}###{id}", selected, ImGuiSelectableFlags.None, new Vector2(0f, height));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (visibleLabel != label)
                ImGui.SetTooltip(label);
        }

        if (selected)
            ImGui.SetItemDefaultFocus();

        return clicked;
    }

    public static float MeasureCompactModalButton(string label, float scale, float maxWidth) =>
        MathF.Min(maxWidth, CalcTextSize(label, ImGui.GetFontSize() * 0.96f).X + 20f * scale);

    public static bool DrawCompactModalButton(ImDrawListPtr draw, string id, string label, Vector2 pos,
        Vector2 size, bool enabled, float scale, float alpha)
    {
        var visibleLabel = TruncateSingleLine(label, ImGui.GetFontSize() * 0.96f, size.X - 12f * scale);
        var clicked = DrawModalButton(draw, id, visibleLabel, pos, size, primary: false, enabled, scale, alpha);
        if (enabled && visibleLabel != label && ImGui.IsItemHovered())
            ImGui.SetTooltip(label);

        return clicked;
    }

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

    public static bool SkewSortOrderButton(ImDrawListPtr draw, string id, bool ascending, Vector2 position, Vector2 size,
        float scale)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

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

        var col = Color(Ink);
        var center = position + size * 0.5f;
        var iconH = 12f * scale;
        var topY = center.Y - iconH * 0.5f;
        var barX = center.X - 8.5f * scale;
        var barW = 8.5f * scale;
        var barH = MathF.Max(1.8f, 2f * scale);

        draw.AddRectFilled(new Vector2(barX, topY), new Vector2(barX + barW, topY + barH), col);
        draw.AddRectFilled(new Vector2(barX, center.Y - barH * 0.5f), new Vector2(barX + barW, center.Y + barH * 0.5f), col);
        draw.AddRectFilled(new Vector2(barX, topY + iconH - barH), new Vector2(barX + barW, topY + iconH), col);

        var arrowX = center.X + 4.8f * scale;
        var shaftHalf = MathF.Max(0.9f, 1.0f * scale);
        var headW = 3.8f * scale;
        var headH = 4.6f * scale;
        if (ascending)
        {
            draw.AddTriangleFilled(
                new Vector2(arrowX, topY),
                new Vector2(arrowX - headW, topY + headH),
                new Vector2(arrowX + headW, topY + headH),
                col);
            draw.AddRectFilled(
                new Vector2(arrowX - shaftHalf, topY + headH - 0.5f),
                new Vector2(arrowX + shaftHalf, topY + iconH),
                col);
        }
        else
        {
            var bottomY = topY + iconH;
            draw.AddRectFilled(
                new Vector2(arrowX - shaftHalf, topY),
                new Vector2(arrowX + shaftHalf, bottomY - headH + 0.5f),
                col);
            draw.AddTriangleFilled(
                new Vector2(arrowX, bottomY),
                new Vector2(arrowX - headW, bottomY - headH),
                new Vector2(arrowX + headW, bottomY - headH),
                col);
        }

        return clicked;
    }

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

    public static ContextMenuScope BeginContextMenu(string id, float scale)
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, White);
        ImGui.PushStyleColor(ImGuiCol.Border, InputBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, MathF.Max(1f, scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 2f * scale));
        return new ContextMenuScope(ImGui.BeginPopup(id));
    }

    public static bool ContextMenuItem(string id, string label, float width, float scale, bool danger = false)
    {
        var height = 30f * scale;
        var fontSize = ImGui.GetFontSize() * 0.96f;
        var textSize = CalcTextSize(label, fontSize);
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(MathF.Max(width, textSize.X + 24f * scale), height);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        if (hovered)
        {
            var bg = danger ? DangerHoverBg : ListHover;
            draw.AddRectFilled(origin, origin + size, Color(bg), 6f * scale);
        }

        var textPos = new Vector2(origin.X + 12f * scale, origin.Y + (height - textSize.Y) * 0.5f);
        var textCol = Color(danger ? AlertText : Ink);
        AddText(draw, textPos, fontSize, textCol, label);

        if (clicked)
            ImGui.CloseCurrentPopup();

        return clicked;
    }

    public readonly struct ContextMenuScope(bool isOpen) : IDisposable
    {
        public bool IsOpen { get; } = isOpen;

        public void Dispose()
        {
            if (IsOpen)
                ImGui.EndPopup();

            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
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

    public static int DrawReplyCard(
        ImDrawListPtr draw,
        Vector2 origin,
        float availWidth,
        float maxCardWidth,
        IReadOnlyList<string> options,
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
            var textSize = CalcTextSize(options[i], optionFontSize, textWrapWidth);
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
            var btnPos = new Vector2(buttonX, cursorY);
            var btnSize = new Vector2(buttonWidth, buttonHeights[i]);

            ImGui.SetCursorScreenPos(btnPos);
            bool clicked;
            var hovered = false;
            var active = false;
            using (ImRaii.Disabled(!enabled))
            {
                clicked = ImGui.InvisibleButton($"##ai-reply-opt-{i}", btnSize);
                if (enabled)
                {
                    hovered = ImGui.IsItemHovered();
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
            AddText(draw, new Vector2(textX, textY), optionFontSize, textCol, options[i], textWrapWidth);

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
