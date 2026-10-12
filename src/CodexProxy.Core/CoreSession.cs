using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexProxy.Core;

public sealed record Node(string Name, string Protocol, bool? Available, int? Delay, bool Current)
{
    public string CountryCode => NodeCountry.Find(Name);
    public string DisplayName => NodeCountry.DisplayName(Name);
    public CodexCheck? Codex { get; init; }
    public string CodexText => Codex?.StatusText ?? UiText.Get("未检测");
    public string CodexDetail => Codex?.Detail ?? UiText.Get("检测 ChatGPT 与 OpenAI API 的网络可达性，不读取账号凭据。");
    public string DelayText => Delay is > 0 ? $"{Delay} ms" : "—";
    public string StatusText => UiText.Get(Available switch { true => "可用", false => "不可用", _ => "待检测" });
    public string ActionText => UiText.Get(Current ? "使用中" : "切换");
    public bool CanSelect => Available == true && !Current;
}

public sealed class CoreSession(string directory, Profile profile, bool localCacheOnly = false) : IAsyncDisposable
{
    public const string Auto = "CODEX-AUTO", Select = "CODEX-SELECT", Provider = "subscription";
    public Profile Profile { get; set; } = profile;
    public string DirectoryPath { get; } = directory;
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private Process? process;
    private StreamWriter? log;
    public bool OwnsProcess => process is { HasExited: false };

    public string CacheName => "subscription-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Profile.Url)))[..16].ToLowerInvariant() + ".yaml";
    public string ProviderName => Path.GetFileNameWithoutExtension(CacheName);

    public object Configuration()
    {
        var exclude = "Direct|Compatible|Reject|RejectDrop|Pass";
        return new Dictionary<string, object>
        {
            ["mixed-port"] = Profile.Port, ["bind-address"] = "127.0.0.1", ["allow-lan"] = false,
            ["mode"] = "rule", ["log-level"] = "warning", ["authentication"] = new[] { "codex:" + Profile.ProxyPassword },
            ["skip-auth-prefixes"] = Array.Empty<string>(), ["tun"] = new { enable = false },
            ["external-controller"] = "127.0.0.1:" + Profile.ControllerPort, ["secret"] = Profile.Secret,
            ["profile"] = new Dictionary<string, object> { ["store-selected"] = false },
            ["proxy-providers"] = new Dictionary<string, object> { [ProviderName] = new Dictionary<string, object>
            {
                ["type"] = localCacheOnly ? "file" : "http", ["url"] = Profile.Url, ["path"] = "./" + CacheName,
                ["interval"] = 86400, ["proxy"] = "DIRECT", ["size-limit"] = 10485760,
                ["health-check"] = new Dictionary<string, object> { ["enable"] = !localCacheOnly, ["url"] = Profile.TestUrl,
                    ["expected-status"] = Profile.Expected, ["interval"] = 300, ["timeout"] = 5000, ["lazy"] = false }
            }},
            ["proxy-groups"] = new object[] {
                new Dictionary<string, object> { ["name"] = Auto, ["type"] = localCacheOnly ? "select" : "url-test", ["use"] = new[] { ProviderName },
                    ["url"] = Profile.TestUrl, ["expected-status"] = Profile.Expected, ["interval"] = 300,
                    ["tolerance"] = 50, ["lazy"] = false, ["exclude-type"] = exclude, ["empty-fallback"] = "REJECT" },
                new Dictionary<string, object> { ["name"] = Select, ["type"] = "select", ["proxies"] = new[] { Auto },
                    ["use"] = new[] { ProviderName }, ["exclude-type"] = exclude, ["empty-fallback"] = "REJECT" } },
            ["rules"] = new[] { "MATCH," + Select }
        };
    }

    public async Task<JsonNode?> ApiAsync(string path, HttpMethod? method = null, object? body = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var profile = Profile;
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"http://127.0.0.1:{profile.ControllerPort}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.Secret);
        if (body is not null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ProxyException(string.Format(UiText.Get("本地代理请求失败（{0}）。"), (int)response.StatusCode));
            var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception error) when ((error is HttpRequestException or TaskCanceledException) && !cancellation.IsCancellationRequested)
        { throw new ProxyException(UiText.Get("无法连接本地代理内核，或请求已超时。")); }
    }

