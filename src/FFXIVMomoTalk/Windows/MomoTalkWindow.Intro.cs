using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
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
}
