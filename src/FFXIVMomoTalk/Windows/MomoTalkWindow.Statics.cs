using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private StaticModalMode staticModalMode;

    public void OpenCreateStaticModal(ActiveParty party)
    {
        CommitActiveNote();
        OpenImmediate();
        CloseSettings(immediate: true);
        SyncSession(directory.Current.Session);
        if (session is null || staticSave is not null)
            return;

        staticModalMode = StaticModalMode.Create;
        createStaticParty = party;
        renameStaticKey = null;
        staticNameDraft = string.Empty;
        staticSaveFailed = false;
        staticModalOpenedThisFrame = true;
        focusStaticNameInput = true;
    }

    public void OpenStaticConversation(StaticGroup group)
    {
        CommitActiveNote();
        OpenImmediate();
        CloseSettings(immediate: true);
        CloseStaticModal();
        SyncSession(directory.Current.Session);
        if (session is null)
            return;

        OpenConversation(group.ToDescriptor());
    }

    private void OpenRenameStaticModal(string stableKey, string currentName)
    {
        if (staticSave is not null)
            return;

        CommitActiveNote();
        CloseSettings(immediate: true);
        staticModalMode = StaticModalMode.Rename;
        createStaticParty = null;
        renameStaticKey = stableKey;
        staticNameDraft = currentName;
        staticSaveFailed = false;
        staticModalOpenedThisFrame = true;
        focusStaticNameInput = true;
    }

    private void CloseStaticModal()
    {
        if (staticSave is not null)
            return;

        staticModalMode = StaticModalMode.None;
        staticModalOpenedThisFrame = false;
        focusStaticNameInput = false;
        createStaticParty = null;
        renameStaticKey = null;
        staticNameDraft = string.Empty;
        staticSaveFailed = false;
    }

    private void DrawStaticModalOverlay(Vector2 winPos, Vector2 winSize, float scale)
    {
        var justOpened = staticModalOpenedThisFrame;
        staticModalOpenedThisFrame = false;
        var isCreate = staticModalMode == StaticModalMode.Create;
        if (staticSave is { IsCompleted: true } completed)
        {
            staticSave = null;
            if (completed.IsCompletedSuccessfully && completed.Result is { } saved)
            {
                CloseStaticModal();
                if (isCreate)
                    OpenConversation(saved.ToDescriptor());
                return;
            }

            staticSaveFailed = true;
            focusStaticNameInput = true;
        }

        var isSaving = staticSave is not null;
        if (!isSaving && !justOpened && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CloseStaticModal();
            return;
        }

        if (isCreate && createStaticParty is null)
        {
            CloseStaticModal();
            return;
        }

        if (!isCreate && string.IsNullOrEmpty(renameStaticKey))
        {
            CloseStaticModal();
            return;
        }

        ImGui.SetCursorScreenPos(winPos);
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var overlay = ImRaii.Child("##static-modal-overlay", winSize, false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!overlay)
            return;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(winPos, winPos + winSize, ChatVisuals.Color(ChatVisuals.ModalBackdrop), 10f * scale);

        var cardWidth = MathF.Min(380f * scale, MathF.Max(270f * scale, winSize.X - 36f * scale));
        var headerHeight = 50f * scale;
        var padX = 22f * scale;
        var padTop = 16f * scale;
        var padBottom = 20f * scale;
        var innerW = MathF.Max(80f * scale, cardWidth - padX * 2f);

        var rosterListHeight = 0f;
        var rosterHeaderHeight = 0f;
        if (isCreate && createStaticParty is { } partyPreview)
        {
            rosterHeaderHeight = ImGui.GetFontSize() * 0.92f + 8f * scale;
            var rowH = 30f * scale;
            var rowsCount = (partyPreview.Members.Count + 1) / 2;
            rosterListHeight = MathF.Min(132f * scale, rowsCount * rowH + 8f * scale) + 12f * scale;
        }

        var labelHeight = ImGui.GetFontSize() * 0.92f + 6f * scale;
        var inputHeight = 36f * scale;
        var errorText = staticSaveFailed ? Loc.Text("SaveStaticFailed") : null;
        ChatVisuals.BannerLayout? errorBanner = errorText is not null
            ? ChatVisuals.MeasureSystemBanner(errorText, innerW, scale)
            : null;
        var errorHeight = errorBanner is { } eb ? eb.Height + 6f * scale : 0f;
        var buttonsHeight = 34f * scale;
        var buttonsTopGap = 16f * scale;

        var cardHeight = headerHeight + padTop + rosterHeaderHeight + rosterListHeight
            + labelHeight + inputHeight + errorHeight + buttonsTopGap + buttonsHeight + padBottom;
        cardHeight = MathF.Min(cardHeight, MathF.Max(220f * scale, winSize.Y - 28f * scale));

        var cardSize = new Vector2(cardWidth, cardHeight);
        var cardPos = winPos + (winSize - cardSize) * 0.5f;
        var cardRounding = 11f * scale;

        draw.AddRectFilled(cardPos + new Vector2(0, 2f * scale), cardPos + cardSize + new Vector2(0, 2f * scale),
            ChatVisuals.Color(ChatVisuals.Shadow), cardRounding);
        draw.AddRectFilled(cardPos, cardPos + cardSize, ChatVisuals.Color(ChatVisuals.White), cardRounding);
        draw.AddRect(cardPos, cardPos + cardSize, ChatVisuals.Color(ChatVisuals.ModalDivider), cardRounding,
            ImDrawFlags.None, MathF.Max(1f, scale));

        var title = isCreate ? Loc.Text("CreateStaticGroup") : Loc.Text("RenameGroup");
        var titleFontSize = ImGui.GetFontSize() * 1.18f;
        var titleSize = ChatVisuals.CalcTextSize(title, titleFontSize);
        var titlePos = new Vector2(cardPos.X + (cardWidth - titleSize.X) * 0.5f,
            cardPos.Y + (headerHeight - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize, ChatVisuals.Color(ChatVisuals.Ink), title, scale);

        var closeSize = 28f * scale;
        var closePos = new Vector2(cardPos.X + cardWidth - 14f * scale - closeSize,
            cardPos.Y + (headerHeight - closeSize) * 0.5f);
        ImGui.SetCursorScreenPos(closePos);
        bool closeClicked;
        using (ImRaii.Disabled(isSaving))
            closeClicked = ImGui.InvisibleButton("##static-modal-close", new Vector2(closeSize));
        if (closeClicked)
        {
            CloseStaticModal();
            return;
        }

        var closeHovered = ImGui.IsItemHovered();
        if (closeHovered)
            draw.AddRectFilled(closePos, closePos + new Vector2(closeSize), ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);
        ChatVisuals.DrawCloseIcon(draw, closePos + new Vector2(closeSize * 0.5f), 14f * scale,
            ChatVisuals.Color(closeHovered ? ChatVisuals.Ink : ChatVisuals.Muted), MathF.Max(1.6f, 1.8f * scale));

        var dividerY = cardPos.Y + headerHeight;
        draw.AddLine(new Vector2(cardPos.X, dividerY), new Vector2(cardPos.X + cardWidth, dividerY),
            ChatVisuals.Color(ChatVisuals.ModalDivider), MathF.Max(1f, scale));

        var cursorY = dividerY + padTop;
        var leftX = cardPos.X + padX;
        var labelFontSize = ImGui.GetFontSize() * 0.92f;

        if (isCreate && createStaticParty is { } party)
        {
            ChatVisuals.AddBoldText(draw, new Vector2(leftX, cursorY), labelFontSize, ChatVisuals.Color(ChatVisuals.Muted),
                Loc.Format("PartyMembers", party.Members.Count), scale);
            cursorY += rosterHeaderHeight;

            var listBoxH = MathF.Max(34f * scale, rosterListHeight - 12f * scale);
            var listBoxPos = new Vector2(leftX, cursorY);
            draw.AddRectFilled(listBoxPos, listBoxPos + new Vector2(innerW, listBoxH), ChatVisuals.Color(ChatVisuals.ListBg),
                8f * scale);
            ImGui.SetCursorScreenPos(listBoxPos + new Vector2(8f * scale, 4f * scale));
            using (var rosterChild = ImRaii.Child("##static-modal-roster",
                new Vector2(MathF.Max(20f * scale, innerW - 16f * scale), MathF.Max(20f * scale, listBoxH - 8f * scale)), false))
            {
                if (rosterChild)
                    DrawMemberGrid(party.Members, scale);
            }

            cursorY += rosterListHeight;
        }

        ChatVisuals.AddBoldText(draw, new Vector2(leftX, cursorY), labelFontSize, ChatVisuals.Color(ChatVisuals.Ink),
            Loc.Text("GroupName"), scale);
        cursorY += labelHeight;

        var inputPos = new Vector2(leftX, cursorY);
        var inputSize = new Vector2(innerW, inputHeight);
        var inputRounding = 8f * scale;
        draw.AddRectFilled(inputPos, inputPos + inputSize, ChatVisuals.Color(ChatVisuals.White), inputRounding);

        var inputPadY = MathF.Max(2f, (inputHeight - ImGui.GetFontSize()) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(inputPos.X + 10f * scale, inputPos.Y));
        var enter = false;
        using (ImRaii.Disabled(isSaving))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, inputPadY)))
        {
            if (focusStaticNameInput)
            {
                ImGui.SetKeyboardFocusHere();
                focusStaticNameInput = false;
            }

            ImGui.SetNextItemWidth(MathF.Max(40f * scale, innerW - 20f * scale));
            enter = ImGui.InputTextWithHint("##static-name-input", Loc.Text("EnterGroupName"), ref staticNameDraft, 64,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var inputActive = ImGui.IsItemActive();
        draw.AddRect(inputPos, inputPos + inputSize,
            ChatVisuals.Color(inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder),
            inputRounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));
        cursorY += inputHeight;

        if (errorText is not null && errorBanner is { } errLayout)
        {
            cursorY += 6f * scale;
            ChatVisuals.DrawSystemBanner(draw, new Vector2(cardPos.X, cursorY), cardWidth, errorText,
                isAlert: true, scale, errLayout);
            cursorY += errLayout.Height;
        }

        cursorY += buttonsTopGap;
        var btnGap = 10f * scale;
        var btnWidth = (innerW - btnGap) * 0.5f;
        var cancelPos = new Vector2(leftX, cursorY);
        var confirmPos = new Vector2(leftX + btnWidth + btnGap, cursorY);
        var canSubmit = !isSaving && !string.IsNullOrWhiteSpace(staticNameDraft);

        if (ChatVisuals.DrawModalButton(draw, "##static-modal-cancel", Loc.Text("Cancel"), cancelPos,
            new Vector2(btnWidth, buttonsHeight), primary: false, enabled: !isSaving, scale))
        {
            CloseStaticModal();
            return;
        }

        var confirmClicked = ChatVisuals.DrawModalButton(draw, "##static-modal-confirm",
            isSaving ? Loc.Text("Saving") : isCreate ? Loc.Text("Create") : Loc.Text("Save"), confirmPos, new Vector2(btnWidth, buttonsHeight),
            primary: true, enabled: canSubmit, scale);

        if (canSubmit && (confirmClicked || enter))
        {
            staticSaveFailed = false;
            staticSave = isCreate
                ? directory.CreateStaticAsync(session!, createStaticParty!, staticNameDraft)
                : directory.RenameStaticAsync(session!, renameStaticKey!, staticNameDraft);
            ObserveStoreTask(staticSave, "Failed to save static group.");
            return;
        }

        var inCard = ImGui.IsMouseHoveringRect(cardPos, cardPos + cardSize);
        if (!isSaving && !justOpened && !inCard && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            CloseStaticModal();
    }

    private void DrawMemberGrid(IReadOnlyList<PlayerIdentity> members, float scale)
    {
        var availW = ImGui.GetContentRegionAvail().X;
        var twoCols = availW >= 240f * scale;
        var colGap = 10f * scale;
        var colW = twoCols ? (availW - colGap) * 0.5f : availW;
        var rowH = 30f * scale;
        var avatarSize = 22f * scale;
        var fontSize = ImGui.GetFontSize() * 0.90f;
        var draw = ImGui.GetWindowDrawList();

        for (var i = 0; i < members.Count; i += twoCols ? 2 : 1)
        {
            var origin = ImGui.GetCursorScreenPos();
            var rowSize = new Vector2(availW, rowH);
            if (ImGui.IsRectVisible(origin, origin + rowSize))
            {
                DrawMemberCell(draw, members[i], origin, colW, rowH, avatarSize, fontSize, scale);
                if (twoCols && i + 1 < members.Count)
                    DrawMemberCell(draw, members[i + 1], origin + new Vector2(colW + colGap, 0), colW, rowH, avatarSize, fontSize, scale);
            }

            ImGui.Dummy(rowSize);
        }
    }

    private void DrawMemberCell(
        ImDrawListPtr draw,
        PlayerIdentity member,
        Vector2 origin,
        float width,
        float height,
        float avatarSize,
        float fontSize,
        float scale)
    {
        var avatarPos = new Vector2(origin.X, origin.Y + (height - avatarSize) * 0.5f);
        ChatVisuals.Avatar(draw, avatars, member, avatarPos, avatarSize);

        var textX = avatarPos.X + avatarSize + 7f * scale;
        var maxTextW = MathF.Max(20f * scale, origin.X + width - textX);
        var label = ChatVisuals.TruncateSingleLine(member.Name, fontSize, maxTextW);
        var textSize = ChatVisuals.CalcTextSize(label, fontSize);
        ChatVisuals.AddText(draw, new Vector2(textX, origin.Y + (height - textSize.Y) * 0.5f), fontSize,
            ChatVisuals.Color(ChatVisuals.Ink), label);
    }

    private enum StaticModalMode
    {
        None,
        Create,
        Rename,
    }
}
