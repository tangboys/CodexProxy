using System.Text.Json;

namespace CodexProxy.Core;

public sealed record DesktopSettings(string Executable)
{
    public static DesktopSettings? Load(string directory)
    {
        var file = Path.Combine(directory, "desktop.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(file)) : null;
    }

    public static async Task<DesktopSettings?> LoadAsync(string directory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var file = Path.Combine(directory, "desktop.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<DesktopSettings>(await File.ReadAllTextAsync(file, cancellation).ConfigureAwait(false)) : null;
    }

    public void Save(string directory) => SaveAsync(directory).ConfigureAwait(false).GetAwaiter().GetResult();

    public async Task SaveAsync(string directory, CancellationToken cancellation = default)
    {
        await Profile.PrivateDirectoryAsync(directory, cancellation).ConfigureAwait(false);
        await Profile.WriteJsonAsync(Path.Combine(directory, "desktop.json"), this, cancellation).ConfigureAwait(false);
    }

    public static async Task<string> ResolveAsync(string directory, bool rescan = false, CancellationToken cancellation = default)
    {
        if (!rescan && await LoadAsync(directory, cancellation).ConfigureAwait(false) is { } saved)
            try { return await Task.Run(() => DesktopLauncher.ValidatePath(saved.Executable), cancellation).ConfigureAwait(false); } catch (ProxyException) { }
        var detected = await DesktopLauncher.FindAsync(cancellation).ConfigureAwait(false);
        await new DesktopSettings(detected).SaveAsync(directory, cancellation).ConfigureAwait(false);
        return detected;
    }
}
