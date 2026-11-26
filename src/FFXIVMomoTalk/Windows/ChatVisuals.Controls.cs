using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace FFXIVMomoTalk.Windows;

internal static partial class ChatVisuals
{
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
        bool cornerIndicator = true, Vector4? indicatorColor = null)
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
                Color(indicatorColor ?? RailBg));
        }

        var fontSize = ImGui.GetFontSize() * 0.94f;
        var visibleLabel = TruncateSingleLine(label, fontSize, MathF.Max(16f * scale, size.X - skew * 2f - 8f * scale));
        var textSize = CalcTextSize(visibleLabel, fontSize);
        var textPos = position + (size - textSize) * 0.5f;
        AddBoldText(draw, textPos, fontSize, Color(Ink), visibleLabel, scale);
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
}
