using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexProxy.Core;
using Forms = System.Windows.Forms;
using static CodexProxy.App;

namespace CodexProxy;

public partial class MainWindow : Window
{
    private readonly ProxyService service;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrafficMonitor traffic = new();
    private readonly Dictionary<string, CodexCheck> codexChecks = [];
    private CancellationTokenSource? checkCancellation;
    private Task? checkTask;
    private Task? pollTask, trafficTask;
    private Task? pathTask;
    private readonly SemaphoreSlim serviceGate = new(1, 1);
    private SubscriptionWindow? subscriptionWindow;
    private KernelSettingsView? kernelView;
    private UpdateView? updateView;
    private string? subscriptionName;
    private string? subscriptionUrl;
    private SubscriptionUsage? subscriptionUsage;
    private DateTimeOffset? usageUpdatedAt, nodesCheckedAt;
    private string? selectedNode, usageRequestedUrl, usageError;
    private string? nodePreviewError;
    private CancellationTokenSource? usageCancellation;
    private Task? usageTask;
    private bool? proxyTransition;
    private bool readingTraffic;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon appIcon;
    private readonly Forms.ContextMenuStrip trayMenu;
    private bool initializingLanguage = true, initializingRefreshInterval = true, shellReady;
    private string currentPage = "overview";
    private int refreshIntervalSeconds = 5;
    private Task? uiSettingsTask;
    private bool busy, exiting, pathBusy, shutdownReady;
    private readonly ObservableCollection<Node> nodes = [];
    public static readonly DependencyProperty ProxyActionsEnabledProperty = DependencyProperty.Register(
        nameof(ProxyActionsEnabled), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public bool ProxyActionsEnabled { get => (bool)GetValue(ProxyActionsEnabledProperty); set => SetValue(ProxyActionsEnabledProperty, value); }
    public static readonly DependencyProperty CoreAvailableProperty = DependencyProperty.Register(
        nameof(CoreAvailable), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool CoreAvailable { get => (bool)GetValue(CoreAvailableProperty); private set => SetValue(CoreAvailableProperty, value); }
    public static readonly DependencyProperty ProxyStoppedProperty = DependencyProperty.Register(
        nameof(ProxyStopped), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public bool ProxyStopped { get => (bool)GetValue(ProxyStoppedProperty); private set => SetValue(ProxyStoppedProperty, value); }

    public MainWindow(string directory)
    {
        InitializeComponent();
        service = new ProxyService(directory);
        NodeGrid.ItemsSource = nodes;
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        appIcon = new System.Drawing.Icon(iconStream);
        tray = new Forms.NotifyIcon { Icon = appIcon,
            Visible = true };
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("", null, (_, _) => Dispatcher.Invoke(Restore));
        trayMenu.Items.Add("", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        LanguageBox.SelectedValue = UiText.Language;
        initializingLanguage = false;
        ApplyTrayLanguage();
        RefreshIntervalBox.SelectedValue = "5";
        RefreshIntervalBox.IsEnabled = false;
        shellReady = true;
        NavigateTo("overview");
        Loaded += async (_, _) =>
        {
            var preferences = LoadUiSettingsAsync(); uiSettingsTask = preferences;
            var detection = DetectPathAsync();
            await RunAsync(() => ReloadAsync(), null);
            await detection; await preferences;
            if (uiSettingsTask == preferences) uiSettingsTask = null;
            if (!exiting) { timer.Start(); trafficTimer.Start(); }
        };
        timer.Tick += async (_, _) =>
        {
            if (busy || exiting || pollTask is not null) return;
            var pending = PollAsync(); pollTask = pending;
            await pending;
            if (pollTask == pending) pollTask = null;
        };
        trafficTimer.Tick += async (_, _) =>
        {
            if (busy || exiting || trafficTask is not null) return;
            var pending = ReadTrafficAsync(); trafficTask = pending;
            await pending;
            if (trafficTask == pending) trafficTask = null;
        };
        Closing += OnClosing;
    }

    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void ApplyTrayLanguage()
    {
        tray.Text = T("Codex Proxy · 专用代理", "Codex Proxy · Dedicated proxy");
        trayMenu.Items[0].Text = T("打开代理管理", "Open proxy manager");
        trayMenu.Items[1].Text = T("退出程序", "Exit app");
    }

    private async void LanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (exiting || initializingLanguage || LanguageBox.SelectedValue is not string language || language == UiText.Language) return;
        LanguageBox.IsEnabled = false;
        try
        {
            await Task.Run(() => LanguageSettings.Save(service.DirectoryPath, language));
            ChangeLanguage(language);
            ApplyTrayLanguage();
            NodeGrid.Items.Refresh(); LogGrid.Items.Refresh(); RenderNodes(); RenderProxyState(); RenderSubscriptionUsage();
            if (!service.ProxyEnabled) ResetTrafficDisplay();
            RefreshPageHeading(); RenderRefreshInterval();
            subscriptionWindow?.RefreshLanguage(); kernelView?.RefreshLanguage(); updateView?.RefreshLanguage();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        { MessageLabel.Text = T("语言保存失败，请检查本地文件权限。", "Could not save the language. Check local file permissions."); }
        finally
        {
            initializingLanguage = true;
            LanguageBox.SelectedValue = UiText.Language;
            initializingLanguage = false;
            LanguageBox.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) { e.Cancel = !shutdownReady; return; }
        e.Cancel = true;
        Hide();
        var status = service.ProxyEnabled && CoreAvailable
            ? T("代理管理已收起到托盘，专用代理继续运行。右键托盘图标可退出。", "Proxy manager is in the tray and the dedicated proxy keeps running. Right-click the tray icon to exit.")
            : T("代理管理已收起到托盘；当前未确认有可用的专用代理。右键托盘图标可退出。", "Proxy manager is in the tray. No ready dedicated proxy is confirmed. Right-click the tray icon to exit.");
        tray.ShowBalloonTip(2500, "Codex Proxy", status, Forms.ToolTipIcon.Info);
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        if (busy) { Restore(); MessageLabel.Text = T("正在处理操作，请完成后再退出。", "An operation is in progress. Please wait before exiting."); return; }
        exiting = true;
        UpdateUiState();
        MessageLabel.Text = T("正在停止检测并清理专用代理…", "Stopping checks and cleaning up the dedicated proxy…");
        try
        {
            usageCancellation?.Cancel();
            if (updateView is { } updates) await updates.CloseAsync();
            if (kernelView is { } kernels) await kernels.CloseAsync();
            if (usageTask is { } pendingUsage) await pendingUsage;
            await StopCodexCheckAsync();
            await WaitForPollingAsync();
            if (pathTask is { } pendingPath)
            {
                try { await pendingPath; }
                catch (Exception error) when (error is ProxyException or System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception)
                { /* The path operation reports its own error. */ }
            }
            if (uiSettingsTask is { } pendingSettings) await pendingSettings;
            subscriptionWindow?.Close();
            timer.Stop(); trafficTimer.Stop();
            await Task.Run(async () => await service.DisposeAsync());
            tray.Dispose(); appIcon.Dispose();
            shutdownReady = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error) when (error is ProxyException or System.IO.IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            exiting = false;
            if (kernelView is not null) kernelView.Applied -= KernelApplied;
            KernelHost.Content = UpdateHost.Content = null;
            kernelView = null; updateView = null;
            if (currentPage == "settings") EnsureSettingsView();
            Restore(); timer.Start(); trafficTimer.Start(); UpdateUiState();
            MessageLabel.Text = T("专用代理清理未完成，请稍后再次退出。", "Dedicated proxy cleanup did not finish. Please try exiting again shortly.");
        }
    }

    private async void ExitClick(object sender, RoutedEventArgs e) => await ExitAsync();

    private async Task RunAsync(Func<Task> action, string? success, bool showProgress = true)
    {
        if (busy || exiting) return;
        if (showProgress) { Progress.Visibility = Visibility.Visible; MessageLabel.Text = T("正在处理，请稍候…", "Working, please wait…"); }
        try { await ExecuteProxyOperationAsync(action, false); if (success is not null) MessageLabel.Text = success; }
        catch (ProxyException error) { MessageLabel.Text = error.Message; }
        catch (OperationCanceledException) { if (showProgress) MessageLabel.Text = T("操作已停止。", "Operation stopped."); }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageLabel.Text = T("操作失败，请检查本地配置、程序路径和文件权限。", "Operation failed. Check the local settings, program path and file permissions."); }
        finally { UpdateUiState(); }
    }

    private void UpdateUiState()
    {
        ProxyActionsEnabled = !busy && !exiting;
        NavigationList.IsEnabled = !exiting;
        ProxyStopped = !service.ProxyEnabled;
        AutoCheck.IsEnabled = TestNodesButton.IsEnabled = ProxyActionsEnabled && nodes.Count > 0;
        RefreshNodesButton.IsEnabled = ProxyActionsEnabled && subscriptionUrl is not null;
        RefreshUsageButton.IsEnabled = ProxyActionsEnabled && subscriptionUrl is not null && usageCancellation is null;
        DetectPathButton.IsEnabled = BrowsePathButton.IsEnabled = ProxyActionsEnabled && !pathBusy;
        LaunchButton.IsEnabled = ProxyActionsEnabled && CoreAvailable && !pathBusy;
        ProxyToggleButton.IsEnabled = ProxyActionsEnabled;
        CheckCodexButton.IsEnabled = ProxyActionsEnabled && CoreAvailable && checkCancellation is null;
        CancelCheck.Visibility = checkCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        CancelCheck.IsEnabled = checkCancellation is { IsCancellationRequested: false };
        Progress.Visibility = busy || checkCancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        RenderProxyState();
    }

    private Task ExecuteProxyOperationAsync(Func<Task> action, bool invalidatesNodes) =>
        ExecuteProxyOperationAsync(action, invalidatesNodes, CancellationToken.None);

    private async Task ExecuteProxyOperationAsync(Func<Task> action, bool invalidatesNodes, CancellationToken cancellation)
    {
        if (exiting) throw new ProxyException(T("程序正在退出。", "The application is exiting."));
        if (invalidatesNodes) { checkCancellation?.Cancel(); UpdateUiState(); }
        await serviceGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (exiting) throw new ProxyException(T("程序正在退出。", "The application is exiting."));
            busy = true; UpdateUiState();
            await WaitForPollingAsync();
            if (invalidatesNodes) { await StopCodexCheckAsync(); codexChecks.Clear(); }
            await action();
        }
        finally { busy = false; UpdateUiState(); serviceGate.Release(); }
    }

    private async Task StopCodexCheckAsync()
    {
        checkCancellation?.Cancel();
        if (checkTask is { } pending) await pending;
    }

    private async Task WaitForPollingAsync()
    {
        if (pollTask is { } polling) await polling;
        if (trafficTask is { } trafficRead) await trafficRead;
    }

    private async Task PollAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await ReloadAsync(timeout.Token); }
        catch (Exception error) when (error is ProxyException or OperationCanceledException or System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception or ObjectDisposedException)
        {
            if (service.ProxyEnabled) { CoreAvailable = false; RenderNodes(); UpdateUiState(); }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellation = default)
    {
        var settings = await Task.Run(() =>
        {
            var profile = service.Profile;
            var subscriptions = service.Subscriptions;
            var name = subscriptions.Items.FirstOrDefault(s => s.Id == subscriptions.ActiveId)?.Name;
            var metadata = profile is null ? null : SubscriptionMetadata.Load(service.DirectoryPath, profile.Url);
            return (Profile: profile, Subscription: name, Metadata: metadata);
        }, cancellation);
        if (subscriptionUrl != settings.Profile?.Url)
        { usageCancellation?.Cancel(); nodes.Clear(); codexChecks.Clear(); CoreAvailable = false; nodesCheckedAt = null; nodePreviewError = null;
            subscriptionUsage = null; usageUpdatedAt = null; usageError = null; }
        subscriptionUrl = settings.Profile?.Url;
        subscriptionName = settings.Subscription;
        selectedNode = settings.Profile?.Selected;
        if (settings.Metadata is { } metadata && (usageUpdatedAt is null || metadata.UpdatedAt >= usageUpdatedAt))
        { subscriptionUsage = metadata.Usage; usageUpdatedAt = metadata.UpdatedAt; }
        RenderNodes(); RenderSubscriptionUsage(); UpdateUiState();
        if (subscriptionUrl is { } url && usageRequestedUrl != url)
        { usageRequestedUrl = url; BeginUsageRefresh(url); }
        var snapshot = await Task.Run(async () =>
        {
            NodeSnapshot? preview = null;
            string? previewError = null;
            try { if (settings.Profile is not null) preview = await service.PreviewNodesAsync(cancellation); }
            catch (Exception error) when (error is ProxyException or System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception)
            { previewError = T("节点预览暂不可用，请检查内核或刷新节点。", "Node preview unavailable. Check the core or refresh nodes."); }
            var ready = service.ProxyEnabled && service.Core is not null && previewError is null;
            var desktop = !ready
                ? service.Desktop?.Started == true ? T("Codex 仍在运行 · 专用代理已停止", "Codex is still running · Dedicated proxy stopped") : T("尚未通过此启动器打开 Codex", "Codex has not been opened through this launcher")
                : service.Desktop is null ? T("代理已就绪 · 请通过此启动器打开 Codex", "Proxy ready · Open Codex through this launcher") : await service.Desktop.StatusAsync(cancellation);
            return (Nodes: preview?.Nodes, CheckedAt: preview?.CheckedAt, Ready: ready, Desktop: desktop, Error: previewError);
        }, cancellation);
        CoreAvailable = snapshot.Ready;
        nodesCheckedAt = snapshot.CheckedAt;
        nodePreviewError = snapshot.Error;
        var selectedName = (NodeGrid.SelectedItem as Node)?.Name;
        var updated = (snapshot.Nodes ?? nodes.ToList()).Select(n => n with { Current = CoreAvailable ? n.Current : !service.ProxyEnabled && n.Name == selectedNode,
            Codex = codexChecks.GetValueOrDefault(n.Name) }).ToList();
        if (nodes.Select(n => n.Name).SequenceEqual(updated.Select(n => n.Name)))
        { for (var i = 0; i < updated.Count; i++) if (nodes[i] != updated[i]) nodes[i] = updated[i]; }
        else { nodes.Clear(); foreach (var node in updated) nodes.Add(node); }
        if (selectedName is not null) NodeGrid.SelectedItem = nodes.FirstOrDefault(n => n.Name == selectedName);
        RenderNodes(); RenderSubscriptionUsage(); UpdateUiState();
        if (!service.ProxyEnabled) ResetTrafficDisplay();
        DesktopLabel.Text = snapshot.Desktop;
    }

    private void RenderNodes()
    {
        EmptyState.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = service.ProxyEnabled && !CoreAvailable ? T("代理内核不可达", "Proxy core unreachable") : T("订阅中暂时没有节点", "No nodes in this subscription");
        EmptyHint.Text = subscriptionUrl is null ? T("添加订阅链接，在这里查看和切换可用节点。", "Add a subscription to view and switch available nodes here.") :
            service.ProxyEnabled ? T("等待内核就绪；若持续不可达，请停止后重新启动。", "Waiting for the core. Stop and start again if it remains unreachable.") :
            T("点击‘刷新节点’加载订阅；无需先启动代理。", "Click Refresh nodes to load the subscription. Starting the proxy is not required.");
        var current = nodes.FirstOrDefault(n => n.Current);
        CurrentTitle.Text = service.ProxyEnabled ? T("当前连接", "Current connection") : T("预选节点", "Selected for startup");
        CurrentConnection.DataContext = current;
        CurrentFlag.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        CurrentLabel.Text = current?.DisplayName ?? (subscriptionUrl is null ? T("添加订阅，开始连接", "Add a subscription to connect") :
            !service.ProxyEnabled && selectedNode == CoreSession.Auto ? T("自动优选 · 启动后选择", "Automatic · Selected on startup") : T("尚未建立代理连接", "No proxy connection established"));
        CurrentDelayLabel.Text = T("延迟 ", "Latency ") + (current?.DelayText ?? "—");
        SubscriptionLabel.Text = T("订阅：", "Subscription: ") + subscriptionName;
        AutoCheck.IsChecked = selectedNode == CoreSession.Auto;
        ModeLabel.Text = subscriptionUrl is null ? T("未配置", "Not configured") : !service.ProxyEnabled ? AutoCheck.IsChecked == true ? T("自动预选", "Automatic preset") : T("手动预选", "Manual preset") :
            !CoreAvailable ? T("未连接", "Disconnected") : AutoCheck.IsChecked == true ? T("自动优选", "Automatic") : T("手动固定", "Manual");
        CountLabel.Text = T($"{nodes.Count} 个节点 · {nodes.Count(n => n.Available == true)} 个可用", $"{nodes.Count} nodes · {nodes.Count(n => n.Available == true)} available");
        NodeStatusHint.Text = nodePreviewError ?? (nodesCheckedAt is { } checkedAt
            ? T($"上次检测 {checkedAt.ToLocalTime():MM-dd HH:mm:ss} · 状态供参考，可点击‘检测节点’更新", $"Last check {checkedAt.ToLocalTime():MM-dd HH:mm:ss} · Click Check nodes to update")
            : T("节点可先选择再启动；尚未记录测速时间。", "Select a node before starting. No check time recorded yet."));
    }

    private void RenderProxyState()
    {
        ProxyToggleButton.Content = proxyTransition is true ? T("启动中…", "Starting…") : proxyTransition is false ? T("停止中…", "Stopping…") :
            service.ProxyEnabled ? T("停止代理", "Stop proxy") : T("启动代理", "Start proxy");
        ProxyStateLabel.Text = proxyTransition is true ? T("代理正在启动", "Proxy starting") : proxyTransition is false ? T("代理正在停止", "Proxy stopping") :
            !service.ProxyEnabled ? T("代理已停止", "Proxy stopped") : CoreAvailable ? T("专用代理已启动", "Dedicated proxy running") : T("代理内核不可达", "Proxy core unreachable");
        ProxyStateDot.Fill = new System.Windows.Media.SolidColorBrush(service.ProxyEnabled && CoreAvailable
            ? System.Windows.Media.Color.FromRgb(36, 117, 212) : System.Windows.Media.Color.FromRgb(167, 181, 197));
        ProxyDetailLabel.Text = !service.ProxyEnabled ? T("可查看、检测与预选节点；点击‘启动代理’后开始转发。", "View, test and preselect nodes. Click Start proxy to enable forwarding.") :
            !CoreAvailable ? T("等待内核就绪；若持续不可达，请停止后重新启动。", "Waiting for the core. Stop and start again if it remains unreachable.") :
            service.Core?.OwnsProcess == false ? T("已连接其他启动器的内核；停止需在原启动器操作。", "Connected to another launcher's core. Stop it from that launcher.") :
            T("内核就绪；仅通过下方 Codex 状态判断接入，模型请求仍需实测。", "Core ready. See Codex status below for routing; verify model requests separately.");
    }

    private void RenderSubscriptionUsage()
    {
        var usage = subscriptionUsage;
        SubscriptionUsageBar.Visibility = usage?.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        SubscriptionUsageBar.Value = usage?.Percent ?? 0;
        if (usage is null)
        {
            SubscriptionUsageLabel.Text = T("已用 — / 总量 —", "Used — / Total —");
            SubscriptionUsageHint.Text = subscriptionUrl is null ? T("添加并启用订阅后查看套餐用量。", "Add and activate a subscription to view its quota.") :
                usageCancellation is not null ? T("正在读取订阅套餐信息…", "Reading subscription quota…") :
                usageError ?? T("订阅响应未提供有效套餐信息，可点击‘刷新用量’重试。", "The subscription response has no valid quota data. Click Refresh usage to retry.");
            return;
        }
        var used = usage.Used is { } usedBytes ? TrafficMonitor.Size(usedBytes) : T("未知", "Unknown");
        var total = usage.Total is { } totalBytes ? TrafficMonitor.Size(totalBytes) : T("未知", "Unknown");
        SubscriptionUsageLabel.Text = T($"已用 {used} / 总量 {total}", $"Used {used} / Total {total}");
        var remaining = usage.Remaining is { } bytes ? T($"剩余 {TrafficMonitor.Size(bytes)} · ", $"Remaining {TrafficMonitor.Size(bytes)} · ") : "";
        var expires = usage.ExpiresAt is { } date ? T($"到期 {date.ToLocalTime():yyyy-MM-dd HH:mm}", $"Expires {date.ToLocalTime():yyyy-MM-dd HH:mm}") : T("到期时间未提供", "Expiry not provided");
        var updated = usageUpdatedAt is { } time ? T($" · 读取于 {time.ToLocalTime():MM-dd HH:mm:ss}", $" · Read {time.ToLocalTime():MM-dd HH:mm:ss}") : "";
        SubscriptionUsageHint.Text = remaining + expires + updated + (usageCancellation is not null ? T(" · 正在刷新…", " · Refreshing…") :
            usageError is not null ? " · " + usageError : T(" · 订阅服务报告", " · Reported by subscription provider"));
    }

    private void BeginUsageRefresh(string url)
    {
        if (exiting) return;
        var previous = usageTask;
        usageCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        usageCancellation = cancellation;
        usageError = null;
        usageTask = FetchUsageAsync(url, cancellation, previous);
        RenderSubscriptionUsage(); UpdateUiState();
    }

    private async Task FetchUsageAsync(string url, CancellationTokenSource cancellation, Task? previous)
    {
        try
        {
            if (previous is not null) await previous;
            var metadata = await Task.Run(() => SubscriptionMetadata.FetchAsync(service.DirectoryPath, url, cancellation.Token), cancellation.Token);
            if (exiting || cancellation.IsCancellationRequested || subscriptionUrl != url) return;
            subscriptionUsage = metadata.Usage; usageUpdatedAt = metadata.UpdatedAt;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ProxyException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        {
            if (!exiting && !cancellation.IsCancellationRequested && subscriptionUrl == url)
                usageError = subscriptionUsage is null ? T("用量获取失败，请点击刷新重试。", "Could not fetch quota. Click Refresh usage to retry.") :
                    T("刷新失败，显示上次读取数据。", "Refresh failed. Showing the last saved data.");
        }
        finally
        {
            if (usageCancellation == cancellation)
            { usageCancellation = null; if (!exiting) { RenderSubscriptionUsage(); UpdateUiState(); } }
            cancellation.Dispose();
        }
    }

    private async void ProxyToggleClick(object sender, RoutedEventArgs e)
    {
        if (busy || exiting) return;
        var start = !service.ProxyEnabled;
        if (start && subscriptionUrl is null) { SettingsClick(sender, e); MessageLabel.Text = T("请先保存并启用一个订阅，再启动代理。", "Save and activate a subscription before starting the proxy."); return; }
        proxyTransition = start;
        try
        {
            await RunAsync(async () =>
            {
                if (start) await Task.Run(async () => { await service.StartProxyAsync(); await service.ProbeNodesAsync(); });
                else { await StopCodexCheckAsync(); await Task.Run(() => service.StopProxyAsync()); traffic.Stopped(); LogGrid.ItemsSource = traffic.Entries; }
                await ReloadAsync();
                if (!start) ResetTrafficDisplay();
            }, start ? T("专用代理已启动。请点击‘启动 Codex’完成桌面版接入。", "Dedicated proxy started. Click Launch Codex to connect the desktop app.") :
                T("专用代理已停止；Codex 保留运行，网络请求可能失败。", "Dedicated proxy stopped. Codex keeps running; network requests may fail."));
        }
        finally { proxyTransition = null; UpdateUiState(); }
    }

    private void ResetTrafficDisplay()
    {
        UploadLabel.Text = "↑  —"; DownloadLabel.Text = "↓  —";
        TrafficStatusLabel.Text = T("专用代理已停止", "Dedicated proxy stopped");
    }

    private async void TestClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await Task.Run(() => service.ProbeNodesAsync()); await ReloadAsync();
        if (!nodes.Any(n => n.Available == true)) throw new ProxyException(T("检测完成：所有节点均不可用，未切换连接。", "Check complete: all nodes are unavailable. The connection was not changed."));
    }, T("节点检测完成。延迟表示检测地址的响应时间。", "Node check complete. Latency is the test URL's response time."));

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshSubscriptionAsync();
    private void RefreshUsageClick(object sender, RoutedEventArgs e)
    { if (!busy && !exiting && usageCancellation is null && subscriptionUrl is { } url) BeginUsageRefresh(url); }
    private Task RefreshSubscriptionAsync() => RunAsync(async () =>
    { await StopCodexCheckAsync(); await Task.Run(service.RefreshAsync); codexChecks.Clear(); var previousUrl = subscriptionUrl; await ReloadAsync();
        if (subscriptionUrl is { } url && url == previousUrl && usageCancellation is null) BeginUsageRefresh(url); }, T("订阅节点已刷新，套餐用量在后台读取。请重新检测 Codex 可达性。", "Nodes refreshed; fetching quota in the background. Check Codex reachability again."));

