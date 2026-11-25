using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private static bool DrawSettingTextField(
        ImDrawListPtr draw,
        string id,
        string hint,
        Vector2 pos,
        float width,
        float height,
        ref string value,
        int maxLength,
        ImGuiInputTextFlags flags,
        float uiScale,
        float alpha,
        bool interactive)
    {
        var size = PrepareSettingField(draw, pos, width, height, uiScale, alpha, out var rounding);
        bool changed;
        using (PushSettingFieldInputScope(width, height, 40f * uiScale, uiScale))
        {
            var effectiveFlags = interactive ? flags : flags | ImGuiInputTextFlags.ReadOnly;
            changed = ImGui.InputTextWithHint(id, hint, ref value, maxLength, effectiveFlags);
        }

        DrawSettingFieldBorder(draw, pos, size, rounding, uiScale, alpha, interactive);
        return changed;
    }

    private static bool DrawSettingIntField(
        ImDrawListPtr draw,
        string id,
        Vector2 pos,
        float width,
        float height,
        ref int value,
        string tooltip,
        float uiScale,
        float alpha,
        bool interactive,
        out bool deactivatedAfterEdit)
    {
        var size = PrepareSettingField(draw, pos, width, height, uiScale, alpha, out var rounding);
        bool changed;
        using (PushSettingFieldInputScope(width, height, 24f * uiScale, uiScale))
        {
            var flags = interactive ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly;
            changed = ImGui.InputInt(id, ref value, 0, 0, "%d", flags);
        }

        deactivatedAfterEdit = interactive && ImGui.IsItemDeactivatedAfterEdit();
        DrawSettingFieldBorder(draw, pos, size, rounding, uiScale, alpha, interactive, tooltip);
        return changed;
    }

    private static Vector2 PrepareSettingField(
        ImDrawListPtr draw,
        Vector2 pos,
        float width,
        float height,
        float uiScale,
        float alpha,
        out float rounding)
    {
        var size = new Vector2(width, height);
        rounding = 7f * uiScale;
        draw.AddRectFilled(pos, pos + size,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.White, alpha)), rounding);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + 8f * uiScale, pos.Y));
        return size;
    }

    private static SettingInputScope PushSettingFieldInputScope(float width, float height, float minInnerWidth, float uiScale)
    {
        var inputPadY = MathF.Max(2f, (height - ImGui.GetFontSize()) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2f * uiScale, inputPadY));
        ImGui.SetNextItemWidth(MathF.Max(minInnerWidth, width - 16f * uiScale));
        return default;
    }

    private static void DrawSettingFieldBorder(
        ImDrawListPtr draw,
        Vector2 pos,
        Vector2 size,
        float rounding,
        float uiScale,
        float alpha,
        bool interactive,
        string? tooltip = null)
    {
        var active = interactive && ImGui.IsItemActive();
        var hovered = interactive && ImGui.IsItemHovered();
        if (hovered && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);

        var borderCol = active
            ? ChatVisuals.InputFocus
            : hovered
                ? ChatVisuals.ModalButtonHoverBorder
                : ChatVisuals.InputBorder;
        draw.AddRect(pos, pos + size,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(borderCol, alpha)),
            rounding, ImDrawFlags.None, MathF.Max(1.3f, 1.5f * uiScale));
    }

    private readonly struct SettingInputScope : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);
        }
    }
}
