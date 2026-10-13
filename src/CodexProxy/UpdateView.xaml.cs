using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using CodexProxy.Core;
using static CodexProxy.App;

namespace CodexProxy;

public partial class UpdateView : System.Windows.Controls.UserControl
{
    private readonly Func<IWebProxy?> proxyProvider;
    private readonly string directory;
    private readonly string currentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    private CancellationTokenSource? queryCancellation;
    private Task? queryTask;
    private AppUpdateResult? result, known;
    private bool closed, autoChecked, usedCore, hasNetworkAttempt, cancelled, browserError;
    private bool CanRender => !closed && IsLoaded && IsVisible;

    public UpdateView(Func<IWebProxy?> proxyProvider, string directory)
    {
        InitializeComponent();
        this.proxyProvider = proxyProvider;
        this.directory = directory;
        Loaded += async (_, _) => await ActivateAsync();
        Unloaded += (_, _) => queryCancellation?.Cancel();
        IsVisibleChanged += async (_, _) => { if (IsVisible) await ActivateAsync(); else queryCancellation?.Cancel(); };
    }

    private async Task ActivateAsync()
    {
        if (!CanRender) return;
        if (queryTask is { } pending) await pending;
        if (!CanRender) return;
        var checkNetwork = !autoChecked;
        autoChecked = true;
        await StartCheckAsync(checkNetwork, false);
    }

    public Task CloseAsync()
    {
        closed = true;
        queryCancellation?.Cancel();
        return queryTask ?? Task.CompletedTask;
    }

    private static bool Compared(AppUpdateResult? value) => value?.Status is AppUpdateStatus.Available or AppUpdateStatus.UpToDate or AppUpdateStatus.UnknownCurrentVersion;

    private bool Remember(AppUpdateResult? value)
    {
        if (value?.Release is null || known is not null && !(value.CheckedAt >= known.CheckedAt)) return false;
        known = value;
        return true;
    }

    private AppUpdateResult KeepKnownData(AppUpdateResult value) => known is null ? value : value with
    { Release = known.Release, Source = known.Source, CheckedAt = known.CheckedAt, FromCache = true };

    public void RefreshLanguage()
    {
        if (!CanRender) return;
        CurrentVersionText.Text = currentVersion;
        KnownLatestLabel.Text = T("已知最新版本", "Latest known version");
        ChannelLabel.Text = T("发布渠道", "Release channel");
        ComparisonLabel.Text = T("本次比较状态", "Comparison status");
        SourceLabel.Text = T("已知信息来源", "Known data source");
        CheckedLabel.Text = T("最近成功检查", "Last successful check");
        RetryLabel.Text = T("下次 API 尝试", "Next API attempt");
        var release = known?.Release;
        LatestVersionText.Text = release?.Version ?? "—";
        ChannelText.Text = release is null ? "—" : release.Prerelease ? T("预览版", "Preview") : T("正式版", "Stable");
        PublishedText.Text = release?.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        NotesText.Text = string.IsNullOrWhiteSpace(release?.Notes) ? T("暂无更新说明。", "No release notes available.") : release.Notes;
        SourceText.Text = known?.Source switch
        {
            AppUpdateSource.GitHubApi => "GitHub Releases API",
            AppUpdateSource.PublicManifest => T("备用公开更新清单", "Public fallback manifest"),
            _ => T("尚无已知信息", "No known release data")
        };
        if (known?.FromCache == true) SourceText.Text += T(" · 本地缓存", " · Local cache");
        else if (release is not null && (queryCancellation is not null || !Compared(result)))
            SourceText.Text += T(" · 上次已知信息", " · Last known information");
        CheckedText.Text = known?.CheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        RetryText.Text = (result?.RetryAt ?? known?.RetryAt) is { } retry && retry > DateTimeOffset.UtcNow
            ? retry.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : T("可点击检查更新", "Check updates is available");
        ComparisonText.Text = queryCancellation is not null ? T("正在读取，尚未完成比较", "Reading data; comparison pending") :
            cancelled ? T("本次已取消", "This check was cancelled") : result?.Status switch
            {
                AppUpdateStatus.Available => T("有可用更新", "Update available"),
                AppUpdateStatus.UpToDate => T("当前版本不低于已知版本", "Current version is at least as recent as the known release"),
                AppUpdateStatus.UnknownCurrentVersion => T("当前版本无法比较", "Current version cannot be compared"),
                null => T("尚未比较", "Not compared yet"),
                _ => T("本次未完成，请稍后重试", "This comparison did not complete; retry later")
            };
        if (Compared(result) && result?.FromCache == true) ComparisonText.Text += T("（依据缓存）", " (from cache)");
        if (Compared(result) && result?.Source == AppUpdateSource.PublicManifest) ComparisonText.Text += T("（备用清单）", " (fallback manifest)");
        NetworkHint.Text = hasNetworkAttempt ? usedCore ? T("上次查询使用运行中的专用代理。", "The last query used the running dedicated proxy.") :
            T("上次查询使用系统默认网络和代理设置；不会启动内核。", "The last query used the system's default network and proxy settings; it does not start a core.") :
            T("首次打开检查一次；切回页面保留结果，可手动再次查询。", "Checks once on first open. Returning preserves results; check again manually if needed.");
        CheckButton.IsEnabled = queryCancellation is null;
        CancelButton.IsEnabled = queryCancellation is { IsCancellationRequested: false };
        Progress.Visibility = queryCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        MessageLabel.Text = browserError ? T("无法打开浏览器，请稍后重试。", "Could not open the browser. Try again later.") :
            queryCancellation is not null ? T("正在读取更新信息…", "Reading update information…") :
            cancelled ? T("查询已取消，已知发布信息保留，可手动重试。", "Check cancelled. Known release information is kept; retry manually.") : result?.Status switch
            {
                AppUpdateStatus.Available => T("有新版本，可打开发布页查看并自行更新。", "An update is available. Open the release page to review and update manually."),
                AppUpdateStatus.UpToDate => T("已根据所示来源完成比较。", "Compared against the displayed source."),
                AppUpdateStatus.UnknownCurrentVersion => T("当前版本无法比较，请打开发布页确认。", "The current version could not be compared. Check the release page."),
                AppUpdateStatus.NoReleases => T("项目还没有发布版本，已有信息保留。", "No releases have been published yet. Known information is kept."),
                AppUpdateStatus.NoComparableRelease => T("没有可识别的版本标签，请打开发布页查看。", "No comparable version tags were found. Check the release page."),
                AppUpdateStatus.NotFound => T("发布信息暂不可用，已有信息保留，请稍后重试。", "Release information is unavailable. Known information is kept; retry later."),
                AppUpdateStatus.RateLimited => T("GitHub 请求已限流；已有信息保留，可在下次 API 尝试时间后重试。", "GitHub's request limit was reached. Known information is kept; retry after the next API attempt time."),
                AppUpdateStatus.AccessDenied => T("GitHub 拒绝了查询请求；已有信息保留，请稍后重试。", "GitHub denied the request. Known information is kept; retry later."),
                AppUpdateStatus.Timeout => T("查询超时；已有信息保留，请检查网络或稍后重试。", "The check timed out. Known information is kept; check the network or retry later."),
                AppUpdateStatus.NetworkError => T("无法连接更新服务；已有信息保留，请检查网络或稍后重试。", "Could not connect to the update service. Known information is kept; check the network or retry later."),
                AppUpdateStatus.ServiceError => T("更新服务返回异常状态；已有信息保留，请稍后重试。", "The update service returned an unexpected status. Known information is kept; retry later."),
                AppUpdateStatus.InvalidResponse => T("发布数据无法解析；已有信息保留，可打开发布页确认。", "The release data could not be read. Known information is kept; check the release page."),
                _ => T("点击‘检查更新’查询正式版和预览版。", "Click Check updates to look for stable and preview releases.")
            };
        if (Compared(result) && result?.Source == AppUpdateSource.PublicManifest)
            MessageLabel.Text += T(" GitHub API 暂不可用，本次使用备用公开清单。", " GitHub API is unavailable; this check used the public fallback manifest.");
    }

