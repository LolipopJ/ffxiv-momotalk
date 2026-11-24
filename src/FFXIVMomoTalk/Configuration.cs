using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace FFXIVMomoTalk;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public const int DefaultContextWindowMinutes = 10;
    public const int MinContextWindowMinutes = 1;
    public const int MaxContextWindowMinutes = 1440;

    public const int DefaultContextMaxMessages = 100;
    public const int MinContextMaxMessages = 1;
    public const int MaxContextMaxMessages = 500;

    public const int DefaultContextMinMessages = 5;
    public const int MinContextMinMessages = 0;
    public const int MaxContextMinMessages = 500;

    public const string DefaultLlmPrompt =
        "You are a player chatting casually in FINAL FANTASY XIV.\n"
        + "Suggest 1 to 3 short, natural, everyday single-line replies\n"
        + "that fit the recent conversation with varied tones or angles.\n"
        + "Always match the language used in the recent messages\n"
        + "(e.g. Chinese, English, or Japanese).\n"
        + "Avoid robotic phrasing, long paragraphs, or explanations.";

    private string llmEndpoint = string.Empty;
    private string llmApiKey = string.Empty;
    private string llmModel = string.Empty;
    private string llmPrompt = DefaultLlmPrompt;
    private int llmContextWindowMinutes = DefaultContextWindowMinutes;
    private int llmContextMaxMessages = DefaultContextMaxMessages;
    private int llmContextMinMessages = DefaultContextMinMessages;

    public int Version { get; set; } = CurrentVersion;

    public string? UiLanguage { get; set; }
    public bool ShowFloatingEntry { get; set; } = true;

    public bool ShowIntroAnimation { get; set; } = true;

    public bool ShowSelfAvatar { get; set; }

    public float? EntryOffsetX { get; set; }

    public float? EntryOffsetY { get; set; }

    public string LlmEndpoint
    {
        get => llmEndpoint;
        set => llmEndpoint = value ?? string.Empty;
    }

    public string LlmApiKey
    {
        get => llmApiKey;
        set => llmApiKey = value ?? string.Empty;
    }

    public string LlmModel
    {
        get => llmModel;
        set => llmModel = value ?? string.Empty;
    }

    public string LlmPrompt
    {
        get => llmPrompt;
        set => llmPrompt = value ?? string.Empty;
    }

    public int LlmContextWindowMinutes
    {
        get => llmContextWindowMinutes;
        set => llmContextWindowMinutes = Math.Clamp(value, MinContextWindowMinutes, MaxContextWindowMinutes);
    }

    public int LlmContextMaxMessages
    {
        get => llmContextMaxMessages;
        set => llmContextMaxMessages = Math.Clamp(value, MinContextMaxMessages, MaxContextMaxMessages);
    }

    public int LlmContextMinMessages
    {
        get => llmContextMinMessages;
        set => llmContextMinMessages = Math.Clamp(value, MinContextMinMessages, MaxContextMinMessages);
    }

    [JsonIgnore]
    public bool IsAiReplyConfigured =>
        !string.IsNullOrWhiteSpace(LlmEndpoint)
        && !string.IsNullOrWhiteSpace(LlmApiKey)
        && !string.IsNullOrWhiteSpace(LlmModel);

    [JsonIgnore]
    public bool PreserveExistingFile { get; private set; }

    [JsonIgnore]
    public Vector2? EntryPosition
    {
        get => EntryOffsetX is { } x && EntryOffsetY is { } y ? new Vector2(x, y) : null;
        set
        {
            EntryOffsetX = value?.X;
            EntryOffsetY = value?.Y;
        }
    }

    public static Configuration Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        if (!pluginInterface.ConfigFile.Exists)
            return new Configuration();

        try
        {
            if (pluginInterface.GetPluginConfig() is Configuration { Version: CurrentVersion } existing)
                return existing;
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to read existing plugin configuration; preserving the file on disk.");
            return new Configuration { PreserveExistingFile = true };
        }

        log.Error("Existing plugin configuration is unrecognized or unsupported; preserving the file on disk.");
        return new Configuration { PreserveExistingFile = true };
    }

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        if (PreserveExistingFile)
            return;

        pluginInterface.SavePluginConfig(this);
    }
}
