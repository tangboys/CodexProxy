using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexProxy.Core;
using CodexProxy.Tests;

var corePath = Environment.GetEnvironmentVariable("MIHOMO_CORE");
if (string.IsNullOrWhiteSpace(corePath))
{
    Console.Error.WriteLine("MIHOMO_CORE is required. Set it to the absolute path of a compatible Mihomo executable before running these checks.");
    Environment.ExitCode = 2;
    return;
}
await using var fixture = new Fixture(corePath);
await using var service = new ProxyService(fixture.DirectoryPath);
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

if (args.Contains("--ui"))
{
    var exe = args[Array.IndexOf(args, "--ui") + 1];
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add("--state-dir"); info.ArgumentList.Add(fixture.DirectoryPath);
    using var ui = Process.Start(info)!;
    Console.WriteLine($"UI fixture PID={ui.Id}; state={fixture.DirectoryPath}");
    await ui.WaitForExitAsync();
    return;
}

var core = await service.StartProxyAsync();
var parentProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
var child = DesktopLauncher.StartInfo("fixture.exe", fixture.Profile);
Check(NodeCountry.Find("日本 Tokyo 01") == "JP" && NodeCountry.Find(Fixture.GoodName) == "HK" &&
    NodeCountry.Find("🇺🇸 美国 2") == "US" && NodeCountry.Find("高速01") == "" && NodeCountry.Find("RUSSIA") == "RU",
    "country aliases, existing flags and unknown names");
Check(NodeCountry.DisplayName("🇯🇵日本 09 | 高级专线") == "日本 09 | 高级专线" && NodeCountry.DisplayName("普通节点") == "普通节点",
    "vector flags replace unsupported emoji without changing the stored node name");
try { DesktopLauncher.ValidatePath(Path.Combine(fixture.DirectoryPath, "missing.exe")); throw new Exception("Expected missing desktop rejection"); }
catch (ProxyException) { Check(true, "missing desktop path rejected before launch"); }
var wrongExe = Path.Combine(fixture.DirectoryPath, "codex.exe");
File.WriteAllText(wrongExe, "This is not a desktop executable.");
try { DesktopLauncher.ValidatePath(wrongExe); throw new Exception("Expected invalid desktop rejection"); }
catch (ProxyException) { Check(true, "invalid/manual non-desktop executable rejected"); }
var installedDesktop = await DesktopLauncher.FindAsync();
Check(DesktopLauncher.ValidatePath(installedDesktop) == installedDesktop, "installed Codex desktop automatically detected and validated");
var wrapper = Path.Combine(Path.GetDirectoryName(installedDesktop)!, "Codex.exe");
if (File.Exists(wrapper)) Check(DesktopLauncher.ValidatePath(wrapper) == installedDesktop, "Store desktop wrapper resolves to proxy-capable GUI executable");
if (Environment.GetEnvironmentVariable("CODEX_TEST_EXE") is { } cliExecutable)
{
    try { DesktopLauncher.ValidatePath(cliExecutable); throw new Exception("Expected CLI rejection"); }
    catch (ProxyException) { Check(true, "real Codex CLI is rejected by desktop file selection"); }
}
new DesktopSettings(installedDesktop).Save(fixture.DirectoryPath);
Check(await DesktopSettings.ResolveAsync(fixture.DirectoryPath) == installedDesktop, "manually chosen desktop path persists");
new DesktopSettings(wrongExe).Save(fixture.DirectoryPath);
Check(await DesktopSettings.ResolveAsync(fixture.DirectoryPath) == installedDesktop, "stale desktop path falls back to installed package");
Check(Environment.GetEnvironmentVariable("HTTP_PROXY") == parentProxy, "parent environment unchanged");
Check(child.Environment["HTTPS_PROXY"]!.Contains("127.0.0.1:" + fixture.Profile.Port) && child.Environment["NO_PROXY"] != "*", "proxy scoped to child process");
await core.ProbeAsync();
var nodes = await core.NodesAsync();
Check(nodes.Count == 3 && nodes.Single(n => n.Name == Fixture.GoodName).Available == true && nodes.Single(n => n.Name == "dead").Available == false, "real node availability and latency");
await core.ChooseAsync(Fixture.GoodName);
Check((await core.ApiAsync("/proxies/" + CoreSession.Select))!["now"]!.GetValue<string>() == Fixture.GoodName, "manual selection");
await core.ChooseAsync(CoreSession.Auto);
fixture.Good.Status = 503;
await core.ProbeAsync();
nodes = await core.NodesAsync();
Check(nodes.Single(n => n.Current).Name == "slower" && nodes.Single(n => n.Name == Fixture.GoodName).Available == false, "auto failover checks expected HTTP status");
fixture.Slow.Status = 503;
try { await core.ChooseAsync(CoreSession.Auto); throw new Exception("Expected offline rejection"); }
catch (ProxyException) { Check(true, "all nodes offline rejects selection and desktop launch preparation"); }
var beforePin = await service.PreviewNodesAsync();
var liveCurrent = (await core.NodesAsync()).FirstOrDefault(n => n.Current)?.Name ?? Fixture.GoodName;
await service.DisableAutomaticAsync(Fixture.GoodName);
Check(service.Profile!.Selected == liveCurrent &&
    (await core.ApiAsync("/proxies/" + CoreSession.Select))!["now"]!.GetValue<string>() == liveCurrent,
    "disabling Auto pins the actual current node even when health checks fail");