    private Task StartCheckAsync(bool checkNetwork, bool force)
    {
        if (!CanRender) return Task.CompletedTask;
        if (queryCancellation is not null) return queryTask ?? Task.CompletedTask;
        var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        queryTask = CheckAsync(cancellation, checkNetwork, force);
        return queryTask;
    }

    private async Task CheckAsync(CancellationTokenSource cancellation, bool checkNetwork, bool force)
    {
        if (checkNetwork) { cancelled = false; browserError = false; }
        try
        {
            var cached = await Task.Run(() => AppUpdate.LoadCached(currentVersion, directory), cancellation.Token);
            if (!CanRender || cancellation.IsCancellationRequested) return;
            var cacheIsCurrent = cached is not null && (known is null || cached.CheckedAt >= known.CheckedAt);
            if (cacheIsCurrent)
            {
                Remember(cached);
                result = KeepKnownData(result is null || Compared(result) ? cached! : result);
            }
            RefreshLanguage();
            if (!checkNetwork) return;
            var proxy = proxyProvider();
            usedCore = proxy is not null; hasNetworkAttempt = true;
            RefreshLanguage();
            var update = await Task.Run(() => AppUpdate.CheckAsync(currentVersion, proxy, cancellation.Token, directory, force), cancellation.Token);
            if (!CanRender || cancellation.IsCancellationRequested) return;
            var accepted = Remember(update);
            if (accepted || known is null) result = update;
            else if (!Compared(update)) result = KeepKnownData(update);
        }
        catch (OperationCanceledException) { if (!closed) cancelled = true; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if (!closed && CanRender) result = KeepKnownData(new(AppUpdateStatus.NetworkError)); }
        finally
        {
            if (queryCancellation == cancellation) queryCancellation = null;
            cancellation.Dispose();
            RefreshLanguage();
        }
    }

    private async void CheckClick(object sender, RoutedEventArgs e) => await StartCheckAsync(true, true);
    private void CancelClick(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); RefreshLanguage(); }

    private void OpenReleasesClick(object sender, RoutedEventArgs e)
    {
        if (!CanRender) return;
        var url = known?.Release?.Url ?? AppUpdate.ReleasesUrl;
        if (!AppUpdate.IsReleaseUrl(url)) url = AppUpdate.ReleasesUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { browserError = true; RefreshLanguage(); }
    }
}
