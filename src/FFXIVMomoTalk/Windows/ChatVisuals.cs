using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVMomoTalk.Windows;

internal static partial class ChatVisuals
{
    public const long TimeDividerGapMs = 5L * 60 * 1000;

    // Default MomoTalk palette extracted from u1805/momotalk
    public static readonly Vector4 HeaderPink = Hex(0xFC96AB);
    public static readonly Vector4 HeaderPinkHover = Hex(0xF27D96);
    public static readonly Vector4 HeaderPinkActive = Hex(0xE26A84);
    public static readonly Vector4 CoverPink = Hex(0xFB94A7);
    public static readonly Vector4 ModalBackdrop = Hex(0x000000, 0.50f);
    public static readonly Vector4 ModalDivider = Hex(0xE7EBEC);
    public static readonly Vector4 ModalButtonBg = Hex(0xE6ECF1);
    public static readonly Vector4 ModalButtonHover = Hex(0xD5E0E9);
    public static readonly Vector4 ModalButtonActive = Hex(0xC0D0DD);
    public static readonly Vector4 ModalButtonBorder = Hex(0xCED8E0);
    public static readonly Vector4 ModalButtonHoverBorder = Hex(0x9BB1C4);
    public static readonly Vector4 SwitchOffBg = Hex(0xE7EBEC);
    public static readonly Vector4 RailBg = Hex(0x4B5A6F);
    public static readonly Vector4 RailActive = Hex(0x68788F);
    public static readonly Vector4 ListBg = Hex(0xF3F7F8);
    public static readonly Vector4 ListHover = Hex(0xE1E7EC);
    public static readonly Vector4 Divider = Hex(0xE1E7EC);
    public static readonly Vector4 InputBorder = Hex(0xDCE5EC);
    public static readonly Vector4 InputFocus = Hex(0xFFB342);
    public static readonly Vector4 White = Hex(0xFFFFFF);
    public static readonly Vector4 Ink = Hex(0x2A323E);
    public static readonly Vector4 Muted = Hex(0x87929E);
    public static readonly Vector4 Placeholder = Hex(0x9E9EA7);
    public static readonly Vector4 IncomingBubble = Hex(0x4B5A6F);
    public static readonly Vector4 OutgoingBubble = Hex(0x4A8AC6);
    public static readonly Vector4 BubbleText = Hex(0xECF2FB);
    public static readonly Vector4 SystemBg = Hex(0xE1E7EC);
    public static readonly Vector4 SystemText = Hex(0x5D6B7A);
    public static readonly Vector4 AlertText = Hex(0xA6324C);
    public static readonly Vector4 DangerHoverBg = Hex(0xFDECEF);
    public static readonly Vector4 SendBarBg = Hex(0xEEEEEE);
    public static readonly Vector4 SendIconIdle = Hex(0xBDBDBD);
    public static readonly Vector4 BadgeText = Hex(0x5D7C8C);
    public static readonly Vector4 BadgeBorder = Hex(0xC2CFD6);
    public static readonly Vector4 AnniversaryHeaderBg = Hex(0xFFF0F4);
    public static readonly Vector4 AnniversaryRowBg = Hex(0xFFF9FB);
    public static readonly Vector4 AnniversaryRowHover = Hex(0xFCEAF0);
    public static readonly Vector4 AnniversaryAccent = Hex(0xE56A87);
    public static readonly Vector4 AnniversaryDivider = Hex(0xF5D5DF);
    public static readonly Vector4 AnniversaryBadgeBg = Hex(0xFDE2EA);
    public static readonly Vector4 AvatarFallbackBg = Hex(0xD5DEE6);
    public static readonly Vector4 AvatarFallbackFg = Hex(0x7B8998);
    public static readonly Vector4 GroupFcBg = Hex(0xE09258);
    public static readonly Vector4 GroupCwlsBg = Hex(0x4AA3A8);
    public static readonly Vector4 GroupLsBg = Hex(0x5EA47C);
    public static readonly Vector4 GroupStaticBg = Hex(0x4A8AC6);
    public static readonly Vector4 MatchSuccessBg = Hex(0xE4F2EC);
    public static readonly Vector4 MatchSuccessText = Hex(0x2F7B58);
    public static readonly Vector4 ReplyCardBg = Hex(0xEFF5F8);
    public static readonly Vector4 ReplyCardBorder = Hex(0xD9E2E8);
    public static readonly Vector4 ReplyCardAccent = Hex(0x3E95E8);
    public static readonly Vector4 ReplyCardDivider = Hex(0xCBD5E1);
    public static readonly Vector4 ReplyCardGeoLight = Hex(0xDCEEF9, 0.72f);
    public static readonly Vector4 ReplyCardGeoMid = Hex(0xCBE2F0, 0.55f);
    public static readonly Vector4 ReplyCardGeoDeep = Hex(0xB9D8EC, 0.40f);
    public static readonly Vector4 ReplyOptionHover = Hex(0xF4F8FB);
    public static readonly Vector4 ReplyOptionActive = Hex(0xE6F0F8);
    public static readonly Vector4 ReplyOptionBorder = Hex(0xDCE5EC);
    public static readonly Vector4 ReplyOptionShadow = Hex(0x2D3A4B, 0.10f);
    public static readonly Vector4 Shadow = Hex(0x2D2342, 0.16f);
    public static readonly Vector4 WindowBorder = Hex(0x2D2342, 0.20f);

