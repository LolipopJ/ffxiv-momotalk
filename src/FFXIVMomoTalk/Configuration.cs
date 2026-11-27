using System.Globalization;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVMomoTalk.Chat;
using FFXIVMomoTalk.Migrations.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FFXIVMomoTalk;

[Serializable]
public sealed class AiReplyExample : IAiReplyExample
{
    public string Context { get; set; } = string.Empty;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Replies { get; set; } = [string.Empty];

    IReadOnlyList<string> IAiReplyExample.Replies => Replies;
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;
    public const int MaxCustomExamples = 10;
    public const int MaxExampleReplies = 3;

    public const int DefaultContextWindowMinutes = 10;
    public const int MinContextWindowMinutes = 1;
    public const int MaxContextWindowMinutes = 1440;

    public const int DefaultContextMaxMessages = 100;
    public const int MinContextMaxMessages = 1;
    public const int MaxContextMaxMessages = 500;

    public const int DefaultContextMinMessages = 5;
    public const int MinContextMinMessages = 0;
    public const int MaxContextMinMessages = 500;

    private string llmEndpoint = string.Empty;
    private string llmApiKey = string.Empty;
    private string llmModel = string.Empty;
    private AiReplyPreset? llmPreset = AiReplyPresets.Default;
    private string llmCustomPrompt = AiReplyPresets.DefaultPrompt;
    private int llmContextWindowMinutes = DefaultContextWindowMinutes;
    private int llmContextMaxMessages = DefaultContextMaxMessages;
    private int llmContextMinMessages = DefaultContextMinMessages;

    public int Version { get; set; } = CurrentVersion;

    public string? UiLanguage { get; set; }
    public bool ShowFloatingEntry { get; set; } = true;

    public bool ShowIntroAnimation { get; set; } = true;

    public bool ShowSelfAvatar { get; set; }

    public bool ShowFriendAnniversaries { get; set; } = true;

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

    public string LlmPromptPreset
    {
        get => llmPreset?.Id ?? AiReplyPresets.CustomId;
        set => llmPreset = value == AiReplyPresets.CustomId ? null : AiReplyPresets.Find(value) ?? AiReplyPresets.Default;
    }

    public string LlmCustomPrompt
    {
        get => llmCustomPrompt;
        set => llmCustomPrompt = value ?? string.Empty;
    }

    public List<AiReplyExample> LlmCustomExamples { get; set; } = [];

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
    public bool IsCustomPreset => llmPreset is null;

    [JsonIgnore]
    public string EffectiveLlmPrompt =>
        llmPreset?.Prompt ?? (string.IsNullOrWhiteSpace(LlmCustomPrompt) ? AiReplyPresets.DefaultPrompt : LlmCustomPrompt);

    [JsonIgnore]
    public IReadOnlyList<IAiReplyExample> EffectiveLlmExamples =>
        llmPreset is { } preset ? preset.Examples : LlmCustomExamples;

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

    public void CopyPresetToCustom(string presetId)
    {
        if (AiReplyPresets.Find(presetId) is not { } preset)
            return;

        LlmCustomPrompt = preset.Prompt;
        LlmCustomExamples = preset.Examples.Select(example => new AiReplyExample
        {
            Context = example.Context,
            Replies = [.. example.Replies],
        }).ToList();
        LlmPromptPreset = AiReplyPresets.CustomId;
    }

    private bool HasValidExamples() =>
        LlmCustomExamples is { Count: <= MaxCustomExamples }
        && LlmCustomExamples.All(example => example is { Context: not null, Replies.Count: >= 1 and <= MaxExampleReplies }
            && example.Replies.All(reply => reply is not null));

    internal static Configuration DeserializeCurrent(JObject json)
    {
        var serializer = new JsonSerializer { MetadataPropertyHandling = MetadataPropertyHandling.Ignore };
        if (json.ToObject<Configuration>(serializer) is not { Version: CurrentVersion } config
            || !config.HasValidExamples())
        {
            throw new JsonSerializationException("Settings do not match the current configuration.");
        }

        return config;
    }

    public static Configuration Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        var file = pluginInterface.ConfigFile;
        if (!file.Exists)
            return new Configuration();

        long? version = null;
        var step = "read version";
        try
        {
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(file.FullName)));
            var json = new JsonSerializer { DateParseHandling = DateParseHandling.None, CheckAdditionalContent = true }
                .Deserialize<JObject>(reader);
            if (json?[nameof(Version)] is { Type: JTokenType.Integer } versionToken)
                version = versionToken.Value<long>();

            if (version == 1)
                return V1ToV2.Migrate(file.FullName, json!, log) ?? new Configuration { PreserveExistingFile = true };

            if (version == CurrentVersion)
            {
                step = "deserialize and validate settings";
                return DeserializeCurrent(json!);
            }

            log.Error("Configuration version check failed (source {SourceVersion}, target v{TargetVersion}). The existing file was preserved.",
                version?.ToString(CultureInfo.InvariantCulture) ?? "unknown", CurrentVersion);
        }
        catch (Exception exception)
        {
            log.Error("Configuration {Step} failed (source {SourceVersion}, target v{TargetVersion}, {ErrorType}). The existing file was preserved.",
                step, version?.ToString(CultureInfo.InvariantCulture) ?? "unknown", CurrentVersion, exception.GetType().Name);
        }

        return new Configuration { PreserveExistingFile = true };
    }

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        if (PreserveExistingFile)
            return;

        pluginInterface.SavePluginConfig(this);
    }
}