Check((await service.PreviewNodesAsync())!.CheckedAt == beforePin!.CheckedAt,
    "disabling Auto does not invent a new node test timestamp");
fixture.Slow.Status = 204;
fixture.Good.Status = 204;
await core.ChooseAsync(CoreSession.Auto);
await using (var borrowed = new CoreSession(fixture.DirectoryPath, service.Profile!))
{
    await borrowed.StartAsync();
    Check(!borrowed.OwnsProcess, "existing dedicated core is borrowed");
}
Check(await core.RunningAsync(), "disposing borrowed session leaves owning core alive");
await SubscriptionChecks.RunAsync(fixture, service, Check);
await NetworkChecks.RunAsync(fixture, service, Check);

await core.ChooseAsync(Fixture.GoodName);
var selectedBefore = (await core.ApiAsync("/proxies/" + CoreSession.Select))!["now"]!.GetValue<string>();
var profileBefore = File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json"));
fixture.Good.Status = 401; fixture.Slow.Status = 403;
var checks = new Dictionary<string, CodexCheck>();
await CodexProbe.RunAsync(core, (name, result) => checks[name] = result, CancellationToken.None,
    new Uri("http://codex-check.invalid/usage"), new Uri("http://api-check.invalid/models"));
Check(checks[Fixture.GoodName].StatusText == "网络可达" && checks[Fixture.GoodName].ChatGpt.Status == 401,
    "Codex probe treats unauthenticated HTTP 401 as network reachability");
Check(checks["slower"].StatusText == "访问受限" && checks["slower"].Api.Status == 403 && checks["dead"].StatusText == "连接失败",
    "Codex probe distinguishes restrictions and failed nodes");
