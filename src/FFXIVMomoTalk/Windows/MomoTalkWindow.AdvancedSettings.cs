using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFXIVMomoTalk.Windows;

internal sealed partial class MomoTalkWindow
{
    private AdvancedSettingsMetrics MeasureAdvancedSettings(
        float innerWidth,
        float labelFontSize,
        float labelHeight,
        float inputHeight,
        float fieldGap,
        float uiScale)
    {
        var hintText = Loc.Text("MessageFilterHint");
        var hintFontSize = labelFontSize * 0.92f;
        var hintHeight = ChatVisuals.CalcTextSize(hintText, hintFontSize, innerWidth).Y;

        var senderError = config.SenderFilterRegexError is { } senderErr
            ? Loc.Format("InvalidRegex", senderErr)
            : null;
        var senderBanner = senderError is not null
            ? ChatVisuals.MeasureSystemBanner(senderError, innerWidth, uiScale)
            : (ChatVisuals.BannerLayout?)null;

        var messageError = config.MessageFilterRegexError is { } messageErr
            ? Loc.Format("InvalidRegex", messageErr)
            : null;
        var messageBanner = messageError is not null
            ? ChatVisuals.MeasureSystemBanner(messageError, innerWidth, uiScale)
            : (ChatVisuals.BannerLayout?)null;

        var bannerGap = 6f * uiScale;
        var bodyHeight = hintHeight + fieldGap
            + labelHeight + inputHeight
            + (senderBanner is { } sb ? bannerGap + sb.Height : 0f)
            + fieldGap
            + labelHeight + inputHeight
            + (messageBanner is { } mb ? bannerGap + mb.Height : 0f);

        return new AdvancedSettingsMetrics(
            innerWidth,
            labelFontSize,
            labelHeight,
            inputHeight,
            fieldGap,
            uiScale,
            hintText,
            hintFontSize,
            hintHeight,
            senderError,
            senderBanner,
            messageError,
            messageBanner,
            bodyHeight);
    }

    private float DrawAdvancedSettings(
        ImDrawListPtr draw,
        float bannerOriginX,
        float leftX,
        float cursorY,
        float cardWidth,
        in AdvancedSettingsMetrics metrics,
        float alpha,
        bool interactive)
    {
        var ink = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Ink, alpha));
        var muted = ChatVisuals.Color(ChatVisuals.ScaleAlpha(ChatVisuals.Muted, alpha));
        var bannerGap = 6f * metrics.UiScale;

        ChatVisuals.AddText(draw, new Vector2(leftX, cursorY), metrics.HintFontSize, muted, metrics.HintText, metrics.InnerWidth);
        cursorY += metrics.HintHeight + metrics.FieldGap;

        ChatVisuals.AddBoldText(draw, new Vector2(leftX, cursorY), metrics.LabelFontSize, ink,
            Loc.Text("SenderFilterRegex"), metrics.UiScale);
        cursorY += metrics.LabelHeight;

        var senderPattern = config.SenderFilterRegex;
        if (DrawSettingTextField(draw, "##adv-sender-filter", "^\\[CT\\]",
            new Vector2(leftX, cursorY), metrics.InnerWidth, metrics.InputHeight, ref senderPattern, 512,
            ImGuiInputTextFlags.None, metrics.UiScale, alpha, interactive))
        {
            config.SenderFilterRegex = senderPattern;
            saveConfig();
        }

        cursorY += metrics.InputHeight;
        if (metrics.SenderBanner is { } senderBanner && !string.IsNullOrEmpty(metrics.SenderError))
        {
            cursorY += bannerGap;
            ChatVisuals.DrawSystemBanner(draw, new Vector2(bannerOriginX, cursorY), cardWidth,
                metrics.SenderError, isAlert: true, metrics.UiScale, senderBanner, alpha);
            cursorY += senderBanner.Height;
        }

        cursorY += metrics.FieldGap;
        ChatVisuals.AddBoldText(draw, new Vector2(leftX, cursorY), metrics.LabelFontSize, ink,
            Loc.Text("MessageFilterRegex"), metrics.UiScale);
        cursorY += metrics.LabelHeight;

        var messagePattern = config.MessageFilterRegex;
        if (DrawSettingTextField(draw, "##adv-message-filter", "^\\[CT\\]|spam",
            new Vector2(leftX, cursorY), metrics.InnerWidth, metrics.InputHeight, ref messagePattern, 512,
            ImGuiInputTextFlags.None, metrics.UiScale, alpha, interactive))
        {
            config.MessageFilterRegex = messagePattern;
            saveConfig();
        }

        cursorY += metrics.InputHeight;
        if (metrics.MessageBanner is { } messageBanner && !string.IsNullOrEmpty(metrics.MessageError))
        {
            cursorY += bannerGap;
            ChatVisuals.DrawSystemBanner(draw, new Vector2(bannerOriginX, cursorY), cardWidth,
                metrics.MessageError, isAlert: true, metrics.UiScale, messageBanner, alpha);
            cursorY += messageBanner.Height;
        }

        return cursorY;
    }

    private readonly record struct AdvancedSettingsMetrics(
        float InnerWidth,
        float LabelFontSize,
        float LabelHeight,
        float InputHeight,
        float FieldGap,
        float UiScale,
        string HintText,
        float HintFontSize,
        float HintHeight,
        string? SenderError,
        ChatVisuals.BannerLayout? SenderBanner,
        string? MessageError,
        ChatVisuals.BannerLayout? MessageBanner,
        float BodyHeight);
}
