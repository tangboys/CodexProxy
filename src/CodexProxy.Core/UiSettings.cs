using System.Text.Json;

namespace CodexProxy.Core;

public sealed record UiSettings(int RefreshIntervalSeconds = 5)
{
    public static UiSettings Load(string directory)
    {
        var file = Path.Combine(directory, "ui-settings.json");
        var settings = File.Exists(file) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(file)) : null;
        return settings is { RefreshIntervalSeconds: 5 or 10 or 30 or 60 } ? settings : new UiSettings();
    }

    public void Save(string directory)
    {
        Profile.PrivateDirectory(directory);
        Profile.WriteJson(Path.Combine(directory, "ui-settings.json"), this);
    }
}