Check((await core.ApiAsync("/proxies/" + CoreSession.Select))!["now"]!.GetValue<string>() == selectedBefore &&
    File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json")) == profileBefore, "Codex probes do not change live selection or saved subscription");
Check(!Directory.EnumerateDirectories(fixture.DirectoryPath, ".codex-check-*").Any(), "Codex probe cleans isolated core and temporary cache");
using (var cancel = new CancellationTokenSource())
{
    try
    {
        await CodexProbe.RunAsync(core, (_, _) => cancel.Cancel(), cancel.Token,
            new Uri("http://codex-check.invalid/usage"), new Uri("http://api-check.invalid/models"));
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException)
    { Check(!Directory.EnumerateDirectories(fixture.DirectoryPath, ".codex-check-*").Any() && await core.RunningAsync(), "cancelled probe cleans its core and leaves live proxy running"); }
}
fixture.Good.Status = 204; fixture.Slow.Status = 204;

var monitor = new TrafficMonitor();
monitor.Update((await core.ApiAsync("/connections"))!, DateTimeOffset.UtcNow);
fixture.Good.ResponseDelay = 1200;
using (var trafficClient = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + fixture.Profile.Port)
    { Credentials = new NetworkCredential("codex", fixture.Profile.ProxyPassword) }, UseProxy = true }))
{
    var sending = trafficClient.GetAsync("http://traffic.invalid/probe?token=must-not-appear");
    await Task.Delay(350);
    monitor.Update((await core.ApiAsync("/connections"))!, DateTimeOffset.UtcNow);
    Check(monitor.ActiveCount > 0 && monitor.Entries.Any(c => c.Destination == "traffic.invalid:80" && c.Route.Contains(Fixture.GoodName)) &&
        monitor.Entries.All(c => !c.Destination.Contains("must-not-appear")), "real traffic log shows destination and selected node without request query");
    using var completed = await sending;
    await Task.Delay(150);
    monitor.Update((await core.ApiAsync("/connections"))!, DateTimeOffset.UtcNow);
    Check(monitor.UploadTotal > 0 && monitor.DownloadTotal > 0 && monitor.Entries.Any(c => !c.Active), "real traffic counters and ended connection history");
}
fixture.Good.ResponseDelay = 40;
var sampleTime = DateTimeOffset.UtcNow;
monitor.Update(JsonNode.Parse("{\"uploadTotal\":100,\"downloadTotal\":200,\"connections\":[]}")!, sampleTime);
monitor.Update(JsonNode.Parse("{\"uploadTotal\":300,\"downloadTotal\":600,\"connections\":[]}")!, sampleTime.AddSeconds(2));
Check(monitor.UploadRate == 100 && monitor.DownloadRate == 200, "traffic rates use actual sample interval");
monitor.Update(JsonNode.Parse("{\"uploadTotal\":0,\"downloadTotal\":0,\"connections\":[]}")!, sampleTime.AddSeconds(3));
Check(monitor.UploadRate == 0 && monitor.DownloadRate == 0, "core counter reset never produces negative rates");
monitor.Clear(); Check(monitor.Entries.Count == 0, "traffic log can be cleared");
var many = new JsonArray();
for (var i = 0; i < 305; i++) many.Add(new JsonObject { ["id"] = i.ToString(), ["start"] = sampleTime.AddSeconds(i).ToString("O"),
    ["metadata"] = new JsonObject { ["host"] = "example.invalid", ["destinationPort"] = "443" }, ["chains"] = new JsonArray("node", "CODEX-SELECT") });
monitor.Update(new JsonObject { ["connections"] = many }, sampleTime.AddSeconds(4));
Check(monitor.Entries.Count == 300 && monitor.Entries.All(c => c.Route == "CODEX-SELECT → node"), "connection history is bounded and route follows inbound to outbound");
Check(new CodexCheck(new(429, 10), new(401, 10), sampleTime).StatusText == "访问受限" &&
    new CodexCheck(new(401, 10), new(null, null, "超时"), sampleTime).StatusText == "部分可达 / 待确认",
    "Codex results distinguish rate limiting and partial reachability");
if (args.Contains("--probe-only")) { Console.WriteLine("All probe and traffic checks passed."); return; }

using (var unauthorized = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + fixture.Profile.Port), UseProxy = true }))
using (var response = await unauthorized.GetAsync(fixture.Profile.TestUrl))
    Check(response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired, "core requires proxy authentication");
await using (var bridge = new BrowserBridge(fixture.Profile))
{
    bridge.Start();
    using var browser = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + bridge.Port), UseProxy = true });
    using var response = await browser.GetAsync(fixture.Profile.TestUrl);
    Check(response.StatusCode == HttpStatusCode.NoContent && bridge.Requests > 0, "Chromium bridge forwards through authenticated core");
    if (args.Contains("--chromium"))
    {
        var executable = await DesktopLauncher.FindAsync();
        var browserData = Path.Combine(fixture.DirectoryPath, "isolated-browser");
        var info = DesktopLauncher.StartInfo(executable, fixture.Profile);
        foreach (var argument in new[] { "--headless", "--no-first-run", "--dump-dom", "--user-data-dir=" + browserData,
            "--proxy-server=http://127.0.0.1:" + bridge.Port, fixture.Profile.TestUrl }) info.ArgumentList.Add(argument);
        info.Environment["CODEX_HOME"] = Path.Combine(fixture.DirectoryPath, "isolated-codex-home");
        Directory.CreateDirectory(info.Environment["CODEX_HOME"]!);
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var before = bridge.Requests;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
        await output; await errors;
        Check(bridge.Requests > before, "installed desktop Chromium routes into dedicated proxy (isolated headless profile)");
    }
}

