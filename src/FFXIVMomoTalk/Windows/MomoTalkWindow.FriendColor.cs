using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private static readonly uint[] PresetNameColors =
    [
        0xD9536F, // Coral Rose
        0xC0392B, // Crimson
        0xD97706, // Amber
        0x9A7B0A, // Ochre Gold
        0x1F8A5B, // Emerald
        0x14848B, // Teal
        0x2E7AC5, // Cobalt
        0x4F46E5, // Indigo
        0x7C3AED, // Violet
        0xC0267A, // Magenta
        0x8B5A2B, // Chestnut
        0x4B5A6F, // Slate
    ];

    private Vector4 GetFriendNameColor(ulong contentId)
    {
        if (colorPickerOpen && colorPickerLivePreview && colorPickerFriendId == contentId)
            return new Vector4(colorPickerRgb, 1f);

        return friendColors.TryGetValue(contentId, out var rgb)
            ? ChatVisuals.Hex(rgb)
            : ChatVisuals.Ink;
    }

    private Vector4 GetGroupSpeakerNameColor(SocialSnapshot snapshot, PlayerIdentity author)
    {
        if (author.ContentId != 0)
            return GetFriendNameColor(author.ContentId);

        if (snapshot.Friends is null)
            return ChatVisuals.Ink;

        var world = string.IsNullOrEmpty(author.World) ? null : author.World;
        var matched = snapshot.ResolveTellPeer(new TellPeer(author.Name, author.WorldId, world));
        return matched is not null ? GetFriendNameColor(matched.ContentId) : ChatVisuals.Ink;
    }

    private void DrawInteractiveFriendName(
        ImDrawListPtr draw,
        ulong contentId,
        string nameText,
        float nameFontSize,
        Vector2 namePos,
        Vector2 nameSize,
        float maxWidth,
        float scale)
    {
        var pad = new Vector2(8f, 3f) * scale;
        var boxWidth = MathF.Min(maxWidth, nameSize.X + pad.X * 2f);
        var boxSize = new Vector2(boxWidth, nameSize.Y + pad.Y * 2f);
        var boxPos = new Vector2(namePos.X + (nameSize.X - boxWidth) * 0.5f, namePos.Y - pad.Y);

        ImGui.SetCursorScreenPos(boxPos);
        var clicked = ImGui.InvisibleButton("##open-name-color", boxSize);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(boxPos, boxPos + boxSize, ChatVisuals.Color(ChatVisuals.ListBg), 8f * scale);
            ImGui.SetTooltip(Loc.Text("EditNameColor"));
        }

        var nameColor = GetFriendNameColor(contentId);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(nameColor), nameText, scale);

        if (clicked)
        {
            CommitActiveNote();
            OpenFriendColorPicker(contentId);
            colorPopupAnchor = new Vector2(namePos.X, boxPos.Y + boxSize.Y + 4f * scale);
            ImGui.OpenPopup("##friend-color-popup");
        }
    }

    private void OpenFriendColorPicker(ulong contentId)
    {
        colorPickerFriendId = contentId;
        colorPickerDirty = false;
        colorPickerHexInvalid = false;
        colorSaveFailed = false;
        if (friendColors.TryGetValue(contentId, out var rgb))
        {
            colorPickerRgb = RgbToVec3(rgb);
            colorPickerHex = FriendProfileStore.FormatHexColor(rgb);
            colorPickerLivePreview = true;
        }
        else
        {
            colorPickerRgb = new Vector3(ChatVisuals.Ink.X, ChatVisuals.Ink.Y, ChatVisuals.Ink.Z);
            colorPickerHex = FriendProfileStore.FormatHexColor(Vec3ToRgb(colorPickerRgb));
            colorPickerLivePreview = false;
        }
    }

    private void DrawFriendColorPopup(ulong friendContentId, float scale)
    {
        if (colorPickerFriendId != friendContentId)
            return;

        var popupWidth = 244f * scale;
        ImGui.SetNextWindowPos(colorPopupAnchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f));

        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, ChatVisuals.White)
            .Push(ImGuiCol.Border, ChatVisuals.InputBorder);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 10f * scale)
            .Push(ImGuiStyleVar.PopupBorderSize, MathF.Max(1f, scale))
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale));
        using var popup = ImRaii.Popup("##friend-color-popup");
        if (!popup)
        {
            if (colorPickerOpen)
            {
                CommitPendingFriendColor();
                colorPickerOpen = false;
                colorPickerLivePreview = false;
            }

            return;
        }

        colorPickerOpen = true;
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var innerW = MathF.Max(100f * scale, ImGui.GetContentRegionAvail().X);
        var headerH = 24f * scale;
        var titleFont = ImGui.GetFontSize() * 0.94f;
        var resetFont = ImGui.GetFontSize() * 0.84f;

        var hasCustom = friendColors.ContainsKey(friendContentId) || colorPickerLivePreview;
        var resetLabel = Loc.Text("ResetColor");
        var resetSize = ChatVisuals.CalcTextSize(resetLabel, resetFont);
        var resetBtnSize = new Vector2(resetSize.X + 12f * scale, headerH);
        var resetPos = new Vector2(origin.X + innerW - resetBtnSize.X, origin.Y);

        var titleText = ChatVisuals.TruncateSingleLine(Loc.Text("NameColor"), titleFont,
            MathF.Max(40f * scale, innerW - resetBtnSize.X - 6f * scale));
        var titleSize = ChatVisuals.CalcTextSize(titleText, titleFont);
        ChatVisuals.AddBoldText(draw, new Vector2(origin.X, origin.Y + (headerH - titleSize.Y) * 0.5f),
            titleFont, ChatVisuals.Color(ChatVisuals.Ink), titleText, scale);

        ImGui.SetCursorScreenPos(resetPos);
        bool resetClicked;
        using (ImRaii.Disabled(!hasCustom || colorSaveTask is not null))
            resetClicked = ImGui.InvisibleButton("##color-reset-btn", resetBtnSize);

        var resetHovered = hasCustom && ImGui.IsItemHovered();
        if (resetHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(resetPos, resetPos + resetBtnSize, ChatVisuals.Color(ChatVisuals.ListHover), 5f * scale);
        }

        ChatVisuals.AddText(draw, resetPos + (resetBtnSize - resetSize) * 0.5f, resetFont,
            ChatVisuals.Color(hasCustom ? ChatVisuals.HeaderPinkActive : ChatVisuals.Placeholder), resetLabel);

        if (resetClicked)
        {
            colorPickerDirty = false;
            colorPickerLivePreview = false;
            colorPickerHexInvalid = false;
            colorPickerRgb = new Vector3(ChatVisuals.Ink.X, ChatVisuals.Ink.Y, ChatVisuals.Ink.Z);
            colorPickerHex = FriendProfileStore.FormatHexColor(Vec3ToRgb(colorPickerRgb));
            SaveFriendColor(friendContentId, null);
        }

        var cursorY = origin.Y + headerH + 6f * scale;
        draw.AddLine(new Vector2(origin.X, cursorY), new Vector2(origin.X + innerW, cursorY),
            ChatVisuals.Color(ChatVisuals.ModalDivider), 1f);
        cursorY += 8f * scale;

        var subLabelFont = ImGui.GetFontSize() * 0.84f;
        ChatVisuals.AddText(draw, new Vector2(origin.X, cursorY), subLabelFont,
            ChatVisuals.Color(ChatVisuals.Muted), Loc.Text("ColorPresets"));
        cursorY += subLabelFont + 5f * scale;

        const int columns = 6;
        var swatchGap = 6f * scale;
        var swatchSide = MathF.Min(28f * scale, MathF.Floor((innerW - swatchGap * (columns - 1)) / columns));
        var gridWidth = columns * swatchSide + (columns - 1) * swatchGap;
        var gridStartX = origin.X + (innerW - gridWidth) * 0.5f;
        var activeRgb = Vec3ToRgb(colorPickerRgb);

        for (var i = 0; i < PresetNameColors.Length; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var swatchPos = new Vector2(gridStartX + col * (swatchSide + swatchGap), cursorY + row * (swatchSide + swatchGap));
            var swatchSize = new Vector2(swatchSide);
            var presetRgb = PresetNameColors[i];

            ImGui.SetCursorScreenPos(swatchPos);
            var picked = ImGui.InvisibleButton($"##color-preset-{i}", swatchSize);
            var hovered = ImGui.IsItemHovered();
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip(FriendProfileStore.FormatHexColor(presetRgb));
            }

            var isCurrent = colorPickerLivePreview && activeRgb == presetRgb;
            var center = swatchPos + swatchSize * 0.5f;
            var radius = swatchSide * 0.5f - 1.5f * scale;
            draw.AddCircleFilled(center, radius, ChatVisuals.Color(ChatVisuals.Hex(presetRgb)), 24);
            if (isCurrent)
                draw.AddCircle(center, radius + 1.5f * scale, ChatVisuals.Color(ChatVisuals.Ink), 24, MathF.Max(1.6f, 1.8f * scale));
            else if (hovered)
                draw.AddCircle(center, radius + 1.2f * scale, ChatVisuals.Color(ChatVisuals.ModalButtonHoverBorder), 24, MathF.Max(1.3f, 1.4f * scale));

            if (picked)
            {
                colorPickerRgb = RgbToVec3(presetRgb);
                colorPickerHex = FriendProfileStore.FormatHexColor(presetRgb);
                colorPickerHexInvalid = false;
                colorPickerLivePreview = true;
                colorPickerDirty = false;
                SaveFriendColor(friendContentId, presetRgb);
            }
        }

        var rows = (PresetNameColors.Length + columns - 1) / columns;
        cursorY += rows * swatchSide + Math.Max(0, rows - 1) * swatchGap + 10f * scale;

        ChatVisuals.AddText(draw, new Vector2(origin.X, cursorY), subLabelFont,
            ChatVisuals.Color(ChatVisuals.Muted), Loc.Text("CustomColor"));
        cursorY += subLabelFont + 5f * scale;

        ImGui.SetCursorScreenPos(new Vector2(origin.X, cursorY));
        ImGui.SetNextItemWidth(innerW);
        const ImGuiColorEditFlags pickerFlags = ImGuiColorEditFlags.NoAlpha
            | ImGuiColorEditFlags.NoSidePreview
            | ImGuiColorEditFlags.NoSmallPreview
            | ImGuiColorEditFlags.NoInputs
            | ImGuiColorEditFlags.NoLabel
            | ImGuiColorEditFlags.NoOptions
            | ImGuiColorEditFlags.PickerHueBar;

        using (ImRaii.Disabled(colorSaveTask is not null))
        {
            if (ImGui.ColorPicker3("##friend-color-picker", ref colorPickerRgb, pickerFlags))
            {
                colorPickerHex = FriendProfileStore.FormatHexColor(Vec3ToRgb(colorPickerRgb));
                colorPickerHexInvalid = false;
                colorPickerLivePreview = true;
                colorPickerDirty = true;
            }
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && colorPickerDirty)
        {
            colorPickerDirty = false;
            SaveFriendColor(friendContentId, Vec3ToRgb(colorPickerRgb));
        }

        ImGui.Dummy(new Vector2(innerW, 6f * scale));
        var hexRowPos = ImGui.GetCursorScreenPos();
        var hexH = 30f * scale;
        var previewSq = hexH;
        var hexGap = 8f * scale;
        var previewRgbVec = colorPickerLivePreview ? new Vector4(colorPickerRgb, 1f) : ChatVisuals.Ink;

        draw.AddRectFilled(hexRowPos, hexRowPos + new Vector2(previewSq), ChatVisuals.Color(previewRgbVec), 6f * scale);
        draw.AddRect(hexRowPos, hexRowPos + new Vector2(previewSq), ChatVisuals.Color(ChatVisuals.BadgeBorder), 6f * scale, ImDrawFlags.None, 1f);

        var hexInputPos = new Vector2(hexRowPos.X + previewSq + hexGap, hexRowPos.Y);
        var hexInputW = MathF.Max(60f * scale, innerW - previewSq - hexGap);
        draw.AddRectFilled(hexInputPos, hexInputPos + new Vector2(hexInputW, hexH), ChatVisuals.Color(ChatVisuals.White), 7f * scale);

        var padY = MathF.Max(2f, (hexH - ImGui.GetFontSize()) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(hexInputPos.X + 8f * scale, hexInputPos.Y));
        bool hexChanged;
        bool hexEnter;
        using (ImRaii.Disabled(colorSaveTask is not null))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, padY)))
        {
            ImGui.SetNextItemWidth(MathF.Max(30f * scale, hexInputW - 14f * scale));
            hexChanged = ImGui.InputTextWithHint("##friend-color-hex", Loc.Text("ColorHexHint"), ref colorPickerHex, 16,
                ImGuiInputTextFlags.EnterReturnsTrue);
            hexEnter = hexChanged;
        }

        var hexActive = ImGui.IsItemActive();
        var hexCommitted = hexEnter || ImGui.IsItemDeactivatedAfterEdit();
        if (hexChanged && !hexEnter)
        {
            if (FriendProfileStore.TryParseHexColor(colorPickerHex, allowShortForm: false, out var liveRgb))
            {
                colorPickerRgb = RgbToVec3(liveRgb);
                colorPickerLivePreview = true;
                colorPickerHexInvalid = false;
            }
        }

        if (hexCommitted)
        {
            if (FriendProfileStore.TryParseHexColor(colorPickerHex, allowShortForm: true, out var parsedRgb))
            {
                colorPickerRgb = RgbToVec3(parsedRgb);
                colorPickerHex = FriendProfileStore.FormatHexColor(parsedRgb);
                colorPickerHexInvalid = false;
                colorPickerLivePreview = true;
                colorPickerDirty = false;
                SaveFriendColor(friendContentId, parsedRgb);
            }
            else
            {
                colorPickerHexInvalid = true;
            }
        }

        var borderCol = colorPickerHexInvalid
            ? ChatVisuals.AlertText
            : hexActive
                ? ChatVisuals.InputFocus
                : ChatVisuals.InputBorder;
        draw.AddRect(hexInputPos, hexInputPos + new Vector2(hexInputW, hexH), ChatVisuals.Color(borderCol),
            7f * scale, ImDrawFlags.None, MathF.Max(1.3f, 1.5f * scale));

        ImGui.SetCursorScreenPos(hexRowPos);
        ImGui.Dummy(new Vector2(innerW, hexH));

        if (colorPickerHexInvalid)
        {
            ImGui.Dummy(new Vector2(innerW, 4f * scale));
            var errPos = ImGui.GetCursorScreenPos();
            var errText = Loc.Text("InvalidHexColor");
            var errSize = ChatVisuals.CalcTextSize(errText, subLabelFont, innerW);
            ChatVisuals.AddText(draw, errPos, subLabelFont, ChatVisuals.Color(ChatVisuals.AlertText), errText, innerW);
            ImGui.Dummy(new Vector2(innerW, errSize.Y));
        }
        else if (colorSaveFailed)
        {
            ImGui.Dummy(new Vector2(innerW, 4f * scale));
            var errPos = ImGui.GetCursorScreenPos();
            var errText = Loc.Text("SaveFailed");
            var errSize = ChatVisuals.CalcTextSize(errText, subLabelFont, innerW);
            ChatVisuals.AddText(draw, errPos, subLabelFont, ChatVisuals.Color(ChatVisuals.AlertText), errText, innerW);
            ImGui.Dummy(new Vector2(innerW, errSize.Y));
        }
    }

    private void CommitPendingFriendColor()
    {
        if (!colorPickerDirty || colorPickerFriendId is not { } cid)
            return;

        colorPickerDirty = false;
        SaveFriendColor(cid, Vec3ToRgb(colorPickerRgb));
    }

    private void SaveFriendColor(ulong friendContentId, uint? colorRgb)
    {
        if (session is null || colorSaveTask is not null)
            return;

        colorSaveFailed = false;
        if (colorRgb is { } rgb)
            friendColors[friendContentId] = rgb & 0xFFFFFF;
        else
            friendColors.Remove(friendContentId);

        colorSaveFriendId = friendContentId;
        colorSaveTask = friendProfileStore.SaveColorAsync(session.ContentId, friendContentId, colorRgb);
        ObserveStoreTask(colorSaveTask, "Failed to save friend name color.");
    }

    private static Vector3 RgbToVec3(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    private static uint Vec3ToRgb(Vector3 rgb)
    {
        var r = (uint)Math.Clamp((int)MathF.Round(rgb.X * 255f), 0, 255);
        var g = (uint)Math.Clamp((int)MathF.Round(rgb.Y * 255f), 0, 255);
        var b = (uint)Math.Clamp((int)MathF.Round(rgb.Z * 255f), 0, 255);
        return (r << 16) | (g << 8) | b;
    }
}