    public static Vector4 Hex(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static Vector4 WithAlpha(Vector4 rgba, float alpha) => new(rgba.X, rgba.Y, rgba.Z, alpha);

    public static Vector4 ScaleAlpha(Vector4 rgba, float factor) => new(rgba.X, rgba.Y, rgba.Z, rgba.W * factor);

    public static uint Color(Vector4 rgba) => ImGui.ColorConvertFloat4ToU32(rgba);

    public static Vector2 CalcTextSize(string text, float fontSize, float wrapWidth = 0f)
    {
        if (string.IsNullOrEmpty(text))
            return new Vector2(0, fontSize);

        return ImGui.CalcTextSizeA(ImGui.GetFont(), fontSize, float.MaxValue, wrapWidth, text, out _);
    }

    public static void AddText(ImDrawListPtr draw, Vector2 pos, float fontSize, uint col, string text, float wrapWidth = 0f) =>
        draw.AddText(ImGui.GetFont(), fontSize, pos, col, text, wrapWidth);

    public static void AddBoldText(ImDrawListPtr draw, Vector2 pos, float fontSize, uint col, string text, float scale)
    {
        var font = ImGui.GetFont();
        var offset = MathF.Max(0.55f, 0.6f * scale);
        draw.AddText(font, fontSize, pos, col, text);
        draw.AddText(font, fontSize, pos + new Vector2(offset, 0), col, text);
    }

    public static string TruncateSingleLine(string text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return string.Empty;

        var singleLine = NormalizeSingleLine(text);
        if (singleLine.Length == 0)
            return string.Empty;

        if (CalcTextSize(singleLine, fontSize).X <= maxWidth)
            return singleLine;

        const string ellipsis = "...";
        var ellipsisWidth = CalcTextSize(ellipsis, fontSize).X;
        if (ellipsisWidth >= maxWidth)
            return ellipsis;

        var low = 0;
        var high = singleLine.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = singleLine[..mid];
            if (CalcTextSize(candidate, fontSize).X + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low == 0 ? ellipsis : singleLine[..low].TrimEnd() + ellipsis;
    }

    private static string NormalizeSingleLine(string text)
    {
        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        var lastWasSpace = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    buffer[length++] = ' ';
                    lastWasSpace = true;
                }
            }
            else
            {
                buffer[length++] = ch;
                lastWasSpace = false;
            }
        }

        while (length > 0 && buffer[length - 1] == ' ')
            length--;

        return new string(buffer[..length]);
    }
}