var previous = service.Profile!;
fixture.Subscription.Payload = fixture.Nodes("新节点 JP");
await service.SaveSubscriptionAsync(previous.Url + "&new=1", previous.CorePath);
Check(service.Profile!.Port == previous.Port && service.Profile.ProxyPassword == previous.ProxyPassword, "subscription reload preserves endpoint and credentials");
Check((await core.NodesAsync()).Any(n => n.Name == "新节点 JP"), "running core reloads new subscription");
var saved = File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json"));
try { await service.SaveSubscriptionAsync("file:///bad", previous.CorePath); throw new Exception("Expected validation failure"); }
catch (ProxyException error) { Check(!error.Message.Contains("fixture-private"), "validation errors do not echo subscription token"); }
Check(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json")) == saved, "invalid subscription preserves settings");
fixture.Subscription.Status = 500;
var activeBeforeFailure = service.Subscriptions.ActiveId;
var broken = await service.SaveSubscriptionEntryAsync(null, "失败订阅", previous.Url + "&broken=1", null, false);
try { await service.ActivateSubscriptionAsync(broken.Id, previous.CorePath); throw new Exception("Expected subscription import failure"); }
catch (ProxyException)
{
    Check(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json")) == saved &&
        (await core.NodesAsync()).Any(n => n.Name == "新节点 JP"), "failed subscription download preserves settings and live nodes");
    Check(service.Subscriptions.ActiveId == activeBeforeFailure && service.Subscriptions.Items.Any(s => s.Id == broken.Id),
        "failed activation preserves active subscription and saved alternatives");
}
try { await service.RefreshAsync(); throw new Exception("Expected refresh failure"); }
catch (ProxyException) { Check((await core.NodesAsync()).Any(n => n.Name == "新节点 JP"), "refresh failure keeps working cache"); }
fixture.Subscription.Status = 200;

// Verify the same Rust app-server used by desktop, without real credentials or model calls.
var codex = Environment.GetEnvironmentVariable("CODEX_TEST_EXE");
if (codex is not null)
{
    fixture.Good.Status = 200; fixture.Slow.Status = 200;
    var info = DesktopLauncher.StartInfo(codex, service.Profile!);
    foreach (var argument in new[] { "-c", "model_provider=proxy_test", "-c",
        "model_providers.proxy_test={name=\"Fixture\",base_url=\"http://api.invalid/v1\",env_key=\"CODEX_PROXY_TEST_KEY\",wire_api=\"responses\",supports_websockets=false}", "app-server" }) info.ArgumentList.Add(argument);
    info.Environment["CODEX_HOME"] = Path.Combine(fixture.DirectoryPath, "isolated-backend");
    Directory.CreateDirectory(info.Environment["CODEX_HOME"]!);
    info.Environment["CODEX_PROXY_TEST_KEY"] = "fixture-only";
    info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
    using var process = Process.Start(info)!;
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    async Task<JsonNode> RpcAsync(int id, string method, object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var message = JsonNode.Parse(line)!;
            if (message["id"]?.ToJsonString() == id.ToString())
            {
                if (message["error"] is not null) throw new Exception("App-server RPC failed: " + message["error"]);
                return message["result"]!;
            }
        }
        throw new Exception("App-server closed output: " + await stderr);
    }
    try
    {
        await RpcAsync(1, "initialize", new { clientInfo = new { name = "proxy-test", version = "0.1" } });
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
        var thread = await RpcAsync(2, "thread/start", new { model = "proxy-test", modelProvider = "proxy_test", cwd = fixture.DirectoryPath, approvalPolicy = "never", sandbox = "read-only", ephemeral = true });
        var threadId = thread["thread"]!["id"]!.GetValue<string>();
        await RpcAsync(3, "turn/start", new { threadId, input = new[] { new { type = "text", text = "Reply proxy-test-ok." } } });
        var completed = false;
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var message = JsonNode.Parse(line)!;
            if (message["method"]?.GetValue<string>() == "turn/completed")
            { completed = message["params"]?["turn"]?["status"]?.GetValue<string>() == "completed"; break; }
        }
        Check(completed && fixture.Good.Posts + fixture.Slow.Posts > 0, "desktop Rust app-server completes model turn through dedicated proxy");
    }
    finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await stderr; }
}
Console.WriteLine("All integration checks passed.");
