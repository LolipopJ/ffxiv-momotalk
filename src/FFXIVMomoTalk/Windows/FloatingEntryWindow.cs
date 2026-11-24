using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed class FloatingEntryWindow : Window
{
    private const string EntryResourceName = "FFXIVMomoTalk.Assets.entry.png";
    private const float BaseHeight = 64f;
    private const float AspectRatio = 197f / 256f;
    private const float HoverPadding = 6f;

    private readonly SocialDirectory directory;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly ISharedImmediateTexture entryTexture;
    private readonly Action onToggleMain;

    private bool dragging;
    private Vector2 dragWindowOrigin;
    private Vector2 dragMouseOrigin;
    private Vector2 dragCurrentPos;

    public FloatingEntryWindow(
        SocialDirectory directory,
        Configuration config,
        Action saveConfig,
        ITextureProvider textures,
        Action onToggleMain)
        : base("##FFXIVMomoTalkEntry")
    {
        this.directory = directory;
        this.config = config;
        this.saveConfig = saveConfig;
        this.onToggleMain = onToggleMain;
        entryTexture = textures.GetFromManifestResource(typeof(Plugin).Assembly, EntryResourceName);

        IsOpen = true;
        RespectCloseHotkey = false;
        ForceMainWindow = true;
        Flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove;
    }

    public override bool DrawConditions()
    {
        if (config.ShowFloatingEntry && directory.Current.Session is not null)
            return true;

        dragging = false;
        return false;
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var windowSize = ComputeWindowSize(scale);
        var viewport = ImGui.GetMainViewport();

        Vector2 desiredPos;
        if (dragging)
            desiredPos = ClampToViewport(dragCurrentPos, windowSize, viewport);
        else if (config.EntryPosition is { } saved)
            desiredPos = ClampToViewport(viewport.Pos + saved, windowSize, viewport);
        else
            desiredPos = viewport.Pos + (viewport.Size - windowSize) * 0.5f;

        ImGui.SetNextWindowPos(desiredPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pad = HoverPadding * scale;
        var imageSize = ComputeImageSize(scale);
        var windowSize = ComputeWindowSize(scale);
        var winPos = ImGui.GetWindowPos();

        ImGui.SetCursorPos(new Vector2(pad, pad));
        var clicked = ImGui.InvisibleButton("##momotalk-entry-btn", imageSize);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        if (ImGui.IsItemActivated())
        {
            dragging = false;
            dragWindowOrigin = winPos;
            dragMouseOrigin = ImGui.GetIO().MousePos;
            dragCurrentPos = winPos;
        }

        if (active)
        {
            var delta = ImGui.GetIO().MousePos - dragMouseOrigin;
            var threshold = 4f * scale;
            if (!dragging && delta.LengthSquared() >= threshold * threshold)
                dragging = true;

            if (dragging)
            {
                dragCurrentPos = ClampToViewport(dragWindowOrigin + delta, windowSize, ImGui.GetMainViewport());
                ImGui.SetWindowPos(dragCurrentPos);
                winPos = dragCurrentPos;
            }
        }

        var wasDragging = dragging;
        if (ImGui.IsItemDeactivated() && dragging)
        {
            dragging = false;
            var viewport = ImGui.GetMainViewport();
            config.EntryPosition = dragCurrentPos - viewport.Pos;
            saveConfig();
        }

        if (clicked && !wasDragging)
            onToggleMain();

        if (hovered && !dragging)
            ImGui.SetTooltip(Loc.Text("AppName"));

        if (entryTexture.GetWrapOrDefault() is not { } wrap)
            return;

        var zoom = dragging ? 1.04f : active ? 0.96f : hovered ? 1.06f : 1f;
        var center = winPos + windowSize * 0.5f;
        var half = imageSize * (0.5f * zoom);
        ImGui.GetWindowDrawList().AddImage(wrap.Handle, center - half, center + half);
    }

    private static Vector2 ComputeImageSize(float scale)
    {
        var height = BaseHeight * scale;
        return new Vector2(height * AspectRatio, height);
    }

    private static Vector2 ComputeWindowSize(float scale) =>
        ComputeImageSize(scale) + new Vector2(HoverPadding * 2f * scale);

    private static Vector2 ClampToViewport(Vector2 pos, Vector2 size, ImGuiViewportPtr viewport)
    {
        var maxX = MathF.Max(viewport.Pos.X, viewport.Pos.X + viewport.Size.X - size.X);
        var maxY = MathF.Max(viewport.Pos.Y, viewport.Pos.Y + viewport.Size.Y - size.Y);
        return new Vector2(
            Math.Clamp(pos.X, viewport.Pos.X, maxX),
            Math.Clamp(pos.Y, viewport.Pos.Y, maxY));
    }
}
