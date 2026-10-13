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
    public NetworkSettings Network => NetworkSettings.Load(DirectoryPath);

    public Task SetSubscriptionDownloadModeAsync(SubscriptionDownloadMode mode, CancellationToken cancellation = default) =>
        (Network with { DownloadMode = mode }).SaveAsync(DirectoryPath, cancellation);

    public async Task SetProxyPortAsync(int? port, CancellationToken cancellation = default)
    {
        if (ProxyEnabled) throw new ProxyException(UiText.Language == "en-US"
            ? "Stop the dedicated proxy before changing its port."
            : "请先停止专用代理，再修改监听端口。");
        var previous = Profile;
        if (previous is not null) await EnsureNoExternalCoreAsync(previous, cancellation).ConfigureAwait(false);
        var settings = Network;
        var chosen = port ?? (previous is null ? (int?)null : StoredProfile.FreePorts().First(p => p != previous.ControllerPort));
        if (chosen is { } candidatePort) NetworkSettings.ValidateProxyPortAvailable(candidatePort, previous?.ControllerPort);
        var changed = previous is not null && chosen != previous.Port;
        try
        {
            if (changed) await (previous! with { Port = chosen!.Value }).SaveAsync(DirectoryPath, cancellation).ConfigureAwait(false);
            await (settings with { ProxyPort = port }).SaveAsync(DirectoryPath, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (changed) await previous!.SaveAsync(DirectoryPath).ConfigureAwait(false);
            throw;
        }
        if (changed && Desktop is not null)
        {
            try { await Desktop.DisposeAsync().ConfigureAwait(false); }
            finally { Desktop = null; }
        }
    }

    private async Task EnsureNoExternalCoreAsync(Profile profile, CancellationToken cancellation)
    {
        if (Core?.OwnsProcess == true) return;
        await using var probe = new CoreSession(DirectoryPath, profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (await probe.RunningAsync(timeout.Token).ConfigureAwait(false))
                throw new ProxyException(UiText.Language == "en-US"
                    ? "Another launcher owns the running core. Stop it from that launcher before changing the subscription or port."
                    : "当前内核由另一个启动器运行，请先在原启动器停止代理，再修改订阅或端口。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new ProxyException(UiText.Language == "en-US"
            ? "Could not confirm that the dedicated core is stopped. Try again later."
            : "无法确认专用内核已停止，请稍后重试。"); }
    }

    private async Task<Profile?> SubscriptionProxyProfileAsync(SubscriptionDownloadMode mode, CancellationToken cancellation)
    {
        if (mode != SubscriptionDownloadMode.Dedicated) return null;
        var core = Core;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (ProxyEnabled && core is not null && await core.RunningAsync(timeout.Token).ConfigureAwait(false)) return core.Profile;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        throw new ProxyException(UiText.Language == "en-US"
            ? "Subscription downloads use the dedicated proxy. Start it with cached nodes first, or choose Direct or System in Settings."
            : "订阅下载已选当前专用代理，请先通过缓存节点启动代理；首次导入请在设置中改为直连或系统代理。");
    }

    public async Task<SubscriptionMetadata> FetchSubscriptionMetadataAsync(string url, CancellationToken cancellation = default)
    {
        var mode = Network.DownloadMode;
        var profile = await SubscriptionProxyProfileAsync(mode, cancellation).ConfigureAwait(false);
        return await SubscriptionMetadata.FetchAsync(DirectoryPath, url, cancellation,
            SubscriptionRouting.WebProxy(mode, profile)).ConfigureAwait(false);
    }

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
            try
            {
                using var detection = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                detection.CancelAfter(TimeSpan.FromSeconds(3));
                bool alreadyRunning;
                try { alreadyRunning = await candidate.RunningAsync(detection.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new ProxyException(UiText.Language == "en-US"
                    ? "Could not confirm the local core state. Check port usage and try again."
                    : "无法确认本地内核状态，请检查端口占用后重试。"); }
                if (!alreadyRunning)
                {
                    NetworkSettings.ValidateProxyPortAvailable(profile.Port, profile.ControllerPort);
                    if (Network.DownloadMode == SubscriptionDownloadMode.Dedicated && !File.Exists(Path.Combine(DirectoryPath, candidate.CacheName)))
                        throw new ProxyException(UiText.Language == "en-US"
                            ? "No cached nodes are available. Choose Direct or System to refresh the subscription before starting the dedicated proxy."
                            : "暂无节点缓存，请先选择直连或系统代理刷新订阅，再启动专用代理。");
                }
                await candidate.StartAsync(cancellation); profile = candidate.Profile; Core = candidate; Desktop?.ReplaceCore(candidate);
            }
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
        if (active && previous is not null) await EnsureNoExternalCoreAsync(previous, cancellation).ConfigureAwait(false);
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
        var network = Network;
        var subscriptionProxy = await SubscriptionProxyProfileAsync(network.DownloadMode, cancellation).ConfigureAwait(false);
        url = string.IsNullOrWhiteSpace(url) ? previous?.Url ?? "" : url.Trim();
        var selectedCore = string.IsNullOrWhiteSpace(corePath) ? SelectedKernelPath ?? await KernelCatalog.BundledPathAsync(DirectoryPath, cancellation) : corePath;
        var candidate = await StoredProfile.CreateAsync(url, selectedCore, cancellation);
        if (previous is not null)
            candidate = candidate with { Port = previous.Port, ControllerPort = previous.ControllerPort,
                Secret = previous.Secret, ProxyPassword = previous.ProxyPassword, Selected = previous.Selected,
                TestUrl = previous.TestUrl, Expected = previous.Expected };
        else if (network.ProxyPort is { } port)
        {
            var controller = candidate.ControllerPort == port ? StoredProfile.FreePorts().First(p => p != port) : candidate.ControllerPort;
            NetworkSettings.ValidateProxyPortAvailable(port, controller);
            candidate = candidate with { Port = port, ControllerPort = controller };
        }
        if (ProxyEnabled && previous is not null)
        {
            var current = await EnsureAsync(cancellation).ConfigureAwait(false);
            if (!current.OwnsProcess) throw new ProxyException(UiText.Language == "en-US"
                ? "Another launcher owns the running core. Exit that launcher before changing the active subscription."
                : "当前内核由另一个启动器运行，请先退出原启动器，再更改当前订阅。");
        }
        else if (previous is not null) await EnsureNoExternalCoreAsync(previous, cancellation).ConfigureAwait(false);
        await StoredProfile.PrivateDirectoryAsync(DirectoryPath, cancellation);
        var staging = Path.Combine(DirectoryPath, ".subscription-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var cacheKey = NodeSnapshot.CacheKey(candidate);
        string[] cacheFiles = [Path.Combine(DirectoryPath, cacheKey + ".yaml"), Path.Combine(DirectoryPath, cacheKey + ".nodes.json")];
        var existed = cacheFiles.Select(File.Exists).ToArray();
        var configPath = Path.Combine(DirectoryPath, "config.json");
        var configBackup = Path.Combine(staging, "previous-config.json");
        var cacheChanged = false;
        var formalChanged = false;
        try
        {
            var ports = StoredProfile.FreePorts();
            await using var check = new CoreSession(staging, candidate with { Port = ports[0], ControllerPort = ports[1] },
                networkSettings: network, subscriptionProxyProfile: subscriptionProxy);
            await check.StartAsync(cancellation);
            if (probe && !ProxyEnabled) await check.ProbeAsync(cancellation);
            var names = await check.NodesAsync(cancellation);
            if (!names.Any(n => n.Name == candidate.Selected)) candidate = candidate with { Selected = CoreSession.Auto };
            for (var i = 0; i < cacheFiles.Length; i++)
                if (existed[i]) await CopyCacheAsync(cacheFiles[i], Path.Combine(staging, "previous-" + i), cancellation);
            if (File.Exists(configPath)) await CopyCacheAsync(configPath, configBackup, cancellation);
            await CopyCacheAsync(Path.Combine(staging, check.CacheName), Path.Combine(DirectoryPath, check.CacheName), cancellation);
            cacheChanged = true;
            await new NodeSnapshot(names, probe ? DateTimeOffset.UtcNow : null).SaveAsync(DirectoryPath, candidate, cancellation);
            if (!ProxyEnabled)
            {
                await candidate.SaveAsync(DirectoryPath, cancellation);
                return;
            }
            if (previous is not null && !string.Equals(previous.CorePath, candidate.CorePath, StringComparison.OrdinalIgnoreCase))
            {
                formalChanged = true;
                await RestartCoreAsync(candidate, previous);
            }
            else if (previous is not null)
            {
                var core = await EnsureAsync(cancellation);
                core.Profile = candidate;
                await StoredProfile.WriteJsonAsync(configPath, core.Configuration(), cancellation);
                formalChanged = true;
                await core.ApiAsync("/configs?force=true", HttpMethod.Put, new { path = configPath }, cancellation);
                await core.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name = candidate.Selected }, cancellation);
            }
            else
            {
                var core = new CoreSession(DirectoryPath, candidate);
                try { await core.StartAsync(cancellation); Core = core; }
                catch { await core.DisposeAsync(); throw; }
            }
            if (probe)
            {
                await Core!.ProbeAsync(cancellation);
                var latest = new NodeSnapshot(await Core.NodesAsync(cancellation), DateTimeOffset.UtcNow);
                await latest.SaveAsync(DirectoryPath, Core.Profile, cancellation);
            }
            await Core!.Profile.SaveAsync(DirectoryPath, cancellation);
        }
        catch
        {
            if (cacheChanged)
            {
                try
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    for (var i = 0; i < cacheFiles.Length; i++)
                        if (existed[i]) await CopyCacheAsync(Path.Combine(staging, "previous-" + i), cacheFiles[i], restore.Token);
                        else File.Delete(cacheFiles[i]);
                    if (previous is not null && Core is { } original)
                    {
                        original.Profile = previous;
                        if (formalChanged)
                        {
                            if (File.Exists(configBackup)) await CopyCacheAsync(configBackup, configPath, restore.Token);
                            else await StoredProfile.WriteJsonAsync(configPath, original.Configuration(), restore.Token);
                            await original.ApiAsync("/configs?force=true", HttpMethod.Put, new { path = configPath }, restore.Token);
                            await original.ApiAsync("/proxies/" + CoreSession.Select, HttpMethod.Put, new { name = previous.Selected }, restore.Token);
                            await previous.SaveAsync(DirectoryPath, restore.Token);
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProxyException or OperationCanceledException)
                { throw new ProxyException(UiText.Language == "en-US"
                    ? "The subscription update failed and restoration did not finish. Stop the proxy and check the saved subscription before restarting."
                    : "订阅更新失败，原配置恢复未完成。请停止代理，检查已保存订阅后再启动。"); }
            }
            throw;
        }
        finally { Directory.Delete(staging, true); } // Only this call's freshly created directory.
    }

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellation)
    {
        var profile = Profile ?? throw new ProxyException(UiText.Get("请先在订阅设置中添加链接。"));
        await ApplySubscriptionAsync(profile.Url, profile.CorePath, cancellation, probe: true).ConfigureAwait(false);
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
