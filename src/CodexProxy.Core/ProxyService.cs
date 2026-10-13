using System.Security.Cryptography;
using System.Text;
using StoredProfile = CodexProxy.Core.Profile;

namespace CodexProxy.Core;

public sealed class ProxyService(string directory) : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public bool ProxyEnabled { get; private set; }
    public CoreSession? Core { get; private set; }
    public DesktopLauncher? Desktop { get; private set; }
    public Profile? Profile => StoredProfile.Load(DirectoryPath);
    public SubscriptionCatalog Subscriptions => SubscriptionCatalog.Load(DirectoryPath, Profile);
    public string? SelectedKernelPath => Profile?.CorePath ?? KernelSettings.Load(DirectoryPath)?.CorePath;

    public async Task<CoreSession> StartProxyAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var previous = ProxyEnabled;
        ProxyEnabled = true;
        try { return await EnsureAsync(cancellation).ConfigureAwait(false); }
        catch { ProxyEnabled = previous; throw; }
    }

    public async Task StopProxyAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Core is { } current)
        {
            if (!current.OwnsProcess && await current.RunningAsync(cancellation).ConfigureAwait(false))
                throw new ProxyException(UiText.Language == "en-US"
                    ? "Another launcher owns the running core. Stop it from that launcher."
                    : "当前内核由另一个启动器运行，请在该启动器中停止代理。");
            await current.DisposeAsync().ConfigureAwait(false);
            Core = null;
        }
        ProxyEnabled = false;
    }

    public async Task SelectKernelAsync(string path)
    {
        var selected = await KernelCatalog.InspectAsync(path, "当前选择");
        var previous = Profile;
        if (previous is null)
        {
            new KernelSettings(selected.Path).Save(DirectoryPath);
            return;
        }
        if (!string.Equals(previous.CorePath, selected.Path, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = previous with { CorePath = selected.Path };
            if (!ProxyEnabled)
            {
                candidate.Save(DirectoryPath);
                return;
            }
            var current = await EnsureAsync();
            if (!current.OwnsProcess) throw new ProxyException(UiText.Language == "en-US"
                ? "Another launcher owns the running core. Exit that launcher before switching cores."
                : "当前内核由另一个启动器运行，请先退出该启动器，再切换内核。");
            var staging = Path.Combine(DirectoryPath, ".kernel-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var ports = StoredProfile.FreePorts();
                await using var check = new CoreSession(staging, candidate with { Port = ports[0], ControllerPort = ports[1] }, localCacheOnly: true);
                File.Copy(Path.Combine(DirectoryPath, current.CacheName), Path.Combine(staging, check.CacheName));
                await check.StartAsync();
            }
            finally { Directory.Delete(staging, true); } // Only this call's freshly created directory.
            await RestartCoreAsync(candidate, previous);
        }
    }

    private async Task RestartCoreAsync(Profile candidate, Profile previous)
    {
        var current = await EnsureAsync();
        if (!current.OwnsProcess) throw new ProxyException(UiText.Language == "en-US"
            ? "Another launcher owns the running core. Exit that launcher before switching cores."
            : "当前内核由另一个启动器运行，请先退出该启动器，再切换内核。");
        await current.DisposeAsync();
        Core = null;
        var replacement = new CoreSession(DirectoryPath, candidate);
        try
        {
            await replacement.StartAsync();
            replacement.Profile.Save(DirectoryPath);
            Core = replacement;
            Desktop?.ReplaceCore(replacement);
        }
        catch
        {
            await replacement.DisposeAsync();
            var restored = new CoreSession(DirectoryPath, previous);
            try
            {
                previous.Save(DirectoryPath);
                await restored.StartAsync();
                Core = restored;
                Desktop?.ReplaceCore(restored);
            }
            catch
            {
                await restored.DisposeAsync();
                throw new ProxyException(UiText.Language == "en-US"
                    ? "The new core could not be applied, and the previous core could not be restored. Check local file permissions, then exit and reopen the manager."
                    : "新内核未能启用，原内核也未能恢复。请检查本地文件权限，然后退出并重新打开管理器。");
            }
            throw new ProxyException(UiText.Language == "en-US"
                ? "The new core could not be applied. The previous core and settings have been restored."
                : "新内核未能启用，已恢复原内核和配置。");
        }
    }

    public async Task<CoreSession> EnsureAsync(CancellationToken cancellation = default)
    {
        if (!ProxyEnabled) throw new ProxyException(UiText.Language == "en-US"
            ? "The proxy is stopped. Click Start proxy first."
            : "代理已停止，请先点击‘启动代理’。");
        var profile = Profile ?? throw new ProxyException(UiText.Get("请先在订阅设置中添加链接。"));
        if (Core is not null && !await Core.RunningAsync(cancellation)) { await Core.DisposeAsync(); Core = null; }
        if (Core is null)
        {
            var candidate = new CoreSession(DirectoryPath, profile);
            try { await candidate.StartAsync(cancellation); profile = candidate.Profile; Core = candidate; Desktop?.ReplaceCore(candidate); }
            catch { await candidate.DisposeAsync(); throw; }
        }
        Core.Profile = profile;
        return Core;
    }

    public async Task<NodeSnapshot?> PreviewNodesAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var profile = Profile;
        if (profile is null) return null;
        var snapshot = await NodeSnapshot.LoadAsync(DirectoryPath, profile, cancellation).ConfigureAwait(false);
        if (ProxyEnabled && Core is { } current)
        {
            var latest = new NodeSnapshot(await current.NodesAsync(cancellation).ConfigureAwait(false), snapshot?.CheckedAt);
            if (snapshot is null || !snapshot.Nodes.Select(n => (n.Name, n.Protocol, n.Available, n.Delay))
                .SequenceEqual(latest.Nodes.Select(n => (n.Name, n.Protocol, n.Available, n.Delay))))
                await latest.SaveAsync(DirectoryPath, profile, cancellation).ConfigureAwait(false);
            return latest;
        }
        snapshot ??= await CachedNodesAsync(profile, false, cancellation).ConfigureAwait(false);
        return snapshot?.ForSelection(profile.Selected);
    }

    public async Task<NodeSnapshot> ProbeNodesAsync(CancellationToken cancellation = default)
    {
        var profile = Profile ?? throw new ProxyException(UiText.Get("请先在订阅设置中添加链接。"));
        if (!ProxyEnabled)
        {
            var cached = await CachedNodesAsync(profile, true, cancellation).ConfigureAwait(false)
                ?? throw new ProxyException(UiText.Language == "en-US"
                    ? "No cached nodes are available. Refresh the subscription first."
                    : "暂无节点缓存，请先刷新订阅。");
            return cached.ForSelection(profile.Selected);
        }
        var current = await EnsureAsync(cancellation).ConfigureAwait(false);
        await current.ProbeAsync(cancellation).ConfigureAwait(false);
        var snapshot = new NodeSnapshot(await current.NodesAsync(cancellation).ConfigureAwait(false), DateTimeOffset.UtcNow);
        await snapshot.SaveAsync(DirectoryPath, current.Profile, cancellation).ConfigureAwait(false);
        return snapshot;
    }

    public async Task SelectNodeAsync(string name, CancellationToken cancellation = default)
    {
        if (ProxyEnabled)
        {
            var current = await EnsureAsync(cancellation).ConfigureAwait(false);
            await current.ChooseAsync(name, cancellation).ConfigureAwait(false);
            var snapshot = new NodeSnapshot(await current.NodesAsync(cancellation).ConfigureAwait(false), DateTimeOffset.UtcNow);
            await snapshot.SaveAsync(DirectoryPath, current.Profile, cancellation).ConfigureAwait(false);
            return;
        }
        var profile = Profile ?? throw new ProxyException(UiText.Get("请先在订阅设置中添加链接。"));
        var preview = await PreviewNodesAsync(cancellation).ConfigureAwait(false);
        if (preview is null || preview.Nodes.Count == 0 || name != CoreSession.Auto && !preview.Nodes.Any(n => n.Name == name))
            throw new ProxyException(UiText.Language == "en-US"
                ? "The selected node is not in the preview. Refresh the subscription first."
                : "所选节点不在预览列表中，请先刷新订阅。");
        await (profile with { Selected = name }).SaveAsync(DirectoryPath, cancellation).ConfigureAwait(false);
    }

    public async Task DisableAutomaticAsync(string fallbackName, CancellationToken cancellation = default)
    {
        if (!ProxyEnabled)
        {
            await SelectNodeAsync(fallbackName, cancellation).ConfigureAwait(false);
            return;
        }
        var current = await EnsureAsync(cancellation).ConfigureAwait(false);
        await current.PinCurrentAsync(fallbackName, cancellation).ConfigureAwait(false);
    }

    private async Task<NodeSnapshot?> CachedNodesAsync(Profile profile, bool probe, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var source = Path.Combine(DirectoryPath, NodeSnapshot.CacheKey(profile) + ".yaml");
        if (!File.Exists(source)) return null;
        var staging = Path.Combine(DirectoryPath, ".node-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var ports = StoredProfile.FreePorts();
            await using var isolated = new CoreSession(staging, profile with { Port = ports[0], ControllerPort = ports[1] }, localCacheOnly: true);
            await CopyCacheAsync(source, Path.Combine(staging, isolated.CacheName), cancellation).ConfigureAwait(false);
            await isolated.StartAsync(cancellation).ConfigureAwait(false);
            if (probe) await isolated.ProbeAsync(cancellation).ConfigureAwait(false);
            var snapshot = new NodeSnapshot(await isolated.NodesAsync(cancellation).ConfigureAwait(false), probe ? DateTimeOffset.UtcNow : null);
            await snapshot.SaveAsync(DirectoryPath, profile, cancellation).ConfigureAwait(false);
            return snapshot;
        }
        finally { Directory.Delete(staging, true); } // Only this call's freshly created directory.
    }

    private static async Task CopyCacheAsync(string source, string destination, CancellationToken cancellation)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task SaveSubscriptionAsync(string url, string? corePath)
    {
        var catalog = Subscriptions;
        var active = catalog.Items.FirstOrDefault(s => s.Id == catalog.ActiveId);
        await SaveSubscriptionEntryAsync(active?.Id, active?.Name ?? UiText.Get("默认订阅"), url, corePath, true);
    }

    public async Task<Subscription> SaveSubscriptionEntryAsync(string? id, string name, string url, string? corePath, bool activate)
    {
        var catalog = Subscriptions;
        var existing = id is null ? null : catalog.Items.FirstOrDefault(s => s.Id == id)
            ?? throw new ProxyException(UiText.Get("所选订阅已不存在，请重新选择。"));
        name = name.Trim();
        if (name.Length == 0) throw new ProxyException(UiText.Get("请填写订阅名称。"));
        url = string.IsNullOrWhiteSpace(url) ? existing?.Url ?? "" : url.Trim();
        StoredProfile.ValidateUrl(url);
        var entry = new Subscription(existing?.Id ?? Guid.NewGuid().ToString("N"), name, url);
        activate |= existing is not null && existing.Id == catalog.ActiveId;
        if (activate) await ApplySubscriptionAsync(url, corePath);
        var entries = catalog.Items.Where(s => s.Id != entry.Id).ToList();
        if (existing is null) entries.Add(entry);
        else entries.Insert(catalog.Items.FindIndex(s => s.Id == entry.Id), entry);
        (catalog with { Items = entries, ActiveId = activate ? entry.Id : catalog.ActiveId }).Save(DirectoryPath);
        return entry;
    }

    public async Task ActivateSubscriptionAsync(string id, string? corePath)
    {
        var catalog = Subscriptions;
        var entry = catalog.Items.FirstOrDefault(s => s.Id == id) ?? throw new ProxyException(UiText.Get("请选择已保存的订阅。"));
        await ApplySubscriptionAsync(entry.Url, corePath);
        (catalog with { ActiveId = id }).Save(DirectoryPath);
    }

    public async Task<bool> DeleteSubscriptionAsync(string id, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var previous = Profile;
        var catalog = SubscriptionCatalog.Load(DirectoryPath, previous);
        var entry = catalog.Items.FirstOrDefault(s => s.Id == id)
            ?? throw new ProxyException(UiText.Get("所选订阅已不存在，请重新选择。"));
        var active = catalog.ActiveId == id;
        if (active && previous is not null && Core is null)
        {
            await using var probe = new CoreSession(DirectoryPath, previous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                if (await probe.RunningAsync(timeout.Token).ConfigureAwait(false))
                    throw new ProxyException(UiText.Language == "en-US"
                        ? "Another launcher owns the running core. Stop it from that launcher before deleting the active subscription."
                        : "当前内核由另一个启动器运行，请先在原启动器停止代理，再删除当前订阅。");
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new ProxyException(UiText.Language == "en-US"
                ? "Could not confirm that the dedicated core is stopped. Try deleting the subscription again later."
                : "无法确认专用内核已停止，请稍后再删除订阅。"); }
        }
        if (active) await StopProxyAsync(cancellation).ConfigureAwait(false);
        await StoredProfile.PrivateDirectoryAsync(DirectoryPath, cancellation).ConfigureAwait(false);
        if (active && previous is not null)
        {
            await StoredProfile.WriteJsonAsync(Path.Combine(DirectoryPath, "kernel.json"), new KernelSettings(previous.CorePath), cancellation).ConfigureAwait(false);
            if (Desktop is not null) { await Desktop.DisposeAsync().ConfigureAwait(false); Desktop = null; }
            File.Delete(Path.Combine(DirectoryPath, "config.json"));
        }
        var remaining = catalog.Items.Where(s => s.Id != id).ToList();
        if (!remaining.Any(s => s.Url == entry.Url))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Url))).ToLowerInvariant();
            foreach (var file in new[] { "subscription-" + hash[..16] + ".yaml", "subscription-" + hash[..16] + ".nodes.json",
                "subscription-metadata-" + hash + ".json" })
            {
                cancellation.ThrowIfCancellationRequested();
                File.Delete(Path.Combine(DirectoryPath, file));
            }
        }
        var profileRemoved = false;
        try
        {
            if (active) { File.Delete(Path.Combine(DirectoryPath, "profile.json")); profileRemoved = true; }
            await StoredProfile.WriteJsonAsync(Path.Combine(DirectoryPath, "subscriptions.json"),
                catalog with { Items = remaining, ActiveId = active ? null : catalog.ActiveId }, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (profileRemoved && previous is not null)
            {
                try { await previous.SaveAsync(DirectoryPath).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new ProxyException(UiText.Language == "en-US"
                    ? "Deletion did not complete and the proxy is stopped. Reopen Subscription settings to check the saved entries."
                    : "删除未完成，代理已停止。请重新打开订阅设置检查已保存条目。"); }
            }
            throw;
        }
        return active;
    }

    private async Task ApplySubscriptionAsync(string url, string? corePath, CancellationToken cancellation = default, bool probe = false)
    {
        var previous = Profile;
        url = string.IsNullOrWhiteSpace(url) ? previous?.Url ?? "" : url.Trim();
        var selectedCore = string.IsNullOrWhiteSpace(corePath) ? SelectedKernelPath ?? await KernelCatalog.BundledPathAsync(DirectoryPath) : corePath;
        var candidate = await StoredProfile.CreateAsync(url, selectedCore, cancellation);
        if (previous is not null)
            candidate = candidate with { Port = previous.Port, ControllerPort = previous.ControllerPort,
                Secret = previous.Secret, ProxyPassword = previous.ProxyPassword, Selected = previous.Selected,
                TestUrl = previous.TestUrl, Expected = previous.Expected };
        if (ProxyEnabled && previous is not null)
        {
            var current = await EnsureAsync(cancellation).ConfigureAwait(false);
            if (!current.OwnsProcess) throw new ProxyException(UiText.Language == "en-US"
                ? "Another launcher owns the running core. Exit that launcher before changing the active subscription."
                : "当前内核由另一个启动器运行，请先退出原启动器，再更改当前订阅。");
        }
        await StoredProfile.PrivateDirectoryAsync(DirectoryPath, cancellation);
        var staging = Path.Combine(DirectoryPath, ".subscription-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var ports = StoredProfile.FreePorts();
            await using var check = new CoreSession(staging, candidate with { Port = ports[0], ControllerPort = ports[1] });
            await check.StartAsync(cancellation);
            if (probe) await check.ProbeAsync(cancellation);
            var names = await check.NodesAsync(cancellation);
            if (!names.Any(n => n.Name == candidate.Selected)) candidate = candidate with { Selected = CoreSession.Auto };
            await CopyCacheAsync(Path.Combine(staging, check.CacheName), Path.Combine(DirectoryPath, check.CacheName), cancellation);
            await new NodeSnapshot(names, probe ? DateTimeOffset.UtcNow : null).SaveAsync(DirectoryPath, candidate, cancellation);
        }
        finally { Directory.Delete(staging, true); } // Only this call's freshly created directory.
        if (!ProxyEnabled)
        {
            await candidate.SaveAsync(DirectoryPath, cancellation);
            return;
        }
        if (previous is not null && !string.Equals(previous.CorePath, candidate.CorePath, StringComparison.OrdinalIgnoreCase))
        {
            await RestartCoreAsync(candidate, previous);
            return;
        }
        else if (previous is not null)
        {
            var core = await EnsureAsync();
            var config = Path.Combine(DirectoryPath, "config.json");
            core.Profile = candidate;
            StoredProfile.WriteJson(config, core.Configuration());
            try
            {
                await core.ApiAsync("/configs?force=true", HttpMethod.Put, new { path = config });
                await core.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name = candidate.Selected });
            }
            catch
            {
                core.Profile = previous;
                StoredProfile.WriteJson(config, core.Configuration());
                await core.ApiAsync("/configs?force=true", HttpMethod.Put, new { path = config });
                await core.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name = previous.Selected });
                throw;
            }
        }
        else
        {
            var core = new CoreSession(DirectoryPath, candidate);
            try { await core.StartAsync(); Core = core; }
            catch { await core.DisposeAsync(); throw; }
        }
        Core!.Profile.Save(DirectoryPath);
    }

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellation)
    {
        if (!ProxyEnabled)
        {
            var profile = Profile ?? throw new ProxyException(UiText.Get("请先在订阅设置中添加链接。"));
            await ApplySubscriptionAsync(profile.Url, profile.CorePath, cancellation, probe: true).ConfigureAwait(false);
            return;
        }
        var core = await EnsureAsync(cancellation);
        await core.ApiAsync("/providers/proxies/" + await core.ProviderNameAsync(cancellation), HttpMethod.Put, cancellation: cancellation);
        var snapshot = await ProbeNodesAsync(cancellation);
        if (core.Profile.Selected != CoreSession.Auto && !snapshot.Nodes.Any(n => n.Name == core.Profile.Selected))
        {
            await core.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name = CoreSession.Auto }, cancellation);
            core.Profile = core.Profile with { Selected = CoreSession.Auto };
            await core.Profile.SaveAsync(DirectoryPath, cancellation);
        }
    }

    public async Task LaunchDesktopAsync()
    {
        var executable = await DesktopSettings.ResolveAsync(DirectoryPath);
        var core = await EnsureAsync();
        await core.ChooseAsync(core.Profile.Selected);
        Desktop ??= new DesktopLauncher(core);
        await Desktop.LaunchAsync(executable);
    }

    public async ValueTask DisposeAsync()
    {
        if (Desktop is not null) await Desktop.DisposeAsync();
        if (Core is not null) await Core.DisposeAsync();
    }
}
