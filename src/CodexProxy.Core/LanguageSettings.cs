using System.Text.Json;

namespace CodexProxy.Core;

public static class LanguageSettings
{
    private sealed record Preference(string Language);

    public static string Load(string directory)
    {
        var file = Path.Combine(directory, "language.json");
        if (!File.Exists(file)) return "zh-CN";
        var language = JsonSerializer.Deserialize<Preference>(File.ReadAllText(file))?.Language;
        return language is "zh-CN" or "en-US" ? language : "zh-CN";
    }

    public static void Save(string directory, string language)
    {
        if (language is not ("zh-CN" or "en-US")) throw new ProxyException(UiText.Get("不支持的界面语言。"));
        Profile.PrivateDirectory(directory);
        Profile.WriteJson(Path.Combine(directory, "language.json"), new Preference(language));
    }
}
