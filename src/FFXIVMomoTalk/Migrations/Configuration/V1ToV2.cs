using Dalamud.Plugin.Services;
using FFXIVMomoTalk.Chat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginConfiguration = FFXIVMomoTalk.Configuration;

namespace FFXIVMomoTalk.Migrations.Configuration;

internal static class V1ToV2
{
    private const string DefaultV1Prompt =
        "You are a player chatting casually in FINAL FANTASY XIV.\n"
        + "Suggest 1 to 3 short, natural, everyday single-line replies\n"
        + "that fit the recent conversation with varied tones or angles.\n"
        + "Always match the language used in the recent messages\n"
        + "(e.g. Chinese, English, or Japanese).\n"
        + "Avoid robotic phrasing, long paragraphs, or explanations.";

    public static PluginConfiguration? Migrate(
        string path,
        JObject json,
        IPluginLog log,
        Action<string, string>? replaceFile = null)
    {
        var step = "back up v1 settings";
        var backupBase = path + ".v1.bak";
        var backupPath = backupBase;
        string? tempPath = null;
        try
        {
            for (var copy = 1; File.Exists(backupPath); copy++)
                backupPath = $"{backupBase}.{copy}";

            File.Copy(path, backupPath, overwrite: false);
            log.Information("Configuration v1 backup created at {BackupPath}. To roll back, close the game and replace {ConfigPath} with a copy of this backup.",
                backupPath, path);

            step = "convert v1 settings";
            var promptToken = json["LlmPrompt"];
            if (promptToken is not null && promptToken.Type is not (JTokenType.Null or JTokenType.String))
                throw new JsonSerializationException("The v1 prompt must be text.");

            var oldPrompt = (string?)promptToken ?? string.Empty;
            var normalizedPrompt = oldPrompt.ReplaceLineEndings("\n").Trim();
            var isCustom = normalizedPrompt.Length > 0 && normalizedPrompt != DefaultV1Prompt;
            json["LlmPromptPreset"] = isCustom ? AiReplyPresets.CustomId : AiReplyPresets.DefaultId;
            json["LlmCustomPrompt"] = isCustom ? oldPrompt : AiReplyPresets.DefaultPrompt;
            json["LlmCustomExamples"] = new JArray();
            json.Remove("LlmPrompt");
            json["Version"] = 2;

            step = "deserialize and validate v2 settings";
            var migrated = PluginConfiguration.DeserializeCurrent(json);

            step = "write v2 temporary file";
            var stagingPath = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tempPath = stagingPath;
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(json.ToString(Formatting.Indented));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            step = "replace the configuration file";
            if (replaceFile is null)
                File.Move(tempPath, path, overwrite: true);
            else
                replaceFile(tempPath, path);
            tempPath = null;
            return migrated;
        }
        catch (Exception exception)
        {
            log.Error("Configuration migration v1 to v2 failed at {Step} ({ErrorType}). The existing file was preserved. Backup path: {BackupPath}.",
                step, exception.GetType().Name, backupPath);
            return null;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.Warning("Configuration migration v1 to v2 could not delete the temporary file at {Path} ({ErrorType}).",
                        tempPath, exception.GetType().Name);
                }
            }
        }
    }
}
