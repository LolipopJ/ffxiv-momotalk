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

    public int Version { get; set; } = CurrentVersion;

    public bool ShowFloatingEntry { get; set; } = true;

    public bool ShowIntroAnimation { get; set; } = true;

    public float? EntryOffsetX { get; set; }

    public float? EntryOffsetY { get; set; }

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
