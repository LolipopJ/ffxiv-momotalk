using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private SettingsTab settingsTab = SettingsTab.General;

    public void OpenSettings()
    {
        CommitActiveNote();
        OpenImmediate();
        CloseStaticModal();
        settingsOpen = true;
        settingsOpenedThisFrame = true;
        showApiKey = false;
    }

    private void CloseSettings(bool immediate = false)
    {
        settingsOpen = false;
        showApiKey = false;
        aiReply.ResetConnectionTest();
        if (immediate)
            settingsAnim = 0f;
    }

    private void DrawSettingsOverlay(Vector2 winPos, Vector2 winSize, float scale)
    {
        var justOpened = settingsOpenedThisFrame;
        settingsOpenedThisFrame = false;

        var interactive = settingsOpen;
        if (interactive && !justOpened && ImGui.IsKeyPressed(ImGuiKey.Escape)
            && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup))
        {
            CloseSettings();
            interactive = false;
        }

        var eased = settingsAnim * settingsAnim * (3f - 2f * settingsAnim);
        if (eased <= 0.001f)
            return;

        var overlayFlags = ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse;
        if (!interactive)
            overlayFlags |= ImGuiWindowFlags.NoInputs;

        ImGui.SetCursorScreenPos(winPos);
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var overlay = ImRaii.Child("##settings-overlay", winSize, false, overlayFlags);
        if (!overlay)
            return;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(winPos, winPos + winSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ModalBackdrop, eased)), 10f * scale);

        var cardScale = Lerp(0.96f, 1f, eased);
        var cardOffsetY = Lerp(8f * scale, 0f, eased);
        var uiScale = scale * cardScale;

        var cardWidth = MathF.Min(420f * scale, MathF.Max(280f * scale, winSize.X - 32f * scale)) * cardScale;
        var headerHeight = 46f * uiScale;
        var tabBarHeight = 38f * uiScale;
        var padX = 20f * uiScale;
        var padTop = 16f * uiScale;
        var padBottom = 18f * uiScale;
        var innerW = MathF.Max(120f * uiScale, cardWidth - padX * 2f);

        var configErrorText = Loc.Text("ConfigReadError");
        ChatVisuals.BannerLayout? configErrorLayout = config.PreserveExistingFile
            ? ChatVisuals.MeasureSystemBanner(configErrorText, innerW, uiScale)
            : null;
        var warnHeight = configErrorLayout is { } layout ? layout.Height + 10f * uiScale : 0f;

        var rowHeight = 34f * uiScale;
        var rowGap = 14f * uiScale;
        var labelFontSize = ImGui.GetFontSize() * 0.92f * cardScale;
        var labelHeight = labelFontSize + 5f * uiScale;
        var subLabelFontSize = ImGui.GetFontSize() * 0.84f * cardScale;
        var subLabelHeight = subLabelFontSize + 3f * uiScale;
        var inputHeight = 32f * uiScale;
        var fieldGap = 12f * uiScale;
        var testBtnHeight = inputHeight;
        var shownTab = settingsTab;
        var aiSettings = new AiSettingsMetrics(innerW, inputHeight, labelFontSize, labelHeight, uiScale);
        List<AiReplyExample>? customExamples = null;
        ChatVisuals.AiExamplePreview[] previewCards = [];
        var examplesHeight = 0f;
        if (shownTab == SettingsTab.AiSettings)
        {
            if (config.IsCustomPreset)
            {
                customExamples = config.LlmCustomExamples;
                examplesHeight = customExamples.Count == 0
                    ? ChatVisuals.CalcTextSize(Loc.Text("NoExamplesYet"), labelFontSize, innerW).Y
                    : customExamples.Sum(example => aiSettings.EditorHeight(example.Replies.Count) + aiSettings.Gap);
            }
            else
            {
                previewCards = config.EffectiveLlmExamples.Select(example =>
                    ChatVisuals.MeasureAiExamplePreview(example, innerW, labelHeight, labelFontSize, uiScale)).ToArray();
                examplesHeight = previewCards.Sum(card => card.Height + aiSettings.Gap);
            }
        }

        var testState = aiReply.GetConnectionTestState();
        var isTestingConnection = testState.Status == AiConnectionTestStatus.Testing;
        var testBannerText = testState.Status is AiConnectionTestStatus.Success or AiConnectionTestStatus.Error
            ? testState.Message?.Format()
            : null;
        ChatVisuals.BannerLayout? testBannerLayout = !string.IsNullOrEmpty(testBannerText)
            ? ChatVisuals.MeasureSystemBanner(testBannerText, innerW, uiScale)
            : null;
        var testBannerHeight = testBannerLayout is { } tbl ? tbl.Height + 6f * uiScale : 0f;
        var translationSectionHeight = labelHeight + inputHeight + fieldGap;
        var contextSectionHeight = labelHeight + subLabelHeight + inputHeight + fieldGap;
        var advancedMetrics = shownTab == SettingsTab.Advanced
            ? MeasureAdvancedSettings(innerW, labelFontSize, labelHeight, inputHeight, fieldGap, uiScale)
            : default;

        var naturalBodyHeight = shownTab switch
        {
            SettingsTab.General => padTop + warnHeight + rowHeight * 5f + rowGap * 4f + padBottom,
            SettingsTab.AiSettings => padTop + warnHeight + (labelHeight + inputHeight + fieldGap) * 2f
              + labelHeight + inputHeight + 10f * uiScale + testBtnHeight + testBannerHeight + fieldGap
              + translationSectionHeight + contextSectionHeight + aiSettings.HeaderHeight + examplesHeight + padBottom,
            _ => padTop + warnHeight + advancedMetrics.BodyHeight + padBottom,
        };
        var maxCardHeight = MathF.Max(240f * scale, winSize.Y - 28f * scale) * cardScale;
        var cardHeight = MathF.Min(headerHeight + tabBarHeight + naturalBodyHeight, maxCardHeight);

        var cardSize = new Vector2(cardWidth, cardHeight);
        var cardPos = winPos + (winSize - cardSize) * 0.5f + new Vector2(0, cardOffsetY);
        var cardRounding = 11f * uiScale;

        draw.AddRectFilled(cardPos + new Vector2(0, 2f * uiScale), cardPos + cardSize + new Vector2(0, 2f * uiScale),
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Shadow, eased)), cardRounding);
        draw.AddRectFilled(cardPos, cardPos + cardSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.White, eased)), cardRounding);
        draw.AddRect(cardPos, cardPos + cardSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ModalDivider, eased)), cardRounding,
            ImDrawFlags.None, MathF.Max(1f, uiScale));

        var titleFontSize = ImGui.GetFontSize() * 1.18f * cardScale;
        var title = Loc.Text("Settings");
        var titleSize = ChatVisuals.CalcTextSize(title, titleFontSize);
        var titlePos = new Vector2(cardPos.X + (cardWidth - titleSize.X) * 0.5f,
            cardPos.Y + (headerHeight - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), title, uiScale);

        var closeSize = 28f * uiScale;
        var closePos = new Vector2(cardPos.X + cardWidth - 12f * uiScale - closeSize,
            cardPos.Y + (headerHeight - closeSize) * 0.5f);
        var closeHovered = false;
        var closeActive = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(closePos);
            if (ImGui.InvisibleButton("##settings-close", new Vector2(closeSize)))
                CloseSettings();

            closeHovered = ImGui.IsItemHovered();
            closeActive = ImGui.IsItemActive();
            if (closeHovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (closeHovered || closeActive)
        {
            var closeBg = closeActive ? ChatVisuals.ModalButtonHover : ChatVisuals.ListHover;
            draw.AddRectFilled(closePos, closePos + new Vector2(closeSize),
                ChatVisuals.Color(ChatVisuals.ScaleAlpha(closeBg, eased)), 6f * uiScale);
        }

        ChatVisuals.DrawCloseIcon(draw, closePos + new Vector2(closeSize * 0.5f), 14f * uiScale,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(closeHovered || closeActive ? ChatVisuals.Ink : ChatVisuals.Muted, eased)),
            MathF.Max(1.6f, 1.8f * uiScale));

        var dividerY = cardPos.Y + headerHeight;
        draw.AddLine(new Vector2(cardPos.X, dividerY), new Vector2(cardPos.X + cardWidth, dividerY),
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ModalDivider, eased)), MathF.Max(1f, uiScale));

        var tabW1 = MathF.Floor(cardWidth / 3f);
        var tabW2 = MathF.Floor(cardWidth / 3f);
        var tabW3 = cardWidth - tabW1 - tabW2;
        if (DrawSettingsTab(draw, "##settings-tab-general", Loc.Text("General"),
            new Vector2(cardPos.X, dividerY), new Vector2(tabW1, tabBarHeight),
            shownTab == SettingsTab.General, uiScale, cardScale, eased, interactive))
        {
            settingsTab = SettingsTab.General;
        }

        if (DrawSettingsTab(draw, "##settings-tab-aisettings", Loc.Text("AiSettings"),
            new Vector2(cardPos.X + tabW1, dividerY), new Vector2(tabW2, tabBarHeight),
            shownTab == SettingsTab.AiSettings, uiScale, cardScale, eased, interactive))
        {
            settingsTab = SettingsTab.AiSettings;
        }

        if (DrawSettingsTab(draw, "##settings-tab-advanced", Loc.Text("Advanced"),
            new Vector2(cardPos.X + tabW1 + tabW2, dividerY), new Vector2(tabW3, tabBarHeight),
            shownTab == SettingsTab.Advanced, uiScale, cardScale, eased, interactive))
        {
            settingsTab = SettingsTab.Advanced;
        }

        var tabBottomY = dividerY + tabBarHeight;
        draw.AddLine(new Vector2(cardPos.X, tabBottomY), new Vector2(cardPos.X + cardWidth, tabBottomY),
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ModalDivider, eased)), MathF.Max(1f, uiScale));

        var bodyPos = new Vector2(cardPos.X, tabBottomY);
        var bodySize = new Vector2(cardWidth, MathF.Max(40f * uiScale, cardHeight - headerHeight - tabBarHeight));
        var bodyFlags = ImGuiWindowFlags.NoBackground;
        if (!interactive)
            bodyFlags |= ImGuiWindowFlags.NoInputs;

        var bodyChildId = shownTab switch
        {
            SettingsTab.General => "##settings-body-general",
            SettingsTab.AiSettings => "##settings-body-aisettings",
            _ => "##settings-body-advanced",
        };
        ImGui.SetCursorScreenPos(bodyPos);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, eased))
        using (var body = ImRaii.Child(bodyChildId, bodySize, false, bodyFlags))
        {
            if (body)
            {
                var bodyDraw = ImGui.GetWindowDrawList();
                var contentOrigin = ImGui.GetCursorScreenPos();
                var leftX = contentOrigin.X + padX;
                var cursorY = contentOrigin.Y + padTop;

                if (configErrorLayout is { } errorLayout)
                {
                    ChatVisuals.DrawSystemBanner(bodyDraw, new Vector2(contentOrigin.X, cursorY), cardWidth,
                        configErrorText, isAlert: true, uiScale, errorLayout, eased);
                    cursorY += errorLayout.Height + 10f * uiScale;
                }

                if (shownTab == SettingsTab.General)
                {
                    DrawLanguageSetting(bodyDraw, new Vector2(leftX, cursorY), innerW, rowHeight, inputHeight, uiScale, eased);
                    cursorY += rowHeight + rowGap;
                    if (DrawSettingToggleRow(bodyDraw, "##setting-floating-entry", Loc.Text("ShowFloatingEntry"),
                        new Vector2(leftX, cursorY), innerW, rowHeight,
                        config.ShowFloatingEntry, ref entrySwitchAnim, uiScale, cardScale, eased, interactive))
                    {
                        config.ShowFloatingEntry = !config.ShowFloatingEntry;
                        saveConfig();
                    }

                    cursorY += rowHeight + rowGap;
                    if (DrawSettingToggleRow(bodyDraw, "##setting-intro-anim", Loc.Text("ShowIntroAnimation"),
                        new Vector2(leftX, cursorY), innerW, rowHeight,
                        config.ShowIntroAnimation, ref introSwitchAnim, uiScale, cardScale, eased, interactive))
                    {
                        config.ShowIntroAnimation = !config.ShowIntroAnimation;
                        saveConfig();
                    }

                    cursorY += rowHeight + rowGap;
                    if (DrawSettingToggleRow(bodyDraw, "##setting-self-avatar", Loc.Text("ShowSelfAvatar"),
                        new Vector2(leftX, cursorY), innerW, rowHeight,
                        config.ShowSelfAvatar, ref selfAvatarSwitchAnim, uiScale, cardScale, eased, interactive))
                    {
                        config.ShowSelfAvatar = !config.ShowSelfAvatar;
                        saveConfig();
                    }

                    cursorY += rowHeight + rowGap;
                    if (DrawSettingToggleRow(bodyDraw, "##setting-friend-anniversaries", Loc.Text("ShowFriendAnniversaries"),
                        new Vector2(leftX, cursorY), innerW, rowHeight,
                        config.ShowFriendAnniversaries, ref friendAnniversariesSwitchAnim, uiScale, cardScale, eased, interactive))
                    {
                        config.ShowFriendAnniversaries = !config.ShowFriendAnniversaries;
                        saveConfig();
                    }

                    cursorY += rowHeight;
                }
                else if (shownTab == SettingsTab.AiSettings)
                {
                    ChatVisuals.AddBoldText(bodyDraw, new Vector2(leftX, cursorY), labelFontSize,
                        ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), Loc.Text("Endpoint"), uiScale);
                    cursorY += labelHeight;
                    var endpointValue = config.LlmEndpoint;
                    if (DrawSettingTextField(bodyDraw, "##ai-endpoint", "https://api.openai.com/v1",
                        new Vector2(leftX, cursorY), innerW, inputHeight, ref endpointValue, 512,
                        ImGuiInputTextFlags.None, uiScale, eased, interactive))
                    {
                        config.LlmEndpoint = endpointValue;
                        SaveAiConfig();
                    }

                    cursorY += inputHeight + fieldGap;
                    ChatVisuals.AddBoldText(bodyDraw, new Vector2(leftX, cursorY), labelFontSize,
                        ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), Loc.Text("ApiKey"), uiScale);
                    cursorY += labelHeight;
                    var toggleWidth = 58f * uiScale;
                    var toggleGap = 6f * uiScale;
                    var apiKeyWidth = MathF.Max(60f * uiScale, innerW - toggleWidth - toggleGap);
                    var apiKeyValue = config.LlmApiKey;
                    if (DrawSettingTextField(bodyDraw, "##ai-apikey", "sk-...",
                        new Vector2(leftX, cursorY), apiKeyWidth, inputHeight, ref apiKeyValue, 512,
                        showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password, uiScale, eased, interactive))
                    {
                        config.LlmApiKey = apiKeyValue;
                        SaveAiConfig();
                    }

                    var togglePos = new Vector2(leftX + apiKeyWidth + toggleGap, cursorY);
                    if (ChatVisuals.DrawModalButton(bodyDraw, "##ai-apikey-toggle", showApiKey ? Loc.Text("Hide") : Loc.Text("Show"),
                        togglePos, new Vector2(toggleWidth, inputHeight), primary: false, enabled: interactive, uiScale, eased))
                    {
                        showApiKey = !showApiKey;
                    }

                    cursorY += inputHeight + fieldGap;
                    ChatVisuals.AddBoldText(bodyDraw, new Vector2(leftX, cursorY), labelFontSize,
                        ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), Loc.Text("Model"), uiScale);
                    cursorY += labelHeight;
                    var modelValue = config.LlmModel;
                    if (DrawSettingTextField(bodyDraw, "##ai-model", "gpt-4o-mini",
                        new Vector2(leftX, cursorY), innerW, inputHeight, ref modelValue, 256,
                        ImGuiInputTextFlags.None, uiScale, eased, interactive))
                    {
                        config.LlmModel = modelValue;
                        SaveAiConfig();
                    }

                    cursorY += inputHeight + 10f * uiScale;
                    var canTestConnection = interactive && config.IsAiReplyConfigured && !isTestingConnection;
                    if (ChatVisuals.DrawModalButton(bodyDraw, "##ai-test-connection",
                        isTestingConnection ? Loc.Text("Testing") : Loc.Text("TestConnection"),
                        new Vector2(leftX, cursorY), new Vector2(innerW, testBtnHeight),
                        primary: true, enabled: canTestConnection, uiScale, eased))
                    {
                        aiReply.TryTestConnection();
                    }

                    cursorY += testBtnHeight;
                    if (testBannerLayout is { } bannerLayout && !string.IsNullOrEmpty(testBannerText))
                    {
                        cursorY += 6f * uiScale;
                        var isSuccess = testState.Status == AiConnectionTestStatus.Success;
                        ChatVisuals.DrawSystemBanner(
                            bodyDraw,
                            new Vector2(contentOrigin.X, cursorY),
                            cardWidth,
                            testBannerText,
                            isAlert: !isSuccess,
                            uiScale,
                            bannerLayout,
                            eased,
                            bgOverride: isSuccess ? ChatVisuals.MatchSuccessBg : null,
                            textOverride: isSuccess ? ChatVisuals.MatchSuccessText : null);
                        cursorY += bannerLayout.Height;
                    }

                    cursorY += fieldGap;
                    ChatVisuals.AddBoldText(bodyDraw, new Vector2(leftX, cursorY), labelFontSize,
                        ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), Loc.Text("TranslationLanguage"), uiScale);
                    cursorY += labelHeight;
                    DrawTranslationLanguageSetting(new Vector2(leftX, cursorY), innerW, inputHeight, uiScale, interactive);
                    cursorY += inputHeight + fieldGap;

                    ChatVisuals.AddBoldText(bodyDraw, new Vector2(leftX, cursorY), labelFontSize,
                        ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), Loc.Text("ChatContext"), uiScale);
                    cursorY += labelHeight;

                    var colGap = 8f * uiScale;
                    var colW = MathF.Max(40f * uiScale, (innerW - colGap * 2f) / 3f);
                    var minColX = leftX + colW + colGap;
                    var maxColX = leftX + (colW + colGap) * 2f;
                    var subLabelCol = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Muted, eased));

                    ChatVisuals.AddText(bodyDraw, new Vector2(leftX, cursorY), subLabelFontSize, subLabelCol, Loc.Text("Minutes"));
                    ChatVisuals.AddText(bodyDraw, new Vector2(minColX, cursorY), subLabelFontSize, subLabelCol, Loc.Text("Maximum"));
                    ChatVisuals.AddText(bodyDraw, new Vector2(maxColX, cursorY), subLabelFontSize, subLabelCol, Loc.Text("Minimum"));
                    cursorY += subLabelHeight;

                    var minutesValue = config.LlmContextWindowMinutes;
                    if (DrawSettingIntField(bodyDraw, "##ai-ctx-minutes", new Vector2(leftX, cursorY), colW, inputHeight,
                        ref minutesValue, Loc.Text("ContextMinutesHint"),
                        uiScale, eased, interactive, out var minutesDeactivated))
                    {
                        if (minutesValue is >= Configuration.MinContextWindowMinutes and <= Configuration.MaxContextWindowMinutes)
                        {
                            config.LlmContextWindowMinutes = minutesValue;
                            saveConfig();
                        }
                    }

                    if (minutesDeactivated)
                    {
                        config.LlmContextWindowMinutes = minutesValue;
                        saveConfig();
                    }

                    var maxMessagesValue = config.LlmContextMaxMessages;
                    if (DrawSettingIntField(bodyDraw, "##ai-ctx-max", new Vector2(minColX, cursorY), colW, inputHeight,
                        ref maxMessagesValue, Loc.Text("ContextMaximumHint"),
                        uiScale, eased, interactive, out var maxDeactivated))
                    {
                        if (maxMessagesValue is >= Configuration.MinContextMaxMessages and <= Configuration.MaxContextMaxMessages)
                        {
                            config.LlmContextMaxMessages = maxMessagesValue;
                            saveConfig();
                        }
                    }

                    if (maxDeactivated)
                    {
                        config.LlmContextMaxMessages = maxMessagesValue;
                        if (config.LlmContextMinMessages > config.LlmContextMaxMessages)
                            config.LlmContextMinMessages = config.LlmContextMaxMessages;
                        saveConfig();
                    }

                    var minMessagesValue = config.LlmContextMinMessages;
                    if (DrawSettingIntField(bodyDraw, "##ai-ctx-min", new Vector2(maxColX, cursorY),
                        MathF.Max(40f * uiScale, innerW - (colW + colGap) * 2f), inputHeight,
                        ref minMessagesValue, Loc.Text("ContextMinimumHint"),
                        uiScale, eased, interactive, out var minDeactivated))
                    {
                        if (minMessagesValue >= Configuration.MinContextMinMessages
                            && minMessagesValue <= config.LlmContextMaxMessages)
                        {
                            config.LlmContextMinMessages = minMessagesValue;
                            saveConfig();
                        }
                    }

                    if (minDeactivated)
                    {
                        config.LlmContextMinMessages = Math.Min(minMessagesValue, config.LlmContextMaxMessages);
                        saveConfig();
                    }

                    cursorY = DrawAiPromptSettings(bodyDraw, new Vector2(leftX, cursorY + inputHeight + fieldGap),
                        aiSettings, customExamples, previewCards, eased, interactive);
                }
                else
                {
                    cursorY = DrawAdvancedSettings(bodyDraw, contentOrigin.X, leftX, cursorY, cardWidth,
                        advancedMetrics, eased, interactive);
                }

                ImGui.SetCursorScreenPos(new Vector2(leftX, cursorY));
                ImGui.Dummy(new Vector2(innerW, padBottom));
            }
        }

        var inCard = ImGui.IsMouseHoveringRect(cardPos, cardPos + cardSize);
        if (interactive && !justOpened && !inCard && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            CloseSettings();
    }

    private void DrawLanguageSetting(ImDrawListPtr draw, Vector2 pos, float width, float height, float comboHeight, float scale, float alpha)
    {
        var comboWidth = width * 0.4f;
        var fontSize = ImGui.GetFontSize();
        var label = ChatVisuals.TruncateSingleLine(Loc.Text("Language"), fontSize, width - comboWidth - 10f * scale);
        ChatVisuals.AddBoldText(draw, pos + new Vector2(0, (height - fontSize) * 0.5f), fontSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha)), label, scale);

        var selected = config.UiLanguage;
        var followClient = Loc.Text("FollowClient");
        var preview = Loc.Languages.FirstOrDefault(language => language.Code == selected).Name ?? followClient;
        var comboPos = pos + new Vector2(width - comboWidth, (height - comboHeight) * 0.5f);
        using var colors = ChatVisuals.PushSettingsComboColors();
        using var style = ChatVisuals.PushSettingsComboStyle(comboHeight, scale);
        using var combo = ChatVisuals.BeginSettingsCombo("##setting-language", preview, comboPos, comboWidth);
        if (!combo)
            return;

        if (ChatVisuals.SelectSettingsOption(followClient, "follow-client", selected is null, comboHeight))
            config.UiLanguage = null;
        foreach (var (code, name) in Loc.Languages)
        {
            if (ChatVisuals.SelectSettingsOption(name, code, selected == code, comboHeight))
                config.UiLanguage = code;
        }

        if (config.UiLanguage != selected)
            saveConfig();
    }

    private void DrawTranslationLanguageSetting(Vector2 pos, float width, float comboHeight, float scale, bool interactive)
    {
        var selected = config.LlmTranslationLanguage;
        var followPlugin = Loc.Text("FollowPluginLanguage");
        var preview = Loc.Languages.FirstOrDefault(language => language.Code == selected).Name ?? followPlugin;
        using var colors = ChatVisuals.PushSettingsComboColors();
        using var style = ChatVisuals.PushSettingsComboStyle(comboHeight, scale);
        using var combo = ChatVisuals.BeginSettingsCombo("##ai-translation-language", preview, pos, width);
        if (!combo || !interactive)
            return;

        if (ChatVisuals.SelectSettingsOption(followPlugin, "follow-plugin", selected is null, comboHeight))
            config.LlmTranslationLanguage = null;
        foreach (var (code, name) in Loc.Languages)
        {
            if (ChatVisuals.SelectSettingsOption(name, code, selected == code, comboHeight))
                config.LlmTranslationLanguage = code;
        }

        if (config.LlmTranslationLanguage != selected)
            saveConfig();
    }

    private static bool DrawSettingsTab(
        ImDrawListPtr draw,
        string id,
        string label,
        Vector2 pos,
        Vector2 size,
        bool active,
        float uiScale,
        float fontScale,
        float alpha,
        bool interactive)
    {
        var clicked = false;
        var hovered = false;
        var pressed = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(pos);
            clicked = ImGui.InvisibleButton(id, size);
            hovered = ImGui.IsItemHovered();
            pressed = ImGui.IsItemActive();
            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (!active && (hovered || pressed))
        {
            var tabBg = pressed
                ? ChatVisuals.ScaleAlpha(ChatVisuals.ModalButtonHover, alpha)
                : ChatVisuals.ScaleAlpha(ChatVisuals.ListHover, alpha * 0.65f);
            draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(tabBg));
        }

        var fontSize = ImGui.GetFontSize() * 0.98f * fontScale;
        var visibleLabel = ChatVisuals.TruncateSingleLine(label, fontSize, MathF.Max(20f * uiScale, size.X - 10f * uiScale));
        var textSize = ChatVisuals.CalcTextSize(visibleLabel, fontSize);
        var textPos = pos + (size - textSize) * 0.5f;
        var textCol = ChatVisuals.Color(ChatVisuals.ScaleAlpha(active || pressed ? ChatVisuals.Ink : ChatVisuals.Muted, alpha));
        if (active)
            ChatVisuals.AddBoldText(draw, textPos, fontSize, textCol, visibleLabel, uiScale);
        else
            ChatVisuals.AddText(draw, textPos, fontSize, textCol, visibleLabel);

        if (active)
        {
            var barH = MathF.Max(2f, 2.5f * uiScale);
            var insetX = MathF.Min(18f * uiScale, size.X * 0.18f);
            draw.AddRectFilled(
                new Vector2(pos.X + insetX, pos.Y + size.Y - barH),
                new Vector2(pos.X + size.X - insetX, pos.Y + size.Y),
                ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.HeaderPink, alpha)));
        }

        return clicked;
    }

    private static bool DrawSettingToggleRow(
        ImDrawListPtr draw,
        string id,
        string label,
        Vector2 pos,
        float width,
        float height,
        bool value,
        ref float anim,
        float uiScale,
        float fontScale,
        float alpha,
        bool interactive)
    {
        var switchWidth = 44f * uiScale;
        var switchHeight = 24f * uiScale;
        var labelFontSize = ImGui.GetFontSize() * 1.02f * fontScale;
        var maxLabelWidth = MathF.Max(40f * uiScale, width - switchWidth - 14f * uiScale);
        var labelText = ChatVisuals.TruncateSingleLine(label, labelFontSize, maxLabelWidth);
        var labelSize = ChatVisuals.CalcTextSize(labelText, labelFontSize);
        var labelPos = new Vector2(pos.X, pos.Y + (height - labelSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, labelPos, labelFontSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha)), labelText, uiScale);

        var switchPos = new Vector2(pos.X + width - switchWidth, pos.Y + (height - switchHeight) * 0.5f);
        return ChatVisuals.DrawToggleSwitch(draw, id, switchPos, value, ref anim, uiScale, alpha, interactive);
    }

    private void SaveAiConfig()
    {
        saveConfig();
        aiReply.ResetConnectionTest();
        if (!config.IsAiReplyConfigured)
            aiReply.ClearAll();
    }

    private enum SettingsTab
    {
        General,
        AiSettings,
        Advanced,
    }
}
