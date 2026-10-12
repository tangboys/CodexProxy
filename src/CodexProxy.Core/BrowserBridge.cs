using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexProxy.Core;

// Chromium ignores credentials embedded in --proxy-server. This loopback bridge
// adds authentication before forwarding to the isolated Mihomo instance; no TLS interception.
public sealed class BrowserBridge(Profile profile) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> clients = new();
    private Task? acceptTask;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public long Requests => Interlocked.Read(ref requests);
    private long requests;

    public void Start() { listener.Start(); acceptTask = AcceptAsync(); }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                clients.TryAdd(client, 0);
                _ = ForwardAsync(client);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ForwardAsync(TcpClient downstream)
    {
        using (downstream)
        using (var upstream = new TcpClient())
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var input = downstream.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 65536)
                {
                    if (await input.ReadAsync(one, timeout.Token) == 0) return;
                    header.WriteByte(one[0]);
                    var buffer = header.GetBuffer();
                    var size = (int)header.Length;
                    if (size >= 4 && buffer[size - 4] == 13 && buffer[size - 3] == 10 && buffer[size - 2] == 13 && buffer[size - 1] == 10) break;
                }
                if (header.Length >= 65536) return;
                var lines = Encoding.Latin1.GetString(header.ToArray()).Split("\r\n");
                if (lines[0].Split(' ').Length != 3) return;
                var connect = lines[0].StartsWith("CONNECT ", StringComparison.Ordinal);
                var forwarded = string.Join("\r\n", lines.Where(line => line.Length > 0 &&
                    !line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase) &&
                    (connect || (!line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                                 !line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)))));
                if (!connect) forwarded += "\r\nConnection: close";
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("codex:" + profile.ProxyPassword));
                await upstream.ConnectAsync(IPAddress.Loopback, profile.Port, timeout.Token);
                var output = upstream.GetStream();
                await output.WriteAsync(Encoding.Latin1.GetBytes(forwarded + "\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"), timeout.Token);
                Interlocked.Increment(ref requests);
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var upload = input.CopyToAsync(output, transfer.Token);
                var download = output.CopyToAsync(input, transfer.Token);
                await Task.WhenAny(upload, download);
                await transfer.CancelAsync();
                await Task.WhenAll(upload, download);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { clients.TryRemove(downstream, out _); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        if (acceptTask is not null) await acceptTask;
        lifetime.Dispose();
    }
}
