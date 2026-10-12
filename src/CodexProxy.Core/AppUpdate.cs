using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProxy.Core;

public enum AppUpdateStatus
{
    Available, UpToDate, UnknownCurrentVersion, NoReleases, NoComparableRelease,
    NotFound, RateLimited, AccessDenied, Timeout, NetworkError, ServiceError, InvalidResponse
}

public sealed record AppRelease(string Version, bool Prerelease, DateTimeOffset? PublishedAt, string Notes, string Url);
public sealed record AppUpdateResult(AppUpdateStatus Status, AppRelease? Release = null);

public static class AppUpdate
{
    public const string ReleasesUrl = "https://github.com/tangboys/CodexProxy/releases";
    private const string ApiUrl = "https://api.github.com/repos/tangboys/CodexProxy/releases?per_page=100";

    public static async Task<AppUpdateResult> CheckAsync(string currentVersion, IWebProxy? proxy = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) };
        if (proxy is not null) handler.Proxy = proxy;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexProxy-UpdateCheck/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        try
        {
            using var response = await client.GetAsync(ApiUrl, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(AppUpdateStatus.NotFound);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(AppUpdateStatus.RateLimited);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");
                var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                return new(remaining || response.Headers.RetryAfter is not null || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    ? AppUpdateStatus.RateLimited : AppUpdateStatus.AccessDenied);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(AppUpdateStatus.AccessDenied);
            if (!response.IsSuccessStatusCode) return new(AppUpdateStatus.ServiceError);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return new(AppUpdateStatus.InvalidResponse);
            AppRelease? latest = null;
            ReleaseVersion? latestVersion = null;
            var hasPublishedRelease = false;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
                hasPublishedRelease = true;
                if (!item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
                var name = tag.GetString() ?? "";
                var version = ReleaseVersion.Parse(name);
                if (version is null) continue;
                var prerelease = item.TryGetProperty("prerelease", out var preview) && preview.ValueKind == JsonValueKind.True;
                var comparison = latestVersion is null ? 1 : version.CompareTo(latestVersion);
                if (comparison < 0 || comparison == 0 && !(latest!.Prerelease && !prerelease)) continue;
                DateTimeOffset? publishedAt = item.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(published.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
                var notes = item.TryGetProperty("body", out var description) && description.ValueKind == JsonValueKind.String ? (description.GetString() ?? "").Trim() : "";
                if (notes.Length > 1200) notes = notes[..1200] + "…";
                var url = item.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null;
                latest = new(name, prerelease, publishedAt, notes, IsReleaseUrl(url) ? url! : ReleasesUrl);
                latestVersion = version;
            }
            if (latest is null) return new(hasPublishedRelease ? AppUpdateStatus.NoComparableRelease : AppUpdateStatus.NoReleases);
            var current = ReleaseVersion.Parse(currentVersion);
            return new(current is null ? AppUpdateStatus.UnknownCurrentVersion : latestVersion!.CompareTo(current) > 0
                ? AppUpdateStatus.Available : AppUpdateStatus.UpToDate, latest);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(AppUpdateStatus.Timeout); }
        catch (HttpRequestException) { return new(AppUpdateStatus.NetworkError); }
        catch (JsonException) { return new(AppUpdateStatus.InvalidResponse); }
    }

    public static bool IsReleaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        const string path = "/tangboys/CodexProxy/releases";
        if (uri.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = path + "/tag/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            ReleaseVersion.Parse(Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..])) is not null;
    }

    private sealed record ReleaseVersion(Version Number, string[] Preview) : IComparable<ReleaseVersion>
    {
        public static ReleaseVersion? Parse(string text)
        {
            var match = Regex.Match(text.Trim(), @"^[vV]?(?<number>\d+\.\d+\.\d+(?:\.\d+)?)(?:-(?<preview>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
            if (!match.Success || !Version.TryParse(match.Groups["number"].Value, out var number)) return null;
            string[] preview = match.Groups["preview"].Success ? match.Groups["preview"].Value.Split('.') : [];
            if (preview.Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return null;
            return new(new Version(number.Major, number.Minor, number.Build, Math.Max(0, number.Revision)), preview);
        }

        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null) return 1;
            var comparison = Number.CompareTo(other.Number);
            if (comparison != 0) return comparison;
            if (Preview.Length == 0 || other.Preview.Length == 0) return other.Preview.Length.CompareTo(Preview.Length);
            for (var i = 0; i < Math.Min(Preview.Length, other.Preview.Length); i++)
            {
                var left = Preview[i]; var right = other.Preview[i];
                var leftNumeric = left.All(char.IsAsciiDigit); var rightNumeric = right.All(char.IsAsciiDigit);
                comparison = leftNumeric && rightNumeric ? left.Length.CompareTo(right.Length) : leftNumeric == rightNumeric ? 0 : leftNumeric ? -1 : 1;
                if (comparison == 0) comparison = string.Compare(left, right, StringComparison.Ordinal);
                if (comparison != 0) return comparison;
            }
            return Preview.Length.CompareTo(other.Preview.Length);
        }
    }
}