    private async void CheckCodexClick(object sender, RoutedEventArgs e)
    {
        if (busy || exiting || !CoreAvailable || checkCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        checkCancellation = cancellation;
        UpdateUiState();
        var pending = CheckCodexAsync(cancellation);
        checkTask = pending;
        await pending;
        if (checkTask == pending) checkTask = null;
    }

    private async Task CheckCodexAsync(CancellationTokenSource cancellation)
    {
        var completed = new Dictionary<string, CodexCheck>();
        void ApplyResult(string name, CodexCheck result)
        {
            codexChecks[name] = result;
            var index = nodes.IndexOf(nodes.FirstOrDefault(n => n.Name == name)!);
            if (index < 0) return;
            var selected = (NodeGrid.SelectedItem as Node)?.Name == name;
            nodes[index] = nodes[index] with { Codex = result };
            if (selected) NodeGrid.SelectedItem = nodes[index];
        }
        try
        {
            CoreSession? core = null;
            await ExecuteProxyOperationAsync(async () =>
            {
                core = await Task.Run(() => service.EnsureAsync(cancellation.Token), cancellation.Token);
                codexChecks.Clear();
                await ReloadAsync(cancellation.Token);
            }, false, cancellation.Token);
            var total = nodes.Count;
            var progress = new System.Progress<(string Name, CodexCheck Result)>(report =>
            {
                if (exiting || cancellation.IsCancellationRequested || checkCancellation != cancellation) return;
                ApplyResult(report.Name, report.Result);
                MessageLabel.Text = T($"Codex 检测 {codexChecks.Count}/{total} · {NodeCountry.DisplayName(report.Name)}：{report.Result.StatusText}", $"Codex check {codexChecks.Count}/{total} · {NodeCountry.DisplayName(report.Name)}: {report.Result.StatusText}");
            });
            await Task.Run(() => CodexProbe.RunAsync(core!, (name, result) =>
            {
                completed[name] = result;
                ((IProgress<(string, CodexCheck)>)progress).Report((name, result));
            }, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var reachable = completed.Values.Count(r => r.ChatGpt.Reachable && r.Api.Reachable);
            MessageLabel.Text = T($"Codex 检测完成：{reachable}/{total} 个节点网络可达。悬停结果查看 ChatGPT / API 状态；未验证账号和模型调用。", $"Codex check complete: {reachable}/{total} nodes reachable. Hover for ChatGPT / API details; account and model requests are not verified.");
        }
        catch (OperationCanceledException) { if (!exiting) MessageLabel.Text = T("检测已停止，已完成的结果予以保留。", "Check stopped. Completed results have been kept."); }
        catch (ProxyException error) { if (!exiting) MessageLabel.Text = error.Message; }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { if (!exiting) MessageLabel.Text = T("检测失败，请检查本地配置和内核。", "Check failed. Check the local settings and core."); }
        finally
        {
            if (checkCancellation == cancellation)
            {
                if (!exiting) foreach (var (name, result) in completed) ApplyResult(name, result);
                checkCancellation = null;
            }
            cancellation.Dispose();
            UpdateUiState();
        }
    }

    private void CancelCheckClick(object sender, RoutedEventArgs e) { checkCancellation?.Cancel(); UpdateUiState(); }

    private async Task ReadTrafficAsync()
    {
        if (readingTraffic || busy || exiting || service.Core is not { } core) return;
        readingTraffic = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var snapshot = await Task.Run(() => core.ApiAsync("/connections", cancellation: timeout.Token));
            if (snapshot is null || exiting) return;
            traffic.Update(snapshot, DateTimeOffset.UtcNow);
            UploadLabel.Text = "↑  " + TrafficMonitor.Size(traffic.UploadRate) + "/s";
            DownloadLabel.Text = "↓  " + TrafficMonitor.Size(traffic.DownloadRate) + "/s";
            TrafficTotalLabel.Text = T($"累计 ↑ {TrafficMonitor.Size(traffic.UploadTotal)}  /  ↓ {TrafficMonitor.Size(traffic.DownloadTotal)}", $"Total ↑ {TrafficMonitor.Size(traffic.UploadTotal)}  /  ↓ {TrafficMonitor.Size(traffic.DownloadTotal)}");
            TrafficStatusLabel.Text = T($"{traffic.ActiveCount} 个活跃连接 · 每秒更新", $"{traffic.ActiveCount} active connections · Updated every second");
            LogGrid.ItemsSource = traffic.Entries;
        }
        catch (Exception error) when (error is ProxyException or System.Text.Json.JsonException or ObjectDisposedException or OperationCanceledException)
        {
            traffic.Unavailable(); UploadLabel.Text = "↑  —"; DownloadLabel.Text = "↓  —";
            TrafficStatusLabel.Text = T("统计暂不可用", "Traffic data unavailable");
        }
        finally { readingTraffic = false; }
    }

    private void ClearLogsClick(object sender, RoutedEventArgs e) { traffic.Clear(); LogGrid.ItemsSource = traffic.Entries; }

    private async Task<bool> DetectDesktopAsync(bool rescan = false)
    {
        try
        {
            PathLabel.Text = await Task.Run(() => DesktopSettings.ResolveAsync(service.DirectoryPath, rescan));
            PathStatusLabel.Text = T("已找到 Codex 桌面版 · 启动前会再次校验", "Codex desktop found · Checked again before launch");
            PathStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 117, 212));
            MessageLabel.Text = T("已选择 Codex 桌面程序。可检测节点后从这里启动。", "Codex desktop selected. Check the nodes, then launch it here.");
            return true;
        }
        catch (ProxyException error)
        {
            PathLabel.Text = ""; PathStatusLabel.Text = T("未检测到 Codex，请点击右侧‘选择程序’", "Codex not found. Click 'Select program' on the right.");
            PathStatusLabel.Foreground = System.Windows.Media.Brushes.DarkOrange;
            MessageLabel.Text = error.Message;
            return false;
        }
    }

