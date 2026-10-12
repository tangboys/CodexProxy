using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexProxy.Core;
using static CodexProxy.App;

namespace CodexProxy;

public partial class KernelWindow : Window
{
    private readonly ProxyService service;
    private readonly Func<Func<Task>, bool, Task> executeProxyOperation;
    private bool busy, applying, closed, currentPathLoaded;
    private string? currentPath;
    private string releaseUrl = "https://github.com/MetaCubeX/mihomo/releases/latest";
    public bool Changed { get; private set; }

    public KernelWindow(ProxyService service, Func<Func<Task>, bool, Task> executeProxyOperation)
    {
        InitializeComponent();
        this.service = service;
        this.executeProxyOperation = executeProxyOperation;
        CurrentPathText.Text = T("正在读取内核设置…", "Reading core settings…");
        Loaded += async (_, _) => await DiscoverAsync();
        Closing += (_, e) =>
        {
            if (applying)
            {
                e.Cancel = true;
                ShowMessage(() => T("正在切换内核，请完成后再关闭。", "The core is being switched. Wait for the operation to finish before closing."));
            }
            else closed = true;
        };
    }

    private void ShowCurrent(string? path)
    {
        currentPath = path; currentPathLoaded = true;
        CurrentPathText.Text = path ??
            T("尚未指定，首次启用订阅时优先使用内置内核。", "Not selected. The bundled core is preferred when you first activate a subscription.");
    }

    public void RefreshLanguage()
    {
        if (closed) return;
        if (currentPathLoaded) ShowCurrent(currentPath);
        else CurrentPathText.Text = T("正在读取内核设置…", "Reading core settings…");
        var selected = KernelList.SelectedItem;
        var savedReleaseUrl = releaseUrl;
        KernelList.Items.Refresh();
        KernelList.SelectedItem = selected;
        releaseUrl = savedReleaseUrl;
        if (messageText is not null) MessageLabel.Text = messageText();
    }

    private Func<string>? messageText;

    private void ShowMessage(Func<string> text)
    {
        messageText = text;
        MessageLabel.Text = text();
    }

