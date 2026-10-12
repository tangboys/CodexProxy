using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexProxy.Core;

public sealed record SubscriptionMetadata(SubscriptionUsage? Usage, DateTimeOffset UpdatedAt)
{
    public static SubscriptionMetadata? Load(string directory, string url)
    {
        try
        {
            var path = CachePath(directory, url);
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<SubscriptionMetadata>(File.ReadAllText(path));
            return metadata is not null && metadata.UpdatedAt != default ? metadata : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    public static async Task<SubscriptionMetadata> FetchAsync(string directory, string url, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Profile.ValidateUrl(url);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10), MaxAutomaticRedirections = 10, MaxResponseDrainSize = 0 })
                { Timeout = TimeSpan.FromSeconds(20) };
            var uri = new Uri(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Match Verge's subscription compatibility UA; no body format is consumed here.
            request.Headers.UserAgent.ParseAdd("clash-verge/v2.5.9");
            if (uri.UserInfo.Length > 0)
            {
                var credentials = uri.UserInfo.Split(':', 2);
                var username = Uri.UnescapeDataString(credentials[0]);
                var password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
                request.RequestUri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ProxyException(UiText.Get("无法刷新订阅套餐信息；已保留上次缓存，请检查直连网络或稍后重试。"));
            SubscriptionUsage? usage = null;
            foreach (var header in response.Headers.OrderBy(h => h.Key.Equals("subscription-userinfo", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                if (header.Key.Equals("subscription-userinfo", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.EndsWith("-subscription-userinfo", StringComparison.OrdinalIgnoreCase))
                {
                    usage = SubscriptionUsage.FromHeader(string.Join(";", header.Value));
                    if (usage is not null) break;
                }
            var metadata = new SubscriptionMetadata(usage, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(directory);
            await Profile.WriteJsonAsync(CachePath(directory, url), metadata, cancellation).ConfigureAwait(false);
            return metadata;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { throw new ProxyException(UiText.Get("无法刷新订阅套餐信息；已保留上次缓存，请检查直连网络或稍后重试。")); }
    }

    private static string CachePath(string directory, string url) => Path.Combine(directory,
        "subscription-metadata-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".json");
}
