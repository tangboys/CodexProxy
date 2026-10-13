using System.Diagnostics;
using System.Text;

namespace CodexProxy.Core;

public sealed class DesktopLauncher(CoreSession core) : IAsyncDisposable
{
    private BrowserBridge? bridge;
    private Process? process;
    private bool observed;
    public bool Started => process is not null && !process.HasExited;
    public int? BridgePort => bridgeStarted ? bridge?.Port : null;
    public void ReplaceCore(CoreSession replacement) { core = replacement; observed = false; }

    public static ProcessStartInfo StartInfo(string executable, Profile profile)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in info.Environment.Keys.ToArray())
            if (new[] { "http_proxy", "https_proxy", "all_proxy", "no_proxy" }.Contains(key.ToLowerInvariant()))
                info.Environment.Remove(key);
        var endpoint = $"http://codex:{profile.ProxyPassword}@127.0.0.1:{profile.Port}";
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) info.Environment[key] = endpoint;
        info.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        return info;
    }

    public static async Task<string> PowerShellAsync(string command, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " + command }) info.ArgumentList.Add(argument);
        try
        {
            var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new ProxyException(UiText.Get("无法读取 Codex 桌面版安装信息。"));
            return result.Output.Trim();
        }
        catch (TimeoutException) { throw new ProxyException(UiText.Get("检查 Codex 安装位置超时。")); }
    }

    public static async Task<string> FindAsync(CancellationToken cancellation = default)
    {
        var root = await PowerShellAsync("Get-AppxPackage OpenAI.Codex | Select-Object -First 1 -ExpandProperty InstallLocation", cancellation).ConfigureAwait(false);
        string[] candidates = [Path.Combine(root, "app", "ChatGPT.exe"),
            Path.Combine(root, "app", "Codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "app", "ChatGPT.exe")];
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return await Task.Run(() => ValidatePath(candidate), cancellation).ConfigureAwait(false); } catch (ProxyException) { }
        }
        throw new ProxyException(UiText.Get("未检测到 Codex 桌面版，请点击‘选择程序’手动选择已安装的桌面 EXE。"));
    }

    public static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ProxyException(UiText.Get("Codex 程序路径无效，请选择已存在的桌面 EXE。"));
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        // The Store's tiny Codex.exe wrapper does not accept the Chromium proxy switch.
        if (Path.GetFileName(path).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, "ChatGPT.exe")))
            path = Path.Combine(directory, "ChatGPT.exe");
        var version = FileVersionInfo.GetVersionInfo(path);
        var name = Path.GetFileName(path);
        if (!(version.ProductName?.Equals("Codex", StringComparison.OrdinalIgnoreCase) == true &&
            (name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, "resources", "app.asar")))))
            throw new ProxyException(UiText.Get("所选文件未识别为 Codex 桌面版。请勿选择 CLI 的 codex.exe 或其他软件。"));
        return path;
    }

    public async Task LaunchAsync(string executable, CancellationToken cancellation = default)
    {
        executable = await Task.Run(() => ValidatePath(executable), cancellation).ConfigureAwait(false);
        var quoted = executable.Replace("'", "''");
        var running = await PowerShellAsync("$p='" + quoted + "'; @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $p -and $_.CommandLine -notmatch '--type=' }).Count", cancellation).ConfigureAwait(false);
        if (running != "0") throw new ProxyException(UiText.Get("Codex 已在运行。请保存工作并完全退出 Codex，再从这里启动。已有进程无法接收新的代理环境。"));
        bridge ??= new BrowserBridge(core.Profile);
        if (!bridgeStarted) { bridge.Start(); bridgeStarted = true; }
        var info = StartInfo(executable, core.Profile);
        info.ArgumentList.Add("--proxy-server=http://127.0.0.1:" + bridge.Port);
        process?.Dispose();
        process = Process.Start(info) ?? throw new ProxyException(UiText.Get("无法启动 Codex 桌面版。"));
        observed = false;
    }
    private bool bridgeStarted;

    public async Task<string> StatusAsync(CancellationToken cancellation = default)
    {
        if (process is null) return UiText.Get("尚未通过此启动器打开 Codex");
        if (process.HasExited) return UiText.Get("Codex 已退出，或被旧实例接管");
        var testHost = new Uri(core.Profile.TestUrl).Host;
        var connections = (await core.ApiAsync("/connections", cancellation: cancellation).ConfigureAwait(false))?["connections"]?.AsArray();
        if (connections is not null && connections.Any(item =>
            item?["metadata"]?["host"]?.GetValue<string>() is { Length: > 0 } host && host != testHost)) observed = true;
        return UiText.Get(observed ? "已观察到专用代理连接 · 模型请求仍需实测" : "已携带代理设置启动 · 等待网络连接");
    }

    public async ValueTask DisposeAsync()
    {
        if (bridge is not null)
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
            bridge = null;
        }
        bridgeStarted = false;
        process?.Dispose(); // Never stop the user's desktop app.
        process = null;
    }
}
