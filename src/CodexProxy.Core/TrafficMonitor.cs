using System.Text.Json.Nodes;

namespace CodexProxy.Core;

public sealed record ConnectionEntry(string Id, DateTimeOffset Started, string Destination, string Address,
    string Network, string Route, long Upload, long Download, bool Active)
{
    public string TimeText => Started.ToLocalTime().ToString("HH:mm:ss");
    public string StateText => UiText.Get(Active ? "连接中" : "已结束（最后采样）");
    public string DestinationText => Destination.StartsWith("目标已隐藏:", StringComparison.Ordinal)
        ? UiText.Get("目标已隐藏") + Destination["目标已隐藏".Length..] : Destination;
    public string RouteText => NodeCountry.DisplayName(Route);
    public string UploadText => TrafficMonitor.Size(Upload);
    public string DownloadText => TrafficMonitor.Size(Download);
}

public sealed class TrafficMonitor
{
    private readonly Dictionary<string, ConnectionEntry> entries = [];
    private DateTimeOffset? previousTime;
    public long UploadTotal { get; private set; }
    public long DownloadTotal { get; private set; }
    public double UploadRate { get; private set; }
    public double DownloadRate { get; private set; }
    public int ActiveCount { get; private set; }
    public List<ConnectionEntry> Entries => entries.Values.OrderByDescending(e => e.Started).ToList();

    public void Update(JsonNode snapshot, DateTimeOffset time)
    {
        var up = snapshot["uploadTotal"]?.GetValue<long>() ?? 0;
        var down = snapshot["downloadTotal"]?.GetValue<long>() ?? 0;
        var seconds = previousTime is { } previous ? (time - previous).TotalSeconds : 0;
        UploadRate = seconds > 0 && up >= UploadTotal ? (up - UploadTotal) / seconds : 0;
        DownloadRate = seconds > 0 && down >= DownloadTotal ? (down - DownloadTotal) / seconds : 0;
        previousTime = time; UploadTotal = up; DownloadTotal = down;
        foreach (var key in entries.Keys.ToArray()) entries[key] = entries[key] with { Active = false };
        ActiveCount = 0;
        foreach (var item in snapshot["connections"]?.AsArray() ?? [])
        {
            if (item is null) continue;
            var id = item["id"]!.GetValue<string>();
            var metadata = item["metadata"]!;
            var ip = metadata["destinationIP"]?.GetValue<string>() ?? "";
            var host = metadata["host"]?.GetValue<string>();
            var port = metadata["destinationPort"]?.ToString() ?? "";
            var destination = string.IsNullOrEmpty(host) ? ip : host;
            // Accept only a host/IP, never a URL, request path, query, or credentials.
            if (destination.IndexOfAny(['/', '?', '@', '#', '\r', '\n']) >= 0) destination = "目标已隐藏";
            var route = string.Join(" → ", (item["chains"]?.AsArray() ?? []).Reverse().Select(n => n?.GetValue<string>()));
            var started = DateTimeOffset.TryParse(item["start"]?.GetValue<string>(), out var parsed) ? parsed : time;
            entries[id] = new(id, started, destination + ":" + port, ip,
                metadata["network"]?.GetValue<string>() ?? "", route,
                item["upload"]?.GetValue<long>() ?? 0, item["download"]?.GetValue<long>() ?? 0, true);
            ActiveCount++;
        }
        foreach (var old in entries.Values.OrderByDescending(e => e.Active).ThenByDescending(e => e.Started).Skip(300).ToArray())
            entries.Remove(old.Id);
    }

    public void Unavailable() { UploadRate = DownloadRate = 0; previousTime = null; }
    public void Stopped()
    {
        Unavailable(); ActiveCount = 0;
        foreach (var id in entries.Keys.ToArray()) entries[id] = entries[id] with { Active = false };
    }
    public void Clear() => entries.Clear();
    public static string Size(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:0.#} {units[unit]}";
    }
}
