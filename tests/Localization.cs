#!/usr/bin/env dotnet
// dotnet run --file tests/Localization.cs after building the plugin in Debug.
#:property PublishAot=false

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;

var pluginPath = args.Length == 0
    ? "src/FFXIVMomoTalk/bin/Debug/FFXIVMomoTalk.dll"
    : args[0];
var assembly = Assembly.LoadFrom(Path.GetFullPath(pluginPath));
var localization = assembly.GetType("FFXIVMomoTalk.Loc", throwOnError: true)!;
var selectLanguage = localization.GetMethod("SetLanguage")!.CreateDelegate<Action<string, string?>>();
var text = localization.GetMethod("Text")!.CreateDelegate<Func<string, string>>();
var reason = localization.GetMethod("Reason")!.CreateDelegate<Func<string, string>>();
var resources = new ResourceManager("FFXIVMomoTalk.Localization.Strings", assembly);
var english = ReadCatalog(CultureInfo.InvariantCulture);

foreach (var (culture, settings, appName) in new[]
{
    ("en", "Settings", "MomoTalk"),
    ("ja", "設定", "モモトーク"),
    ("de", "Einstellungen", "MomoTalk"),
    ("fr", "Paramètres", "MomoTalk"),
    ("zh-Hans", "设置", "MomoTalk"),
    ("zh-Hant", "設定", "MomoTalk"),
})
{
    if (culture != "en")
    {
        var catalog = ReadCatalog(CultureInfo.GetCultureInfo(culture));
        if (!english.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(catalog.Keys))
            throw new InvalidOperationException($"{culture} has missing or extra translations.");

        foreach (var (key, value) in catalog)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                CompositeFormat.Parse(value).MinimumArgumentCount != CompositeFormat.Parse(english[key]).MinimumArgumentCount)
            {
                throw new InvalidOperationException($"{culture}/{key} has empty text or mismatched placeholders.");
            }
        }
    }

    selectLanguage(culture == "en" ? "ja" : "en", culture);
    CheckInterface(culture, settings, appName);
    if (!culture.StartsWith("zh", StringComparison.Ordinal))
    {
        selectLanguage(culture, null);
        CheckInterface(culture, settings, appName);
    }
}

const string savedReason = "TellRecipientBusy";
selectLanguage("en", "zh-Hans");
var chineseReason = reason(savedReason);
selectLanguage("en", null);
if (chineseReason == reason(savedReason) || reason(savedReason) == savedReason ||
    reason("Native game error.") != "Native game error.")
{
    throw new InvalidOperationException("Stored rejection reasons did not follow the language selection.");
}

Console.WriteLine("Six catalogs, client language selection, overrides, and stored rejection reasons passed.");

void CheckInterface(string culture, string settings, string appName)
{
    if (text("Settings") != settings || text("AppName") != appName)
        throw new InvalidOperationException($"Selecting {culture} did not apply its catalog.");
}

Dictionary<string, string> ReadCatalog(CultureInfo culture) =>
    (resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
        ?? throw new InvalidOperationException($"Missing satellite catalog for {culture}."))
    .Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
