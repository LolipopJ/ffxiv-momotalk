using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed class MomoTalkWindow : Window
{
    private const float NarrowThreshold = 640f;
    private const string LogoResourceName = "FFXIVMomoTalk.Assets.momo-logo.png";
    private const string CoverResourceName = "FFXIVMomoTalk.Assets.momotalk-cover.png";
    private const float IntroSolidDuration = 0.62f;
    private const float IntroTotalDuration = 0.84f;
    private const float SettingsTransitionDuration = 0.20f;

    private readonly SocialDirectory directory;
    private readonly HistoryStore history;
    private readonly ChatCapture capture;
    private readonly ChatSender sender;
    private readonly AvatarCache avatars;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly ISharedImmediateTexture coverTexture;
    private readonly Stopwatch introTimer = new();
    private readonly Dictionary<ConversationKey, Draft> drafts = new();
    private readonly Dictionary<ConversationKey, string> notes = new();

    private PlayerSession? session;
    private NavTab activeTab = NavTab.Chats;
    private PlayerIdentity? inspectedFriend;
    private ConversationDescriptor? inspectedGroup;
    private ConversationDescriptor? selected;
    private ConversationHistory? conversation;
    private IReadOnlyList<ConversationSummary> summaries = [];
    private IReadOnlyList<ConversationSummary> rows = [];
    private Task<IReadOnlyList<ConversationSummary>>? summaryQuery;
    private Task<IReadOnlyDictionary<ConversationKey, string>>? notesQuery;
    private long summaryRevision = -1;
    private long notesRevision = -1;
    private bool summaryFailed;
    private bool sortByRecent = true;
    private bool sortAscending;
    private bool narrowShowingDetail;
    private string contactsSearch = string.Empty;
    private string chatsSearch = string.Empty;
    private string groupsSearch = string.Empty;
    private float firstMessageY;
    private SendWarning? unassignedWarning;
    private Vector2 dragWindowOrigin;
    private Vector2 dragMouseOrigin;
    private bool playingIntro;
    private bool introWaitingCover;
    private bool suppressNextIntro;
    private bool settingsOpen;
    private bool settingsOpenedThisFrame;
    private float settingsAnim;
    private float entrySwitchAnim;
    private float introSwitchAnim;
    private float selfAvatarSwitchAnim;
    private StaticModalMode staticModalMode;
    private bool staticModalOpenedThisFrame;
    private bool focusStaticNameInput;
    private ActiveParty? createStaticParty;
    private string? renameStaticKey;
    private string staticNameDraft = string.Empty;
    private string? staticModalError;
    private ConversationKey? editingNoteFor;
    private string noteDraft = string.Empty;
    private bool focusNoteInput;
    private PlayerIdentity? contextFriend;
    private ConversationDescriptor? contextConversation;
    private StaticGroup? contextStatic;
    private string? contextMessageBody;
    private ConversationKey? inputFocusedConversation;
    private bool inputFocused;
    private bool wasShowingTyping;

    public MomoTalkWindow(
        SocialDirectory directory,
        HistoryStore history,
        ChatCapture capture,
        ChatSender sender,
        AvatarCache avatars,
        Configuration config,
        Action saveConfig,
        ITextureProvider textures) : base("FFXIV MomoTalk")
    {
        this.directory = directory;
        this.history = history;
        this.capture = capture;
        this.sender = sender;
        this.avatars = avatars;
        this.config = config;
        this.saveConfig = saveConfig;
        logoTexture = textures.GetFromManifestResource(typeof(Plugin).Assembly, LogoResourceName);
        coverTexture = textures.GetFromManifestResource(typeof(Plugin).Assembly, CoverResourceName);
        entrySwitchAnim = config.ShowFloatingEntry ? 1f : 0f;
        introSwitchAnim = config.ShowIntroAnimation ? 1f : 0f;
        selfAvatarSwitchAnim = config.ShowSelfAvatar ? 1f : 0f;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(840, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 360),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public void PreloadAssets()
    {
        _ = logoTexture.GetWrapOrDefault();
        _ = coverTexture.GetWrapOrDefault();
    }

    public void OpenSettings()
    {
        CommitActiveNote();
        OpenImmediate();
        CloseStaticModal();
        settingsOpen = true;
        settingsOpenedThisFrame = true;
    }

    public void OpenCreateStaticModal(ActiveParty party)
    {
        CommitActiveNote();
        OpenImmediate();
        settingsOpen = false;
        settingsAnim = 0f;
        SyncSession(directory.Current.Session);
        if (session is null)
            return;

        staticModalMode = StaticModalMode.Create;
        createStaticParty = party;
        renameStaticKey = null;
        staticNameDraft = string.Empty;
        staticModalError = null;
        staticModalOpenedThisFrame = true;
        focusStaticNameInput = true;
    }

    public void OpenStaticConversation(StaticGroup group)
    {
        CommitActiveNote();
        OpenImmediate();
        settingsOpen = false;
        settingsAnim = 0f;
        CloseStaticModal();
        SyncSession(directory.Current.Session);
        if (session is null)
            return;

        OpenConversation(group.ToDescriptor());
    }

    private void OpenImmediate()
    {
        if (!IsOpen)
        {
            suppressNextIntro = true;
            IsOpen = true;
        }

        BringToFront();
        playingIntro = false;
        introWaitingCover = false;
        introTimer.Reset();
    }

    private void OpenRenameStaticModal(string stableKey, string currentName)
    {
        CommitActiveNote();
        settingsOpen = false;
        settingsAnim = 0f;
        staticModalMode = StaticModalMode.Rename;
        createStaticParty = null;
        renameStaticKey = stableKey;
        staticNameDraft = currentName;
        staticModalError = null;
        staticModalOpenedThisFrame = true;
        focusStaticNameInput = true;
    }

    private void CloseStaticModal()
    {
        staticModalMode = StaticModalMode.None;
        staticModalOpenedThisFrame = false;
        focusStaticNameInput = false;
        createStaticParty = null;
        renameStaticKey = null;
        staticNameDraft = string.Empty;
        staticModalError = null;
    }

    public override void OnOpen()
    {
        if (suppressNextIntro)
        {
            suppressNextIntro = false;
            playingIntro = false;
            introWaitingCover = false;
            introTimer.Reset();
            return;
        }

        if (config.ShowIntroAnimation)
        {
            playingIntro = true;
            introWaitingCover = coverTexture.GetWrapOrDefault() is null;
            introTimer.Restart();
        }
    }

    public override void OnClose()
    {
        playingIntro = false;
        introWaitingCover = false;
        introTimer.Reset();
        settingsOpen = false;
        settingsOpenedThisFrame = false;
        settingsAnim = 0f;
        CloseStaticModal();
        editingNoteFor = null;
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
    }

    public override void PreDraw()
    {
        RespectCloseHotkey = !settingsOpen && staticModalMode == StaticModalMode.None && editingNoteFor is null;
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleColor(ImGuiCol.Text, ChatVisuals.Ink);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, ChatVisuals.Placeholder);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ChatVisuals.White);
        ImGui.PushStyleColor(ImGuiCol.Border, ChatVisuals.WindowBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, ChatVisuals.Divider);
        ImGui.PushStyleColor(ImGuiCol.Header, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ChatVisuals.ListHover);
        ImGui.PushStyleColor(ImGuiCol.Button, ChatVisuals.SystemBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ChatVisuals.InputBorder);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ChatVisuals.BadgeBorder);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.48f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.68f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, ChatVisuals.WithAlpha(ChatVisuals.RailBg, 0.70f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, MathF.Max(1f, scale));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f * scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(6);
        ImGui.PopStyleColor(23);
    }

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();

        var snapshot = directory.Current;
        SyncSession(snapshot.Session);
        ApplySendWarnings();
        if (session is not null)
        {
            SyncLiveState(snapshot);
            UpdateSummaries(snapshot);
            UpdateNotes();
        }

        if (playingIntro && introWaitingCover)
        {
            if (coverTexture.GetWrapOrDefault() is not null || introTimer.Elapsed.TotalSeconds >= 0.5)
            {
                introWaitingCover = false;
                introTimer.Restart();
            }
            else
            {
                DrawIntroCover(draw, winPos, winSize, elapsed: 0f, alpha: 1f, scale);
                return;
            }
        }

        var introElapsed = playingIntro ? (float)introTimer.Elapsed.TotalSeconds : IntroTotalDuration;
        if (playingIntro && introElapsed >= IntroTotalDuration)
        {
            playingIntro = false;
            introTimer.Reset();
        }

        if (playingIntro && introElapsed < IntroSolidDuration)
        {
            DrawIntroCover(draw, winPos, winSize, introElapsed, alpha: 1f, scale);
            return;
        }

        var headerHeight = 46f * scale;
        DrawHeader(draw, winPos, winSize.X, headerHeight, scale);

        var bodyPos = winPos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(winSize.X, MathF.Max(1f, winSize.Y - headerHeight));

        if (session is null)
        {
            DrawLoggedOut(draw, bodyPos, bodySize, scale);
        }
        else
        {
            var narrow = bodySize.X < NarrowThreshold * scale;
            if (narrow)
                DrawNarrowLayout(draw, snapshot, bodyPos, bodySize, scale);
            else
                DrawWideLayout(draw, snapshot, bodyPos, bodySize, scale);
        }

        if (playingIntro)
        {
            var fadeT = Math.Clamp((introElapsed - IntroSolidDuration) / (IntroTotalDuration - IntroSolidDuration), 0f, 1f);
            var smoothFade = fadeT * fadeT * (3f - 2f * fadeT);
            DrawIntroOverlay(winPos, winSize, introElapsed, 1f - smoothFade, scale);
            return;
        }

        var dt = MathF.Max(0.0001f, ImGui.GetIO().DeltaTime);
        var settingsTarget = settingsOpen ? 1f : 0f;
        var settingsStep = dt / SettingsTransitionDuration;
        settingsAnim = settingsAnim < settingsTarget
            ? MathF.Min(settingsTarget, settingsAnim + settingsStep)
            : MathF.Max(settingsTarget, settingsAnim - settingsStep);

        if (settingsOpen || settingsAnim > 0.001f)
            DrawSettingsOverlay(winPos, winSize, scale);
        else if (session is not null && staticModalMode != StaticModalMode.None)
            DrawStaticModalOverlay(winPos, winSize, scale);
    }

    private void DrawHeader(ImDrawListPtr draw, Vector2 winPos, float width, float height, float scale)
    {
        var rounding = 10f * scale;
        draw.AddRectFilled(winPos, winPos + new Vector2(width, height), ChatVisuals.Color(ChatVisuals.HeaderPink),
            rounding, ImDrawFlags.RoundCornersTop);

        var btnSize = 30f * scale;
        var btnY = winPos.Y + (height - btnSize) * 0.5f;
        var closePos = new Vector2(winPos.X + width - btnSize - 10f * scale, btnY);
        var settingsPos = new Vector2(closePos.X - btnSize - 4f * scale, btnY);
        var dragWidth = MathF.Max(1f, settingsPos.X - winPos.X - 6f * scale);

        ImGui.SetCursorScreenPos(winPos);
        ImGui.InvisibleButton("##header-drag", new Vector2(dragWidth, height));
        if (ImGui.IsItemActivated())
        {
            dragWindowOrigin = ImGui.GetWindowPos();
            dragMouseOrigin = ImGui.GetIO().MousePos;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            ImGui.SetWindowPos(dragWindowOrigin + (ImGui.GetIO().MousePos - dragMouseOrigin));

        var logoSize = 26f * scale;
        var logoPos = new Vector2(winPos.X + 12f * scale, winPos.Y + (height - logoSize) * 0.5f);
        if (logoTexture.GetWrapOrDefault() is { } logoWrap)
            draw.AddImage(logoWrap.Handle, logoPos, logoPos + new Vector2(logoSize));

        var titleFontSize = ImGui.GetFontSize() * 1.42f;
        var titleSize = ChatVisuals.CalcTextSize("MomoTalk", titleFontSize);
        var titlePos = new Vector2(logoPos.X + logoSize + 6f * scale, winPos.Y + (height - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize, ChatVisuals.Color(ChatVisuals.White), "MomoTalk", scale);

        ImGui.SetCursorScreenPos(settingsPos);
        if (ImGui.InvisibleButton("##window-settings", new Vector2(btnSize)))
        {
            CommitActiveNote();
            CloseStaticModal();
            settingsOpen = !settingsOpen;
            settingsOpenedThisFrame = settingsOpen;
        }

        var settingsHovered = ImGui.IsItemHovered();
        var settingsActive = ImGui.IsItemActive();
        if (settingsOpen || settingsHovered || settingsActive)
        {
            var bg = settingsActive || settingsOpen ? ChatVisuals.HeaderPinkActive : ChatVisuals.HeaderPinkHover;
            draw.AddRectFilled(settingsPos, settingsPos + new Vector2(btnSize), ChatVisuals.Color(bg), 6f * scale);
        }

        ChatVisuals.DrawSettingsIcon(draw, settingsPos + new Vector2(btnSize * 0.5f), 17f * scale,
            ChatVisuals.Color(ChatVisuals.White), scale);
        if (settingsHovered)
            ImGui.SetTooltip("Settings");

        ImGui.SetCursorScreenPos(closePos);
        if (ImGui.InvisibleButton("##window-close", new Vector2(btnSize)))
            IsOpen = false;

        var closeHovered = ImGui.IsItemHovered();
        var closeActive = ImGui.IsItemActive();
        if (closeHovered || closeActive)
        {
            var bg = closeActive ? ChatVisuals.HeaderPinkActive : ChatVisuals.HeaderPinkHover;
            draw.AddRectFilled(closePos, closePos + new Vector2(btnSize), ChatVisuals.Color(bg), 6f * scale);
        }

        ChatVisuals.DrawCloseIcon(draw, closePos + new Vector2(btnSize * 0.5f), 14f * scale,
            ChatVisuals.Color(ChatVisuals.White), MathF.Max(1.6f, 1.8f * scale));
    }

    private void DrawIntroOverlay(Vector2 winPos, Vector2 winSize, float elapsed, float alpha, float scale)
    {
        ImGui.SetCursorScreenPos(winPos);
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var overlay = ImRaii.Child("##intro-overlay", winSize, false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!overlay)
            return;

        DrawIntroCover(ImGui.GetWindowDrawList(), winPos, winSize, elapsed, alpha, scale);
    }

    private void DrawIntroCover(ImDrawListPtr draw, Vector2 winPos, Vector2 winSize, float elapsed, float alpha, float scale)
    {
        var winMin = winPos;
        var winMax = winPos + winSize;
        var rounding = 10f * scale;
        var pink = ChatVisuals.Color(ChatVisuals.WithAlpha(ChatVisuals.CoverPink, alpha));

        if (coverTexture.GetWrapOrDefault() is not { } wrap)
        {
            draw.AddRectFilled(winMin, winMax, pink, rounding);
            return;
        }

        EvaluateIntroBounce(elapsed, scale, out var scaleX, out var scaleY, out var offsetY);
        var baseSize = MathF.Min(winSize.X, winSize.Y) * 0.80f;
        var halfSize = new Vector2(baseSize * scaleX, baseSize * scaleY) * 0.5f;
        var center = winPos + winSize * 0.5f + new Vector2(0, offsetY);
        var rawMin = center - halfSize;
        var rawMax = center + halfSize;
        var rawSize = rawMax - rawMin;

        var safeTop = MathF.Ceiling(winMin.Y + rounding);
        var safeBottom = MathF.Floor(winMax.Y - rounding);
        var safeLeft = MathF.Ceiling(winMin.X);
        var safeRight = MathF.Floor(winMax.X);

        if (safeBottom <= safeTop || safeRight <= safeLeft || rawSize.X <= 0f || rawSize.Y <= 0f)
        {
            draw.AddRectFilled(winMin, winMax, pink, rounding);
            return;
        }

        var slotMin = new Vector2(
            Math.Clamp(MathF.Round(rawMin.X), safeLeft, safeRight),
            Math.Clamp(MathF.Round(rawMin.Y), safeTop, safeBottom));
        var slotMax = new Vector2(
            Math.Clamp(MathF.Round(rawMax.X), slotMin.X, safeRight),
            Math.Clamp(MathF.Round(rawMax.Y), slotMin.Y, safeBottom));

        draw.PushClipRect(winMin, new Vector2(winMax.X, slotMin.Y), true);
        draw.AddRectFilled(winMin, new Vector2(winMax.X, slotMin.Y + 2f), pink, rounding, ImDrawFlags.RoundCornersTop);
        draw.PopClipRect();

        draw.PushClipRect(new Vector2(winMin.X, slotMax.Y), winMax, true);
        draw.AddRectFilled(new Vector2(winMin.X, slotMax.Y - 2f), winMax, pink, rounding, ImDrawFlags.RoundCornersBottom);
        draw.PopClipRect();

        if (slotMin.X > winMin.X && slotMax.Y > slotMin.Y)
            draw.AddRectFilled(new Vector2(winMin.X, slotMin.Y), new Vector2(slotMin.X, slotMax.Y), pink);

        if (slotMax.X < winMax.X && slotMax.Y > slotMin.Y)
            draw.AddRectFilled(new Vector2(slotMax.X, slotMin.Y), new Vector2(winMax.X, slotMax.Y), pink);

        if (slotMax.X > slotMin.X && slotMax.Y > slotMin.Y)
        {
            var uvMin = new Vector2((slotMin.X - rawMin.X) / rawSize.X, (slotMin.Y - rawMin.Y) / rawSize.Y);
            var uvMax = new Vector2((slotMax.X - rawMin.X) / rawSize.X, (slotMax.Y - rawMin.Y) / rawSize.Y);
            draw.AddImage(wrap.Handle, slotMin, slotMax, uvMin, uvMax,
                ChatVisuals.Color(new Vector4(1f, 1f, 1f, alpha)));
        }
    }

    private static void EvaluateIntroBounce(float t, float scale, out float scaleX, out float scaleY, out float offsetY)
    {
        if (t < 0.11f)
        {
            var u = EaseOutQuad(t / 0.11f);
            scaleX = Lerp(0.84f, 0.96f, u);
            scaleY = Lerp(0.84f, 1.06f, u);
            offsetY = Lerp(6f, -20f, u) * scale;
        }
        else if (t < 0.22f)
        {
            var u = EaseInQuad((t - 0.11f) / 0.11f);
            scaleX = Lerp(0.96f, 1.07f, u);
            scaleY = Lerp(1.06f, 0.93f, u);
            offsetY = Lerp(-20f, 0f, u) * scale;
        }
        else if (t < 0.34f)
        {
            var u = EaseOutQuad((t - 0.22f) / 0.12f);
            scaleX = Lerp(1.07f, 0.97f, u);
            scaleY = Lerp(0.93f, 1.04f, u);
            offsetY = Lerp(0f, -14f, u) * scale;
        }
        else if (t < 0.46f)
        {
            var u = EaseInQuad((t - 0.34f) / 0.12f);
            scaleX = Lerp(0.97f, 1.03f, u);
            scaleY = Lerp(1.04f, 0.97f, u);
            offsetY = Lerp(-14f, 0f, u) * scale;
        }
        else if (t < 0.56f)
        {
            var u = EaseOutQuad((t - 0.46f) / 0.10f);
            scaleX = Lerp(1.03f, 1f, u);
            scaleY = Lerp(0.97f, 1f, u);
            offsetY = 0f;
        }
        else if (t < IntroSolidDuration)
        {
            scaleX = 1f;
            scaleY = 1f;
            offsetY = 0f;
        }
        else
        {
            var u = Math.Clamp((t - IntroSolidDuration) / (IntroTotalDuration - IntroSolidDuration), 0f, 1f);
            var zoom = Lerp(1f, 1.04f, u);
            scaleX = zoom;
            scaleY = zoom;
            offsetY = 0f;
        }
    }

    private static float EaseOutQuad(float u) => 1f - (1f - u) * (1f - u);

    private static float EaseInQuad(float u) => u * u;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void DrawSettingsOverlay(Vector2 winPos, Vector2 winSize, float scale)
    {
        var justOpened = settingsOpenedThisFrame;
        settingsOpenedThisFrame = false;

        var interactive = settingsOpen;
        if (interactive && !justOpened && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            settingsOpen = false;
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

        var cardWidth = MathF.Min(360f * scale, MathF.Max(260f * scale, winSize.X - 36f * scale)) * cardScale;
        var headerHeight = 50f * uiScale;
        var padX = 22f * uiScale;
        var padTop = 20f * uiScale;
        var padBottom = 22f * uiScale;
        var rowHeight = 34f * uiScale;
        var rowGap = 14f * uiScale;

        const string configErrorText = "Existing config file could not be read and was preserved.";
        ChatVisuals.BannerLayout? configErrorLayout = config.PreserveExistingFile
            ? ChatVisuals.MeasureSystemBanner(configErrorText, MathF.Max(80f * uiScale, cardWidth - padX * 2f), uiScale)
            : null;
        var warnHeight = configErrorLayout is { } layout ? layout.Height + 10f * uiScale : 0f;

        var cardHeight = headerHeight + padTop + warnHeight + rowHeight * 3f + rowGap * 2f + padBottom;
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

        var titleFontSize = ImGui.GetFontSize() * 1.22f * cardScale;
        var titleSize = ChatVisuals.CalcTextSize("Settings", titleFontSize);
        var titlePos = new Vector2(cardPos.X + (cardWidth - titleSize.X) * 0.5f,
            cardPos.Y + (headerHeight - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, eased)), "Settings", uiScale);

        var closeSize = 28f * uiScale;
        var closePos = new Vector2(cardPos.X + cardWidth - 14f * uiScale - closeSize,
            cardPos.Y + (headerHeight - closeSize) * 0.5f);
        var closeHovered = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(closePos);
            if (ImGui.InvisibleButton("##settings-close", new Vector2(closeSize)))
                settingsOpen = false;
            closeHovered = ImGui.IsItemHovered();
        }

        if (closeHovered)
            draw.AddRectFilled(closePos, closePos + new Vector2(closeSize),
                ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ListHover, eased)), 6f * uiScale);

        ChatVisuals.DrawCloseIcon(draw, closePos + new Vector2(closeSize * 0.5f), 14f * uiScale,
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(closeHovered ? ChatVisuals.Ink : ChatVisuals.Muted, eased)),
            MathF.Max(1.6f, 1.8f * uiScale));

        var dividerY = cardPos.Y + headerHeight;
        draw.AddLine(new Vector2(cardPos.X, dividerY), new Vector2(cardPos.X + cardWidth, dividerY),
            ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.ModalDivider, eased)), MathF.Max(1f, uiScale));

        var cursorY = dividerY + padTop;
        if (configErrorLayout is { } errorLayout)
        {
            ChatVisuals.DrawSystemBanner(draw, new Vector2(cardPos.X, cursorY), cardWidth, configErrorText,
                isAlert: true, uiScale, errorLayout, eased);
            cursorY += errorLayout.Height + 10f * uiScale;
        }

        if (DrawSettingToggleRow(draw, "##setting-floating-entry", "Show Floating Entry",
            new Vector2(cardPos.X + padX, cursorY), cardWidth - padX * 2f, rowHeight,
            config.ShowFloatingEntry, ref entrySwitchAnim, uiScale, cardScale, eased, interactive))
        {
            config.ShowFloatingEntry = !config.ShowFloatingEntry;
            saveConfig();
        }

        cursorY += rowHeight + rowGap;
        if (DrawSettingToggleRow(draw, "##setting-intro-anim", "Show Intro Animation",
            new Vector2(cardPos.X + padX, cursorY), cardWidth - padX * 2f, rowHeight,
            config.ShowIntroAnimation, ref introSwitchAnim, uiScale, cardScale, eased, interactive))
        {
            config.ShowIntroAnimation = !config.ShowIntroAnimation;
            saveConfig();
        }

        cursorY += rowHeight + rowGap;
        if (DrawSettingToggleRow(draw, "##setting-self-avatar", "Show Self Avatar",
            new Vector2(cardPos.X + padX, cursorY), cardWidth - padX * 2f, rowHeight,
            config.ShowSelfAvatar, ref selfAvatarSwitchAnim, uiScale, cardScale, eased, interactive))
        {
            config.ShowSelfAvatar = !config.ShowSelfAvatar;
            saveConfig();
        }

        var inCard = ImGui.IsMouseHoveringRect(cardPos, cardPos + cardSize);
        if (interactive && !justOpened && !inCard && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            settingsOpen = false;
    }

    private void DrawStaticModalOverlay(Vector2 winPos, Vector2 winSize, float scale)
    {
        var justOpened = staticModalOpenedThisFrame;
        staticModalOpenedThisFrame = false;

        if (!justOpened && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CloseStaticModal();
            return;
        }

        var isCreate = staticModalMode == StaticModalMode.Create;
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
        ChatVisuals.BannerLayout? errorBanner = staticModalError is not null
            ? ChatVisuals.MeasureSystemBanner(staticModalError, innerW, scale)
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

        var title = isCreate ? "Create Static Group" : "Rename Group";
        var titleFontSize = ImGui.GetFontSize() * 1.18f;
        var titleSize = ChatVisuals.CalcTextSize(title, titleFontSize);
        var titlePos = new Vector2(cardPos.X + (cardWidth - titleSize.X) * 0.5f,
            cardPos.Y + (headerHeight - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize, ChatVisuals.Color(ChatVisuals.Ink), title, scale);

        var closeSize = 28f * scale;
        var closePos = new Vector2(cardPos.X + cardWidth - 14f * scale - closeSize,
            cardPos.Y + (headerHeight - closeSize) * 0.5f);
        ImGui.SetCursorScreenPos(closePos);
        if (ImGui.InvisibleButton("##static-modal-close", new Vector2(closeSize)))
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
                $"Party Members ({party.Members.Count})", scale);
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
            "Group Name", scale);
        cursorY += labelHeight;

        var inputPos = new Vector2(leftX, cursorY);
        var inputSize = new Vector2(innerW, inputHeight);
        var inputRounding = 8f * scale;
        draw.AddRectFilled(inputPos, inputPos + inputSize, ChatVisuals.Color(ChatVisuals.White), inputRounding);

        var inputPadY = MathF.Max(2f, (inputHeight - ImGui.GetFontSize()) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(inputPos.X + 10f * scale, inputPos.Y));
        var enter = false;
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
            enter = ImGui.InputTextWithHint("##static-name-input", "Enter group name...", ref staticNameDraft, 64,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var inputActive = ImGui.IsItemActive();
        draw.AddRect(inputPos, inputPos + inputSize,
            ChatVisuals.Color(inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder),
            inputRounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));
        cursorY += inputHeight;

        if (staticModalError is not null && errorBanner is { } errLayout)
        {
            cursorY += 6f * scale;
            ChatVisuals.DrawSystemBanner(draw, new Vector2(cardPos.X, cursorY), cardWidth, staticModalError,
                isAlert: true, scale, errLayout);
            cursorY += errLayout.Height;
        }

        cursorY += buttonsTopGap;
        var btnGap = 10f * scale;
        var btnWidth = (innerW - btnGap) * 0.5f;
        var cancelPos = new Vector2(leftX, cursorY);
        var confirmPos = new Vector2(leftX + btnWidth + btnGap, cursorY);
        var canSubmit = !string.IsNullOrWhiteSpace(staticNameDraft);

        if (ChatVisuals.DrawModalButton(draw, "##static-modal-cancel", "Cancel", cancelPos,
            new Vector2(btnWidth, buttonsHeight), primary: false, enabled: true, scale))
        {
            CloseStaticModal();
            return;
        }

        var confirmClicked = ChatVisuals.DrawModalButton(draw, "##static-modal-confirm",
            isCreate ? "Create" : "Save", confirmPos, new Vector2(btnWidth, buttonsHeight),
            primary: true, enabled: canSubmit, scale);

        if (confirmClicked || enter)
        {
            if (!canSubmit)
            {
                staticModalError = "Enter a group name.";
            }
            else if (isCreate && createStaticParty is { } partyToSave)
            {
                var created = directory.CreateStatic(session!, partyToSave, staticNameDraft);
                CloseStaticModal();
                if (created is not null)
                    OpenConversation(created.ToDescriptor());
            }
            else if (!isCreate && renameStaticKey is { } keyToRename)
            {
                var trimmed = staticNameDraft.Trim();
                if (directory.RenameStatic(session!, keyToRename, trimmed))
                {
                    var renamedKey = ConversationKey.Static(keyToRename);
                    if (selected?.Key == renamedKey)
                        selected = selected with { DisplayName = trimmed };
                    if (inspectedGroup?.Key == renamedKey)
                        inspectedGroup = inspectedGroup with { DisplayName = trimmed };
                }

                CloseStaticModal();
            }

            return;
        }

        var inCard = ImGui.IsMouseHoveringRect(cardPos, cardPos + cardSize);
        if (!justOpened && !inCard && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
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

    private static void DrawLoggedOut(ImDrawListPtr draw, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        draw.AddRectFilled(bodyPos, bodyPos + bodySize, ChatVisuals.Color(ChatVisuals.White), 10f * scale,
            ImDrawFlags.RoundCornersBottom);
        const string message = "Log in to view your conversations.";
        var fontSize = ImGui.GetFontSize();
        var maxTextWidth = MathF.Max(60f * scale, bodySize.X - 48f * scale);
        var textSize = ChatVisuals.CalcTextSize(message, fontSize, maxTextWidth);
        var pos = bodyPos + (bodySize - textSize) * 0.5f;
        ChatVisuals.AddText(draw, pos, fontSize, ChatVisuals.Color(ChatVisuals.Muted), message, maxTextWidth);
    }

    private void DrawWideLayout(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        var railWidth = 54f * scale;
        var remaining = MathF.Max(1f, bodySize.X - railWidth);
        var listWidth = Math.Clamp(remaining * 0.42f, 236f * scale, 340f * scale);
        if (listWidth > remaining * 0.58f)
            listWidth = remaining * 0.58f;

        var rightWidth = MathF.Max(1f, remaining - listWidth);

        DrawLeftNavRail(draw, bodyPos, new Vector2(railWidth, bodySize.Y), scale);

        var listPos = bodyPos + new Vector2(railWidth, 0);
        DrawListPane(draw, snapshot, listPos, new Vector2(listWidth, bodySize.Y), scale);

        var dividerX = listPos.X + listWidth;
        draw.AddLine(new Vector2(dividerX, bodyPos.Y), new Vector2(dividerX, bodyPos.Y + bodySize.Y),
            ChatVisuals.Color(ChatVisuals.Divider), MathF.Max(1f, scale));

        var rightPos = new Vector2(dividerX, bodyPos.Y);
        var rightSize = new Vector2(rightWidth, bodySize.Y);
        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactProfile(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
            case NavTab.Groups:
                DrawGroupDetail(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
            default:
                DrawConversationPane(draw, snapshot, rightPos, rightSize, narrow: false, scale);
                break;
        }
    }

    private void DrawNarrowLayout(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        if (!narrowShowingDetail)
        {
            var navHeight = 48f * scale;
            var listHeight = MathF.Max(1f, bodySize.Y - navHeight);
            DrawListPane(draw, snapshot, bodyPos, new Vector2(bodySize.X, listHeight), scale);
            DrawBottomNavBar(draw, bodyPos + new Vector2(0, listHeight), new Vector2(bodySize.X, navHeight), scale);
            return;
        }

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactProfile(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
            case NavTab.Groups:
                DrawGroupDetail(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
            default:
                DrawConversationPane(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
                break;
        }
    }

    private void DrawLeftNavRail(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottomLeft);

        var tabHeight = 54f * scale;
        DrawNavTabButton(draw, "##nav-contacts", NavTab.Contacts, pos, new Vector2(size.X, tabHeight),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-groups", NavTab.Groups, pos + new Vector2(0, tabHeight),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-chats", NavTab.Chats, pos + new Vector2(0, tabHeight * 2f),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
    }

    private void DrawBottomNavBar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottom);

        var third = MathF.Floor(size.X / 3f);
        DrawNavTabButton(draw, "##bnav-contacts", NavTab.Contacts, pos, new Vector2(third, size.Y),
            ImDrawFlags.RoundCornersBottomLeft, scale);
        DrawNavTabButton(draw, "##bnav-groups", NavTab.Groups, pos + new Vector2(third, 0), new Vector2(third, size.Y),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##bnav-chats", NavTab.Chats, pos + new Vector2(third * 2f, 0),
            new Vector2(size.X - third * 2f, size.Y), ImDrawFlags.RoundCornersBottomRight, scale);
    }

    private void DrawNavTabButton(ImDrawListPtr draw, string id, NavTab tab, Vector2 pos, Vector2 size,
        ImDrawFlags roundCorners, float scale)
    {
        ImGui.SetCursorScreenPos(pos);
        if (ImGui.InvisibleButton(id, size))
        {
            CommitActiveNote();
            activeTab = tab;
            narrowShowingDetail = false;
        }

        var selectedTab = activeTab == tab;
        var hovered = ImGui.IsItemHovered();
        var bg = selectedTab
            ? ChatVisuals.RailActive
            : hovered
                ? Vector4.Lerp(ChatVisuals.RailBg, ChatVisuals.RailActive, 0.55f)
                : ChatVisuals.RailBg;

        if (selectedTab || hovered)
        {
            var rounding = roundCorners == ImDrawFlags.None ? 0f : 10f * scale;
            draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(bg), rounding, roundCorners);
        }

        // Blend an opaque secondary color against bg instead of alpha transparency so overlapping icon primitives render uniformly.
        var iconStrength = selectedTab ? 1f : hovered ? 0.85f : 0.50f;
        var iconColor = ChatVisuals.Color(Vector4.Lerp(bg, ChatVisuals.White, iconStrength));
        var bgCol = ChatVisuals.Color(bg);
        var center = pos + size * 0.5f;
        var iconSize = 24f * scale;
        switch (tab)
        {
            case NavTab.Contacts:
                ChatVisuals.DrawContactsIcon(draw, center, iconSize, iconColor);
                break;
            case NavTab.Groups:
                ChatVisuals.DrawGroupsIcon(draw, center, iconSize, iconColor, bgCol);
                break;
            case NavTab.Chats:
                ChatVisuals.DrawChatIcon(draw, center, iconSize, iconColor, bgCol);
                break;
        }

        if (hovered)
        {
            ImGui.SetTooltip(tab switch
            {
                NavTab.Contacts => "Friends",
                NavTab.Groups => "Groups",
                NavTab.Chats => "Chats",
                _ => string.Empty,
            });
        }
    }

    private void DrawListPane(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.ListBg));

        var headerHeight = 52f * scale;
        DrawListToolbar(draw, snapshot, pos, new Vector2(size.X, headerHeight), scale);

        var bodyPos = pos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(size.X, MathF.Max(1f, size.Y - headerHeight));
        ImGui.SetCursorScreenPos(bodyPos);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var body = ImRaii.Child("##list-body", bodySize, false);
        if (!body)
            return;

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawContactsList(snapshot, scale);
                break;
            case NavTab.Groups:
                DrawGroupsList(snapshot, scale);
                break;
            default:
                DrawChatsList(snapshot, scale);
                break;
        }
    }

    private void DrawListToolbar(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        var padX = 10f * scale;
        var controlH = 32f * scale;
        var controlY = pos.Y + (size.Y - controlH) * 0.5f;

        var showSort = activeTab == NavTab.Chats;
        var sortWidth = showSort ? 74f * scale : 0f;
        var dirWidth = showSort ? 42f * scale : 0f;
        var gap = showSort ? 8f * scale : 0f;
        var dirGap = showSort ? 5f * scale : 0f;
        var searchWidth = MathF.Max(60f * scale, size.X - padX * 2f - sortWidth - dirWidth - gap - dirGap);

        switch (activeTab)
        {
            case NavTab.Contacts:
                DrawSearchPill(draw, "##contacts-search", "Search friends...", ref contactsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
            case NavTab.Groups:
                DrawSearchPill(draw, "##groups-search", "Search groups...", ref groupsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
            default:
                DrawSearchPill(draw, "##chats-search", "Search chats...", ref chatsSearch,
                    new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
                break;
        }

        if (showSort)
        {
            var sortPos = new Vector2(pos.X + padX + searchWidth + gap, controlY + 1f * scale);
            var sortSize = new Vector2(sortWidth, controlH - 2f * scale);
            if (ChatVisuals.SkewButton(draw, "##sort-toggle", sortByRecent ? "Recent" : "Name", sortPos, sortSize, scale))
            {
                sortByRecent = !sortByRecent;
                Reorder(snapshot);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortByRecent ? "Sorted by recent message (click for Name)" : "Sorted by name (click for Recent)");

            var dirPos = new Vector2(sortPos.X + sortWidth + dirGap, sortPos.Y);
            var dirSize = new Vector2(dirWidth, sortSize.Y);
            if (ChatVisuals.SkewSortOrderButton(draw, "##sort-order", sortAscending, dirPos, dirSize, scale))
            {
                sortAscending = !sortAscending;
                Reorder(snapshot);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortAscending ? "Ascending (click for Descending)" : "Descending (click for Ascending)");
        }

        var lineY = pos.Y + size.Y - MathF.Max(1f, scale);
        draw.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + size.X, lineY),
            ChatVisuals.Color(ChatVisuals.Divider), MathF.Max(1.5f, 1.8f * scale));
    }

    private static void DrawSearchPill(ImDrawListPtr draw, string id, string hint, ref string query, Vector2 pos, Vector2 size,
        float scale)
    {
        var rounding = size.Y * 0.5f;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), rounding);

        var iconCenter = new Vector2(pos.X + 15f * scale, pos.Y + size.Y * 0.5f);
        ChatVisuals.DrawSearchIcon(draw, iconCenter, 13f * scale, ChatVisuals.Color(ChatVisuals.Placeholder),
            MathF.Max(1.4f, 1.5f * scale));

        var inputLeft = pos.X + 26f * scale;
        var inputWidth = MathF.Max(20f * scale, size.X - 36f * scale);
        var padY = MathF.Max(2f, (size.Y - ImGui.GetFontSize()) * 0.5f);

        ImGui.SetCursorScreenPos(new Vector2(inputLeft, pos.Y));
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, padY)))
        {
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputTextWithHint(id, hint, ref query, 128);
        }

        var borderCol = ImGui.IsItemActive() ? ChatVisuals.InputFocus : ChatVisuals.InputBorder;
        draw.AddRect(pos, pos + size, ChatVisuals.Color(borderCol), rounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));
    }

    private void DrawContactsList(SocialSnapshot snapshot, float scale)
    {
        if (snapshot.Friends is null)
        {
            DrawListNotice("Open the in-game Friend List to load your friends.", scale);
            return;
        }

        if (snapshot.Friends.Count == 0)
        {
            DrawListNotice("Your in-game Friend List is empty.", scale);
            return;
        }

        var filtered = snapshot.Friends.Values
            .Where(friend => MatchesFriendSearch(friend, contactsSearch, notes.GetValueOrDefault(ConversationKey.Tell(friend.ContentId))))
            .OrderBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(friend => friend.WorldId)
            .ThenBy(friend => friend.ContentId)
            .ToArray();

        if (filtered.Length == 0)
        {
            DrawListNotice("No matching friends.", scale);
            return;
        }

        foreach (var friend in filtered)
        {
            var isSelected = inspectedFriend?.ContentId == friend.ContentId;
            var note = notes.GetValueOrDefault(ConversationKey.Tell(friend.ContentId));
            DrawListRow(ConversationDescriptor.FromFriend(friend), note, friend.World, isSelected,
                $"##contact-{friend.ContentId:X16}", out var clicked, out var doubleClicked, out var rightClicked, scale);
            if (rightClicked)
            {
                contextFriend = friend;
                ImGui.OpenPopup("##contact-row-menu");
            }
            else if (doubleClicked)
            {
                CommitActiveNote();
                OpenConversation(ConversationDescriptor.FromFriend(friend));
            }
            else if (clicked)
            {
                if (inspectedFriend?.ContentId != friend.ContentId)
                    CommitActiveNote();

                inspectedFriend = friend;
                narrowShowingDetail = true;
            }
        }

        using var contactMenu = ChatVisuals.BeginContextMenu("##contact-row-menu", scale);
        if (contactMenu.IsOpen && contextFriend is { } menuPeer)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-contact-lodestone", "Open Lodestone", 156f * scale, scale))
                Util.OpenLink(avatars.GetLodestoneUrl(menuPeer));
        }
    }

    private void DrawGroupsList(SocialSnapshot snapshot, float scale)
    {
        var items = BuildActiveGroupItems(snapshot);
        if (items.Count == 0)
        {
            DrawListNotice(
                "No groups yet. Join an FC or Linkshell, or right-click a party member in-game to create a Static.",
                scale);
            return;
        }

        var anyMatch = false;
        foreach (var item in items)
        {
            var note = notes.GetValueOrDefault(item.Conversation.Key);
            if (!MatchesGroupSearch(item, groupsSearch, note))
                continue;

            anyMatch = true;
            var isSelected = inspectedGroup?.Key == item.Conversation.Key;
            var subtitle = string.IsNullOrEmpty(note) ? item.Subtitle : note;
            DrawListRow(item.Conversation, subtitle, item.Tag, isSelected,
                $"##group-{item.Conversation.Key.StorageId}", out var clicked, out var doubleClicked, out var rightClicked, scale);

            if (rightClicked && item.Static is { } rightClickedStatic)
            {
                contextStatic = rightClickedStatic;
                ImGui.OpenPopup("##group-row-menu");
            }
            else if (doubleClicked)
            {
                CommitActiveNote();
                OpenConversation(item.Conversation);
            }
            else if (clicked)
            {
                if (inspectedGroup?.Key != item.Conversation.Key)
                    CommitActiveNote();

                inspectedGroup = item.Conversation;
                narrowShowingDetail = true;
            }
        }

        if (!anyMatch)
            DrawListNotice("No matching groups.", scale);

        using var groupMenu = ChatVisuals.BeginContextMenu("##group-row-menu", scale);
        if (groupMenu.IsOpen && contextStatic is { } menuStatic)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-group-rename", "Rename Group", 156f * scale, scale))
                OpenRenameStaticModal(menuStatic.StableKey, menuStatic.Name);
        }
    }

    private void DrawChatsList(SocialSnapshot snapshot, float scale)
    {
        if (summaryFailed)
        {
            ImGui.SetCursorPos(new Vector2(12f * scale, 10f * scale));
            ImGui.TextColored(ChatVisuals.AlertText, "Failed to read conversations.");
            ImGui.SetCursorPosX(12f * scale);
            if (ImGui.SmallButton("Retry"))
            {
                summaryFailed = false;
                summaryRevision = -1;
            }

            ImGui.Spacing();
        }

        if (rows.Count == 0)
        {
            DrawListNotice(summaryQuery is null
                ? "No conversations yet. Pick a friend or group to start chatting."
                : "Loading conversations...", scale);
            return;
        }

        var anyMatch = false;
        foreach (var row in rows)
        {
            var conversationDesc = snapshot.RefreshDescriptor(row.Conversation);
            var tag = snapshot.FormatChannelTag(conversationDesc);
            var note = notes.GetValueOrDefault(conversationDesc.Key);
            if (!MatchesChatSearch(snapshot, conversationDesc, tag, chatsSearch, note))
                continue;

            anyMatch = true;
            var isSelected = selected?.Key == conversationDesc.Key;
            var subtitle = row.Latest is { } latest
                ? FormatPreview(conversationDesc.Key, latest)
                : "No messages yet";
            DrawListRow(conversationDesc, subtitle, tag, isSelected, $"##chat-{conversationDesc.Key.StorageId}",
                out var clicked, out _, out var rightClicked, scale);
            if (rightClicked)
            {
                contextConversation = conversationDesc;
                ImGui.OpenPopup("##chat-row-menu");
            }
            else if (clicked)
            {
                OpenConversation(conversationDesc);
            }
        }

        if (!anyMatch)
            DrawListNotice("No matching conversations.", scale);

        using var chatMenu = ChatVisuals.BeginContextMenu("##chat-row-menu", scale);
        if (chatMenu.IsOpen && contextConversation is { } menuConv)
        {
            if (menuConv.ToTellPeer() is { } tellPeer)
            {
                if (ChatVisuals.ContextMenuItem("##ctx-chat-lodestone", "Open Lodestone", 156f * scale, scale))
                    Util.OpenLink(avatars.GetLodestoneUrl(tellPeer));
            }
            else if (menuConv.Key.Kind == ConversationKind.Static)
            {
                if (ChatVisuals.ContextMenuItem("##ctx-chat-rename", "Rename Group", 156f * scale, scale))
                    OpenRenameStaticModal(menuConv.Key.StableKey, menuConv.DisplayName);
            }

            if (ChatVisuals.ContextMenuItem("##ctx-chat-delete", "Delete Chat", 156f * scale, scale, danger: true))
                DeleteChatFromList(snapshot, menuConv);
        }
    }

    private static void DrawListNotice(string message, float scale)
    {
        var pad = 16f * scale;
        ImGui.SetCursorPos(new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X - pad);
        ImGui.TextColored(ChatVisuals.Muted, message);
        ImGui.PopTextWrapPos();
    }

    private void DrawListRow(ConversationDescriptor conversationDesc, string? subtitle, string trailingTag,
        bool isSelected, string id, out bool clicked, out bool doubleClicked, out bool rightClicked, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = 60f * scale;
        var size = new Vector2(width, rowHeight);

        clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        rightClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        if (!ImGui.IsRectVisible(origin, origin + size))
            return;

        var draw = ImGui.GetWindowDrawList();
        if (isSelected || hovered)
            draw.AddRectFilled(origin, origin + size, ChatVisuals.Color(ChatVisuals.ListHover));

        draw.AddLine(new Vector2(origin.X, origin.Y + rowHeight - 1f),
            new Vector2(origin.X + width, origin.Y + rowHeight - 1f),
            ChatVisuals.Color(ChatVisuals.Divider), 1f);

        draw.PushClipRect(origin, origin + size, true);

        var padX = 12f * scale;
        var avatarSize = 40f * scale;
        var avatarPos = origin + new Vector2(padX, (rowHeight - avatarSize) * 0.5f);
        if (conversationDesc.ToTellPeer() is { } peer)
            ChatVisuals.Avatar(draw, avatars, peer, avatarPos, avatarSize);
        else
            ChatVisuals.GroupAvatar(draw, conversationDesc.Key.Kind, avatarPos, avatarSize);

        var tagFontSize = ImGui.GetFontSize() * 0.84f;
        var maxTagWidth = MathF.Min(96f * scale, width * 0.30f);
        var tagText = ChatVisuals.TruncateSingleLine(trailingTag, tagFontSize, maxTagWidth);
        var tagSize = ChatVisuals.CalcTextSize(tagText, tagFontSize);
        var tagX = origin.X + width - padX - tagSize.X;
        var tagY = origin.Y + (rowHeight - tagSize.Y) * 0.5f;
        if (tagText.Length > 0)
            ChatVisuals.AddText(draw, new Vector2(tagX, tagY), tagFontSize, ChatVisuals.Color(ChatVisuals.Muted), tagText);

        var textLeft = avatarPos.X + avatarSize + 10f * scale;
        var rightEdge = tagText.Length > 0 ? tagX - 10f * scale : origin.X + width - padX;
        var textWidth = MathF.Max(20f * scale, rightEdge - textLeft);
        var nameFontSize = ImGui.GetFontSize() * 1.04f;
        var nameText = ChatVisuals.TruncateSingleLine(conversationDesc.DisplayName, nameFontSize, textWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subFontSize = ImGui.GetFontSize() * 0.88f;
            var subText = ChatVisuals.TruncateSingleLine(subtitle, subFontSize, textWidth);
            var subSize = ChatVisuals.CalcTextSize(subText, subFontSize);
            var lineGap = 3f * scale;
            var blockY = origin.Y + (rowHeight - nameSize.Y - subSize.Y - lineGap) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(textLeft, blockY), nameFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                nameText, scale);
            ChatVisuals.AddText(draw, new Vector2(textLeft, blockY + nameSize.Y + lineGap), subFontSize,
                ChatVisuals.Color(ChatVisuals.Muted), subText);
        }
        else
        {
            var nameY = origin.Y + (rowHeight - nameSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(textLeft, nameY), nameFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                nameText, scale);
        }

        draw.PopClipRect();
    }

    private void DrawContactProfile(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var contentPos = pos;
        var contentSize = size;
        if (narrow)
        {
            var subHeaderH = 42f * scale;
            DrawSubHeader(draw, pos, size.X, subHeaderH, inspectedFriend?.Name ?? "Friends", null, showBack: true, scale);
            contentPos = pos + new Vector2(0, subHeaderH);
            contentSize = new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH));
        }

        if (inspectedFriend is not { } friend)
        {
            var prompt = snapshot.Friends is null
                ? "Open the in-game Friend List to load your friends."
                : "Select a friend to view profile.";
            var fontSize = ImGui.GetFontSize();
            var maxW = MathF.Max(60f * scale, contentSize.X - 48f * scale);
            var textSize = ChatVisuals.CalcTextSize(prompt, fontSize, maxW);
            ChatVisuals.AddText(draw, contentPos + (contentSize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), prompt, maxW);
            return;
        }

        var avatarSize = MathF.Min(112f * scale, MathF.Min(contentSize.X * 0.42f, contentSize.Y * 0.36f));
        avatarSize = MathF.Max(64f * scale, avatarSize);

        var maxCardTextWidth = MathF.Max(60f * scale, contentSize.X - 36f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.28f;
        var nameText = ChatVisuals.TruncateSingleLine(friend.Name, nameFontSize, maxCardTextWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        var friendKey = ConversationKey.Tell(friend.ContentId);
        var noteLayout = MeasureProfileNote(friendKey, maxCardTextWidth, scale);

        var badgeFontSize = ImGui.GetFontSize() * 0.96f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var worldLabel = ChatVisuals.TruncateSingleLine($"@ {friend.World}", badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(worldLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var totalHeight = avatarSize + 18f * scale + nameSize.Y + 10f * scale + noteLayout.BoxSize.Y + 14f * scale + badgeSize.Y;
        var startY = contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.Avatar(draw, avatars, friend, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.19f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##profile-start-chat", actionCenter, actionRadius, scale))
        {
            CommitActiveNote();
            OpenConversation(ConversationDescriptor.FromFriend(friend));
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open chat");

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + 18f * scale);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var notePos = new Vector2(centerX - noteLayout.BoxSize.X * 0.5f, namePos.Y + nameSize.Y + 10f * scale);
        DrawProfileNote(draw, friendKey, notePos, noteLayout, scale);

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, notePos.Y + noteLayout.BoxSize.Y + 14f * scale);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), worldLabel);
    }

    private void DrawGroupDetail(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var contentPos = pos;
        var contentSize = size;
        if (narrow)
        {
            var subHeaderH = 42f * scale;
            DrawSubHeader(draw, pos, size.X, subHeaderH, inspectedGroup?.DisplayName ?? "Groups", null, showBack: true, scale);
            contentPos = pos + new Vector2(0, subHeaderH);
            contentSize = new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH));
        }

        if (inspectedGroup is not { } group)
        {
            const string prompt = "Select a group to view details.";
            var fontSize = ImGui.GetFontSize();
            var maxW = MathF.Max(60f * scale, contentSize.X - 48f * scale);
            var textSize = ChatVisuals.CalcTextSize(prompt, fontSize, maxW);
            ChatVisuals.AddText(draw, contentPos + (contentSize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), prompt, maxW);
            return;
        }

        StaticGroup? staticInfo = null;
        if (group.Key.Kind == ConversationKind.Static && snapshot.Statics is not null)
            snapshot.Statics.TryGetValue(group.Key.StableKey, out staticInfo);

        var avatarSize = staticInfo is not null
            ? Math.Clamp(MathF.Min(contentSize.X * 0.30f, contentSize.Y * 0.24f), 60f * scale, 92f * scale)
            : Math.Clamp(MathF.Min(contentSize.X * 0.42f, contentSize.Y * 0.36f), 64f * scale, 112f * scale);

        var maxCardTextWidth = MathF.Max(60f * scale, contentSize.X - 36f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.24f;
        var nameText = ChatVisuals.TruncateSingleLine(group.DisplayName, nameFontSize, maxCardTextWidth);
        var nameSize = ChatVisuals.CalcTextSize(nameText, nameFontSize);

        var noteLayout = MeasureProfileNote(group.Key, maxCardTextWidth, scale);

        var badgeFontSize = ImGui.GetFontSize() * 0.94f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var badgeLabel = group.Key.Kind switch
        {
            ConversationKind.FreeCompany => "Free Company",
            ConversationKind.CrossWorldLinkshell => snapshot.FindActiveSlot(group.Key) is { } cwSlot
                ? $"Cross-World Linkshell · Slot {cwSlot}"
                : "Cross-World Linkshell",
            ConversationKind.Linkshell => snapshot.FindActiveSlot(group.Key) is { } lsSlot
                ? $"Linkshell · Slot {lsSlot}"
                : "Linkshell",
            ConversationKind.Static => staticInfo is not null
                ? $"Static · {staticInfo.Members.Count} members"
                : "Static",
            _ => string.Empty,
        };
        badgeLabel = ChatVisuals.TruncateSingleLine(badgeLabel, badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(badgeLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var nameGap = staticInfo is not null ? 14f * scale : 18f * scale;
        var noteGap = staticInfo is not null ? 8f * scale : 10f * scale;
        var badgeGap = staticInfo is not null ? 10f * scale : 14f * scale;
        var totalHeight = avatarSize + nameGap + nameSize.Y + noteGap + noteLayout.BoxSize.Y + badgeGap + badgeSize.Y;
        var startY = staticInfo is not null
            ? contentPos.Y + 20f * scale
            : contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.GroupAvatar(draw, group.Key.Kind, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.20f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##group-start-chat", actionCenter, actionRadius, scale))
        {
            CommitActiveNote();
            OpenConversation(group);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open chat");

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + nameGap);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var notePos = new Vector2(centerX - noteLayout.BoxSize.X * 0.5f, namePos.Y + nameSize.Y + noteGap);
        DrawProfileNote(draw, group.Key, notePos, noteLayout, scale);

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, notePos.Y + noteLayout.BoxSize.Y + badgeGap);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), badgeLabel);

        if (staticInfo is null)
            return;

        var matched = snapshot.IsStaticMatched(staticInfo.StableKey);
        var statusLabel = matched ? "Party matched — active in current party" : "Not currently in full static party";
        var statusFontSize = ImGui.GetFontSize() * 0.88f;
        var statusPad = new Vector2(12f, 4f) * scale;
        var statusText = ChatVisuals.TruncateSingleLine(statusLabel, statusFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - statusPad.X * 2f));
        var statusTextSize = ChatVisuals.CalcTextSize(statusText, statusFontSize);
        var statusSize = statusTextSize + statusPad * 2f;
        var statusPos = new Vector2(centerX - statusSize.X * 0.5f, badgePos.Y + badgeSize.Y + 10f * scale);
        draw.AddRectFilled(statusPos, statusPos + statusSize,
            ChatVisuals.Color(matched ? ChatVisuals.MatchSuccessBg : ChatVisuals.SystemBg), statusSize.Y * 0.5f);
        ChatVisuals.AddText(draw, statusPos + statusPad, statusFontSize,
            ChatVisuals.Color(matched ? ChatVisuals.MatchSuccessText : ChatVisuals.SystemText), statusText);

        var rosterTop = statusPos.Y + statusSize.Y + 16f * scale;
        var rosterW = MathF.Min(380f * scale, maxCardTextWidth);
        var rosterX = centerX - rosterW * 0.5f;
        var rosterAvailH = contentPos.Y + contentSize.Y - rosterTop - 16f * scale;
        if (rosterAvailH < 48f * scale)
            return;

        var rosterBoxPos = new Vector2(rosterX, rosterTop);
        var rosterBoxSize = new Vector2(rosterW, rosterAvailH);
        draw.AddRectFilled(rosterBoxPos, rosterBoxPos + rosterBoxSize, ChatVisuals.Color(ChatVisuals.ListBg), 10f * scale);

        ImGui.SetCursorScreenPos(rosterBoxPos + new Vector2(12f * scale, 10f * scale));
        using var rosterChild = ImRaii.Child("##group-detail-roster",
            new Vector2(MathF.Max(20f * scale, rosterW - 24f * scale), MathF.Max(20f * scale, rosterAvailH - 20f * scale)),
            false);
        if (rosterChild)
            DrawMemberGrid(staticInfo.Members, scale);
    }

    private ProfileNoteLayout MeasureProfileNote(ConversationKey key, float maxCardTextWidth, float scale)
    {
        var note = notes.GetValueOrDefault(key);
        var isEditing = editingNoteFor == key;
        var noteFontSize = ImGui.GetFontSize() * 0.98f;
        var maxNoteWidth = MathF.Min(280f * scale, maxCardTextWidth);
        var notePad = new Vector2(12f, 6f) * scale;
        var wrapWidth = MathF.Max(40f * scale, maxNoteWidth - notePad.X * 2f);
        var displayNote = string.IsNullOrEmpty(note) ? "Add a note..." : note;
        var noteTextSize = ChatVisuals.CalcTextSize(displayNote, noteFontSize, wrapWidth);
        var noteBoxSize = isEditing
            ? new Vector2(MathF.Min(260f * scale, maxCardTextWidth), 32f * scale)
            : new Vector2(MathF.Min(maxNoteWidth, noteTextSize.X + notePad.X * 2f), noteTextSize.Y + notePad.Y * 2f);
        return new ProfileNoteLayout(note, displayNote, noteFontSize, wrapWidth, noteTextSize, noteBoxSize, isEditing);
    }

    private void DrawProfileNote(
        ImDrawListPtr draw,
        ConversationKey key,
        Vector2 notePos,
        ProfileNoteLayout layout,
        float scale)
    {
        var noteBoxSize = layout.BoxSize;
        if (layout.IsEditing)
        {
            var inputRounding = 8f * scale;
            draw.AddRectFilled(notePos, notePos + noteBoxSize, ChatVisuals.Color(ChatVisuals.White), inputRounding);
            var inputPadY = MathF.Max(2f, (noteBoxSize.Y - ImGui.GetFontSize()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(notePos.X + 8f * scale, notePos.Y));
            var enter = false;
            using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
            using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
            using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, inputPadY)))
            {
                if (focusNoteInput)
                {
                    ImGui.SetKeyboardFocusHere();
                    focusNoteInput = false;
                }

                ImGui.SetNextItemWidth(MathF.Max(40f * scale, noteBoxSize.X - 16f * scale));
                enter = ImGui.InputTextWithHint($"##note-input-{key.StorageId}", "Add a note...", ref noteDraft, 160,
                    ImGuiInputTextFlags.EnterReturnsTrue);
            }

            var inputActive = ImGui.IsItemActive();
            draw.AddRect(notePos, notePos + noteBoxSize,
                ChatVisuals.Color(inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder),
                inputRounding, ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                editingNoteFor = null;
            }
            else if (enter || ImGui.IsItemDeactivated())
            {
                SaveNote(key, noteDraft);
                editingNoteFor = null;
            }

            return;
        }

        ImGui.SetCursorScreenPos(notePos);
        if (ImGui.InvisibleButton($"##note-btn-{key.StorageId}", noteBoxSize))
        {
            CommitActiveNote();
            editingNoteFor = key;
            noteDraft = layout.Note ?? string.Empty;
            focusNoteInput = true;
        }

        if (ImGui.IsItemHovered())
        {
            draw.AddRectFilled(notePos, notePos + noteBoxSize, ChatVisuals.Color(ChatVisuals.ListBg), 8f * scale);
            ImGui.SetTooltip("Click to edit note");
        }

        var noteColor = string.IsNullOrEmpty(layout.Note) ? ChatVisuals.Placeholder : ChatVisuals.Muted;
        var textOrigin = new Vector2(
            notePos.X + (noteBoxSize.X - layout.TextSize.X) * 0.5f,
            notePos.Y + (noteBoxSize.Y - layout.TextSize.Y) * 0.5f);
        ChatVisuals.AddText(draw, textOrigin, layout.FontSize, ChatVisuals.Color(noteColor),
            layout.DisplayNote, layout.WrapWidth);
    }

    private void DrawConversationPane(ImDrawListPtr draw, SocialSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var subHeaderH = 42f * scale;
        if (selected is not { } currentConv || conversation is not { } view)
        {
            if (narrow)
                DrawSubHeader(draw, pos, size.X, subHeaderH, "Chats", null, showBack: true, scale);

            var emptyPos = narrow ? pos + new Vector2(0, subHeaderH) : pos;
            var emptySize = narrow ? new Vector2(size.X, MathF.Max(1f, size.Y - subHeaderH)) : size;
            const string emptyPrompt = "Choose a conversation.";
            var fontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(emptyPrompt, fontSize);
            ChatVisuals.AddText(draw, emptyPos + (emptySize - textSize) * 0.5f, fontSize,
                ChatVisuals.Color(ChatVisuals.Muted), emptyPrompt);
            return;
        }

        var subLabel = currentConv.Key.Kind == ConversationKind.Tell
            ? (string.IsNullOrEmpty(currentConv.WorldName) ? null : $"@ {currentConv.WorldName}")
            : snapshot.FormatChannelTag(currentConv);
        DrawSubHeader(draw, pos, size.X, subHeaderH, currentConv.DisplayName, subLabel, showBack: narrow, scale);
        view.Update();

        var draft = drafts[currentConv.Key];
        var sendTarget = snapshot.FindSendTarget(currentConv.Key, out var unavailable);
        var warning = draft.Warning is { AttachedToMessage: false } local ? local : unassignedWarning;
        var status = unavailable ?? (warning is { AttachedToMessage: false } visible ? visible.Message : null);
        var statusIsAlert = unavailable is null;
        var writeFailed = capture.FailedWriteOwner == session!.ContentId;
        var showTyping = sendTarget is not null
            && inputFocused
            && inputFocusedConversation == currentConv.Key
            && !string.IsNullOrWhiteSpace(draft.Text);

        var bannerMaxWidth = MathF.Max(80f * scale, size.X - 36f * scale);
        ChatVisuals.BannerLayout? statusBanner = status is not null
            ? ChatVisuals.MeasureSystemBanner(status, bannerMaxWidth, scale)
            : null;
        const string writeErrorText = "A message could not be saved. See /xllog for details.";
        ChatVisuals.BannerLayout? writeErrorBanner = writeFailed
            ? ChatVisuals.MeasureSystemBanner(writeErrorText, bannerMaxWidth, scale)
            : null;

        var statusHeight = statusBanner?.Height ?? 0f;
        var writeErrHeight = writeErrorBanner?.Height ?? 0f;
        var sendBarHeight = 52f * scale;
        var composerHeight = sendBarHeight + statusHeight + writeErrHeight + (statusHeight + writeErrHeight > 0 ? 4f * scale : 0f);
        var timelinePos = pos + new Vector2(0, subHeaderH);
        var timelineHeight = MathF.Max(1f, size.Y - subHeaderH - composerHeight);

        var timelineContentWidth = MathF.Max(1f, size.X - ImGui.GetStyle().ScrollbarSize);

        using var convId = ImRaii.PushId(currentConv.Key.StorageId);
        ImGui.SetCursorScreenPos(timelinePos);
        using (var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (var timeline = ImRaii.Child("##messages", new Vector2(size.X, timelineHeight), false))
        {
            if (timeline)
                DrawTimeline(view, currentConv, timelineContentWidth, showTyping, scale);
        }

        wasShowingTyping = showTyping;

        var footerPos = timelinePos + new Vector2(0, timelineHeight);
        if (status is not null && statusBanner is { } sb)
        {
            ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, status, statusIsAlert, scale, sb);
            footerPos.Y += sb.Height;
        }

        if (writeErrorBanner is { } wb)
        {
            ChatVisuals.DrawSystemBanner(draw, footerPos, size.X, writeErrorText, isAlert: true, scale, wb);
            footerPos.Y += wb.Height;
        }

        var sendBarPos = pos + new Vector2(0, size.Y - sendBarHeight);
        DrawSendBar(draw, currentConv, sendTarget, draft, sendBarPos, new Vector2(size.X, sendBarHeight), bottomCorners, scale);
    }

    private void DrawSubHeader(ImDrawListPtr draw, Vector2 pos, float width, float height, string title, string? subtitle,
        bool showBack, float scale)
    {
        var left = pos.X + 14f * scale;
        if (showBack)
        {
            var btnSize = 28f * scale;
            var btnPos = new Vector2(pos.X + 8f * scale, pos.Y + (height - btnSize) * 0.5f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##narrow-back", new Vector2(btnSize)))
            {
                CommitActiveNote();
                narrowShowingDetail = false;
            }

            if (ImGui.IsItemHovered())
                draw.AddRectFilled(btnPos, btnPos + new Vector2(btnSize), ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);

            ChatVisuals.DrawBackIcon(draw, btnPos + new Vector2(btnSize * 0.5f), 14f * scale,
                ChatVisuals.Color(ChatVisuals.Ink), MathF.Max(1.6f, 1.8f * scale));
            left = btnPos.X + btnSize + 8f * scale;
        }

        var rightLimit = pos.X + width - 14f * scale;
        var avail = MathF.Max(30f * scale, rightLimit - left);
        var titleFontSize = ImGui.GetFontSize() * 1.06f;

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subFontSize = ImGui.GetFontSize() * 0.90f;
            var subSize = ChatVisuals.CalcTextSize(subtitle, subFontSize);
            var titleMax = MathF.Max(30f * scale, avail - subSize.X - 8f * scale);
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, titleMax);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            var titleY = pos.Y + (height - titleSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(left, titleY), titleFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                titleText, scale);

            var subX = left + titleSize.X + 8f * scale;
            var subY = pos.Y + (height - subSize.Y) * 0.5f;
            var clampedSub = ChatVisuals.TruncateSingleLine(subtitle, subFontSize, MathF.Max(0, rightLimit - subX));
            ChatVisuals.AddText(draw, new Vector2(subX, subY), subFontSize, ChatVisuals.Color(ChatVisuals.Muted),
                clampedSub);
        }
        else
        {
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, avail);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            ChatVisuals.AddBoldText(draw, new Vector2(left, pos.Y + (height - titleSize.Y) * 0.5f), titleFontSize,
                ChatVisuals.Color(ChatVisuals.Ink), titleText, scale);
        }

        var lineY = pos.Y + height - 1f;
        draw.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + width, lineY),
            ChatVisuals.Color(ChatVisuals.Divider), 1f);
    }

    private void DrawTimeline(ConversationHistory view, ConversationDescriptor currentConv, float contentWidth,
        bool showTyping, float scale)
    {
        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f * scale;
        var followLatest = view.RevealLatest
            || (view.AddedMessages && view.PrependAnchor is null && atBottom)
            || (showTyping && !wasShowingTyping && atBottom);

        ImGui.Dummy(new Vector2(0, 6f * scale));
        var availWidth = MathF.Min(ImGui.GetContentRegionAvail().X, contentWidth);

        if (view.ReadFailed)
        {
            DrawTimelineBanner("History could not be read.", isAlert: true, availWidth, scale);
            DrawCenteredPillButton("Retry", "##retry-history", availWidth, scale, view.Retry);
        }
        else if (view.HasOlder)
        {
            using var disabled = ImRaii.Disabled(view.IsLoading);
            DrawCenteredPillButton(view.IsLoading ? "Loading earlier messages..." : "Load earlier messages",
                "##load-older", availWidth, scale, view.LoadPage);
        }
        else if (view.Messages.Count == 0 && !showTyping)
        {
            var label = view.IsLoading ? "Loading history..." : "No messages yet.";
            var fontSize = ImGui.GetFontSize();
            var textSize = ChatVisuals.CalcTextSize(label, fontSize);
            var origin = ImGui.GetCursorScreenPos();
            ChatVisuals.AddText(ImGui.GetWindowDrawList(), origin + new Vector2((availWidth - textSize.X) * 0.5f, 12f * scale),
                fontSize, ChatVisuals.Color(ChatVisuals.Muted), label);
            ImGui.Dummy(new Vector2(availWidth, textSize.Y + 24f * scale));
        }

        var previousFirstY = firstMessageY;
        var first = true;
        long? previousSentAt = null;
        SenderGroupKey? previousSpeaker = null;
        var previousHadRejection = false;
        var tellPeer = currentConv.ToTellPeer();

        foreach (var message in view.Messages)
        {
            var author = message.Outgoing
                ? session!.Player
                : tellPeer ?? message.Sender;
            var speaker = SenderGroupKey.From(message.Outgoing, author);
            var showTimeDivider = ChatVisuals.ShouldInsertTimeDivider(previousSentAt, message.SentAt);
            var isGroupStart = previousSpeaker is null
                || !previousSpeaker.Value.Equals(speaker)
                || showTimeDivider
                || previousHadRejection;

            if (showTimeDivider)
            {
                var label = ChatVisuals.FormatTimeDivider(message.SentAt);
                var divider = ChatVisuals.MeasureTimeDivider(label, scale);
                var origin = ImGui.GetCursorScreenPos();
                if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, divider.Height)))
                    ChatVisuals.DrawTimeDivider(ImGui.GetWindowDrawList(), origin, availWidth, label, scale, divider);

                ImGui.Dummy(new Vector2(availWidth, divider.Height));
            }
            else if (isGroupStart)
            {
                ImGui.Dummy(new Vector2(availWidth, 6f * scale));
            }

            var y = ImGui.GetCursorPosY();
            if (first)
            {
                firstMessageY = y;
                first = false;
            }

            if (view.PrependAnchor == message.Id)
                ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() + y - previousFirstY));

            DrawMessageBubble(message, author, isGroupStart, availWidth, scale);

            if (message.Rejection is { } rejection)
                DrawTimelineBanner(rejection, isAlert: true, availWidth, scale);

            previousSentAt = message.SentAt;
            previousSpeaker = speaker;
            previousHadRejection = message.Rejection is not null;
        }

        if (showTyping)
        {
            var typingGroupStart = previousSpeaker is null || !previousSpeaker.Value.Outgoing || previousHadRejection;
            if (typingGroupStart && view.Messages.Count > 0)
                ImGui.Dummy(new Vector2(availWidth, 6f * scale));

            DrawTypingBubbleRow(session!.Player, typingGroupStart, availWidth, scale);
        }

        ImGui.Dummy(new Vector2(0, 8f * scale));
        if (followLatest && (view.Messages.Count > 0 || showTyping))
            ImGui.SetScrollHereY(1f);

        view.ClearScrollRequests();

        using var msgMenu = ChatVisuals.BeginContextMenu("##message-context-menu", scale);
        if (msgMenu.IsOpen && contextMessageBody is { } copyBody)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-copy-text", "Copy Text", 140f * scale, scale))
                ImGui.SetClipboardText(copyBody);
        }
    }

    private static void DrawCenteredPillButton(string label, string id, float availWidth, float scale, Action onClick)
    {
        var fontSize = ImGui.GetFontSize() * 0.88f;
        var textSize = ChatVisuals.CalcTextSize(label, fontSize);
        var btnSize = textSize + new Vector2(22f, 10f) * scale;
        var origin = ImGui.GetCursorScreenPos();
        var btnPos = new Vector2(origin.X + (availWidth - btnSize.X) * 0.5f, origin.Y + 4f * scale);

        ImGui.SetCursorScreenPos(btnPos);
        if (ImGui.InvisibleButton(id, btnSize))
            onClick();

        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(btnPos, btnPos + btnSize,
            ChatVisuals.Color(hovered ? ChatVisuals.InputBorder : ChatVisuals.SystemBg), btnSize.Y * 0.5f);
        ChatVisuals.AddText(draw, btnPos + (btnSize - textSize) * 0.5f, fontSize,
            ChatVisuals.Color(ChatVisuals.SystemText), label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(availWidth, btnSize.Y + 10f * scale));
    }

    private static void DrawTimelineBanner(string text, bool isAlert, float availWidth, float scale)
    {
        var maxWidth = MathF.Max(80f * scale, availWidth - 44f * scale);
        var layout = ChatVisuals.MeasureSystemBanner(text, maxWidth, scale);
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, layout.Height)))
            ChatVisuals.DrawSystemBanner(ImGui.GetWindowDrawList(), origin, availWidth, text, isAlert, scale, layout);

        ImGui.Dummy(new Vector2(availWidth, layout.Height));
    }

    private void DrawMessageBubble(ChatMessage message, PlayerIdentity author, bool isGroupStart, float availWidth, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var sideMargin = 16f * scale;
        var avatarSize = 36f * scale;
        var avatarGap = 11f * scale;
        var showAvatarSlot = !message.Outgoing || config.ShowSelfAvatar;
        var avatarOffset = showAvatarSlot ? avatarSize + avatarGap : 0f;
        var fontSize = ImGui.GetFontSize();
        var padding = new Vector2(11f, 7f) * scale;
        var innerWidth = MathF.Max(80f * scale, availWidth - sideMargin * 2f);
        var maxBody = MathF.Max(36f * scale,
            MathF.Min(420f * scale, innerWidth * 0.78f - avatarOffset - padding.X * 2f));
        var textSize = ChatVisuals.CalcTextSize(message.Body, fontSize, maxBody);
        var bubbleSize = new Vector2(MathF.Min(maxBody, textSize.X) + padding.X * 2f, textSize.Y + padding.Y * 2f);

        var showName = isGroupStart && showAvatarSlot;
        var nameFontSize = fontSize * 0.88f;
        var nameOffset = showName ? nameFontSize + 4f * scale : 0f;
        var bubbleTop = origin.Y + nameOffset;
        var contentHeight = isGroupStart && showAvatarSlot
            ? MathF.Max(avatarSize, nameOffset + bubbleSize.Y)
            : nameOffset + bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            var bubbleX = message.Outgoing
                ? origin.X + availWidth - sideMargin - avatarOffset - bubbleSize.X
                : origin.X + sideMargin + avatarOffset;
            var bubblePos = new Vector2(bubbleX, bubbleTop);

            if (isGroupStart && showAvatarSlot)
            {
                var avatarX = message.Outgoing
                    ? origin.X + availWidth - sideMargin - avatarSize
                    : origin.X + sideMargin;
                ChatVisuals.Avatar(draw, avatars, author, new Vector2(avatarX, origin.Y), avatarSize);
            }

            if (showName)
            {
                var displayName = message.Outgoing ? "You" : author.Name;
                var nameText = ChatVisuals.TruncateSingleLine(displayName, nameFontSize,
                    MathF.Max(40f * scale, innerWidth - avatarOffset));
                var nameX = message.Outgoing
                    ? origin.X + availWidth - sideMargin - avatarOffset - ChatVisuals.CalcTextSize(nameText, nameFontSize).X
                    : bubbleX;
                ChatVisuals.AddText(draw, new Vector2(nameX, origin.Y), nameFontSize,
                    ChatVisuals.Color(ChatVisuals.Ink), nameText);
            }

            ChatVisuals.DrawBubble(
                draw,
                bubblePos,
                bubbleSize,
                message.Outgoing ? ChatVisuals.OutgoingBubble : ChatVisuals.IncomingBubble,
                message.Body,
                fontSize,
                padding,
                maxBody,
                message.Outgoing,
                isGroupStart,
                scale);

            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(bubblePos, bubblePos + bubbleSize))
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextMessageBody = message.Body;
                    ImGui.OpenPopup("##message-context-menu");
                }
                else if (!ImGui.IsPopupOpen("##message-context-menu"))
                {
                    ImGui.SetTooltip(ChatVisuals.FormatFullTime(message.SentAt));
                }
            }
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private void DrawTypingBubbleRow(PlayerIdentity self, bool isGroupStart, float availWidth, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var sideMargin = 16f * scale;
        var avatarSize = 36f * scale;
        var avatarGap = 11f * scale;
        var showAvatarSlot = config.ShowSelfAvatar;
        var avatarOffset = showAvatarSlot ? avatarSize + avatarGap : 0f;
        var fontSize = ImGui.GetFontSize();
        var showName = isGroupStart && showAvatarSlot;
        var nameFontSize = fontSize * 0.88f;
        var nameOffset = showName ? nameFontSize + 4f * scale : 0f;
        var bubbleSize = new Vector2(62f, 32f) * scale;
        var bubbleTop = origin.Y + nameOffset;
        var contentHeight = isGroupStart && showAvatarSlot
            ? MathF.Max(avatarSize, nameOffset + bubbleSize.Y)
            : nameOffset + bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            if (isGroupStart && showAvatarSlot)
            {
                var avatarX = origin.X + availWidth - sideMargin - avatarSize;
                ChatVisuals.Avatar(draw, avatars, self, new Vector2(avatarX, origin.Y), avatarSize);
            }

            if (showName)
            {
                var nameX = origin.X + availWidth - sideMargin - avatarOffset - ChatVisuals.CalcTextSize("You", nameFontSize).X;
                ChatVisuals.AddText(draw, new Vector2(nameX, origin.Y), nameFontSize,
                    ChatVisuals.Color(ChatVisuals.Ink), "You");
            }

            var bubbleX = origin.X + availWidth - sideMargin - avatarOffset - bubbleSize.X;
            ChatVisuals.DrawTypingBubble(draw, new Vector2(bubbleX, bubbleTop), bubbleSize,
                (float)ImGui.GetTime(), isGroupStart, scale);
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private void DrawSendBar(ImDrawListPtr draw, ConversationDescriptor currentConv, SendTarget? target, Draft draft,
        Vector2 pos, Vector2 size, ImDrawFlags bottomCorners, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.SendBarBg), 10f * scale, bottomCorners);
        draw.AddLine(pos, pos + new Vector2(size.X, 0), ChatVisuals.Color(ChatVisuals.Divider), 1f);

        var padX = 12f * scale;
        var pillH = 34f * scale;
        var pillY = pos.Y + (size.Y - pillH) * 0.5f;
        var sendSize = 34f * scale;
        var sendGap = 8f * scale;
        var pillWidth = MathF.Max(60f * scale, size.X - padX * 2f - sendSize - sendGap);
        var pillPos = new Vector2(pos.X + padX, pillY);
        var pillRounding = pillH * 0.5f;

        draw.AddRectFilled(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(ChatVisuals.White), pillRounding);

        var hint = currentConv.Key.Kind switch
        {
            ConversationKind.Tell => "Write a Tell...",
            ConversationKind.FreeCompany => "Message Free Company...",
            ConversationKind.CrossWorldLinkshell => "Message CWLS...",
            ConversationKind.Linkshell => "Message Linkshell...",
            ConversationKind.Static => "Message Static...",
            _ => "Write a message...",
        };

        var inputPadX = 14f * scale;
        var inputPadY = MathF.Max(2f, (pillH - ImGui.GetFontSize()) * 0.5f);
        var enter = false;
        ImGui.SetCursorScreenPos(new Vector2(pillPos.X + inputPadX, pillPos.Y));
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * scale, inputPadY)))
        {
            ImGui.SetNextItemWidth(MathF.Max(20f * scale, pillWidth - inputPadX * 2f));
            enter = ImGui.InputTextWithHint("##chat-input", hint, ref draft.Text, 2048,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var inputActive = ImGui.IsItemActive();
        var canSend = target is not null;
        if (enter && target is not null)
        {
            SubmitDraft(target, draft);
            ImGui.SetKeyboardFocusHere(-1);
        }

        inputFocused = inputActive;
        inputFocusedConversation = inputActive ? currentConv.Key : null;

        var borderCol = inputActive ? ChatVisuals.InputFocus : ChatVisuals.InputBorder;
        draw.AddRect(pillPos, pillPos + new Vector2(pillWidth, pillH), ChatVisuals.Color(borderCol), pillRounding,
            ImDrawFlags.None, MathF.Max(1.5f, 1.8f * scale));

        var sendPos = new Vector2(pillPos.X + pillWidth + sendGap, pillY);
        ImGui.SetCursorScreenPos(sendPos);
        var clicked = false;
        using (ImRaii.Disabled(!canSend))
            clicked = ImGui.InvisibleButton("##send", new Vector2(sendSize));

        var hasText = !string.IsNullOrWhiteSpace(draft.Text);
        var sendHovered = canSend && ImGui.IsItemHovered();
        var iconCol = !canSend || !hasText
            ? ChatVisuals.SendIconIdle
            : sendHovered
                ? ChatVisuals.RailBg
                : ChatVisuals.OutgoingBubble;
        ChatVisuals.DrawSendIcon(draw, sendPos + new Vector2(sendSize * 0.5f), 20f * scale, ChatVisuals.Color(iconCol));

        if (clicked && target is not null)
            SubmitDraft(target, draft);
    }

    private void SyncSession(PlayerSession? active)
    {
        if (ReferenceEquals(session, active))
            return;

        session = active;
        inspectedFriend = null;
        inspectedGroup = null;
        selected = null;
        conversation = null;
        summaryQuery = null;
        notesQuery = null;
        summaryRevision = -1;
        notesRevision = -1;
        summaryFailed = false;
        summaries = [];
        rows = [];
        notes.Clear();
        drafts.Clear();
        unassignedWarning = null;
        narrowShowingDetail = false;
        contactsSearch = string.Empty;
        chatsSearch = string.Empty;
        groupsSearch = string.Empty;
        editingNoteFor = null;
        noteDraft = string.Empty;
        contextFriend = null;
        contextConversation = null;
        contextStatic = null;
        contextMessageBody = null;
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
        CloseStaticModal();
    }

    private void SyncLiveState(SocialSnapshot snapshot)
    {
        if (selected is { } currentSelected)
            selected = snapshot.RefreshDescriptor(currentSelected);

        if (inspectedFriend is { } friend && snapshot.Friends is not null)
        {
            if (snapshot.Friends.TryGetValue(friend.ContentId, out var current))
            {
                inspectedFriend = current;
            }
            else
            {
                if (editingNoteFor == ConversationKey.Tell(friend.ContentId))
                    editingNoteFor = null;
                inspectedFriend = null;
                if (activeTab == NavTab.Contacts)
                    narrowShowingDetail = false;
            }
        }

        if (inspectedGroup is { } group)
        {
            var stillActive = group.Key.Kind switch
            {
                ConversationKind.FreeCompany => snapshot.FreeCompany?.Key == group.Key,
                ConversationKind.Linkshell => snapshot.FindActiveSlot(group.Key) is not null,
                ConversationKind.CrossWorldLinkshell => snapshot.FindActiveSlot(group.Key) is not null,
                ConversationKind.Static => snapshot.Statics is null || snapshot.Statics.ContainsKey(group.Key.StableKey),
                _ => false,
            };

            if (stillActive)
            {
                inspectedGroup = snapshot.RefreshDescriptor(group);
            }
            else
            {
                if (editingNoteFor == group.Key)
                    editingNoteFor = null;
                inspectedGroup = null;
                if (activeTab == NavTab.Groups)
                    narrowShowingDetail = false;
            }
        }
    }

    private void UpdateSummaries(SocialSnapshot snapshot)
    {
        if (summaryQuery is { IsCompleted: true } completed)
        {
            summaryQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                summaries = completed.Result;
                Reorder(snapshot);
            }
            else
            {
                _ = completed.Exception;
                summaryFailed = true;
            }
        }

        if (summaryQuery is null && !summaryFailed && summaryRevision != history.Revision)
        {
            summaryRevision = history.Revision;
            summaryQuery = history.ReadConversationsAsync(session!.ContentId);
        }
    }

    private void UpdateNotes()
    {
        if (notesQuery is { IsCompleted: true } completed)
        {
            notesQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                notes.Clear();
                foreach (var (key, value) in completed.Result)
                    notes[key] = value;
            }
            else
            {
                _ = completed.Exception;
            }
        }

        if (notesQuery is null && notesRevision != history.NotesRevision)
        {
            notesRevision = history.NotesRevision;
            notesQuery = history.ReadNotesAsync(session!.ContentId);
        }
    }

    private void CommitActiveNote()
    {
        if (editingNoteFor is not { } noteKey)
            return;

        SaveNote(noteKey, noteDraft);
        editingNoteFor = null;
    }

    private void SaveNote(ConversationKey key, string rawNote)
    {
        if (session is null)
            return;

        var trimmed = rawNote.Trim();
        if (trimmed.Length == 0)
            notes.Remove(key);
        else
            notes[key] = trimmed;

        _ = history.SaveNoteAsync(session.ContentId, key, trimmed);
    }

    private void DeleteChatFromList(SocialSnapshot snapshot, ConversationDescriptor conversationDesc)
    {
        if (session is null)
            return;

        _ = history.HideConversationAsync(session.ContentId, conversationDesc.Key);
        summaries = summaries.Where(summary => summary.Conversation.Key != conversationDesc.Key).ToArray();
        if (selected?.Key == conversationDesc.Key)
        {
            selected = null;
            conversation = null;
            narrowShowingDetail = false;
            inputFocused = false;
            inputFocusedConversation = null;
            wasShowingTyping = false;
        }

        Reorder(snapshot);
    }

    private void OpenConversation(ConversationDescriptor conversationDesc)
    {
        activeTab = NavTab.Chats;
        narrowShowingDetail = true;
        _ = history.UnhideConversationAsync(session!.ContentId, conversationDesc.Key);
        if (selected?.Key == conversationDesc.Key)
        {
            selected = conversationDesc;
            return;
        }

        selected = conversationDesc;
        conversation = new ConversationHistory(history, session!.ContentId, conversationDesc.Key);
        firstMessageY = 0;
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
        drafts.TryAdd(conversationDesc.Key, new Draft());
        Reorder(directory.Current);
    }

    private void Reorder(SocialSnapshot snapshot)
    {
        IEnumerable<ConversationSummary> visible = summaries
            .Select(row => row with { Conversation = snapshot.RefreshDescriptor(row.Conversation) });
        if (selected is { } current && !summaries.Any(row => row.Conversation.Key == current.Key))
            visible = visible.Append(new ConversationSummary(current, null));

        IOrderedEnumerable<ConversationSummary> ranked;
        if (sortByRecent)
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.Latest is null ? 1 : 0)
                    .ThenBy(row => row.Latest?.SentAt)
                    .ThenBy(row => row.Latest?.Id)
                    .ThenBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.Latest?.SentAt)
                    .ThenByDescending(row => row.Latest?.Id)
                    .ThenBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.Conversation.DisplayName, StringComparer.OrdinalIgnoreCase);
        }

        rows = ranked
            .ThenBy(row => row.Conversation.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Conversation.Key.StorageId, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<GroupListItem> BuildActiveGroupItems(SocialSnapshot snapshot)
    {
        var items = new List<GroupListItem>();
        if (snapshot.FreeCompany is { } fc)
            items.Add(new GroupListItem(fc.ToDescriptor(), "Free Company", "FC", null));

        foreach (var (slot, cwls) in snapshot.CrossWorldLinkshells.OrderBy(pair => pair.Key))
            items.Add(new GroupListItem(cwls.ToDescriptor(), "Cross-World Linkshell", $"CWLS {slot}", null));

        foreach (var (slot, ls) in snapshot.Linkshells.OrderBy(pair => pair.Key))
            items.Add(new GroupListItem(ls.ToDescriptor(), "Linkshell", $"LS {slot}", null));

        if (snapshot.Statics is not null)
        {
            foreach (var group in snapshot.Statics.Values
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.CreatedAt))
            {
                var subtitle = snapshot.IsStaticMatched(group.StableKey)
                    ? $"{group.Members.Count} members · Party matched"
                    : $"{group.Members.Count} members";
                items.Add(new GroupListItem(group.ToDescriptor(), subtitle, "Static", group));
            }
        }

        return items;
    }

    private static string FormatPreview(ConversationKey key, ChatMessage latest)
    {
        if (latest.Outgoing)
            return $"You: {latest.Body}";

        return key.IsGroup && !string.IsNullOrWhiteSpace(latest.Sender.Name)
            ? $"{latest.Sender.Name}: {latest.Body}"
            : latest.Body;
    }

    private static bool MatchesFriendSearch(PlayerIdentity peer, string query, string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        return peer.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || peer.World.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesGroupSearch(GroupListItem item, string query, string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        if (item.Conversation.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || item.Tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return item.Static is { } group
            && group.Members.Any(member => member.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesChatSearch(
        SocialSnapshot snapshot,
        ConversationDescriptor conversationDesc,
        string tag,
        string query,
        string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        if (conversationDesc.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || conversationDesc.WorldName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return conversationDesc.Key.Kind == ConversationKind.Static
            && snapshot.Statics is not null
            && snapshot.Statics.TryGetValue(conversationDesc.Key.StableKey, out var group)
            && group.Members.Any(member => member.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void SubmitDraft(SendTarget target, Draft draft)
    {
        if (!sender.TrySubmit(session!, target, draft.Text, out var error))
        {
            draft.Warning = new SendWarning(session!, target.Key, error);
            return;
        }

        draft.Text = string.Empty;
        draft.Warning = null;
        unassignedWarning = null;
    }

    private void ApplySendWarnings()
    {
        while (sender.TakeWarning() is { } warning)
        {
            if (!ReferenceEquals(warning.Session, session) || warning.AttachedToMessage)
                continue;

            if (warning.Conversation is { } key)
            {
                if (!drafts.TryGetValue(key, out var draft))
                {
                    draft = new Draft();
                    drafts.Add(key, draft);
                }

                draft.Warning = warning;
            }
            else
            {
                unassignedWarning = warning;
            }
        }
    }

    private enum NavTab
    {
        Contacts,
        Groups,
        Chats,
    }

    private enum StaticModalMode
    {
        None,
        Create,
        Rename,
    }

    private sealed record GroupListItem(
        ConversationDescriptor Conversation,
        string Subtitle,
        string Tag,
        StaticGroup? Static);

    private readonly record struct ProfileNoteLayout(
        string? Note,
        string DisplayNote,
        float FontSize,
        float WrapWidth,
        Vector2 TextSize,
        Vector2 BoxSize,
        bool IsEditing);

    private readonly record struct SenderGroupKey(bool Outgoing, ulong ContentId, uint WorldId, string Name)
    {
        public static SenderGroupKey From(bool outgoing, PlayerIdentity author) =>
            outgoing
                ? new SenderGroupKey(true, 0, 0, string.Empty)
                : author.ContentId != 0
                    ? new SenderGroupKey(false, author.ContentId, 0, string.Empty)
                    : new SenderGroupKey(false, 0, author.WorldId, author.Name.Trim().ToLowerInvariant());
    }

    private sealed class Draft
    {
        public string Text = string.Empty;
        public SendWarning? Warning;
    }
}
