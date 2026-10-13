using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexProxy.Core;
using static CodexProxy.App;

namespace CodexProxy;

public partial class KernelSettingsView : System.Windows.Controls.UserControl
{
    private const string ReleasesUrl = "https://github.com/MetaCubeX/mihomo/releases/latest";
    private readonly ProxyService service;
    private readonly Func<Func<Task>, bool, Task> executeProxyOperation;
    private CancellationTokenSource? operationCancellation;
    private Task? operationTask;
    private List<KernelOption> options = [];
    private bool busy, applying, closed, initialized, currentPathLoaded;
    private string? currentPath, selectedPath;
    private string releaseUrl = ReleasesUrl;
    private Func<string>? messageText;
    private bool CanRender => !closed && IsLoaded && IsVisible;
    public bool Changed { get; private set; }
    public event Action? Applied;

    public KernelSettingsView(ProxyService service, Func<Func<Task>, bool, Task> executeProxyOperation)
    {
        InitializeComponent();
        this.service = service;
        this.executeProxyOperation = executeProxyOperation;
        Loaded += async (_, _) => await ActivateAsync();
        Unloaded += (_, _) => Pause();
        IsVisibleChanged += async (_, _) => { if (IsVisible) await ActivateAsync(); else Pause(); };
    }

    private async Task ActivateAsync()
    {
        if (!CanRender) return;
        if (operationTask is { } pending) await pending;
        if (!CanRender) return;
        RefreshLanguage();
        if (!initialized) await DiscoverAsync();
        else
        {
            var previousMessage = messageText;
            await RunAsync(async cancellation =>
            {
                var path = await Task.Run(() => service.SelectedKernelPath, cancellation);
                if (!CanRender || cancellation.IsCancellationRequested) return;
                if (!currentPathLoaded || !string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
                { currentPath = path; currentPathLoaded = true; }
                if (previousMessage is not null) ShowMessage(previousMessage);
            }, () => T("正在读取当前内核…", "Reading the current core…"));
        }
    }

    private void Pause() { if (!applying) operationCancellation?.Cancel(); }

    public Task CloseAsync()
    {
        closed = true;
        Pause();
        return operationTask ?? Task.CompletedTask;
    }

    public void RefreshLanguage()
    {
        if (!CanRender) return;
        CurrentPathText.Text = currentPathLoaded ? currentPath ?? T("尚未指定，首次启用订阅时优先使用内置内核。", "Not selected. The bundled core is preferred when you first activate a subscription.") :
            T("正在读取内核设置…", "Reading core settings…");
        if (!ReferenceEquals(KernelList.ItemsSource, options))
        {
            var preferred = selectedPath;
            KernelList.ItemsSource = options;
            KernelList.SelectedItem = options.FirstOrDefault(k => string.Equals(k.Path, preferred, StringComparison.OrdinalIgnoreCase)) ??
                options.FirstOrDefault(k => string.Equals(k.Path, currentPath, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
        }
        var savedReleaseUrl = releaseUrl;
        KernelList.Items.Refresh();
        releaseUrl = savedReleaseUrl;
        DetectButton.IsEnabled = BrowseButton.IsEnabled = UpdateButton.IsEnabled = ApplyButton.IsEnabled = !busy;
        KernelList.IsEnabled = !applying;
        CancelButton.Content = T("停止查询", "Cancel query");
        CancelButton.IsEnabled = !applying && operationCancellation is { IsCancellationRequested: false };
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (messageText is not null) MessageLabel.Text = messageText();
    }

    private void ShowMessage(Func<string> text)
    {
        messageText = text;
        if (CanRender) MessageLabel.Text = text();
    }

    private Task RunAsync(Func<CancellationToken, Task> action, Func<string> progress, bool applies = false)
    {
        if (busy || !CanRender) return Task.CompletedTask;
        var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        busy = true; applying = applies;
        ShowMessage(progress); RefreshLanguage();
        operationTask = RunOperationAsync(action, cancellation);
        return operationTask;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation)
    {
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { ShowMessage(() => T("查询已停止，可重新检测。", "Query cancelled. You can detect again.")); }
        catch (ProxyException error) { ShowMessage(() => error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception or InvalidOperationException)
        { ShowMessage(() => T("操作失败，请检查内核路径、网络和本地文件权限。", "Operation failed. Check the core path, network and local file permissions.")); }
        finally
        {
            busy = false; applying = false;
            if (operationCancellation == cancellation) operationCancellation = null;
            cancellation.Dispose();
            RefreshLanguage();
        }
    }

    private Task DiscoverAsync() => RunAsync(async cancellation =>
    {
        var result = await Task.Run(async () =>
        {
            var selected = service.SelectedKernelPath;
            return (SelectedPath: selected, Options: await KernelCatalog.DiscoverAsync(service.DirectoryPath, selected, cancellation));
        }, cancellation);
        if (!CanRender || cancellation.IsCancellationRequested) return;
        selectedPath = (KernelList.SelectedItem as KernelOption)?.Path ?? result.SelectedPath;
        currentPath = result.SelectedPath; currentPathLoaded = true;
        options = result.Options; initialized = true;
        ShowMessage(() => options.Count == 0 ? T("未检测到可用的 Mihomo 内核，请手动选择。", "No compatible Mihomo core found. Choose a core file manually.") :
            T($"检测到 {options.Count} 个 Mihomo 内核。切换将短暂重启专用代理，新连接可能需要重试。", $"Found {options.Count} Mihomo cores. Switching briefly restarts the dedicated proxy; connections may need a retry."));
    }, () => T("正在检测内置内核与常见安装目录…", "Detecting the bundled core and common installation folders…"));

    private async void DetectClick(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        if (busy || !CanRender) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = T("选择 Mihomo 内核", "Choose a Mihomo core"), Filter = "Mihomo (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dialog.FileName;
        var previousSelection = selectedPath;
        await RunAsync(async cancellation =>
        {
            var selected = await Task.Run(() => KernelCatalog.InspectAsync(path, "手动选择", cancellation), cancellation);
            if (!CanRender || cancellation.IsCancellationRequested) return;
            options = options.Where(k => !string.Equals(k.Path, selected.Path, StringComparison.OrdinalIgnoreCase)).Append(selected).ToList();
            if (selectedPath == previousSelection) selectedPath = selected.Path;
            ShowMessage(() => T("已识别 Mihomo，点击‘应用所选内核’后生效。", "Mihomo detected. Click Apply selected core to use it."));
        }, () => T("正在读取内核版本…", "Reading the core version…"));
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { selectedPath = (KernelList.SelectedItem as KernelOption)?.Path; releaseUrl = ReleasesUrl; }

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (KernelList.SelectedItem is not KernelOption selected) return;
        var path = selected.Path;
        await RunAsync(async _ =>
        {
            string? appliedPath = null;
            var applied = false;
            await executeProxyOperation(() => Task.Run(async () =>
            { await service.SelectKernelAsync(path); appliedPath = service.SelectedKernelPath; applied = true; }), true);
            if (!applied) throw new ProxyException(T("内核未能应用，请稍后重试。", "The core could not be applied. Try again later."));
            Changed = true;
            currentPath = appliedPath; currentPathLoaded = true;
            ShowMessage(() => service.ProxyEnabled ? T("内核选择已保存并应用。已有请求可能需要重试。", "Core selection saved and applied. Existing requests may need a retry.") :
                T("内核选择已保存，点击‘启动代理’时生效。", "Core selection saved. It takes effect when you click Start proxy."));
            if (!closed) Applied?.Invoke();
        }, () => T("正在验证并应用内核，请稍候…", "Checking and applying the core, please wait…"), true);
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (KernelList.SelectedItem is not KernelOption selected) return;
        await RunAsync(async cancellation =>
        {
            var result = await Task.Run(() => KernelCatalog.CheckUpdateAsync(selected, cancellation), cancellation);
            if (!CanRender || cancellation.IsCancellationRequested) return;
            if (!string.Equals(selectedPath, selected.Path, StringComparison.OrdinalIgnoreCase))
            { ShowMessage(() => T("版本检查已完成。当前选择已改变，可再次检查所选内核。", "Version check finished. The selection changed; check the currently selected core again if needed.")); return; }
            releaseUrl = result.ReleaseUrl;
            string Status() => result.UpdateAvailable switch
            {
                true => T("有新版本，可打开官方发布页查看。", "An update is available. Open the official releases page for details."),
                false => T("所选内核不低于当前稳定版。", "The selected core is at least as recent as the current stable release."),
                null => T("当前为开发版或版本无法比较，请在官方发布页确认。", "This is a development build or its version cannot be compared. Check the official releases page.")
            };
            ShowMessage(() => T($"所选版本：{result.CurrentVersion} · 官方稳定版：{result.LatestVersion}\n{Status()}", $"Selected: {result.CurrentVersion} · Official stable: {result.LatestVersion}\n{Status()}"));
        }, () => T("正在检查 Mihomo 官方稳定版，只查询版本，不替换内核…", "Checking the official stable release. This only checks the version; it does not replace the core…"));
    }

    private void CancelClick(object sender, RoutedEventArgs e) { Pause(); RefreshLanguage(); }

    private void OpenReleasesClick(object sender, RoutedEventArgs e)
    {
        if (!CanRender) return;
        try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { ShowMessage(() => T("无法打开浏览器，请访问 Mihomo 官方发布页。", "Could not open the browser. Visit the official Mihomo releases page.")); }
    }
}
