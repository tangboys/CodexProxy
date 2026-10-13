using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexProxy.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SubscriptionDownloadMode>))]
public enum SubscriptionDownloadMode { Direct, System, Dedicated }

public sealed record NetworkSettings(SubscriptionDownloadMode DownloadMode = SubscriptionDownloadMode.Direct, int? ProxyPort = null)
{
    private const string FileName = "network-settings.json";

    public static NetworkSettings Load(string directory)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<NetworkSettings>(File.ReadAllText(Path.Combine(directory, FileName)))
                ?? throw new ProxyException(UiText.Language == "en-US" ? "Network settings are invalid. Review and save them again." : "网络设置无效，请检查后重新保存。");
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new ProxyException(UiText.Language == "en-US"
            ? "Could not read network settings. Check the saved values and local file permissions."
            : "无法读取网络设置，请检查保存的内容和本地文件权限。"); }
    }

    public async Task SaveAsync(string directory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Validate();
        await Profile.PrivateDirectoryAsync(directory, cancellation).ConfigureAwait(false);
        await Profile.WriteJsonAsync(Path.Combine(directory, FileName), this, cancellation).ConfigureAwait(false);
    }

    private void Validate()
    {
        if (!Enum.IsDefined(DownloadMode)) throw new ProxyException(UiText.Language == "en-US"
            ? "Choose Direct, System, or Dedicated for subscription downloads."
            : "订阅下载模式必须是直连、系统代理或专用代理。");
        if (ProxyPort is { } port) ValidatePort(port);
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ProxyException(UiText.Language == "en-US"
            ? "The dedicated proxy port must be between 1 and 65535. Leave it blank for an automatic port."
            : "专用代理端口必须在 1–65535 之间；留空使用自动端口。");
    }

    public static void ValidateProxyPortAvailable(int port, int? controllerPort = null)
    {
        ValidatePort(port);
        if (port == controllerPort) throw new ProxyException(UiText.Language == "en-US"
            ? "The proxy port cannot be the same as the controller port. Choose another port."
            : "代理端口不能与控制端口相同，请更换端口。");
        try
        {
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            tcp.Bind(endpoint);
            udp.Bind(endpoint);
        }
        catch (SocketException)
        { throw new ProxyException(UiText.Language == "en-US"
            ? $"TCP or UDP port {port} is in use or unavailable. Choose another dedicated proxy port."
            : $"TCP 或 UDP 端口 {port} 已被占用或不可用，请更换专用代理端口。"); }
    }
}
