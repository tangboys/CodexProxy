using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CodexProxy.Core;
using static CodexProxy.App;

namespace CodexProxy;

public partial class SubscriptionWindow : Window
{
    private readonly ProxyService service;
    private readonly Func<Func<Task>, bool, Task> executeProxyOperation;
    private readonly Func<Task>? beforeActiveDelete;
    private bool saving, loading, closed;
    private string? editingId;
    private string initialCorePath = "";
    private SubscriptionCatalog? catalog;
    public bool Changed { get; private set; }
    public bool ActiveChanged { get; private set; }
    public event Action? Applied;

    public SubscriptionWindow(ProxyService service, Func<Func<Task>, bool, Task> executeProxyOperation, Func<Task>? beforeActiveDelete = null)
    {
        InitializeComponent();
        this.service = service;
        this.executeProxyOperation = executeProxyOperation;
        this.beforeActiveDelete = beforeActiveDelete;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            if (saving)
            {
                e.Cancel = true;
                ShowMessage(() => T("正在保存或应用设置，请完成后再关闭。", "Settings are being saved or applied. Wait for the operation to finish before closing."));
            }
            else closed = true;
        };
    }

    private async Task LoadAsync()
    {
        loading = true;
        Editor.IsEnabled = SaveButton.IsEnabled = ActivateButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        try
        {
            string? selectedCore = null;
            SubscriptionCatalog? initialCatalog = null;
            await executeProxyOperation(() => Task.Run(() =>
            {
                selectedCore = service.SelectedKernelPath;
                initialCatalog = service.Subscriptions;
            }), false);
            if (closed) return;
            CorePath.Text = initialCorePath = selectedCore ?? "";
            ReloadList(initialCatalog!, initialCatalog!.ActiveId);
            Editor.IsEnabled = SaveButton.IsEnabled = ActivateButton.IsEnabled = true;
        }
        catch (Exception error) when (error is ProxyException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        {
            if (!closed) ShowMessage(() => error is ProxyException ? error.Message :
                T("无法读取订阅设置，请检查本地文件权限后重新打开。", "Could not read subscription settings. Check local file permissions and reopen this window."));
        }
        finally
        {
            loading = false;
            if (!closed) Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void ReloadList(SubscriptionCatalog updated, string? selectedId, bool preserveVisibility = false)
    {
        catalog = updated;
        var showUrl = preserveVisibility ? ShowUrl.IsChecked == true : true;
        loading = true;
        SubscriptionList.ItemsSource = catalog.Items.Select(s => s with { Active = s.Id == catalog.ActiveId }).ToList();
        SubscriptionList.SelectedItem = SubscriptionList.Items.Cast<Subscription>().FirstOrDefault(s => s.Id == selectedId);
        loading = false;
        LoadEditor(SubscriptionList.SelectedItem as Subscription, showUrl);
    }

    private void LoadEditor(Subscription? entry, bool showUrl = true)
    {
        editingId = entry?.Id;
        NameText.Text = entry?.Name ?? "";
        UrlPassword.Password = entry?.Url ?? "";
        UrlText.Text = showUrl ? entry?.Url ?? "" : "";
        ShowUrl.IsChecked = showUrl;
        UrlPassword.Visibility = showUrl ? Visibility.Collapsed : Visibility.Visible;
        UrlText.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;
        RefreshLanguage();
        DeleteButton.IsEnabled = !saving && entry is not null;
    }

    public void RefreshLanguage()
    {
        if (closed) return;
        if (catalog is not null)
        {
            var activeName = catalog.Items.FirstOrDefault(s => s.Id == catalog.ActiveId)?.Name ?? T("尚未启用", "None active");
            SavedLabel.Text = T($"已保存 {catalog.Items.Count} 个订阅 · 当前：{activeName}", $"{catalog.Items.Count} subscriptions saved · Active: {activeName}");
        }
        var entry = SubscriptionList.SelectedItem as Subscription;
        EntryTitle.Text = entry is null ? T("新增订阅", "Add subscription") : entry.Active ? T("编辑当前订阅", "Edit active subscription") : T("编辑备用订阅", "Edit saved subscription");
        UrlHint.Text = entry is null ? T("填写 HTTP(S) 链接；可先保存，稍后再启用。", "Enter an HTTP(S) URL. You can save it now and activate it later.") : T("可直接修改已保存的链接；留空保留原链接。", "Edit the saved URL directly. Leave it blank to keep the existing URL.");
        DeleteButton.ToolTip = entry?.Active == true ? T("删除后停止专用代理并清空当前选择，其他订阅保留，不会关闭 Codex。", "Deleting stops the dedicated proxy and clears the active choice. Other subscriptions are kept; Codex is not closed.") : null;
        DeleteHint.Text = entry?.Active == true ? T("删除当前订阅会停止专用代理；不会自动启用其他订阅。", "Deleting the active subscription stops the proxy; no other subscription is activated automatically.") : "";
        var wasLoading = loading;
        loading = true;
        try { SubscriptionList.Items.Refresh(); SubscriptionList.SelectedItem = entry; }
        finally { loading = wasLoading; }
        if (messageText is not null) ErrorLabel.Text = messageText();
    }

    private Func<string>? messageText;

    private void ShowMessage(Func<string> text)
    {
        messageText = text;
        ErrorLabel.Text = text();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!loading && !saving) LoadEditor(SubscriptionList.SelectedItem as Subscription); }

    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (loading || saving) return;
        SubscriptionList.SelectedItem = null; LoadEditor(null); NameText.Focus();
    }

    private void ShowUrlClick(object sender, RoutedEventArgs e)
    {
        if (ShowUrl.IsChecked == true) { UrlText.Text = UrlPassword.Password; UrlText.Visibility = Visibility.Visible; UrlPassword.Visibility = Visibility.Collapsed; }
        else { UrlPassword.Password = UrlText.Text; UrlPassword.Visibility = Visibility.Visible; UrlText.Visibility = Visibility.Collapsed; UrlText.Clear(); }
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        if (loading || saving) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("Mihomo 内核 (*.exe)|*.exe", "Mihomo core (*.exe)|*.exe"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) CorePath.Text = dialog.FileName;
    }

    private void SetSaving(bool value)
    {
        saving = value;
        SubscriptionList.IsEnabled = AddButton.IsEnabled = BrowseButton.IsEnabled =
            SaveButton.IsEnabled = ActivateButton.IsEnabled = CancelButton.IsEnabled = !value;
        NameText.IsReadOnly = UrlText.IsReadOnly = CorePath.IsReadOnly = value;
        UrlPassword.IsEnabled = !value;
        DeleteButton.IsEnabled = !value && SubscriptionList.SelectedItem is Subscription;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void ActivateClick(object sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task SaveAsync(bool activate)
    {
        if (saving || loading || closed) return;
        var id = editingId;
        var name = NameText.Text;
        var url = ShowUrl.IsChecked == true ? UrlText.Text : UrlPassword.Password;
        var corePath = string.Equals(CorePath.Text, initialCorePath, StringComparison.Ordinal) ? null : CorePath.Text;
        var applying = activate || id is not null && catalog?.ActiveId == id;
        Subscription? savedEntry = null;
        SubscriptionCatalog? updated = null;
        string? currentCorePath = null;
        SetSaving(true);
        ShowMessage(() => applying ? T("正在验证并启用订阅，失败时保留当前连接…", "Checking and activating the subscription. The current connection is kept if this fails…") : T("正在保存订阅…", "Saving subscription…"));
        try
        {
            await executeProxyOperation(() => Task.Run(async () =>
            {
                savedEntry = await service.SaveSubscriptionEntryAsync(id, name, url, corePath, activate);
                currentCorePath = service.SelectedKernelPath ?? "";
                updated = service.Subscriptions;
                if (applying && service.ProxyEnabled) await service.ProbeNodesAsync();
            }), applying);
            ShowMessage(() => applying ? service.ProxyEnabled
                ? T("订阅已启用，节点已更新。新连接使用此订阅。", "Subscription activated and nodes updated. New connections use this subscription.")
                : T("当前订阅已保存。点击主界面的‘启动代理’后开始连接。", "Active subscription saved. Click Start proxy in the main window to connect.")
                : T("备用订阅已保存，当前连接保持原样。", "Subscription saved. The current connection is unchanged."));
        }
        catch (ProxyException error) { ShowMessage(() => error.Message); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        { ShowMessage(() => T("保存失败，请检查内核路径和本地文件权限。", "Could not save. Check the core path and local file permissions.")); }
        finally
        {
            if (savedEntry is not null)
            {
                Changed = true; ActiveChanged |= applying;
                if (currentCorePath is not null) CorePath.Text = initialCorePath = currentCorePath;
                if (updated is not null) ReloadList(updated, savedEntry.Id, true);
            }
            SetSaving(false);
            if (savedEntry is not null && !closed) Applied?.Invoke();
        }
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (saving || loading || closed || editingId is null) return;
        var id = editingId;
        SubscriptionCatalog? updated = null;
        var deleted = false;
        var deletedActive = false;
        SetSaving(true);
        ShowMessage(() => T("正在删除订阅；删除当前订阅将停止专用代理…", "Deleting the subscription. Deleting the active subscription stops the dedicated proxy…"));
        try
        {
            await executeProxyOperation(async () =>
            {
                var active = await Task.Run(() => service.Subscriptions.ActiveId == id);
                if (active && beforeActiveDelete is not null) await beforeActiveDelete();
                deletedActive = await Task.Run(() => service.DeleteSubscriptionAsync(id));
                deleted = true;
                updated = await Task.Run(() => service.Subscriptions);
            }, true);
            if (deleted) ShowMessage(() => deletedActive
                ? T("当前订阅已删除，专用代理已停止；其他订阅保留且未自动启用，不会关闭 Codex。重新配置后请退出旧 Codex 再启动。", "The active subscription was deleted and the dedicated proxy stopped. Other subscriptions are kept and none was activated; Codex is not closed. Exit the old Codex instance before launching it after reconfiguring.")
                : T("备用订阅已删除，当前连接保持原样。", "Subscription deleted. The current connection is unchanged."));
        }
        catch (ProxyException error) { ShowMessage(() => error.Message); }
        catch (OperationCanceledException) { ShowMessage(() => T("删除已取消，请检查当前订阅状态。", "Deletion was cancelled. Check the current subscription status.")); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        { ShowMessage(() => T("删除未完成，请检查本地文件权限，并在概览确认代理状态。", "Deletion did not complete. Check local file permissions and the proxy status in Overview.")); }
        finally
        {
            Changed |= deleted;
            ActiveChanged |= deletedActive;
            if (updated is not null) ReloadList(updated, updated.ActiveId, true);
            SetSaving(false);
            if (!closed) Applied?.Invoke();
        }
    }
}
