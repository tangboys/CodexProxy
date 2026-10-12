using System.Text.Json;

namespace CodexProxy.Core;

public sealed record KernelSettings(string CorePath)
{
    public static KernelSettings? Load(string directory)
    {
        var file = Path.Combine(directory, "kernel.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<KernelSettings>(File.ReadAllText(file)) : null;
    }

    public void Save(string directory)
    {
        Profile.PrivateDirectory(directory);
        Profile.WriteJson(Path.Combine(directory, "kernel.json"), this);
    }
}
