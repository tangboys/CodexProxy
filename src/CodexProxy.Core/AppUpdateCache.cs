using System.Text.Json;

namespace CodexProxy.Core;

internal sealed record AppUpdateCache(AppRelease? Release = null, AppUpdateSource? Source = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? RetryAt = null, DateTimeOffset? AttemptedAt = null,
    AppUpdateStatus? Failure = null)
{
    internal static AppUpdateCache? Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "app-update-cache.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return null;
            return JsonSerializer.Deserialize<AppUpdateCache>(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    internal Task SaveAsync(string directory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        return Profile.WriteJsonAsync(Path.Combine(directory, "app-update-cache.json"), this, cancellation);
    }
}
