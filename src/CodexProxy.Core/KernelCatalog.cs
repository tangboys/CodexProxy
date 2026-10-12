using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProxy.Core;

public sealed record KernelOption(string Path, string Version, string Source)
{
    public string DisplayName => UiText.Get(Source) + " · " + Version;
}

public sealed record KernelUpdate(string CurrentVersion, string LatestVersion, bool? UpdateAvailable, string ReleaseUrl);

public static class KernelCatalog
{
    private const string ReleasesUrl = "https://github.com/MetaCubeX/mihomo/releases";
    private static readonly SemaphoreSlim BundledGate = new(1, 1);

    public static async Task<string?> BundledPathAsync(string directory)
    {
        await BundledGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var resource = typeof(KernelCatalog).Assembly.GetManifestResourceStream("CodexProxy.Bundled.mihomo.exe");
            if (resource is null)
            {
                var fallback = Path.Combine(AppContext.BaseDirectory, "bin", "mihomo.exe");
                return File.Exists(fallback) ? fallback : null;
            }

            var hash = await SHA256.HashDataAsync(resource).ConfigureAwait(false);
            var folder = Path.Combine(directory, "cores", "bundled-" + Convert.ToHexString(hash)[..16].ToLowerInvariant());
            var path = Path.Combine(folder, "mihomo.exe");
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                if ((await SHA256.HashDataAsync(existing).ConfigureAwait(false)).AsSpan().SequenceEqual(hash)) return path;
            }

            await Profile.PrivateDirectoryAsync(directory).ConfigureAwait(false);
            Directory.CreateDirectory(folder);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                resource.Position = 0;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                    await resource.CopyToAsync(output).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        finally { BundledGate.Release(); }
    }

    public static async Task<List<KernelOption>> DiscoverAsync(string directory, string? selectedPath)
    {
        var candidates = new List<(string Path, string Source)>();
        var bundled = await BundledPathAsync(directory).ConfigureAwait(false);
        if (bundled is not null) candidates.Add((bundled, "内置内核"));
        if (!string.IsNullOrWhiteSpace(selectedPath)) candidates.Add((selectedPath, "当前选择"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] clashRoots = [Path.Combine(programFiles, "Clash Verge"), Path.Combine(programFilesX86, "Clash Verge"),
            Path.Combine(local, "Programs", "Clash Verge"), Path.Combine(local, "Clash Verge"), @"D:\Clash Verge"];
        foreach (var root in clashRoots)
            foreach (var name in new[] { "verge-mihomo.exe", "verge-mihomo-alpha.exe", "mihomo.exe" })
                candidates.Add((Path.Combine(root, name), "Clash Verge"));
        string[] v2rayRoots = [Path.Combine(programFiles, "v2rayN"), Path.Combine(local, "Programs", "v2rayN"),
            Path.Combine(local, "v2rayN"), @"C:\v2rayN", @"C:\v2rayN-windows-64", @"D:\v2rayN", @"D:\v2rayN-windows-64"];
        foreach (var root in v2rayRoots)
            foreach (var relative in new[] { @"bin\mihomo\mihomo.exe", @"bin\mihomo.exe", "mihomo.exe" })
                candidates.Add((Path.Combine(root, relative), "v2rayN"));
        foreach (var root in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "mihomo.exe", "verge-mihomo.exe" })
                candidates.Add((Path.Combine(Environment.ExpandEnvironmentVariables(root.Trim().Trim('"')), name), "PATH"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KernelOption>();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path)) continue;
            try
            {
                var fullPath = Path.GetFullPath(candidate.Path);
                if (seen.Add(fullPath)) result.Add(await InspectAsync(fullPath, candidate.Source).ConfigureAwait(false));
            }
            catch (ProxyException) { } // Invalid candidates do not hide the other installed cores.
        }
        return result;
    }

    public static async Task<KernelOption> InspectAsync(string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ProxyException(UiText.Get("内核路径无效，请选择已存在的 Mihomo EXE。"));
        try
        {
            path = Path.GetFullPath(path);
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-v");
            var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var version = Regex.Match(result.Output + "\n" + result.Error, @"(?im)^\s*Mihomo\s+(?:Meta\s+)?(?<version>\S+)");
            if (result.ExitCode != 0 || !version.Success)
                throw new ProxyException(UiText.Get("所选文件未识别为 Mihomo 内核。"));
            return new(path, version.Groups["version"].Value, source);
        }
        catch (TimeoutException) { throw new ProxyException(UiText.Get("读取内核版本超时，请重新选择 Mihomo EXE。")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
        { throw new ProxyException(UiText.Get("无法读取 Mihomo 内核版本。")); }
    }

    public static async Task<KernelUpdate> CheckUpdateAsync(KernelOption selected)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexProxy-KernelManager/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync("https://api.github.com/repos/MetaCubeX/mihomo/releases/latest", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProxyException(string.Format(UiText.Get("官方更新查询失败（{0}）。"), (int)response.StatusCode));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                return new(selected.Version, "", null, ReleasesUrl);
            var latest = release.GetProperty("tag_name").GetString() ?? "";
            var url = release.GetProperty("html_url").GetString();
            var releaseUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.AbsolutePath.Equals("/MetaCubeX/mihomo/releases", StringComparison.OrdinalIgnoreCase) ||
                 uri.AbsolutePath.StartsWith("/MetaCubeX/mihomo/releases/", StringComparison.OrdinalIgnoreCase)) ? uri.AbsoluteUri : ReleasesUrl;
            var currentVersion = StableVersion(selected.Version);
            var latestVersion = StableVersion(latest);
            return new(selected.Version, latest, currentVersion is null || latestVersion is null ? null : latestVersion > currentVersion, releaseUrl);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ProxyException(UiText.Get("无法查询官方更新，请检查网络连接后重试。")); }
    }

    private static Version? StableVersion(string value) => Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+$", RegexOptions.IgnoreCase) &&
        Version.TryParse(value.TrimStart('v', 'V'), out var version) ? version : null;
}
