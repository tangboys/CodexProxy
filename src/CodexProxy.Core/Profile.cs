using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexProxy.Core;

public sealed class ProxyException(string message) : Exception(message);

public sealed record Profile
{
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("core")] public required string CorePath { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("controller_port")] public int ControllerPort { get; init; }
    [JsonPropertyName("secret")] public required string Secret { get; init; }
    [JsonPropertyName("proxy_password")] public required string ProxyPassword { get; init; }
    [JsonPropertyName("selected")] public string Selected { get; init; } = CoreSession.Auto;
    [JsonPropertyName("test_url")] public string TestUrl { get; init; } = "https://www.gstatic.com/generate_204";
    [JsonPropertyName("expected")] public string Expected { get; init; } = "204";

    public static Profile Create(string url, string? corePath = null) =>
        CreateAsync(url, corePath).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<Profile> CreateAsync(string url, string? corePath = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateUrl(url);
        var path = await FindCoreAsync(corePath, cancellation).ConfigureAwait(false);
        var ports = FreePorts();
        return new Profile { Url = url, CorePath = path, Port = ports[0], ControllerPort = ports[1],
            Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            ProxyPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) };
    }

    public static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ProxyException(UiText.Get("订阅必须是 HTTP(S) 链接。"));
    }

    public static int[] FreePorts()
    {
        using var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var second = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        second.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return [((IPEndPoint)first.LocalEndPoint!).Port, ((IPEndPoint)second.LocalEndPoint!).Port];
    }

    public static string FindCore(string? explicitPath) =>
        FindCoreAsync(explicitPath).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<string> FindCoreAsync(string? explicitPath, CancellationToken cancellation = default)
    {
        string?[] paths = string.IsNullOrWhiteSpace(explicitPath) ?
            [Path.Combine(AppContext.BaseDirectory, "bin", "mihomo.exe"),
             @"D:\Clash Verge\verge-mihomo.exe", @"D:\v2rayN-windows-64\bin\mihomo\mihomo.exe"] : [explicitPath];
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (path is null || !File.Exists(path)) continue;
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-v");
            try
            {
                var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
                if (result.ExitCode == 0 && result.Output.Contains("mihomo", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(path);
            }
            catch (TimeoutException) { }
        }
        throw new ProxyException(UiText.Get("未找到 Mihomo，请在订阅设置中选择 mihomo.exe。也可使用本机 v2rayN 的 Mihomo 内核。"));
    }

    public static Profile? Load(string directory)
    {
        var path = Path.Combine(directory, "profile.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) : null;
    }

    public void Save(string directory) => WriteJson(Path.Combine(directory, "profile.json"), this);

    public Task SaveAsync(string directory, CancellationToken cancellation = default) =>
        WriteJsonAsync(Path.Combine(directory, "profile.json"), this, cancellation);

    public static void WriteJson(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static async Task WriteJsonAsync(string path, object value, CancellationToken cancellation = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void PrivateDirectory(string directory) =>
        PrivateDirectoryAsync(directory).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task PrivateDirectoryAsync(string directory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) return;
        var info = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { directory, "/inheritance:r", "/grant:r",
            Environment.UserDomainName + "\\" + Environment.UserName + ":(OI)(CI)F" }) info.ArgumentList.Add(argument);
        try
        {
            var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
            if (result.ExitCode == 0) return;
        }
        catch (TimeoutException) { }
        throw new ProxyException(UiText.Get("无法限制状态目录权限，请使用自己的用户目录。"));
    }
}
