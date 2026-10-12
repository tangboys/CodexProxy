using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodexProxy.Core;

public sealed record SubscriptionUsage(long? Upload, long? Download, long? Total, long? Expire)
{
    private const long MaximumUnixSeconds = 253402300799;

    [JsonIgnore] public long? Used => Upload is >= 0 and { } upload && Download is >= 0 and { } download &&
        upload <= long.MaxValue - download ? upload + download : null;

    [JsonIgnore] public long? Remaining => Total is > 0 and { } total && Used is { } used ? Math.Max(0, total - used) : null;
    [JsonIgnore] public double? Percent => Total is > 0 and { } total && Used is { } used ? Math.Clamp((double)used / total * 100, 0, 100) : null;
    [JsonIgnore] public DateTimeOffset? ExpiresAt => Expire is > 0 and <= MaximumUnixSeconds and { } expire ? DateTimeOffset.FromUnixTimeSeconds(expire) : null;

    public static SubscriptionUsage? FromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        long? upload = null, download = null, total = null, expire = null;
        foreach (var field in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0) continue;
            var name = field[..separator].Trim();
            long? value = long.TryParse(field[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var number) && number >= 0 ? number : null;
            if (name.Equals("upload", StringComparison.OrdinalIgnoreCase)) upload = value;
            else if (name.Equals("download", StringComparison.OrdinalIgnoreCase)) download = value;
            else if (name.Equals("total", StringComparison.OrdinalIgnoreCase)) total = value;
            else if (name.Equals("expire", StringComparison.OrdinalIgnoreCase)) expire = value;
        }
        return Create(upload, download, total, expire);
    }

    internal static SubscriptionUsage? FromJson(JsonNode? node)
    {
        if (node is not JsonObject fields) return null;
        return Create(ReadNumber(fields, "Upload"), ReadNumber(fields, "Download"),
            ReadNumber(fields, "Total"), ReadNumber(fields, "Expire"));
    }

    private static SubscriptionUsage? Create(long? upload, long? download, long? total, long? expire)
    {
        if (upload == 0 && download == 0 && total == 0 && expire == 0) return null;
        total = total is > 0 ? total : null;
        expire = expire is > 0 and <= MaximumUnixSeconds ? expire : null;
        if (upload is null && download is null && total is null && expire is null) return null;
        return new(upload, download, total, expire);
    }

    private static long? ReadNumber(JsonObject fields, string name)
    {
        foreach (var field in fields)
            if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return field.Value is JsonValue value && value.TryGetValue<long>(out var number) && number >= 0 ? number : null;
        return null;
    }
}