    private async Task DetectPathAsync(bool rescan = false)
    {
        if (pathBusy || busy || exiting) return;
        pathBusy = true; UpdateUiState();
        try { pathTask = DetectDesktopAsync(rescan); await pathTask; }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception)
        { MessageLabel.Text = T("无法读取 Codex 路径，请检查本地文件权限。", "Could not read the Codex path. Check local file permissions."); }
        finally { pathTask = null; pathBusy = false; UpdateUiState(); }
    }

    private async void DetectPathClick(object sender, RoutedEventArgs e) => await DetectPathAsync(true);

    private async Task<bool> PickDesktopAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = T("选择 Codex 桌面程序（不是 CLI）", "Select Codex desktop (not CLI)"),
            Filter = T("Windows 程序 (*.exe)|*.exe", "Windows programs (*.exe)|*.exe"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) { MessageLabel.Text = T("未选择程序，启动已取消。", "No program selected. Launch cancelled."); return false; }
        var selectedPath = dialog.FileName;
        var path = await Task.Run(async () =>
        {
            var selected = DesktopLauncher.ValidatePath(selectedPath);
            await new DesktopSettings(selected).SaveAsync(service.DirectoryPath);
            return selected;
        });
        PathLabel.Text = path; PathStatusLabel.Text = T("已手动选择 Codex 桌面版 · 路径已保存", "Codex desktop selected manually · Path saved");
        PathStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 117, 212));
        MessageLabel.Text = T("Codex 桌面程序路径已保存。", "Codex desktop path saved.");
        return true;
    }

    private async void BrowsePathClick(object sender, RoutedEventArgs e)
    {
        if (pathBusy || busy || exiting) return;
        pathBusy = true; UpdateUiState();
        try { pathTask = PickDesktopAsync(); await pathTask; }
        catch (ProxyException error) { MessageLabel.Text = error.Message; }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Win32Exception)
        { MessageLabel.Text = T("无法保存 Codex 路径，请检查本地文件权限。", "Could not save the Codex path. Check local file permissions."); }
        finally { pathTask = null; pathBusy = false; UpdateUiState(); }
    }

    private async void SelectClick(object sender, RoutedEventArgs e)
    {
        var name = (string)((System.Windows.Controls.Button)sender).Tag;
        await RunAsync(async () => { await Task.Run(() => service.SelectNodeAsync(name)); await ReloadAsync(); },
            service.ProxyEnabled ? T("已切换节点。新连接使用新节点，已有流式请求可能需要重试。", "Node switched. New connections use this node; an existing stream may need to be retried.") :
            T("已预选节点，点击‘启动代理’后生效。", "Node selected for startup. Click Start proxy to apply it."));
    }

    private async void AutoClick(object sender, RoutedEventArgs e)
    {
        var automatic = AutoCheck.IsChecked == true;
        var name = automatic ? CoreSession.Auto : nodes.FirstOrDefault(n => n.Current)?.Name ??
            (NodeGrid.SelectedItem as Node)?.Name ?? nodes.FirstOrDefault()?.Name;
        await RunAsync(async () =>
        {
            if (name is null) throw new ProxyException(T("请直接在节点列表中选择要固定的节点。", "Choose a node directly from the list to pin it."));
            await Task.Run(() => automatic ? service.SelectNodeAsync(name) : service.DisableAutomaticAsync(name)); await ReloadAsync();
        }, !service.ProxyEnabled ? T("已保存预选模式，启动代理后生效。", "Selection mode saved. It takes effect when the proxy starts.") :
            automatic ? T("已开启自动选择，每 5 分钟检测一次。", "Automatic selection enabled. Nodes are checked every 5 minutes.") : T("已关闭自动选择并固定节点。", "Automatic selection disabled and node pinned."));
        AutoCheck.IsChecked = selectedNode == CoreSession.Auto;
    }

    private async void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (pathBusy || !CoreAvailable) return;
        await RunAsync(async () =>
        {
            if (!await Task.Run(() => service.Profile is not null)) { SettingsClick(sender, e); return; }
            if (!await DetectDesktopAsync() && !await PickDesktopAsync()) return;
            await Task.Run(service.LaunchDesktopAsync); await ReloadAsync();
            MessageLabel.Text = T("Codex 已携带代理配置启动。请发送一条消息验证实际模型连接。", "Codex started with the proxy. Send a message to verify a real model request.");
        }, null);
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (shellReady && !exiting && NavigationList.SelectedValue is string page) NavigateTo(page);
    }

    private void NavigateTo(string page)
    {
        currentPage = page;
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        NodesPage.Visibility = page == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (!Equals(NavigationList.SelectedValue, page)) NavigationList.SelectedValue = page;
        RefreshPageHeading();
        if (page == "settings") EnsureSettingsView();
    }

    private void RefreshPageHeading()
    {
        (PageTitle.Text, PageSubtitle.Text) = currentPage switch
        {
            "nodes" => (T("节点", "Nodes"), T("查看测速记录，预选节点或检查 Codex 访问。", "Review test results, select a node, or check Codex access.")),
            "logs" => (T("日志", "Logs"), T("查看本次运行的连接目标、流量和节点链路。", "Inspect this session's destinations, traffic, and node routes.")),
            "settings" => (T("设置", "Settings"), T("统一管理界面、程序路径、代理内核和更新。", "Manage the interface, app path, proxy core, and updates.")),
            _ => (T("概览", "Overview"), T("连接状态与订阅用量，一眼查看。", "Your connection status and subscription usage at a glance."))
        };
    }

    private void ViewNodesClick(object sender, RoutedEventArgs e) => NavigateTo("nodes");

    private void SettingsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (shellReady && !exiting && ReferenceEquals(e.Source, SettingsTabs) && currentPage == "settings") EnsureSettingsView();
    }

    private void EnsureSettingsView()
    {
        if (SettingsTabs.SelectedItem is not TabItem tab) return;
        if (Equals(tab.Tag, "kernel") && kernelView is null)
        {
            kernelView = new KernelSettingsView(service, ExecuteProxyOperationAsync);
            kernelView.Applied += KernelApplied;
            KernelHost.Content = kernelView;
        }
        else if (Equals(tab.Tag, "updates") && updateView is null)
        {
            updateView = new UpdateView(CreateUpdateProxy, service.DirectoryPath);
            UpdateHost.Content = updateView;
        }
    }

    private System.Net.IWebProxy? CreateUpdateProxy() => service.ProxyEnabled && CoreAvailable && service.Core is { } core
        ? new System.Net.WebProxy($"http://127.0.0.1:{core.Profile.Port}")
            { Credentials = new System.Net.NetworkCredential("codex", core.Profile.ProxyPassword) } : null;

    private async void KernelApplied()
    {
        if (exiting) return;
        traffic.Unavailable();
        await RunAsync(() => ReloadAsync(), null, false);
    }

    private async Task LoadUiSettingsAsync()
    {
        try
        {
            var settings = await Task.Run(() => UiSettings.Load(service.DirectoryPath));
            if (exiting) return;
            refreshIntervalSeconds = settings.RefreshIntervalSeconds;
            timer.Interval = TimeSpan.FromSeconds(refreshIntervalSeconds);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if (!exiting) MessageLabel.Text = T("刷新间隔读取失败，已使用默认 5 秒。", "Could not read the refresh interval. Using the default 5 seconds."); }
        finally
        {
            initializingRefreshInterval = false;
            if (!exiting)
            {
                initializingRefreshInterval = true;
                RefreshIntervalBox.SelectedValue = refreshIntervalSeconds.ToString();
                initializingRefreshInterval = false;
                RefreshIntervalBox.IsEnabled = true;
                RenderRefreshInterval();
            }
        }
    }

    private void RenderRefreshInterval() => RefreshIntervalStatus.Text =
        T($"节点与连接状态每 {refreshIntervalSeconds} 秒刷新；流量每秒更新。", $"Nodes and connection status refresh every {refreshIntervalSeconds} seconds; traffic updates each second.");

    private async void RefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializingRefreshInterval || exiting || uiSettingsTask is { IsCompleted: false } ||
            !int.TryParse(RefreshIntervalBox.SelectedValue?.ToString(), out var seconds) || seconds == refreshIntervalSeconds) return;
        RefreshIntervalBox.IsEnabled = false;
        var pending = SaveRefreshIntervalAsync(seconds); uiSettingsTask = pending;
        await pending;
        if (uiSettingsTask == pending) uiSettingsTask = null;
    }

    private async Task SaveRefreshIntervalAsync(int seconds)
    {
        try
        {
            await Task.Run(() => new UiSettings(seconds).Save(service.DirectoryPath));
            refreshIntervalSeconds = seconds;
            timer.Interval = TimeSpan.FromSeconds(seconds);
            if (!exiting) RenderRefreshInterval();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception or ProxyException)
        { if (!exiting) MessageLabel.Text = T("刷新间隔保存失败，请检查本地文件权限。", "Could not save the refresh interval. Check local file permissions."); }
        finally
        {
            if (!exiting)
            {
                initializingRefreshInterval = true;
                RefreshIntervalBox.SelectedValue = refreshIntervalSeconds.ToString();
                initializingRefreshInterval = false;
                RefreshIntervalBox.IsEnabled = true;
            }
        }
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (exiting) return;
        if (subscriptionWindow is not null) { subscriptionWindow.Activate(); return; }
        var window = new SubscriptionWindow(service, ExecuteProxyOperationAsync, StopUsageRefreshAsync) { Owner = this };
        subscriptionWindow = window;
        window.Applied += SubscriptionApplied;
        window.Closed += async (_, _) =>
        {
            var changed = window.Changed;
            window.Applied -= SubscriptionApplied;
            subscriptionWindow = null;
            if (changed && !exiting) await RunAsync(() => ReloadAsync(), null, false);
        };
        window.Show();
    }

    private async Task StopUsageRefreshAsync()
    {
        usageCancellation?.Cancel();
        if (usageTask is { } pending) await pending;
        usageRequestedUrl = null;
    }

    private async void SubscriptionApplied()
    {
        if (exiting) return;
        if (!service.ProxyEnabled) traffic.Unavailable();
        await RunAsync(() => ReloadAsync(), null, false);
    }
}
