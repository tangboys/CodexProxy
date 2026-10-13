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
public enum AppUpdateSource { GitHubApi, PublicManifest }
public sealed record AppUpdateResult(AppUpdateStatus Status, AppRelease? Release = null)
{
    public AppUpdateSource? Source { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public bool FromCache { get; init; }
}

public static class AppUpdate
{
    public const string ReleasesUrl = "https://github.com/tangboys/CodexProxy/releases";
    private const string ApiUrl = "https://api.github.com/repos/tangboys/CodexProxy/releases?per_page=100";
    private const string ManifestUrl = "https://raw.githubusercontent.com/tangboys/CodexProxy/main/updates/latest.json";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim CheckGate = new(1, 1);

    public static AppUpdateResult? LoadCached(string currentVersion, string directory)
    {
        var cache = ReadCache(directory);
        return cache is null ? null : CachedResult(currentVersion, cache);
    }

    public static async Task<AppUpdateResult> CheckAsync(string currentVersion, IWebProxy? proxy = null,
        CancellationToken cancellation = default, string? directory = null, bool force = false)
    {
        cancellation.ThrowIfCancellationRequested();
        await CheckGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var cache = ReadCache(directory) ?? new AppUpdateCache();
            var now = DateTimeOffset.UtcNow;
            if (!force && cache.AttemptedAt is { } attempted && now >= attempted && now - attempted < CacheDuration)
                return CachedResult(currentVersion, cache);
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) };
            if (proxy is not null) handler.Proxy = proxy;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexProxy-UpdateCheck/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
            var api = cache.RetryAt > now ? new AppUpdateResult(AppUpdateStatus.RateLimited) { RetryAt = cache.RetryAt }
                : await FetchAsync(client, ApiUrl, currentVersion, false, cancellation).ConfigureAwait(false);
            cache = cache with { RetryAt = api.RetryAt, Failure = api.Status };
            if (api.Release is not null)
                return await RememberAsync(directory, cache, api.Release, AppUpdateSource.GitHubApi, currentVersion, cancellation).ConfigureAwait(false);
            if (api.Status == AppUpdateStatus.RateLimited)
            {
                cache = cache with { Failure = api.Status, AttemptedAt = DateTimeOffset.UtcNow };
                await SaveCacheAsync(directory, cache, cancellation).ConfigureAwait(false);
            }
            if (api.Status is AppUpdateStatus.NoReleases or AppUpdateStatus.NoComparableRelease)
                return await FailureAsync(directory, cache, api.Status, cancellation).ConfigureAwait(false);
            var manifest = await FetchAsync(client, ManifestUrl, currentVersion, true, cancellation).ConfigureAwait(false);
            if (manifest.Release is not null)
                return await RememberAsync(directory, cache, manifest.Release, AppUpdateSource.PublicManifest, currentVersion, cancellation).ConfigureAwait(false);
            var failure = api.Status == AppUpdateStatus.NotFound ? manifest.Status : api.Status;
            return await FailureAsync(directory, cache, failure, cancellation).ConfigureAwait(false);
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<AppUpdateResult> FetchAsync(HttpClient client, string url, string currentVersion,
        bool manifest, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(AppUpdateStatus.NotFound);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(AppUpdateStatus.RateLimited) { RetryAt = manifest ? null : ReadRetryAt(response) };
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");
                var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                var limited = remaining || response.Headers.RetryAfter is not null || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                return new(limited ? AppUpdateStatus.RateLimited : AppUpdateStatus.AccessDenied)
                    { RetryAt = limited && !manifest ? ReadRetryAt(response) : null };
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(AppUpdateStatus.AccessDenied);
            if (!response.IsSuccessStatusCode) return new(AppUpdateStatus.ServiceError);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            return manifest ? ReadManifest(document.RootElement, currentVersion) : ReadApi(document.RootElement, currentVersion);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(AppUpdateStatus.Timeout); }
        catch (HttpRequestException) { return new(AppUpdateStatus.NetworkError); }
        catch (JsonException) { return new(AppUpdateStatus.InvalidResponse); }
    }

    private static AppUpdateResult ReadApi(JsonElement root, string currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Array) return new(AppUpdateStatus.InvalidResponse);
        AppRelease? latest = null;
        ReleaseVersion? latestVersion = null;
        var hasPublishedRelease = false;
        foreach (var item in root.EnumerateArray())
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
            DateTimeOffset? publishedAt = item.TryGetProperty("published_at", out var published) && TryPublishedAt(published, out var date) ? date : null;
            var notes = item.TryGetProperty("body", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() ?? "" : "";
            var url = item.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null;
            latest = new(name, prerelease, publishedAt, TrimNotes(notes), IsReleaseUrl(url) ? url! : ReleasesUrl);
            latestVersion = version;
        }
        return latest is null ? new(hasPublishedRelease ? AppUpdateStatus.NoComparableRelease : AppUpdateStatus.NoReleases)
            : Compare(currentVersion, latest);
    }

    private static AppUpdateResult ReadManifest(JsonElement root, string currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || ReleaseVersion.Parse(version.GetString()) is null ||
            !root.TryGetProperty("prerelease", out var preview) || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("published_at", out var published) || !TryPublishedAt(published, out var publishedAt) ||
            !root.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || !IsReleaseUrl(url.GetString()))
            return new(AppUpdateStatus.InvalidResponse);
        var name = version.GetString()!;
        var uri = new Uri(url.GetString()!);
        const string prefix = "/tangboys/CodexProxy/releases/tag/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]) != name) return new(AppUpdateStatus.InvalidResponse);
        return Compare(currentVersion, new(name, preview.GetBoolean(), publishedAt, TrimNotes(notes.GetString()!), uri.AbsoluteUri));
    }

    private static bool TryPublishedAt(JsonElement value, out DateTimeOffset date)
    {
        date = default;
        return value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out date) &&
            Regex.IsMatch(value.GetString()!, @"T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);
    }

    private static string TrimNotes(string notes)
    {
        notes = notes.Trim();
        return notes.Length > 1200 ? notes[..1200] + "…" : notes;
    }

    private static AppUpdateResult Compare(string currentVersion, AppRelease release)
    {
        var current = ReleaseVersion.Parse(currentVersion);
        return new(current is null ? AppUpdateStatus.UnknownCurrentVersion : ReleaseVersion.Parse(release.Version)!.CompareTo(current) > 0
            ? AppUpdateStatus.Available : AppUpdateStatus.UpToDate, release);
    }

    private static DateTimeOffset ReadRetryAt(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        var retry = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= DateTimeOffset.MaxValue - now)
            retry = now + delta;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset > now && (retry is null || reset > retry)) retry = reset;
        }
        return retry is { } future && future > now ? future : now + CacheDuration;
    }

    private static AppUpdateCache? ReadCache(string? directory)
    {
        var cache = directory is null ? null : AppUpdateCache.Load(directory);
        if (cache is null) return null;
        if (cache.Release is { } release && (ReleaseVersion.Parse(release.Version) is null || !IsReleaseUrl(release.Url) ||
            release.Notes is null || release.Notes.Length > 1201 || cache.Source is null || !Enum.IsDefined(cache.Source.Value) || cache.CheckedAt is null ||
            cache.Source == AppUpdateSource.PublicManifest && release.PublishedAt is null))
            cache = cache with { Release = null, Source = null, CheckedAt = null };
        if (cache.Failure is { } failure && (!Enum.IsDefined(failure) ||
            failure is AppUpdateStatus.Available or AppUpdateStatus.UpToDate or AppUpdateStatus.UnknownCurrentVersion)) cache = cache with { Failure = null };
        if (cache.Release is null && cache.Failure is null && cache.RetryAt is null) return null;
        return cache with { RetryAt = cache.RetryAt > DateTimeOffset.UtcNow ? cache.RetryAt : null };
    }

    private static AppUpdateResult CachedResult(string currentVersion, AppUpdateCache cache) =>
        new(cache.Failure ?? (cache.Release is { } release ? Compare(currentVersion, release).Status : AppUpdateStatus.NotFound), cache.Release)
        { Source = cache.Source, CheckedAt = cache.CheckedAt, RetryAt = cache.RetryAt, FromCache = true };

    private static async Task<AppUpdateResult> RememberAsync(string? directory, AppUpdateCache previous, AppRelease release,
        AppUpdateSource source, string currentVersion, CancellationToken cancellation)
    {
        if (source == AppUpdateSource.PublicManifest && previous.Release is { } known &&
            ReleaseVersion.Parse(known.Version)!.CompareTo(ReleaseVersion.Parse(release.Version)) > 0)
            return await FailureAsync(directory, previous, previous.Failure ?? AppUpdateStatus.InvalidResponse, cancellation).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var cache = new AppUpdateCache(release, source, now, previous.RetryAt, now);
        await SaveCacheAsync(directory, cache, cancellation).ConfigureAwait(false);
        return Compare(currentVersion, release) with { Source = source, CheckedAt = now, RetryAt = cache.RetryAt };
    }

    private static async Task<AppUpdateResult> FailureAsync(string? directory, AppUpdateCache cache, AppUpdateStatus status,
        CancellationToken cancellation)
    {
        cache = cache with { Failure = status, AttemptedAt = DateTimeOffset.UtcNow };
        await SaveCacheAsync(directory, cache, cancellation).ConfigureAwait(false);
        return new(status, cache.Release) { Source = cache.Source, CheckedAt = cache.CheckedAt, RetryAt = cache.RetryAt, FromCache = cache.Release is not null };
    }

    private static async Task SaveCacheAsync(string? directory, AppUpdateCache cache, CancellationToken cancellation)
    {
        if (directory is null) return;
        try { await cache.SaveAsync(directory, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
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
        public static ReleaseVersion? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
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
