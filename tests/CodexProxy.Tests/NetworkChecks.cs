using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexProxy.Core;

namespace CodexProxy.Tests;

static class NetworkChecks
{
    public static async Task RunAsync(Fixture fixture, ProxyService service, Action<bool, string> check)
    {
        var directory = Path.Combine(fixture.DirectoryPath, "network-settings-checks");
        Directory.CreateDirectory(directory);
        await using var isolated = new ProxyService(directory);
        check(isolated.Network == new NetworkSettings(), "missing network settings default to Direct and an automatic port");
        using var controller = Bind(SocketType.Stream);
        var original = fixture.Profile with { ControllerPort = Port(controller) };
        await original.SaveAsync(directory);
        foreach (var mode in Enum.GetValues<SubscriptionDownloadMode>())
        {
            await isolated.SetSubscriptionDownloadModeAsync(mode);
            check(NetworkSettings.Load(directory).DownloadMode == mode && !isolated.ProxyEnabled && isolated.Core is null,
                $"subscription download mode {mode} persists without starting a proxy");
        }
        var settingsPath = Path.Combine(directory, "network-settings.json");
        var settingsBefore = File.ReadAllText(settingsPath);
        NetworkSettings[] invalid = [new((SubscriptionDownloadMode)99), new(ProxyPort: 0), new(ProxyPort: 65536)];
        foreach (var settings in invalid)
        {
            try { await settings.SaveAsync(directory); throw new Exception("Expected invalid network settings rejection"); }
            catch (ProxyException)
            { check(File.ReadAllText(settingsPath) == settingsBefore, "invalid mode or port leaves saved network settings unchanged"); }
        }
        await File.WriteAllTextAsync(settingsPath, "{");
        try { _ = isolated.Network; throw new Exception("Expected malformed network settings rejection"); }
        catch (ProxyException) { check(true, "malformed network settings are reported instead of silently replaced with defaults"); }
        await File.WriteAllTextAsync(settingsPath, settingsBefore);

        var profilePath = Path.Combine(directory, "profile.json");
        var profileBefore = File.ReadAllText(profilePath);
        using (var tcp = Bind(SocketType.Stream))
        {
            try { await isolated.SetProxyPortAsync(Port(tcp)); throw new Exception("Expected occupied TCP port rejection"); }
            catch (ProxyException)
            { check(File.ReadAllText(profilePath) == profileBefore && File.ReadAllText(settingsPath) == settingsBefore,
                "a TCP-occupied mixed port leaves the profile and network settings unchanged"); }
        }
        using (var udp = Bind(SocketType.Dgram))
        {
            try { await isolated.SetProxyPortAsync(Port(udp)); throw new Exception("Expected occupied UDP port rejection"); }
            catch (ProxyException)
            { check(File.ReadAllText(profilePath) == profileBefore && File.ReadAllText(settingsPath) == settingsBefore,
                "a UDP-occupied mixed port leaves the profile and network settings unchanged"); }
        }
        try { await isolated.SetProxyPortAsync(original.ControllerPort); throw new Exception("Expected controller port rejection"); }
        catch (ProxyException)
        { check(File.ReadAllText(profilePath) == profileBefore && File.ReadAllText(settingsPath) == settingsBefore,
            "the mixed port cannot reuse the controller port"); }
        int manualPort;
        using (var tcp = Bind(SocketType.Stream))
        using (var udp = Bind(SocketType.Dgram, Port(tcp))) manualPort = Port(tcp);
        await isolated.SetProxyPortAsync(manualPort);
        check(isolated.Network.ProxyPort == manualPort && isolated.Profile == (original with { Port = manualPort }) &&
            !isolated.ProxyEnabled && isolated.Core is null && isolated.Desktop is null,
            "a stopped proxy saves its manual mixed port while preserving the controller, credentials and other profile fields");
        await isolated.SetProxyPortAsync(null);
        var automatic = isolated.Profile!;
        check(isolated.Network.ProxyPort is null && automatic.Port is > 0 and <= 65535 && automatic.Port != original.ControllerPort &&
            automatic == (original with { Port = automatic.Port }) && !isolated.ProxyEnabled && isolated.Core is null,
            "automatic port selection persists without changing credentials or starting a proxy");
        var runningCore = service.Core;
        var runningProfile = service.Profile;
        var runningSettings = service.Network;
        try { await service.SetProxyPortAsync(null); throw new Exception("Expected running proxy port-change rejection"); }
        catch (ProxyException)
        { check(service.ProxyEnabled && runningCore is not null && ReferenceEquals(service.Core, runningCore) &&
            service.Profile == runningProfile && service.Network == runningSettings && await runningCore.RunningAsync(),
            "a running proxy rejects port changes and preserves the shared fixture core"); }
        await CheckRoutingAsync(directory, original, check);
    }

