using System.Diagnostics;
using System.Net;

namespace CodexProxy.Core;

public sealed record EndpointCheck(int? Status, int? Milliseconds, string? Error = null)
{
    public bool Reachable => Status is >= 200 and < 300 or 401;
    public bool Restricted => Status is 403 or 429 or 451;
    public string Text => Status is { } code ? $"HTTP {code} · {Milliseconds} ms" : UiText.Get(Error ?? "连接失败");
}

public sealed record CodexCheck(EndpointCheck ChatGpt, EndpointCheck Api, DateTimeOffset CheckedAt)
{
    public string StatusText => UiText.Get(ChatGpt.Reachable && Api.Reachable ? "网络可达" :
        ChatGpt.Restricted || Api.Restricted ? "访问受限" :
        (ChatGpt.Status is null or >= 500) && (Api.Status is null or >= 500) ? "连接失败" : "部分可达 / 待确认");
    public string Detail => string.Format(UiText.Get("ChatGPT：{0}\nOpenAI API：{1}\n检测时间：{2:HH:mm:ss}\n401 表示未认证但网络可达；403/429 可能是访问策略或限流。未验证登录、地区支持或模型调用。"), ChatGpt.Text, Api.Text, CheckedAt.ToLocalTime());
}

public static class CodexProbe
{
    public static readonly Uri ChatGpt = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri OpenAi = new("https://api.openai.com/v1/models");

    public static async Task RunAsync(CoreSession main, Action<string, CodexCheck> report, CancellationToken cancellation,
        Uri? chatGpt = null, Uri? api = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var snapshot = main.Profile;
        var sourceDirectory = main.DirectoryPath;
        await using var controller = new CoreSession(sourceDirectory, snapshot);
        var sourceCache = Path.Combine(sourceDirectory, controller.CacheName);
        var names = (await controller.NodesAsync(cancellation).ConfigureAwait(false)).Select(n => n.Name).ToArray();
        cancellation.ThrowIfCancellationRequested();
        var directory = Path.Combine(sourceDirectory, ".codex-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); // Inherits the private parent directory's permissions.
        try
        {
            var ports = Profile.FreePorts();
            var profile = snapshot with { Port = ports[0], ControllerPort = ports[1], Selected = CoreSession.Auto };
            await using var isolated = new CoreSession(directory, profile, localCacheOnly: true);
            await using (var input = new FileStream(sourceCache, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(Path.Combine(directory, isolated.CacheName), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
            await isolated.StartAsync(cancellation).ConfigureAwait(false);
            foreach (var name in names)
            {
                cancellation.ThrowIfCancellationRequested();
                await isolated.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name }, cancellation).ConfigureAwait(false);
                // A fresh pool per node prevents reusing a TLS tunnel opened through another node.
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = true, AllowAutoRedirect = false,
                    UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
                    Proxy = new WebProxy($"http://127.0.0.1:{profile.Port}") { Credentials = new NetworkCredential("codex", profile.ProxyPassword) } })
                    { Timeout = TimeSpan.FromSeconds(8) };
                var results = await Task.WhenAll(RequestAsync(client, chatGpt ?? ChatGpt, cancellation),
                    RequestAsync(client, api ?? OpenAi, cancellation)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                report(name, new(results[0], results[1], DateTimeOffset.UtcNow));
            }
        }
        finally { Directory.Delete(directory, true); } // Only this call's freshly created, owned directory.
    }

    private static async Task<EndpointCheck> RequestAsync(HttpClient client, Uri url, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            return new((int)response.StatusCode, (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(null, null, "超时"); }
        catch (HttpRequestException) { return new(null, null, "连接失败（网络或 TLS）"); }
    }
}
