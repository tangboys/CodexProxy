using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodexProxy.Core;

namespace CodexProxy.Tests;

sealed class FixtureServer(int status, int delay = 0) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int Status = status;
    public string Payload = "";
    public int Posts;
    public int ResponseDelay = delay;
    private Task? task;
    public void Start() { listener.Start(); task = LoopAsync(); }
    private async Task LoopAsync()
    {
        try { while (!lifetime.IsCancellationRequested) _ = HandleAsync(await listener.AcceptTcpClientAsync(lifetime.Token)); }
        catch (Exception error) when (error is OperationCanceledException or SocketException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                while (!lifetime.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(lifetime.Token);
                    if (line is null) return;
                    var length = 0;
                    while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':')[1]);
                    if (line.StartsWith("CONNECT "))
                    {
                        await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), lifetime.Token);
                        continue;
                    }
                    if (length > 0)
                    {
                        var body = new char[length];
                        var offset = 0;
                        while (offset < body.Length) { var read = await reader.ReadAsync(body.AsMemory(offset), lifetime.Token); if (read == 0) break; offset += read; }
                    }
                    if (ResponseDelay > 0) await Task.Delay(ResponseDelay, lifetime.Token);
                    var payload = Status == 200 ? Payload : "";
                    var kind = "application/json";
                    if (line.StartsWith("POST ")) { Interlocked.Increment(ref Posts); payload = Events(); kind = "text/event-stream"; }
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    var response = $"HTTP/1.1 {Status} Test\r\nContent-Type: {kind}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), lifetime.Token);
                    if (!line.StartsWith("HEAD ")) await stream.WriteAsync(bytes, lifetime.Token);
                    return;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
    }

    private static string Events()
    {
        var item = new { id = "msg_test", type = "message", role = "assistant", status = "completed",
            content = new[] { new { type = "output_text", text = "proxy-test-ok", annotations = Array.Empty<object>() } } };
        object[] events = [
            new { type = "response.created", response = new { id = "resp_test", status = "in_progress", output = Array.Empty<object>() } },
            new { type = "response.output_item.added", output_index = 0, item = new { id = "msg_test", type = "message", role = "assistant", status = "in_progress", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", item_id = "msg_test", output_index = 0, content_index = 0, delta = "proxy-test-ok" },
            new { type = "response.output_item.done", output_index = 0, item },
            new { type = "response.completed", response = new { id = "resp_test", status = "completed", output = new[] { item }, usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } }
        ];
        return string.Concat(events.Select(e => { var json = JsonSerializer.Serialize(e); return "event: " + System.Text.Json.Nodes.JsonNode.Parse(json)!["type"] + "\ndata: " + json + "\n\n"; }));
    }

    public async ValueTask DisposeAsync()
    { await lifetime.CancelAsync(); listener.Stop(); if (task is not null) await task; lifetime.Dispose(); }
}

sealed class Fixture : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "codex-proxy-csharp-test-" + Guid.NewGuid().ToString("N"));
    public FixtureServer Good { get; } = new(204, 40);
    public FixtureServer Slow { get; } = new(204, 200);
    public FixtureServer Subscription { get; } = new(200);
    public Profile Profile { get; }
    public const string GoodName = "可用 / HK #1";
    public Fixture(string core)
    {
        Good.Start(); Slow.Start(); Subscription.Start();
        Subscription.Payload = Nodes(GoodName);
        Profile.PrivateDirectory(DirectoryPath);
        Profile = Profile.Create($"http://127.0.0.1:{Subscription.Port}/sub?token=fixture-private", core) with
            { TestUrl = "http://check.invalid/ping" };
        Profile.Save(DirectoryPath);
    }
    public string Nodes(string goodName) => JsonSerializer.Serialize(new { proxies = new[] {
        new { name = goodName, type = "http", server = "127.0.0.1", port = Good.Port },
        new { name = "slower", type = "http", server = "127.0.0.1", port = Slow.Port },
        new { name = "dead", type = "http", server = "127.0.0.1", port = 1 } } });
    public async ValueTask DisposeAsync()
    {
        await Subscription.DisposeAsync(); await Good.DisposeAsync(); await Slow.DisposeAsync();
        Directory.Delete(DirectoryPath, true);
    }
}