    public async Task<bool> RunningAsync(CancellationToken cancellation = default)
    {
        try { return (await ApiAsync("/configs", cancellation: cancellation).ConfigureAwait(false))?["mixed-port"]?.GetValue<int>() == Profile.Port; }
        catch (ProxyException) { return false; }
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            if (await RunningAsync(deadline.Token).ConfigureAwait(false)) return;
            var config = Path.Combine(DirectoryPath, "config.json");
            await Profile.WriteJsonAsync(config, Configuration(), deadline.Token).ConfigureAwait(false);
            var info = new ProcessStartInfo(Profile.CorePath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-d", DirectoryPath, "-f", config }) info.ArgumentList.Add(argument);
            log = new StreamWriter(Path.Combine(DirectoryPath, "core.log"), false, Encoding.UTF8) { AutoFlush = true };
            process = Process.Start(info) ?? throw new ProxyException(UiText.Get("无法启动 Mihomo。"));
            void Log(object sender, DataReceivedEventArgs args) { if (args.Data is not null) lock (log) log.WriteLine(args.Data); }
            process.OutputDataReceived += Log;
            process.ErrorDataReceived += Log;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new ProxyException(UiText.Get("Mihomo 启动失败，请本地检查 core.log。"));
                try
                {
                    var nodes = await NodesAsync(deadline.Token).ConfigureAwait(false);
                    if (nodes.Count > 0)
                    {
                        var selected = Profile.Selected == Auto || nodes.Any(n => n.Name == Profile.Selected) ? Profile.Selected : Auto;
                        await ApiAsync("/proxies/" + Select, HttpMethod.Put, new { name = selected }, deadline.Token).ConfigureAwait(false);
                        if (selected != Profile.Selected)
                        {
                            Profile = Profile with { Selected = selected };
                            await Profile.SaveAsync(DirectoryPath, deadline.Token).ConfigureAwait(false);
                        }
                        return;
                    }
                }
                catch (ProxyException) { }
                await Task.Delay(200, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new ProxyException(UiText.Get("35 秒内未导入节点。请检查订阅能否直接获取，以及格式是否受 Mihomo 支持。")); }
    }

    public async Task<string> ProviderNameAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (OwnsProcess) return ProviderName;
        var response = await ApiAsync("/providers/proxies", cancellation: cancellation).ConfigureAwait(false);
        if (response is JsonObject root && root["providers"] is JsonObject providers)
        {
            if (providers.ContainsKey(ProviderName)) return ProviderName;
            if (providers.ContainsKey(Provider)) return Provider;
        }
        return ProviderName;
    }

    public async Task<SubscriptionUsage?> SubscriptionUsageAsync(CancellationToken cancellation = default)
    {
        var name = await ProviderNameAsync(cancellation).ConfigureAwait(false);
        var provider = await ApiAsync("/providers/proxies/" + name, cancellation: cancellation).ConfigureAwait(false);
        return SubscriptionUsage.FromJson((provider as JsonObject)?["subscriptionInfo"]);
    }

    public async Task<List<Node>> NodesAsync(CancellationToken cancellation = default)
    {
        var profile = Profile;
        var selected = (await ApiAsync("/proxies/" + Select, cancellation: cancellation).ConfigureAwait(false))?["now"]?.GetValue<string>();
        var current = selected == Auto ? (await ApiAsync("/proxies/" + Auto, cancellation: cancellation).ConfigureAwait(false))?["now"]?.GetValue<string>() : selected;
        var provider = await ProviderNameAsync(cancellation).ConfigureAwait(false);
        var entries = (await ApiAsync("/providers/proxies/" + provider, cancellation: cancellation).ConfigureAwait(false))?["proxies"]?.AsArray() ?? [];
        var result = new List<Node>();
        foreach (var item in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var type = item?["type"]?.GetValue<string>() ?? "";
            if (new[] { "direct", "reject", "rejectdrop", "compatible", "pass" }.Contains(type.ToLowerInvariant())) continue;
            var name = item!["name"]!.GetValue<string>();
            var test = item["extra"]?[profile.TestUrl];
            var history = test?["history"]?.AsArray();
            bool? available = history?.Count > 0 ? test?["alive"]?.GetValue<bool>() ?? false : null;
            var delay = available == true ? history![^1]?["delay"]?.GetValue<int>() : null;
            result.Add(new Node(name, type, available, delay, name == current));
        }
        return result;
    }

    public async Task ProbeAsync(CancellationToken cancellation = default)
    {
        try { await ApiAsync($"/group/{Auto}/delay?url={Uri.EscapeDataString(Profile.TestUrl)}&timeout=5000&expected={Uri.EscapeDataString(Profile.Expected)}", cancellation: cancellation).ConfigureAwait(false); }
        catch (ProxyException error) when (error.Message.Contains("504")) { }
    }

    public async Task ChooseAsync(string name, CancellationToken cancellation = default)
    {
        await ProbeAsync(cancellation).ConfigureAwait(false);
        var nodes = await NodesAsync(cancellation).ConfigureAwait(false);
        if (!nodes.Any(n => n.Available == true && (name == Auto || n.Name == name)))
            throw new ProxyException(UiText.Get("所选节点或所有节点检测失败，保留原选择。"));
        await ApiAsync("/proxies/" + Select, HttpMethod.Put, new { name }, cancellation).ConfigureAwait(false);
        Profile = Profile with { Selected = name };
        await Profile.SaveAsync(DirectoryPath, cancellation).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (process is { } owned)
        {
            if (!owned.HasExited)
            {
                try { owned.Kill(); }
                catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && owned.HasExited) { }
            }
            await owned.WaitForExitAsync().ConfigureAwait(false);
            owned.Dispose();
            process = null;
        }
        log?.Dispose();
        log = null;
        client.Dispose();
    }
}