    private static async Task CheckRoutingAsync(string directory, Profile profile, Action<bool, string> check)
    {
        profile = profile with { Url = "https://subscription.fixture.invalid/sub" };
        await using var directCore = new CoreSession(directory, profile, networkSettings: new());
        var direct = Configuration(directCore);
        var provider = direct["proxy-providers"]![directCore.ProviderName]!;
        check(provider["type"]!.GetValue<string>() == "http" && provider["proxy"]!.GetValue<string>() == "DIRECT" &&
            provider["interval"]!.GetValue<int>() == 86400 && provider["health-check"]!["enable"]!.GetValue<bool>() &&
            provider["health-check"]!["interval"]!.GetValue<int>() == 300 &&
            direct["proxy-groups"]![0]!["type"]!.GetValue<string>() == "url-test" &&
            direct["proxy-groups"]![0]!["interval"]!.GetValue<int>() == 300 && direct["proxies"] is null,
            "Direct retains the HTTP provider, daily subscription refresh and five-minute node checks without an upstream proxy");
        var previousProxy = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy("http://proxy.fixture.invalid:18888")
                { BypassProxyOnLocal = false, Credentials = new NetworkCredential("fixture-user", "fixture-password") };
            await using var systemCore = new CoreSession(directory, profile, networkSettings: new(SubscriptionDownloadMode.System));
            var system = Configuration(systemCore);
            var upstream = system["proxies"]!.AsArray().Single()!;
            check(system["proxy-providers"]![systemCore.ProviderName]!["proxy"]!.GetValue<string>() == SubscriptionRouting.UpstreamName &&
                upstream["name"]!.GetValue<string>() == SubscriptionRouting.UpstreamName && upstream["type"]!.GetValue<string>() == "http" &&
                upstream["server"]!.GetValue<string>() == "proxy.fixture.invalid" && upstream["port"]!.GetValue<int>() == 18888 &&
                upstream["username"]!.GetValue<string>() == "fixture-user" && upstream["password"]!.GetValue<string>() == "fixture-password" &&
                system["proxy-groups"]!.AsArray().All(group => group!["proxies"] is not JsonArray groupProxies ||
                    groupProxies.All(name => name!.GetValue<string>() != SubscriptionRouting.UpstreamName)),
                "System represents explicit HTTP Basic credentials in a separate subscription upstream outside the Codex groups");
        }
        finally { HttpClient.DefaultProxy = previousProxy; }
        await using var formalCore = new CoreSession(directory, profile, networkSettings: new(SubscriptionDownloadMode.Dedicated));
        var formal = Configuration(formalCore);
        check(formal["proxy-providers"]![formalCore.ProviderName]!["proxy"]!.GetValue<string>() == CoreSession.Select && formal["proxies"] is null,
            "the formal Dedicated provider uses the Codex selection group without routing through its own mixed listener");
        var stagingProfile = profile with { Port = 12345, ControllerPort = 12346, ProxyPassword = "fixture-staging-password" };
        await using var stagingCore = new CoreSession(directory, stagingProfile, networkSettings: new(SubscriptionDownloadMode.Dedicated),
            subscriptionProxyProfile: profile);
        var staging = Configuration(stagingCore);
        var dedicatedUpstream = staging["proxies"]!.AsArray().Single()!;
        check(staging["proxy-providers"]![stagingCore.ProviderName]!["proxy"]!.GetValue<string>() == SubscriptionRouting.UpstreamName &&
            dedicatedUpstream["server"]!.GetValue<string>() == "127.0.0.1" && dedicatedUpstream["port"]!.GetValue<int>() == profile.Port &&
            dedicatedUpstream["username"]!.GetValue<string>() == "codex" &&
            dedicatedUpstream["password"]!.GetValue<string>() == profile.ProxyPassword &&
            staging["mixed-port"]!.GetValue<int>() == stagingProfile.Port &&
            staging["authentication"]![0]!.GetValue<string>() == "codex:" + stagingProfile.ProxyPassword,
            "Dedicated staging imports through the existing proxy endpoint and credentials while retaining its own separate listener");
        await using var cachedCore = new CoreSession(directory, profile, localCacheOnly: true,
            networkSettings: new(SubscriptionDownloadMode.Dedicated), subscriptionProxyProfile: profile);
        var cached = Configuration(cachedCore);
        var cachedProvider = cached["proxy-providers"]![cachedCore.ProviderName]!;
        check(cachedProvider["type"]!.GetValue<string>() == "file" && cachedProvider["proxy"]!.GetValue<string>() == "DIRECT" &&
            !cachedProvider["health-check"]!["enable"]!.GetValue<bool>() && cached["proxies"] is null &&
            cached["proxy-groups"]![0]!["type"]!.GetValue<string>() == "select",
            "local-cache previews ignore subscription download routing and use the existing file provider without an upstream");
    }

    private static JsonNode Configuration(CoreSession core) => JsonSerializer.SerializeToNode(core.Configuration())!;

    private static Socket Bind(SocketType type, int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp)
            { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;
}
