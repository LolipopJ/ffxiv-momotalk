using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow : Window
{
    private const float NarrowThreshold = 640f;
    private const string LogoResourceName = "FFXIVMomoTalk.Assets.momo-logo.png";
    private const string CoverResourceName = "FFXIVMomoTalk.Assets.momotalk-cover.png";
    private const float NativeListButtonHeight = 28f;
    private const float IntroSolidDuration = 0.62f;
    private const float IntroTotalDuration = 0.84f;
    private const float SettingsTransitionDuration = 0.20f;

    private readonly SocialDirectory directory;
    private readonly MessageStore messages;
    private readonly NotesStore notesStore;
    private readonly StaticStore staticsStore;
    private readonly CacheStore cache;
    private readonly FriendProfileStore friendProfileStore;
    private readonly ChatCapture capture;
    private readonly ChatSender sender;
    private readonly AvatarCache avatars;
    private readonly AiReplyService aiReply;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly IPluginLog log;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly ISharedImmediateTexture coverTexture;
    private readonly Stopwatch introTimer = new();
    private readonly Dictionary<ConversationKey, Draft> drafts = new();
    private readonly Dictionary<ConversationKey, string> notes = new();
    private readonly Dictionary<ConversationKey, NoteEdit> noteEdits = new();
    private readonly Dictionary<ulong, List<string>> friendTags = new();
    private readonly Dictionary<ulong, uint> friendColors = new();
    private readonly HashSet<string> activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ConversationKey> hidden = new();
    private readonly Dictionary<long, (string Body, string? Footer, float WrapWidth, Vector2 TextSize, Vector2 FooterSize)> bubbleMeasurements = new();
    private nint bubbleFont;
    private float bubbleFontSize;

    private PlayerSession? session;
    private NavTab activeTab = NavTab.Chats;
    private PlayerIdentity? inspectedFriend;
    private ConversationDescriptor? inspectedGroup;
    private ConversationDescriptor? selected;
    private ConversationHistory? conversation;
    private IReadOnlyList<ConversationSummary> summaries = [];
    private ConversationSummary[] rows = [];
    private Task<IReadOnlyList<ConversationSummary>>? summaryQuery;
    private Task<IReadOnlyDictionary<ConversationKey, string>>? notesQuery;
    private Task<FriendCustomizations>? friendProfilesQuery;
    private Task<HashSet<ConversationKey>>? hiddenQuery;
    private long summaryRevision = -1;
    private long notesRevision = -1;
    private long friendProfilesRevision = -1;
    private long hiddenRevision = -1;
    private bool hiddenLoaded;
    private bool summariesLoaded;
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
    private bool showApiKey;
    private int aiExampleEditorRevision;
    private readonly WrappedPromptEditor aiPromptEditor = new();
    private float entrySwitchAnim;
    private float introSwitchAnim;
    private float selfAvatarSwitchAnim;
    private float friendAnniversariesSwitchAnim;
    private bool staticModalOpenedThisFrame;
    private bool focusStaticNameInput;
    private ActiveParty? createStaticParty;
    private string? renameStaticKey;
    private string staticNameDraft = string.Empty;
    private bool staticSaveFailed;
    private Task<StaticGroup?>? staticSave;
    private IReadOnlyDictionary<string, StaticGroup>? lastStatics;
    private ConversationKey? editingNoteFor;
    private bool focusNoteInput;
    private string tagDraft = string.Empty;
    private bool focusTagInput;
    private ulong? tagPopupFriendId;
    private Vector2 tagPopupAnchor;
    private ulong? tagSaveFriendId;
    private Task<IReadOnlyList<string>>? tagSaveTask;
    private bool tagSaveFailed;
    private ulong? colorPickerFriendId;
    private bool colorPickerOpen;
    private bool colorPickerLivePreview;
    private bool colorPickerDirty;
    private Vector3 colorPickerRgb;
    private string colorPickerHex = string.Empty;
    private bool colorPickerHexInvalid;
    private Vector2 colorPopupAnchor;
    private ulong? colorSaveFriendId;
    private Task? colorSaveTask;
    private bool colorSaveFailed;
    private Vector2 filterPopupAnchor;
    private PlayerIdentity? contextFriend;
    private ConversationDescriptor? contextConversation;
    private StaticGroup? contextStatic;
    private long? contextMessageId;
    private string? contextMessageBody;
    private bool contextMessageIsSystem;
    private ConversationKey? inputFocusedConversation;
    private bool inputFocused;
    private bool wasShowingTyping;
    private ConversationKey? lastReplyStateConv;
    private AiReplyStatus lastReplyStatus;
    private long lastReplyGenerationId;
    private long lastTranslationRevision;
    private string? dismissedUnavailableStores;

    public MomoTalkWindow(
        SocialDirectory directory,
        MessageStore messages,
        NotesStore notesStore,
        StaticStore staticsStore,
        CacheStore cache,
        FriendProfileStore friendProfileStore,
        ChatCapture capture,
        ChatSender sender,
        AvatarCache avatars,
        AiReplyService aiReply,
        Configuration config,
        Action saveConfig,
        ITextureProvider textures,
        IPluginLog log) : base("FFXIV MomoTalk")
    {
        this.directory = directory;
        this.messages = messages;
        this.notesStore = notesStore;
        this.staticsStore = staticsStore;
        this.cache = cache;
        this.friendProfileStore = friendProfileStore;
        this.capture = capture;
        this.sender = sender;
        this.avatars = avatars;
        this.aiReply = aiReply;
        this.config = config;
        this.saveConfig = saveConfig;
        this.log = log;
        logoTexture = textures.GetFromManifestResource(typeof(Plugin).Assembly, LogoResourceName);
        coverTexture = textures.GetFromManifestResource(typeof(Plugin).Assembly, CoverResourceName);
        entrySwitchAnim = config.ShowFloatingEntry ? 1f : 0f;
        introSwitchAnim = config.ShowIntroAnimation ? 1f : 0f;
        selfAvatarSwitchAnim = config.ShowSelfAvatar ? 1f : 0f;
        friendAnniversariesSwitchAnim = config.ShowFriendAnniversaries ? 1f : 0f;

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
        CloseSettings(immediate: true);
        settingsOpenedThisFrame = false;
        if (!staticSaveFailed)
            CloseStaticModal();
        CommitActiveNote();
        CommitPendingFriendColor();
        inputFocused = false;
        inputFocusedConversation = null;
        wasShowingTyping = false;
    }

    public override void PreDraw()
    {
        RespectCloseHotkey = !settingsOpen
            && staticModalMode == StaticModalMode.None
            && editingNoteFor is null
            && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup);
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
            UpdateHidden(snapshot);
            UpdateSummaries(snapshot);
            UpdateNotes();
            UpdateFriendProfiles();
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
            if (GetUnavailableStores(session.ContentId) is { } unavailableStores)
            {
                if (!StringComparer.Ordinal.Equals(unavailableStores, dismissedUnavailableStores))
                {
                    var storageError = Loc.Format("DatabaseError", unavailableStores);
                    var barHeight = DrawStorageErrorBar(draw, bodyPos, bodySize.X, storageError, scale, out var dismissed);
                    if (dismissed)
                        dismissedUnavailableStores = unavailableStores;
                    bodyPos.Y += barHeight;
                    bodySize.Y = MathF.Max(1f, bodySize.Y - barHeight);
                }
            }
            else
            {
                dismissedUnavailableStores = null;
            }

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
        var title = Loc.Text("AppName");
        var titleSize = ChatVisuals.CalcTextSize(title, titleFontSize);
        var titlePos = new Vector2(logoPos.X + logoSize + 6f * scale, winPos.Y + (height - titleSize.Y) * 0.5f);
        ChatVisuals.AddBoldText(draw, titlePos, titleFontSize, ChatVisuals.Color(ChatVisuals.White), title, scale);

        ImGui.SetCursorScreenPos(settingsPos);
        if (ImGui.InvisibleButton("##window-settings", new Vector2(btnSize)))
        {
            CommitActiveNote();
            CloseStaticModal();
            if (settingsOpen)
            {
                CloseSettings();
            }
            else
            {
                settingsOpen = true;
                settingsOpenedThisFrame = true;
            }
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
            ImGui.SetTooltip(Loc.Text("Settings"));

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

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void DrawLoggedOut(ImDrawListPtr draw, Vector2 bodyPos, Vector2 bodySize, float scale)
    {
        draw.AddRectFilled(bodyPos, bodyPos + bodySize, ChatVisuals.Color(ChatVisuals.White), 10f * scale,
            ImDrawFlags.RoundCornersBottom);
        var message = Loc.Text("LoginToViewChats");
        var fontSize = ImGui.GetFontSize();
        var maxTextWidth = MathF.Max(60f * scale, bodySize.X - 48f * scale);
        var textSize = ChatVisuals.CalcTextSize(message, fontSize, maxTextWidth);
        var pos = bodyPos + (bodySize - textSize) * 0.5f;
        ChatVisuals.AddText(draw, pos, fontSize, ChatVisuals.Color(ChatVisuals.Muted), message, maxTextWidth);
    }

    private static ConversationDescriptor LocalizeChannelName(SocialSnapshot snapshot, ConversationDescriptor conversation)
    {
        var key = conversation.Key;
        var channel = key.Kind switch
        {
            ConversationKind.FreeCompany => snapshot.FreeCompany,
            ConversationKind.Linkshell => snapshot.Linkshells.Values.FirstOrDefault(item => item.Key == key),
            ConversationKind.CrossWorldLinkshell => snapshot.CrossWorldLinkshells.Values.FirstOrDefault(item => item.Key == key),
            _ => null,
        };
        if (channel is not { HasRealName: false } || channel.Key != key || channel.Name != conversation.DisplayName)
            return conversation;

        var name = Loc.Text(key.Kind switch
        {
            ConversationKind.FreeCompany => "FreeCompany",
            ConversationKind.Linkshell => "Linkshell",
            _ => "CrossWorldLinkshell",
        });
        if (channel.Slot is { } slot)
            name = $"{name} {slot}";

        return conversation with { DisplayName = name };
    }

    private static string FormatChannelTag(SocialSnapshot snapshot, ConversationDescriptor conversation)
    {
        var label = conversation.Key.Kind switch
        {
            ConversationKind.Tell => conversation.WorldName,
            ConversationKind.FreeCompany => Loc.Text("FreeCompanyTag"),
            ConversationKind.Linkshell => Loc.Text("LinkshellTag"),
            ConversationKind.CrossWorldLinkshell => Loc.Text("CrossWorldLinkshellTag"),
            ConversationKind.Static => Loc.Text("Static"),
            _ => string.Empty,
        };
        return snapshot.FindActiveSlot(conversation.Key) is { } slot ? $"{label} {slot}" : label;
    }
}
