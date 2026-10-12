using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using CodexProxy.Core;
using static CodexProxy.App;

namespace CodexProxy;

public partial class UpdateWindow : Window
{
    private readonly Func<IWebProxy?> proxyProvider;
    private readonly string currentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    private CancellationTokenSource? queryCancellation;
    private Task? queryTask;
    private AppUpdateResult? result;
    private bool closed, usedCore, cancelled, browserError;

    public UpdateWindow(Func<IWebProxy?> proxyProvider)
    {
        InitializeComponent();
        this.proxyProvider = proxyProvider;
        RefreshLanguage();
        Loaded += async (_, _) => await StartCheckAsync();
        Closing += (_, _) => { closed = true; queryCancellation?.Cancel(); };
    }

    public Task CloseAsync()
    {
        if (!closed) Close();
        return queryTask ?? Task.CompletedTask;
    }

    public void RefreshLanguage()
    {
        if (closed) return;
        CurrentVersionText.Text = currentVersion;
        var release = result?.Release;
        LatestVersionText.Text = release is null ? "—" : release.Version + (release.Prerelease ? T(" · 预览版", " · Preview") : T(" · 正式版", " · Stable"));
        PublishedText.Text = release?.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        NotesText.Text = string.IsNullOrWhiteSpace(release?.Notes) ? T("暂无更新说明。", "No release notes available.") : release.Notes;
        NetworkHint.Text = usedCore ? T("本次查询使用正在运行的专用代理。", "This query uses the running dedicated proxy.") :
            T("本次查询使用系统默认网络和代理设置；不会启动内核。", "This query uses the system's default network and proxy settings; it does not start a core.");
        CheckButton.IsEnabled = queryCancellation is null;
        CancelButton.IsEnabled = queryCancellation is { IsCancellationRequested: false };
        Progress.Visibility = queryCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        MessageLabel.Text = browserError ? T("无法打开浏览器，请稍后重试。", "Could not open the browser. Try again later.") :
            queryCancellation is not null ? T("正在查询 GitHub 发布版本…", "Checking GitHub releases…") :
            cancelled ? T("查询已取消。", "Update check cancelled.") : result?.Status switch
            {
                AppUpdateStatus.Available => T("有新版本，可打开发布页查看并自行更新。", "An update is available. Open the release page to review and update manually."),
                AppUpdateStatus.UpToDate => T("当前版本不低于最新已发布版本。", "The current version is at least as recent as the latest published release."),
                AppUpdateStatus.UnknownCurrentVersion => T("当前版本无法比较，请打开发布页确认。", "The current version could not be compared. Check the release page."),
                AppUpdateStatus.NoReleases => T("项目还没有发布版本。", "No releases have been published yet."),
                AppUpdateStatus.NoComparableRelease => T("没有可识别的版本标签，请打开发布页查看。", "No comparable version tags were found. Check the release page."),
                AppUpdateStatus.NotFound => T("发布信息尚不可用，项目可能尚未公开或发布。", "Release information is not available. The project may not be public or published yet."),
                AppUpdateStatus.RateLimited => T("GitHub 请求已限流，请稍后重试。", "GitHub's request limit was reached. Try again later."),
                AppUpdateStatus.AccessDenied => T("GitHub 拒绝了查询请求，请稍后重试。", "GitHub denied the request. Try again later."),
                AppUpdateStatus.Timeout => T("查询超时，请检查网络或稍后重试。", "The update check timed out. Check the network or try again later."),
                AppUpdateStatus.NetworkError => T("无法连接 GitHub；若专用代理已停止或切换，请重新查询。", "Could not connect to GitHub. Retry if the dedicated proxy was stopped or switched."),
                AppUpdateStatus.ServiceError => T("GitHub 返回异常状态，请稍后重试。", "GitHub returned an unexpected status. Try again later."),
                AppUpdateStatus.InvalidResponse => T("发布数据无法解析，请打开发布页确认。", "The release data could not be read. Check the release page."),
                _ => T("点击‘检查更新’查询正式版和预览版。", "Click Check updates to look for stable and preview releases.")
            };
    }

    private Task StartCheckAsync()
    {
        if (closed || queryCancellation is not null) return Task.CompletedTask;
        var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        queryTask = CheckAsync(cancellation);
        return queryTask;
    }

    private async Task CheckAsync(CancellationTokenSource cancellation)
    {
        result = null; cancelled = false; browserError = false;
        try
        {
            var proxy = proxyProvider();
            usedCore = proxy is not null;
            RefreshLanguage();
            var update = await Task.Run(() => AppUpdate.CheckAsync(currentVersion, proxy, cancellation.Token), cancellation.Token);
            if (!closed && !cancellation.IsCancellationRequested) result = update;
        }
        catch (OperationCanceledException) { if (!closed) cancelled = true; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception)
        { if (!closed) result = new(AppUpdateStatus.NetworkError); }
        finally
        {
            if (queryCancellation == cancellation) queryCancellation = null;
            cancellation.Dispose();
            if (!closed) RefreshLanguage();
        }
    }

    private async void CheckClick(object sender, RoutedEventArgs e) => await StartCheckAsync();
    private void CancelClick(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); RefreshLanguage(); }
    private void DoneClick(object sender, RoutedEventArgs e) => Close();

    private void OpenReleasesClick(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        var url = result?.Release?.Url ?? AppUpdate.ReleasesUrl;
        if (!AppUpdate.IsReleaseUrl(url)) url = AppUpdate.ReleasesUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { browserError = true; RefreshLanguage(); }
    }
}
