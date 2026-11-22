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

    private readonly FriendDirectory directory;
    private readonly HistoryStore history;
    private readonly ChatCapture capture;
    private readonly TellSender sender;
    private readonly AvatarCache avatars;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly ISharedImmediateTexture coverTexture;
    private readonly Stopwatch introTimer = new();
    private readonly Dictionary<ulong, Draft> drafts = new();
    private readonly Dictionary<ulong, string> notes = new();

    private PlayerSession? session;
    private NavTab activeTab = NavTab.Chats;
    private PlayerIdentity? inspectedFriend;
    private PlayerIdentity? selected;
    private ConversationHistory? conversation;
    private IReadOnlyList<ConversationSummary> summaries = [];
    private IReadOnlyList<ConversationSummary> rows = [];
    private Task<IReadOnlyList<ConversationSummary>>? summaryQuery;
    private Task<IReadOnlyDictionary<ulong, string>>? notesQuery;
    private long summaryRevision = -1;
    private long notesRevision = -1;
    private bool summaryFailed;
    private bool sortByRecent = true;
    private bool sortAscending;
    private bool narrowShowingDetail;
    private string contactsSearch = string.Empty;
    private string chatsSearch = string.Empty;
    private float firstMessageY;
    private TellWarning? unassignedWarning;
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
    private ulong? editingNoteFor;
    private string noteDraft = string.Empty;
    private bool focusNoteInput;
    private PlayerIdentity? contextPeer;
    private string? contextMessageBody;
    private ulong? tellInputFocusedPeer;
    private bool tellInputFocused;
    private bool wasShowingTyping;

    public MomoTalkWindow(
        FriendDirectory directory,
        HistoryStore history,
        ChatCapture capture,
        TellSender sender,
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
        if (!IsOpen)
        {
            suppressNextIntro = true;
            IsOpen = true;
        }

        BringToFront();
        playingIntro = false;
        introWaitingCover = false;
        introTimer.Reset();
        settingsOpen = true;
        settingsOpenedThisFrame = true;
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
        editingNoteFor = null;
        tellInputFocused = false;
        tellInputFocusedPeer = null;
        wasShowingTyping = false;
    }

    public override void PreDraw()
    {
        RespectCloseHotkey = !settingsOpen && editingNoteFor is null;
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
            SyncPeers(snapshot);
            UpdateSummaries();
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

        // Draw 4 non-overlapping pink strips around the cover slot so alpha never double-blends.
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
        const string message = "Log in to view your friend conversations.";
        var fontSize = ImGui.GetFontSize();
        var maxTextWidth = MathF.Max(60f * scale, bodySize.X - 48f * scale);
        var textSize = ChatVisuals.CalcTextSize(message, fontSize, maxTextWidth);
        var pos = bodyPos + (bodySize - textSize) * 0.5f;
        ChatVisuals.AddText(draw, pos, fontSize, ChatVisuals.Color(ChatVisuals.Muted), message, maxTextWidth);
    }

    private void DrawWideLayout(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
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
        if (activeTab == NavTab.Contacts)
            DrawContactProfile(draw, snapshot, rightPos, rightSize, narrow: false, scale);
        else
            DrawConversationPane(draw, snapshot, rightPos, rightSize, narrow: false, scale);
    }

    private void DrawNarrowLayout(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        if (!narrowShowingDetail)
        {
            var navHeight = 48f * scale;
            var listHeight = MathF.Max(1f, bodySize.Y - navHeight);
            DrawListPane(draw, snapshot, bodyPos, new Vector2(bodySize.X, listHeight), scale);
            DrawBottomNavBar(draw, bodyPos + new Vector2(0, listHeight), new Vector2(bodySize.X, navHeight), scale);
            return;
        }

        if (activeTab == NavTab.Contacts)
            DrawContactProfile(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
        else
            DrawConversationPane(draw, snapshot, bodyPos, bodySize, narrow: true, scale);
    }

    private void DrawLeftNavRail(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottomLeft);

        var tabHeight = 54f * scale;
        DrawNavTabButton(draw, "##nav-contacts", NavTab.Contacts, pos, new Vector2(size.X, tabHeight),
            ImDrawFlags.None, scale);
        DrawNavTabButton(draw, "##nav-chats", NavTab.Chats, pos + new Vector2(0, tabHeight),
            new Vector2(size.X, tabHeight), ImDrawFlags.None, scale);
    }

    private void DrawBottomNavBar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.RailBg), 10f * scale,
            ImDrawFlags.RoundCornersBottom);

        var half = size.X * 0.5f;
        DrawNavTabButton(draw, "##bnav-contacts", NavTab.Contacts, pos, new Vector2(half, size.Y),
            ImDrawFlags.RoundCornersBottomLeft, scale);
        DrawNavTabButton(draw, "##bnav-chats", NavTab.Chats, pos + new Vector2(half, 0), new Vector2(size.X - half, size.Y),
            ImDrawFlags.RoundCornersBottomRight, scale);
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
                ? ChatVisuals.WithAlpha(ChatVisuals.RailActive, 0.55f)
                : ChatVisuals.RailBg;

        if (selectedTab || hovered)
        {
            var rounding = roundCorners == ImDrawFlags.None ? 0f : 10f * scale;
            draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(bg), rounding, roundCorners);
        }

        var iconAlpha = selectedTab ? 1f : hovered ? 0.85f : 0.50f;
        var iconColor = ChatVisuals.Color(ChatVisuals.WithAlpha(ChatVisuals.White, iconAlpha));
        var center = pos + size * 0.5f;
        var iconSize = 24f * scale;
        if (tab == NavTab.Contacts)
            ChatVisuals.DrawContactsIcon(draw, center, iconSize, iconColor);
        else
            ChatVisuals.DrawChatIcon(draw, center, iconSize, iconColor, ChatVisuals.Color(bg));

        if (hovered)
            ImGui.SetTooltip(tab == NavTab.Contacts ? "Friends" : "Chats");
    }

    private void DrawListPane(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, float scale)
    {
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.ListBg));

        var headerHeight = 52f * scale;
        DrawListToolbar(draw, pos, new Vector2(size.X, headerHeight), scale);

        var bodyPos = pos + new Vector2(0, headerHeight);
        var bodySize = new Vector2(size.X, MathF.Max(1f, size.Y - headerHeight));
        ImGui.SetCursorScreenPos(bodyPos);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var body = ImRaii.Child("##list-body", bodySize, false);
        if (!body)
            return;

        if (activeTab == NavTab.Contacts)
            DrawContactsList(snapshot, scale);
        else
            DrawChatsList(scale);
    }

    private void DrawListToolbar(ImDrawListPtr draw, Vector2 pos, Vector2 size, float scale)
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

        if (activeTab == NavTab.Contacts)
            DrawSearchPill(draw, "##contacts-search", "Search friends...", ref contactsSearch,
                new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);
        else
            DrawSearchPill(draw, "##chats-search", "Search chats...", ref chatsSearch,
                new Vector2(pos.X + padX, controlY), new Vector2(searchWidth, controlH), scale);

        if (showSort)
        {
            var sortPos = new Vector2(pos.X + padX + searchWidth + gap, controlY + 1f * scale);
            var sortSize = new Vector2(sortWidth, controlH - 2f * scale);
            if (ChatVisuals.SkewButton(draw, "##sort-toggle", sortByRecent ? "Recent" : "Name", sortPos, sortSize, scale))
            {
                sortByRecent = !sortByRecent;
                Reorder();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sortByRecent ? "Sorted by recent message (click for Name)" : "Sorted by name (click for Recent)");

            var dirPos = new Vector2(sortPos.X + sortWidth + dirGap, sortPos.Y);
            var dirSize = new Vector2(dirWidth, sortSize.Y);
            if (ChatVisuals.SkewSortOrderButton(draw, "##sort-order", sortAscending, dirPos, dirSize, scale))
            {
                sortAscending = !sortAscending;
                Reorder();
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

    private void DrawContactsList(FriendSnapshot snapshot, float scale)
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
            .Where(friend => MatchesSearch(friend, contactsSearch, notes.GetValueOrDefault(friend.ContentId)))
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
            var note = notes.GetValueOrDefault(friend.ContentId);
            DrawListRow(friend, note, isSelected, $"##contact-{friend.ContentId:X16}",
                out var clicked, out var doubleClicked, out var rightClicked, scale);
            if (rightClicked)
            {
                contextPeer = friend;
                ImGui.OpenPopup("##contact-row-menu");
            }
            else if (doubleClicked)
            {
                CommitActiveNote();
                OpenConversation(friend);
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
        if (contactMenu.IsOpen && contextPeer is { } menuPeer)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-contact-lodestone", "Open Lodestone", 156f * scale, scale))
                Util.OpenLink(avatars.GetLodestoneUrl(menuPeer));
        }
    }

    private void DrawChatsList(float scale)
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
                ? "No conversations yet. Pick a friend in Friends to start chatting."
                : "Loading conversations...", scale);
            return;
        }

        var anyMatch = false;
        foreach (var row in rows)
        {
            if (!MatchesSearch(row.Peer, chatsSearch))
                continue;

            anyMatch = true;
            var isSelected = selected?.ContentId == row.Peer.ContentId;
            var subtitle = row.Latest is { } latest ? FormatPreview(latest) : "No messages yet";
            DrawListRow(row.Peer, subtitle, isSelected, $"##chat-{row.Peer.ContentId:X16}",
                out var clicked, out _, out var rightClicked, scale);
            if (rightClicked)
            {
                contextPeer = row.Peer;
                ImGui.OpenPopup("##chat-row-menu");
            }
            else if (clicked)
            {
                OpenConversation(row.Peer);
            }
        }

        if (!anyMatch)
            DrawListNotice("No matching conversations.", scale);

        using var chatMenu = ChatVisuals.BeginContextMenu("##chat-row-menu", scale);
        if (chatMenu.IsOpen && contextPeer is { } menuPeer)
        {
            if (ChatVisuals.ContextMenuItem("##ctx-chat-lodestone", "Open Lodestone", 156f * scale, scale))
                Util.OpenLink(avatars.GetLodestoneUrl(menuPeer));

            if (ChatVisuals.ContextMenuItem("##ctx-chat-delete", "Delete Chat", 156f * scale, scale, danger: true))
                DeleteChatFromList(menuPeer);
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

    private void DrawListRow(PlayerIdentity peer, string? subtitle, bool isSelected, string id,
        out bool clicked, out bool doubleClicked, out bool rightClicked, float scale)
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
        ChatVisuals.Avatar(draw, avatars, peer, avatarPos, avatarSize);

        var worldFontSize = ImGui.GetFontSize() * 0.84f;
        var maxWorldWidth = MathF.Min(96f * scale, width * 0.30f);
        var worldText = ChatVisuals.TruncateSingleLine(peer.World, worldFontSize, maxWorldWidth);
        var worldSize = ChatVisuals.CalcTextSize(worldText, worldFontSize);
        var worldX = origin.X + width - padX - worldSize.X;
        var worldY = origin.Y + (rowHeight - worldSize.Y) * 0.5f;
        ChatVisuals.AddText(draw, new Vector2(worldX, worldY), worldFontSize, ChatVisuals.Color(ChatVisuals.Muted), worldText);

        var textLeft = avatarPos.X + avatarSize + 10f * scale;
        var textWidth = MathF.Max(20f * scale, worldX - textLeft - 10f * scale);
        var nameFontSize = ImGui.GetFontSize() * 1.04f;
        var nameText = ChatVisuals.TruncateSingleLine(peer.Name, nameFontSize, textWidth);
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

    private void DrawContactProfile(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
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

        var note = notes.GetValueOrDefault(friend.ContentId);
        var isEditingNote = editingNoteFor == friend.ContentId;
        var noteFontSize = ImGui.GetFontSize() * 0.98f;
        var maxNoteWidth = MathF.Min(280f * scale, maxCardTextWidth);
        var notePad = new Vector2(12f, 6f) * scale;
        var displayNote = string.IsNullOrEmpty(note) ? "Add a note..." : note;
        var noteTextSize = ChatVisuals.CalcTextSize(displayNote, noteFontSize, MathF.Max(40f * scale, maxNoteWidth - notePad.X * 2f));
        var noteBoxSize = isEditingNote
            ? new Vector2(MathF.Min(260f * scale, maxCardTextWidth), 32f * scale)
            : new Vector2(MathF.Min(maxNoteWidth, noteTextSize.X + notePad.X * 2f), noteTextSize.Y + notePad.Y * 2f);

        var badgeFontSize = ImGui.GetFontSize() * 0.96f;
        var badgePad = new Vector2(14f, 5f) * scale;
        var worldLabel = ChatVisuals.TruncateSingleLine($"@ {friend.World}", badgeFontSize,
            MathF.Max(40f * scale, maxCardTextWidth - badgePad.X * 2f));
        var badgeTextSize = ChatVisuals.CalcTextSize(worldLabel, badgeFontSize);
        var badgeSize = badgeTextSize + badgePad * 2f;

        var totalHeight = avatarSize + 18f * scale + nameSize.Y + 10f * scale + noteBoxSize.Y + 14f * scale + badgeSize.Y;
        var startY = contentPos.Y + MathF.Max(24f * scale, (contentSize.Y - totalHeight) * 0.44f);
        var centerX = contentPos.X + contentSize.X * 0.5f;

        var avatarPos = new Vector2(centerX - avatarSize * 0.5f, startY);
        ChatVisuals.Avatar(draw, avatars, friend, avatarPos, avatarSize, dropShadow: true);

        var actionRadius = MathF.Max(15f * scale, avatarSize * 0.19f);
        var actionCenter = avatarPos + new Vector2(avatarSize * 0.84f, avatarSize * 0.84f);
        if (ChatVisuals.DrawProfileChatButton(draw, "##profile-start-chat", actionCenter, actionRadius, scale))
        {
            CommitActiveNote();
            OpenConversation(friend);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open chat");

        var namePos = new Vector2(centerX - nameSize.X * 0.5f, startY + avatarSize + 18f * scale);
        ChatVisuals.AddBoldText(draw, namePos, nameFontSize, ChatVisuals.Color(ChatVisuals.Ink), nameText, scale);

        var notePos = new Vector2(centerX - noteBoxSize.X * 0.5f, namePos.Y + nameSize.Y + 10f * scale);
        if (isEditingNote)
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
                enter = ImGui.InputTextWithHint("##friend-note-input", "Add a note...", ref noteDraft, 160,
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
                SaveFriendNote(friend.ContentId, noteDraft);
                editingNoteFor = null;
            }
        }
        else
        {
            ImGui.SetCursorScreenPos(notePos);
            if (ImGui.InvisibleButton("##profile-note", noteBoxSize))
            {
                editingNoteFor = friend.ContentId;
                noteDraft = note ?? string.Empty;
                focusNoteInput = true;
            }

            var noteHovered = ImGui.IsItemHovered();
            if (noteHovered)
            {
                draw.AddRectFilled(notePos, notePos + noteBoxSize, ChatVisuals.Color(ChatVisuals.ListBg), 8f * scale);
                ImGui.SetTooltip("Click to edit note");
            }

            var noteColor = string.IsNullOrEmpty(note) ? ChatVisuals.Placeholder : ChatVisuals.Muted;
            var textOrigin = new Vector2(
                notePos.X + (noteBoxSize.X - noteTextSize.X) * 0.5f,
                notePos.Y + (noteBoxSize.Y - noteTextSize.Y) * 0.5f);
            ChatVisuals.AddText(draw, textOrigin, noteFontSize, ChatVisuals.Color(noteColor), displayNote,
                MathF.Max(40f * scale, maxNoteWidth - notePad.X * 2f));
        }

        var badgePos = new Vector2(centerX - badgeSize.X * 0.5f, notePos.Y + noteBoxSize.Y + 14f * scale);
        draw.AddRect(badgePos, badgePos + badgeSize, ChatVisuals.Color(ChatVisuals.BadgeBorder),
            badgeSize.Y * 0.5f, ImDrawFlags.None, MathF.Max(1.5f, 1.6f * scale));
        ChatVisuals.AddText(draw, badgePos + badgePad, badgeFontSize, ChatVisuals.Color(ChatVisuals.BadgeText), worldLabel);
    }

    private void DrawConversationPane(ImDrawListPtr draw, FriendSnapshot snapshot, Vector2 pos, Vector2 size, bool narrow,
        float scale)
    {
        var bottomCorners = narrow ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersBottomRight;
        draw.AddRectFilled(pos, pos + size, ChatVisuals.Color(ChatVisuals.White), 10f * scale, bottomCorners);

        var subHeaderH = 42f * scale;
        if (selected is not { } peer || conversation is not { } view)
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

        DrawSubHeader(draw, pos, size.X, subHeaderH, peer.Name, peer.World, showBack: narrow, scale);
        view.Update();

        var draft = drafts[peer.ContentId];
        var friend = snapshot.FindRecipient(peer.ContentId, out var unavailable);
        var warning = draft.Warning is { AttachedToMessage: false } local ? local : unassignedWarning;
        var status = unavailable ?? (warning is { AttachedToMessage: false } visible ? visible.Message : null);
        var statusIsAlert = unavailable is null;
        var writeFailed = capture.FailedWriteOwner == session!.ContentId;
        var showTyping = friend is not null
            && tellInputFocused
            && tellInputFocusedPeer == peer.ContentId
            && !string.IsNullOrWhiteSpace(draft.Text);

        var bannerMaxWidth = MathF.Max(80f * scale, size.X - 36f * scale);
        ChatVisuals.BannerLayout? statusBanner = status is not null
            ? ChatVisuals.MeasureSystemBanner(status, bannerMaxWidth, scale)
            : null;
        const string writeErrorText = "A Tell could not be saved. See /xllog for details.";
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

        using var peerId = ImRaii.PushId(peer.ContentId.ToString("X16"));
        ImGui.SetCursorScreenPos(timelinePos);
        using (var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (var timeline = ImRaii.Child("##messages", new Vector2(size.X, timelineHeight), false))
        {
            if (timeline)
                DrawTimeline(view, peer, timelineContentWidth, showTyping, scale);
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
        DrawSendBar(draw, peer, friend, draft, sendBarPos, new Vector2(size.X, sendBarHeight), bottomCorners, scale);
    }

    private void DrawSubHeader(ImDrawListPtr draw, Vector2 pos, float width, float height, string title, string? world,
        bool showBack, float scale)
    {
        var left = pos.X + 14f * scale;
        if (showBack)
        {
            var btnSize = 28f * scale;
            var btnPos = new Vector2(pos.X + 8f * scale, pos.Y + (height - btnSize) * 0.5f);
            ImGui.SetCursorScreenPos(btnPos);
            if (ImGui.InvisibleButton("##narrow-back", new Vector2(btnSize)))
                narrowShowingDetail = false;

            if (ImGui.IsItemHovered())
                draw.AddRectFilled(btnPos, btnPos + new Vector2(btnSize), ChatVisuals.Color(ChatVisuals.ListHover), 6f * scale);

            ChatVisuals.DrawBackIcon(draw, btnPos + new Vector2(btnSize * 0.5f), 14f * scale,
                ChatVisuals.Color(ChatVisuals.Ink), MathF.Max(1.6f, 1.8f * scale));
            left = btnPos.X + btnSize + 8f * scale;
        }

        var rightLimit = pos.X + width - 14f * scale;
        var avail = MathF.Max(30f * scale, rightLimit - left);
        var titleFontSize = ImGui.GetFontSize() * 1.06f;

        if (!string.IsNullOrEmpty(world))
        {
            var worldFontSize = ImGui.GetFontSize() * 0.90f;
            var worldLabel = $"@ {world}";
            var worldSize = ChatVisuals.CalcTextSize(worldLabel, worldFontSize);
            var titleMax = MathF.Max(30f * scale, avail - worldSize.X - 8f * scale);
            var titleText = ChatVisuals.TruncateSingleLine(title, titleFontSize, titleMax);
            var titleSize = ChatVisuals.CalcTextSize(titleText, titleFontSize);
            var titleY = pos.Y + (height - titleSize.Y) * 0.5f;
            ChatVisuals.AddBoldText(draw, new Vector2(left, titleY), titleFontSize, ChatVisuals.Color(ChatVisuals.Ink),
                titleText, scale);

            var worldX = left + titleSize.X + 8f * scale;
            var worldY = pos.Y + (height - worldSize.Y) * 0.5f;
            var clampedWorld = ChatVisuals.TruncateSingleLine(worldLabel, worldFontSize, MathF.Max(0, rightLimit - worldX));
            ChatVisuals.AddText(draw, new Vector2(worldX, worldY), worldFontSize, ChatVisuals.Color(ChatVisuals.Muted),
                clampedWorld);
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

    private void DrawTimeline(ConversationHistory view, PlayerIdentity peer, float contentWidth, bool showTyping, float scale)
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
        bool? previousOutgoing = null;
        var previousHadRejection = false;

        foreach (var message in view.Messages)
        {
            var showTimeDivider = ChatVisuals.ShouldInsertTimeDivider(previousSentAt, message.SentAt);
            var isGroupStart = previousOutgoing is null || previousOutgoing.Value != message.Outgoing
                || showTimeDivider || previousHadRejection;

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

            DrawMessageBubble(message, message.Outgoing ? session!.Player : peer, isGroupStart, availWidth, scale);

            if (message.Rejection is { } rejection)
                DrawTimelineBanner(rejection, isAlert: true, availWidth, scale);

            previousSentAt = message.SentAt;
            previousOutgoing = message.Outgoing;
            previousHadRejection = message.Rejection is not null;
        }

        if (showTyping)
        {
            var typingGroupStart = previousOutgoing is null || !previousOutgoing.Value || previousHadRejection;
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

    private void DrawMessageBubble(TellMessage message, PlayerIdentity author, bool isGroupStart, float availWidth, float scale)
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

        var showName = isGroupStart && !message.Outgoing;
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
                var nameText = ChatVisuals.TruncateSingleLine(author.Name, nameFontSize,
                    MathF.Max(40f * scale, innerWidth - avatarOffset));
                ChatVisuals.AddText(draw, new Vector2(bubbleX, origin.Y), nameFontSize,
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
        var bubbleSize = new Vector2(62f, 32f) * scale;
        var contentHeight = isGroupStart && showAvatarSlot
            ? MathF.Max(avatarSize, bubbleSize.Y)
            : bubbleSize.Y;
        var totalHeight = contentHeight + 4f * scale;

        if (ImGui.IsRectVisible(origin, origin + new Vector2(availWidth, totalHeight)))
        {
            var draw = ImGui.GetWindowDrawList();
            if (isGroupStart && showAvatarSlot)
            {
                var avatarX = origin.X + availWidth - sideMargin - avatarSize;
                ChatVisuals.Avatar(draw, avatars, self, new Vector2(avatarX, origin.Y), avatarSize);
            }

            var bubbleX = origin.X + availWidth - sideMargin - avatarOffset - bubbleSize.X;
            ChatVisuals.DrawTypingBubble(draw, new Vector2(bubbleX, origin.Y), bubbleSize,
                (float)ImGui.GetTime(), isGroupStart, scale);
        }

        ImGui.Dummy(new Vector2(availWidth, totalHeight));
    }

    private void DrawSendBar(ImDrawListPtr draw, PlayerIdentity peer, PlayerIdentity? friend, Draft draft, Vector2 pos,
        Vector2 size, ImDrawFlags bottomCorners, float scale)
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
            enter = ImGui.InputTextWithHint("##tell", "Write a Tell...", ref draft.Text, 2048,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var inputActive = ImGui.IsItemActive();
        var canSend = friend is not null;
        if (enter && canSend)
        {
            SubmitDraft(peer, draft);
            ImGui.SetKeyboardFocusHere(-1);
        }

        tellInputFocused = inputActive;
        tellInputFocusedPeer = inputActive ? peer.ContentId : null;

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

        if (clicked && canSend)
            SubmitDraft(peer, draft);
    }

    private void SyncSession(PlayerSession? active)
    {
        if (ReferenceEquals(session, active))
            return;

        session = active;
        inspectedFriend = null;
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
        editingNoteFor = null;
        noteDraft = string.Empty;
        contextPeer = null;
        contextMessageBody = null;
        tellInputFocused = false;
        tellInputFocusedPeer = null;
        wasShowingTyping = false;
    }

    private void SyncPeers(FriendSnapshot snapshot)
    {
        if (snapshot.Friends is null)
            return;

        if (selected is { } currentSelected
            && snapshot.Friends.TryGetValue(currentSelected.ContentId, out var refreshedSelected))
        {
            selected = refreshedSelected;
        }

        if (inspectedFriend is not { } friend)
            return;

        if (snapshot.Friends.TryGetValue(friend.ContentId, out var current))
        {
            inspectedFriend = current;
        }
        else
        {
            inspectedFriend = null;
            editingNoteFor = null;
            if (activeTab == NavTab.Contacts)
                narrowShowingDetail = false;
        }
    }

    private void UpdateSummaries()
    {
        if (summaryQuery is { IsCompleted: true } completed)
        {
            summaryQuery = null;
            if (completed.IsCompletedSuccessfully)
            {
                summaries = completed.Result;
                Reorder();
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
                foreach (var (cid, value) in completed.Result)
                    notes[cid] = value;
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
        if (editingNoteFor is not { } peerId)
            return;

        SaveFriendNote(peerId, noteDraft);
        editingNoteFor = null;
    }

    private void SaveFriendNote(ulong peerContentId, string rawNote)
    {
        if (session is null)
            return;

        var trimmed = rawNote.Trim();
        if (trimmed.Length == 0)
            notes.Remove(peerContentId);
        else
            notes[peerContentId] = trimmed;

        _ = history.SaveNoteAsync(session.ContentId, peerContentId, trimmed);
    }

    private void DeleteChatFromList(PlayerIdentity peer)
    {
        if (session is null)
            return;

        _ = history.HideConversationAsync(session.ContentId, peer.ContentId);
        summaries = summaries.Where(summary => summary.Peer.ContentId != peer.ContentId).ToArray();
        if (selected?.ContentId == peer.ContentId)
        {
            selected = null;
            conversation = null;
            narrowShowingDetail = false;
            tellInputFocused = false;
            tellInputFocusedPeer = null;
            wasShowingTyping = false;
        }

        Reorder();
    }

    private void OpenConversation(PlayerIdentity peer)
    {
        activeTab = NavTab.Chats;
        narrowShowingDetail = true;
        _ = history.UnhideConversationAsync(session!.ContentId, peer.ContentId);
        if (selected?.ContentId == peer.ContentId)
            return;

        selected = peer;
        conversation = new ConversationHistory(history, session!.ContentId, peer.ContentId);
        firstMessageY = 0;
        tellInputFocused = false;
        tellInputFocusedPeer = null;
        wasShowingTyping = false;
        drafts.TryAdd(peer.ContentId, new Draft());
        Reorder();
    }

    private void Reorder()
    {
        IEnumerable<ConversationSummary> visible = summaries;
        if (selected is { } peer && !summaries.Any(row => row.Peer.ContentId == peer.ContentId))
            visible = visible.Append(new ConversationSummary(peer, null));

        IOrderedEnumerable<ConversationSummary> ranked;
        if (sortByRecent)
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.Latest is null ? 1 : 0)
                    .ThenBy(row => row.Latest?.SentAt)
                    .ThenBy(row => row.Latest?.Id)
                    .ThenBy(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.Latest?.SentAt)
                    .ThenByDescending(row => row.Latest?.Id)
                    .ThenBy(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ranked = sortAscending
                ? visible.OrderBy(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(row => row.Peer.Name, StringComparer.OrdinalIgnoreCase);
        }

        rows = ranked
            .ThenBy(row => row.Peer.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Peer.WorldId)
            .ThenBy(row => row.Peer.ContentId)
            .ToArray();
    }

    private static string FormatPreview(TellMessage latest) =>
        latest.Outgoing ? $"You: {latest.Body}" : latest.Body;

    private static bool MatchesSearch(PlayerIdentity peer, string query, string? note = null)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return true;

        return peer.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || peer.World.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(note) && note.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void SubmitDraft(PlayerIdentity peer, Draft draft)
    {
        if (!sender.TrySubmit(session!, peer, draft.Text, out var error))
        {
            draft.Warning = new TellWarning(session!, peer.ContentId, error);
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

            if (warning.Peer is { } peer)
            {
                if (!drafts.TryGetValue(peer, out var draft))
                {
                    draft = new Draft();
                    drafts.Add(peer, draft);
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
        Chats,
    }

    private sealed class Draft
    {
        public string Text = string.Empty;
        public TellWarning? Warning;
    }
}
