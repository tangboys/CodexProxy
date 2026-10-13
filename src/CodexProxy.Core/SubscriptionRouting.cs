using System.Net;
using System.Text;

namespace CodexProxy.Core;

public static class SubscriptionRouting
{
    public const string UpstreamName = "CODEX-SUBSCRIPTION-UPSTREAM";

    public static IWebProxy? WebProxy(SubscriptionDownloadMode mode, Profile? dedicatedProfile = null) => mode switch
    {
        SubscriptionDownloadMode.Direct => null,
        SubscriptionDownloadMode.System => HttpClient.DefaultProxy,
        SubscriptionDownloadMode.Dedicated => dedicatedProfile is not null
            ? new System.Net.WebProxy(new Uri($"http://127.0.0.1:{dedicatedProfile.Port}"))
                { BypassProxyOnLocal = false, Credentials = new NetworkCredential("codex", dedicatedProfile.ProxyPassword) }
            : throw new ProxyException(UiText.Language == "en-US"
                ? "The dedicated proxy must already be running to download subscriptions through it. Start it using cached nodes, or choose Direct or System."
                : "通过专用代理下载订阅前，专用代理必须已运行。请先使用缓存节点启动，或选择直连、系统代理。"),
        _ => throw new ProxyException(UiText.Language == "en-US"
            ? "Choose Direct, System, or Dedicated for subscription downloads."
            : "订阅下载模式必须是直连、系统代理或专用代理。")
    };

    public static (string Name, Dictionary<string, object>? Upstream) ProviderProxy(
        SubscriptionDownloadMode mode, string url, Profile? dedicatedProfile = null)
    {
        if (mode == SubscriptionDownloadMode.Dedicated && dedicatedProfile is null)
            return (CoreSession.Select, null);
        var proxy = WebProxy(mode, dedicatedProfile);
        if (proxy is null) return ("DIRECT", null);
        Profile.ValidateUrl(url);
        var target = new Uri(url);
        if (proxy.IsBypassed(target)) return ("DIRECT", null);
        var address = proxy.GetProxy(target) ?? throw UnsupportedProxy();
        if (address == target) return ("DIRECT", null);
        var scheme = address.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(address.Host) ||
            address.AbsolutePath is not ("" or "/") || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw UnsupportedProxy();
        var port = scheme == "socks5" && address.Port == -1 ? 1080 : address.Port;
        if (port is < 1 or > 65535) throw UnsupportedProxy();

        NetworkCredential? credential = null;
        if (address.UserInfo.Length > 0)
        {
            var parts = address.UserInfo.Split(':', 2);
            credential = new NetworkCredential(Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        else if (proxy.Credentials is { } credentials)
        {
            credential = credentials.GetCredential(address, "Basic");
            if (ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials) || credential is null &&
                (credentials.GetCredential(address, "Negotiate") is not null || credentials.GetCredential(address, "NTLM") is not null ||
                 credentials.GetCredential(address, "Digest") is not null))
                throw UnsupportedAuthentication();
        }

        var upstream = new Dictionary<string, object>
        {
            ["name"] = UpstreamName, ["type"] = scheme == "socks5" ? "socks5" : "http",
            ["server"] = address.IdnHost.Trim('[', ']'), ["port"] = port
        };
        if (scheme == "https") upstream["tls"] = true;
        if (credential is not null)
        {
            var username = scheme != "socks5" && credential.Domain.Length > 0
                ? credential.Domain + "\\" + credential.UserName : credential.UserName;
            if (username.Length == 0 || scheme != "socks5" && credential.Password.Length == 0 ||
                scheme == "socks5" && (Encoding.UTF8.GetByteCount(username) > 255 || Encoding.UTF8.GetByteCount(credential.Password) > 255))
                throw UnsupportedAuthentication();
            upstream["username"] = username;
            upstream["password"] = credential.Password;
        }
        return (UpstreamName, upstream);
    }

    private static ProxyException UnsupportedProxy() => new(UiText.Language == "en-US"
        ? "The current system proxy cannot be used for subscription downloads. Supported proxy types are HTTP, HTTPS, and SOCKS5. Choose another download mode or change the system proxy."
        : "当前系统代理无法用于订阅下载，仅支持 HTTP、HTTPS 和 SOCKS5。请选择其他下载模式或调整系统代理。");

    private static ProxyException UnsupportedAuthentication() => new(UiText.Language == "en-US"
        ? "The current system proxy authentication cannot be represented by Mihomo. Use an explicit username and password with HTTP Basic or SOCKS5, or choose another download mode."
        : "当前系统代理认证无法转换为 Mihomo 配置。请使用明确的 HTTP Basic 或 SOCKS5 用户名、密码，或选择其他下载模式。");
}