    private async Task RunAsync(Func<Task> action, string progress, bool applies = false)
    {
        if (busy || closed) return;
        busy = true; applying = applies;
        DetectButton.IsEnabled = BrowseButton.IsEnabled = UpdateButton.IsEnabled = ApplyButton.IsEnabled = false;
        KernelList.IsEnabled = DoneButton.IsEnabled = !applies;
        Progress.Visibility = Visibility.Visible;
        ShowMessage(() => progress);
        try { await action(); }
        catch (ProxyException error) { if (!closed) ShowMessage(() => error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        { if (!closed) ShowMessage(() => T("操作失败，请检查内核路径、网络和本地文件权限。", "Operation failed. Check the core path, network and local file permissions.")); }
        finally
        {
            busy = false; applying = false;
            if (!closed)
            {
                DetectButton.IsEnabled = BrowseButton.IsEnabled = UpdateButton.IsEnabled = ApplyButton.IsEnabled =
                    KernelList.IsEnabled = DoneButton.IsEnabled = true;
                Progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task DiscoverAsync() => await RunAsync(async () =>
    {
        var result = await Task.Run(async () =>
        {
            var selectedPath = service.SelectedKernelPath;
            return (SelectedPath: selectedPath, Options: await KernelCatalog.DiscoverAsync(service.DirectoryPath, selectedPath));
        });
        if (closed) return;
        var selectedPath = (KernelList.SelectedItem as KernelOption)?.Path ?? result.SelectedPath;
        ShowCurrent(result.SelectedPath);
        KernelList.ItemsSource = result.Options;
        KernelList.SelectedItem = result.Options.FirstOrDefault(k => string.Equals(k.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? result.Options.FirstOrDefault();
        ShowMessage(() => result.Options.Count == 0
            ? T("未检测到可用的 Mihomo 内核，请手动选择。", "No compatible Mihomo core found. Choose a core file manually.")
            : T($"检测到 {result.Options.Count} 个 Mihomo 内核。切换将短暂重启专用代理，新连接可能需要重试。", $"Found {result.Options.Count} Mihomo cores. Switching briefly restarts the dedicated proxy; connections may need a retry."));
    }, T("正在检测内置内核与常见安装目录…", "Detecting the bundled core and common installation folders…"));

    private async void DetectClick(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        if (busy || closed) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = T("选择 Mihomo 内核", "Choose a Mihomo core"), Filter = "Mihomo (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        var previousSelection = KernelList.SelectedItem;
        await RunAsync(async () =>
        {
            var selected = await Task.Run(() => KernelCatalog.InspectAsync(path, "手动选择"));
            if (closed) return;
            var browsing = KernelList.SelectedItem;
            var options = KernelList.Items.Cast<KernelOption>().Where(k => !string.Equals(k.Path, selected.Path, StringComparison.OrdinalIgnoreCase)).Append(selected).ToList();
            KernelList.ItemsSource = options;
            KernelList.SelectedItem = ReferenceEquals(browsing, previousSelection) ? selected :
                options.FirstOrDefault(k => string.Equals(k.Path, (browsing as KernelOption)?.Path, StringComparison.OrdinalIgnoreCase)) ?? selected;
            ShowMessage(() => T("已识别 Mihomo，点击‘应用所选内核’后生效。", "Mihomo detected. Click Apply selected core to use it."));
        }, T("正在读取内核版本…", "Reading the core version…"));
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { releaseUrl = "https://github.com/MetaCubeX/mihomo/releases/latest"; }

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (KernelList.SelectedItem is not KernelOption selected) return;
        var path = selected.Path;
        await RunAsync(async () =>
        {
            await executeProxyOperation(() => Task.Run(() => service.SelectKernelAsync(path)), true);
            Changed = true;
            var currentPath = await Task.Run(() => service.SelectedKernelPath);
            if (closed) return;
            ShowCurrent(currentPath);
            ShowMessage(() => service.ProxyEnabled ? T("内核选择已保存并应用。已有请求可能需要重试。", "Core selection saved and applied. Existing requests may need a retry.") :
                T("内核选择已保存，点击‘启动代理’时生效。", "Core selection saved. It takes effect when you click Start proxy."));
        }, T("正在验证并应用内核，请稍候…", "Checking and applying the core, please wait…"), true);
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (KernelList.SelectedItem is not KernelOption selected) return;
        await RunAsync(async () =>
        {
            var result = await Task.Run(() => KernelCatalog.CheckUpdateAsync(selected));
            if (closed) return;
            if (!string.Equals((KernelList.SelectedItem as KernelOption)?.Path, selected.Path, StringComparison.OrdinalIgnoreCase))
            {
                ShowMessage(() => T("版本检查已完成。当前选择已改变，可再次检查所选内核。", "Version check finished. The selection changed; check the currently selected core again if needed."));
                return;
            }
            releaseUrl = result.ReleaseUrl;
            string Status() => result.UpdateAvailable switch
            {
                true => T("有新版本，可打开官方发布页查看。", "An update is available. Open the official releases page for details."),
                false => T("所选内核不低于当前稳定版。", "The selected core is at least as recent as the current stable release."),
                null => T("当前为开发版或版本无法比较，请在官方发布页确认。", "This is a development build or its version cannot be compared. Check the official releases page.")
            };
            ShowMessage(() => T($"所选版本：{result.CurrentVersion} · 官方稳定版：{result.LatestVersion}\n{Status()}", $"Selected: {result.CurrentVersion} · Official stable: {result.LatestVersion}\n{Status()}"));
        }, T("正在检查 Mihomo 官方稳定版，只查询版本，不替换内核…", "Checking the official stable release. This only checks the version; it does not replace the core…"));
    }

    private void OpenReleasesClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); }
        catch (Win32Exception) { ShowMessage(() => T("无法打开浏览器，请访问 Mihomo 官方发布页。", "Could not open the browser. Visit the official Mihomo releases page.")); }
    }

    private void DoneClick(object sender, RoutedEventArgs e) => Close();
}
