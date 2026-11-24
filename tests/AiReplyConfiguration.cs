#!/usr/bin/env dotnet
// dotnet run --file tests/AiReplyConfiguration.cs after building the plugin in Debug.
#:property PublishAot=false

using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string defaultV1Prompt = """
    You are a player chatting casually in FINAL FANTASY XIV.
    Suggest 1 to 3 short, natural, everyday single-line replies
    that fit the recent conversation with varied tones or angles.
    Always match the language used in the recent messages
    (e.g. Chinese, English, or Japanese).
    Avoid robotic phrasing, long paragraphs, or explanations.
    """;
const string customPrompt = "  今日は、\"夜\"に集合。\r\n用中文回复，路径是 C:\\raid。  ";

var pluginPath = Path.GetFullPath("src/FFXIVMomoTalk/bin/Debug/FFXIVMomoTalk.dll");
var dalamudPath = Environment.GetEnvironmentVariable("DALAMUD_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Path.GetDirectoryName(pluginPath)!, $"{name.Name}.dll");
    if (!File.Exists(path))
        path = Path.Combine(dalamudPath, $"{name.Name}.dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
};

var plugin = Assembly.LoadFrom(pluginPath);
var configType = plugin.GetType("FFXIVMomoTalk.Configuration", throwOnError: true)!;
var load = configType.GetMethod("Load")!;
var loadParameters = load.GetParameters();
var directory = Directory.CreateTempSubdirectory("momotalk-ai-config-");
var configPath = Path.Combine(directory.FullName, "FFXIVMomoTalk.json");
var backupPath = configPath + ".v1.bak";
var errors = new List<string>();
var host = ConfigTestProxy.Create(loadParameters[0].ParameterType, (method, _) => method.Name switch
{
    "get_ConfigFile" => new FileInfo(configPath),
    "GetPluginConfig" => null,
    "SavePluginConfig" => throw new InvalidOperationException("The protected configuration attempted to save."),
    _ => throw new NotSupportedException(method.Name),
});
var log = ConfigTestProxy.Create(loadParameters[1].ParameterType, (method, arguments) =>
{
    if (method.Name == "Error")
        errors.Add(string.Join(" ", arguments.Select(argument => argument is object[] values ? string.Join(" ", values) : argument)));
    return null;
});

try
{
    dynamic fresh = Load();
    Check(fresh.Version == 2, "Fresh configuration should initialize at v2.");
    Check(fresh.LlmPromptPreset == "default" && fresh.LlmCustomExamples.Count == 0 && !File.Exists(configPath),
        "Fresh configuration should select the default preset without writing a file.");

    var defaultV1 = V1Settings("\r\n" + defaultV1Prompt.ReplaceLineEndings("\r\n") + "\r\n");
    File.WriteAllText(configPath, defaultV1);
    dynamic migratedDefault = Load();
    Check(migratedDefault.Version == 2 && migratedDefault.LlmPromptPreset == "default",
        "An unchanged v1 prompt should migrate to the default preset.");
    Check(File.ReadAllText(backupPath) == defaultV1, "The v1 backup should contain the original configuration.");

    var customV1 = V1Settings(customPrompt);
    File.WriteAllText(configPath, customV1);
    using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        dynamic failed = Load();
        Check(failed.PreserveExistingFile && File.ReadAllText(configPath) == customV1,
            "A failed replacement should preserve the live file.");
        Check(errors.Any(error => error.Contains("Configuration") && error.Contains("1")
            && error.Contains("2") && error.Contains("replace", StringComparison.OrdinalIgnoreCase)),
            "A failed replacement should log the data domain, versions, and failed step.");
        failed.Save((dynamic)host);
    }

    dynamic migrated = Load();
    Check(migrated.Version == 2 && migrated.LlmPromptPreset == "custom" && migrated.LlmCustomPrompt == customPrompt
        && migrated.LlmCustomExamples.Count == 0 && !migrated.PreserveExistingFile,
        "Retry should migrate the custom prompt verbatim with empty custom examples.");
    Check(File.ReadAllText(backupPath) == defaultV1 && File.ReadAllText(backupPath + ".1") == customV1,
        "Retry should retain earlier backups without overwriting them.");
    Check(!directory.EnumerateFiles("*.tmp").Any(), "Migration should clean up temporary files.");

    var expected = JsonNode.Parse(customV1)!.AsObject();
    expected.Remove("Version");
    expected.Remove("LlmPrompt");
    var actual = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
    Check(!actual.ContainsKey("LlmPrompt") && expected.All(setting => JsonNode.DeepEquals(setting.Value, actual[setting.Key])),
        "Migration should remove the old prompt and preserve the remaining settings and type metadata.");

    dynamic reloaded = Load();
    Check(reloaded.LlmCustomPrompt == customPrompt && reloaded.LlmApiKey == "2026-10-01T00:00:00Z"
        && reloaded.UiLanguage == "ja" && reloaded.LlmContextMaxMessages == 18,
        "A valid v2 file should reload when the host configuration lookup returns null.");

    actual["LlmCustomExamples"] = JsonNode.Parse("""
        [{"$type":"FFXIVMomoTalk.AiReplyExample, FFXIVMomoTalk","Context":"次は？","Replies":["行こう。","明日でもいいよ。"]}]
        """);
    File.WriteAllText(configPath, actual.ToJsonString());
    dynamic savedExamples = Load();
    Check(!savedExamples.PreserveExistingFile && savedExamples.LlmCustomExamples[0].Context == "次は？"
        && savedExamples.LlmCustomExamples[0].Replies.Count == 2, "Reload should preserve saved examples and their exact reply counts.");

    File.Copy(backupPath + ".1", configPath, overwrite: true);
    Check(File.ReadAllText(configPath) == customV1 && JsonNode.Parse(File.ReadAllText(configPath))!["Version"]!.GetValue<int>() == 1,
        "Restoring the backup should recover the exact v1 configuration.");

    foreach (var unsupported in new[]
    {
        "{}", """{"Version":0}""", """{"Version":3}""", """{"Version":"1"}""", "{",
        """{"Version":2,"LlmCustomExamples":[{"Context":"Example","Replies":[]}]}""",
    })
    {
        File.WriteAllText(configPath, unsupported);
        var previousErrors = errors.Count;
        dynamic protectedConfig = Load();
        Check(protectedConfig.PreserveExistingFile && errors.Count == previousErrors + 1,
            "An unknown, future, or unreadable configuration should be reported and protected.");
        protectedConfig.Save((dynamic)host);
        Check(File.ReadAllText(configPath) == unsupported, "Saving should leave a protected configuration untouched.");
    }

    foreach (var preset in new[] { "default", "sensei", "tavern-reliable", "tavern-cunning", "tavern-wise", "tavern-aloof", "tavern-cheerful" })
    {
        reloaded.LlmPromptPreset = preset;
        var promptText = (string)reloaded.EffectiveLlmPrompt;
        var examples = ((IEnumerable)reloaded.EffectiveLlmExamples).Cast<object>().ToArray();
        var expectedExamples = preset == "sensei" ? 10 : 5;
        Check(reloaded.LlmPromptPreset == preset
            && !string.IsNullOrWhiteSpace(promptText)
            && !promptText.Contains('\r')
            && Encoding.UTF8.GetByteCount(promptText) <= 4096,
            $"Preset {preset} should supply a valid LF-only prompt within editor bounds.");
        Check(examples.Length == expectedExamples && examples.All(example =>
            {
                var context = Context(example);
                var exampleReplies = Replies(example).ToArray();
                return !string.IsNullOrWhiteSpace(context)
                    && !context.Contains('\r') && !context.Contains('\n')
                    && Encoding.UTF8.GetByteCount(context) <= 1024
                    && exampleReplies.Length == 3
                    && exampleReplies.All(reply =>
                        !string.IsNullOrWhiteSpace(reply)
                        && !reply.Contains('\r') && !reply.Contains('\n')
                        && Encoding.UTF8.GetByteCount(reply) <= 440);
            }),
            $"Preset {preset} should supply {expectedExamples} single-line examples with three bounded replies each.");
    }

    reloaded.LlmPromptPreset = "unknown";
    Check(reloaded.LlmPromptPreset == "default", "An unknown preset ID should select the default.");
    reloaded.LlmPromptPreset = "custom";
    Check(reloaded.LlmCustomPrompt == customPrompt && reloaded.LlmCustomExamples.Count == 0,
        "Selecting built-in presets should leave custom content intact.");
    reloaded.LlmPromptPreset = "tavern-wise";
    var wisePrompt = (string)reloaded.EffectiveLlmPrompt;
    var wiseReply = Replies(((IEnumerable)reloaded.EffectiveLlmExamples).Cast<object>().First()).First();
    reloaded.CopyPresetToCustom("tavern-wise");
    Check(reloaded.IsCustomPreset && reloaded.LlmCustomPrompt == wisePrompt
        && reloaded.LlmCustomExamples[0].Replies[0] == wiseReply, "Copy should switch to Custom with the complete preset.");
    reloaded.LlmCustomExamples[0].Replies[0] = "Change the copy.";
    reloaded.LlmPromptPreset = "tavern-wise";
    Check(Replies(((IEnumerable)reloaded.EffectiveLlmExamples).Cast<object>().First()).First() == wiseReply,
        "Editing a copied reply should leave the built-in preset intact.");

    reloaded.CopyPresetToCustom("sensei");
    Check(reloaded.IsCustomPreset && reloaded.LlmCustomExamples.Count == 10,
        "Copy should transfer all 10 Sensei examples to Custom.");
    actual["LlmPromptPreset"] = "custom";
    actual["LlmCustomPrompt"] = (string)reloaded.LlmCustomPrompt;
    actual["LlmCustomExamples"] = JsonNode.Parse(JsonSerializer.Serialize((object)reloaded.LlmCustomExamples));
    File.WriteAllText(configPath, actual.ToJsonString());
    dynamic loadedSensei = Load();
    Check(!loadedSensei.PreserveExistingFile && loadedSensei.LlmCustomExamples.Count == 10,
        "Reload should accept all 10 copied Sensei examples within MaxCustomExamples.");

    var targetType = plugin.GetType("FFXIVMomoTalk.Chat.SendTarget", throwOnError: true)!;
    var key = Activator.CreateInstance(plugin.GetType("FFXIVMomoTalk.Chat.ConversationKey", throwOnError: true)!);
    var target = Activator.CreateInstance(targetType, key, "/fc", null);
    var aiReply = plugin.GetType("FFXIVMomoTalk.Chat.AiReplyService", throwOnError: true)!;
    var snapshotExamples = aiReply.GetMethod("SnapshotExamples", BindingFlags.Static | BindingFlags.NonPublic)!;
    var buildPrompt = aiReply.GetMethod("BuildSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
    reloaded.LlmPromptPreset = "custom";
    reloaded.LlmCustomExamples.Clear();
    reloaded.LlmCustomPrompt = "用近期对话的语言回复。";
    AddExample("  ", "Ignored reply");
    AddExample("Draft without replies", " \r\n ");
    var noExamples = BuildPrompt();
    Check(!noExamples.Contains("Dialogue examples") && noExamples.Contains("Mandatory output format constraints"),
        "Draft examples should not produce an empty section.");

    AddExample("  日本語で「集合」？\r\n明日だよ。 ", "可以说 \"好\"，路径 C:\\raid。", " ", "もちろん！");
    var prompt = BuildPrompt();
    Check(prompt.StartsWith("用近期对话的语言回复。", StringComparison.Ordinal) && prompt.Contains("tone and option-variety")
        && prompt.Contains("language used by the recent conversation messages")
        && !prompt.Contains("Ignored reply") && !prompt.Contains("Draft without replies"),
        "The prompt should retain the custom style, explain language matching, and omit invalid drafts.");
    var lines = prompt.Split('\n');
    const string incomingPrefix = "Incoming: ";
    const string expectedPrefix = "Expected JSON: ";
    var incoming = lines.Single(line => line.StartsWith(incomingPrefix, StringComparison.Ordinal));
    var expectedJson = lines.Single(line => line.StartsWith(expectedPrefix, StringComparison.Ordinal));
    Check(JsonSerializer.Deserialize<string>(incoming[incomingPrefix.Length..]) == "日本語で「集合」？ 明日だよ。",
        "The example context should be serialized and cleaned to a single line.");
    using var parsed = JsonDocument.Parse(expectedJson[expectedPrefix.Length..]);
    var replies = parsed.RootElement.GetProperty("replies");
    Check(replies.GetArrayLength() == 2 && replies[0].GetString() == "可以说 \"好\"，路径 C:\\raid。"
        && replies[1].GetString() == "もちろん！", "The example JSON should preserve escaped multilingual replies.");

    Console.WriteLine("AI reply configuration, migration recovery, and prompt examples passed.");

    void AddExample(string context, params string[] replies)
    {
        dynamic example = Activator.CreateInstance(plugin.GetType("FFXIVMomoTalk.AiReplyExample", throwOnError: true)!)!;
        example.Context = context;
        example.Replies = replies.ToList();
        reloaded.LlmCustomExamples.Add(example);
    }

    string BuildPrompt()
    {
        var examples = snapshotExamples.Invoke(null, [(object)reloaded.EffectiveLlmExamples]);
        return (string)buildPrompt.Invoke(null, [(string)reloaded.EffectiveLlmPrompt, target, examples])!;
    }
}
finally
{
    foreach (var file in directory.EnumerateFiles())
        file.Delete();
    directory.Delete();
}

object Load() => load.Invoke(null, [host, log])!;

static string Context(object example) =>
    (string)example.GetType().GetProperty("Context")!.GetValue(example)!;

static IEnumerable<string> Replies(object example) =>
    (IEnumerable<string>)example.GetType().GetProperty("Replies")!.GetValue(example)!;

static string V1Settings(string prompt) => JsonSerializer.Serialize(new
{
    Version = 1,
    UiLanguage = "ja",
    ShowFloatingEntry = false,
    ShowIntroAnimation = false,
    ShowSelfAvatar = true,
    EntryOffsetX = 17.5,
    EntryOffsetY = 26,
    LlmEndpoint = "https://example.invalid/v1",
    LlmApiKey = "2026-10-01T00:00:00Z",
    LlmModel = "fixture-model",
    LlmContextWindowMinutes = 7,
    LlmContextMaxMessages = 18,
    LlmContextMinMessages = 3,
    LlmPrompt = prompt,
}).Insert(1, """
    "$type":"FFXIVMomoTalk.Configuration, FFXIVMomoTalk",
    """);

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

public class ConfigTestProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?>? handle;

    public static object Create(Type interfaceType, Func<MethodInfo, object?[], object?> handle)
    {
        var proxy = (ConfigTestProxy)DispatchProxy.Create(interfaceType, typeof(ConfigTestProxy));
        proxy.handle = handle;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        handle!(targetMethod!, args ?? []);
}
