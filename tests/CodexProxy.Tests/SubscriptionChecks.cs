using System.Security.Cryptography;
using System.Text;
using CodexProxy.Core;

namespace CodexProxy.Tests;

static class SubscriptionChecks
{
    public static async Task RunAsync(Fixture fixture, ProxyService service, Action<bool, string> check)
    {
        var core = await service.EnsureAsync();
        var original = service.Profile!;
        var before = File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json"));
        var catalog = service.Subscriptions;
        var first = catalog.Items.Single();
        check(first.Url == original.Url && catalog.ActiveId == first.Id &&
            File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json")) == before, "legacy subscription migrates without changing the active profile");
        var second = await service.SaveSubscriptionEntryAsync(null, "备用订阅", original.Url + "&second=1", null, false);
        check(service.Subscriptions.Items.Count == 2 && service.Subscriptions.ActiveId == first.Id &&
            File.ReadAllText(Path.Combine(fixture.DirectoryPath, "profile.json")) == before, "adding inactive subscription preserves live profile");
        var restored = SubscriptionCatalog.Load(fixture.DirectoryPath, service.Profile);
        check(restored.Items.Count == 2 && restored.ActiveId == first.Id, "subscription list and active choice survive reload");
        second = await service.SaveSubscriptionEntryAsync(second.Id, "备用已改名", "", null, false);
        check(second.Name == "备用已改名" && second.Url == original.Url + "&second=1" && !second.ToString().Contains("fixture-private"),
            "subscription editing keeps an unchanged URL and does not expose it in list display text");
        second = await service.SaveSubscriptionEntryAsync(second.Id, second.Name, original.Url + "&edited=1", null, false);
        check(service.Subscriptions.Items.Single(s => s.Id == second.Id).Url == original.Url + "&edited=1" &&
            service.Subscriptions.ActiveId == first.Id, "editing a saved subscription URL persists without changing the active connection");
        var savedCatalog = File.ReadAllText(Path.Combine(fixture.DirectoryPath, "subscriptions.json"));
        try { await service.SaveSubscriptionEntryAsync(null, "非法链接", "file:///bad", null, false); throw new Exception("Expected invalid URL rejection"); }
        catch (ProxyException) { check(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "subscriptions.json")) == savedCatalog, "invalid subscription does not change the list"); }
        fixture.Subscription.Payload = fixture.Nodes("订阅 B JP");
        await service.ActivateSubscriptionAsync(second.Id, original.CorePath);
        check(service.Subscriptions.ActiveId == second.Id && service.Subscriptions.Items.Single(s => s.Id == first.Id).Url == original.Url &&
            (await core.NodesAsync()).Any(n => n.Name == "订阅 B JP"), "activating second subscription loads its nodes and keeps first entry");
        check(service.Profile!.Port == original.Port && service.Profile.ProxyPassword == original.ProxyPassword,
            "subscription switching preserves proxy endpoint and credentials");
        fixture.Subscription.Payload = fixture.Nodes(Fixture.GoodName);
        await service.ActivateSubscriptionAsync(first.Id, original.CorePath);
        var names = await core.NodesAsync();
        check(service.Subscriptions.ActiveId == first.Id && names.Any(n => n.Name == Fixture.GoodName) && names.All(n => n.Name != "订阅 B JP"),
            "switching back restores one subscription without merging nodes");
        await service.DeleteSubscriptionAsync(second.Id);
        check(service.Subscriptions.Items.Count == 1 && service.Subscriptions.ActiveId == first.Id, "deleting inactive subscription leaves active proxy intact");

        var empty = Path.Combine(fixture.DirectoryPath, "empty-state");
        await using var fresh = new ProxyService(empty);
        var pending = await fresh.SaveSubscriptionEntryAsync(null, "首个订阅", original.Url, null, false);
        check(fresh.Profile is null && fresh.Subscriptions.ActiveId is null && fresh.Subscriptions.Items.Single().Id == pending.Id,
            "first subscription can be saved before a core is configured or started");
        var backup = await fresh.SaveSubscriptionEntryAsync(null, "保留备用", original.Url + "&remaining=1", null, false);
        var shared = await fresh.SaveSubscriptionEntryAsync(null, "共享链接", original.Url, null, false);
        await fresh.ActivateSubscriptionAsync(pending.Id, original.CorePath);
        var owned = await fresh.StartProxyAsync();
        await SubscriptionMetadata.FetchAsync(empty, pending.Url);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pending.Url))).ToLowerInvariant();
        string[] cacheFiles = [Path.Combine(empty, "subscription-" + hash[..16] + ".yaml"),
            Path.Combine(empty, "subscription-" + hash[..16] + ".nodes.json"),
            Path.Combine(empty, "subscription-metadata-" + hash + ".json")];
        var profilePath = Path.Combine(empty, "profile.json");
        var catalogPath = Path.Combine(empty, "subscriptions.json");
        var profileBeforeDelete = File.ReadAllText(profilePath);
        var catalogBeforeDelete = File.ReadAllText(catalogPath);
        await using (var otherLauncher = new ProxyService(empty))
        {
            try { await otherLauncher.DeleteSubscriptionAsync(pending.Id); throw new Exception("Expected external core rejection"); }
            catch (ProxyException)
            {
                check(otherLauncher.Core is null && !otherLauncher.ProxyEnabled && await owned.RunningAsync() &&
                    File.ReadAllText(profilePath) == profileBeforeDelete && File.ReadAllText(catalogPath) == catalogBeforeDelete,
                    "active deletion detects an external core even when this manager is stopped and has no session");
            }
            var borrowed = await otherLauncher.StartProxyAsync();
            try { await otherLauncher.DeleteSubscriptionAsync(pending.Id); throw new Exception("Expected borrowed core rejection"); }
            catch (ProxyException)
            {
                check(!borrowed.OwnsProcess && await owned.RunningAsync() && File.ReadAllText(profilePath) == profileBeforeDelete &&
                    File.ReadAllText(catalogPath) == catalogBeforeDelete, "active deletion refuses a live borrowed core without changing saved subscriptions");
            }
        }
        check(cacheFiles.All(File.Exists), "active subscription has separate node, YAML and quota caches");
        await fresh.DeleteSubscriptionAsync(shared.Id);
        check(cacheFiles.All(File.Exists) && fresh.Subscriptions.ActiveId == pending.Id && await owned.RunningAsync(),
            "deleting a duplicate subscription preserves shared URL caches and the active core");
        catalogBeforeDelete = File.ReadAllText(catalogPath);
        if (OperatingSystem.IsWindows())
        {
            using (var lockedCatalog = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await fresh.DeleteSubscriptionAsync(pending.Id); throw new Exception("Expected catalog commit failure"); }
                catch (IOException) { }
            }
            check(!fresh.ProxyEnabled && fresh.Core is null && File.ReadAllText(profilePath) == profileBeforeDelete &&
                File.ReadAllText(catalogPath) == catalogBeforeDelete && fresh.Subscriptions.ActiveId == pending.Id,
                "failed catalog commit restores the original profile and entries while keeping the proxy stopped");
        }
        var deletedActive = await fresh.DeleteSubscriptionAsync(pending.Id);
        check(deletedActive && !fresh.ProxyEnabled && fresh.Core is null && fresh.Profile is null &&
            !File.Exists(profilePath) && !File.Exists(Path.Combine(empty, "config.json")) && cacheFiles.All(p => !File.Exists(p)),
            "active deletion stops the owned proxy and removes its profile, runtime config and unshared caches");
        check(fresh.Subscriptions.ActiveId is null && fresh.Subscriptions.Items.Single().Id == backup.Id &&
            fresh.SelectedKernelPath == original.CorePath, "active deletion preserves backup subscriptions and the chosen core without auto-activating a backup");
        await using var reopened = new ProxyService(empty);
        check(reopened.Profile is null && reopened.Subscriptions.ActiveId is null && reopened.Subscriptions.Items.Single().Id == backup.Id &&
            !reopened.ProxyEnabled && reopened.Core is null, "deleted active subscription does not migrate back or restart after reopening state");
        check(await core.RunningAsync(), "isolated active deletion leaves the original fixture core alive for subsequent checks");
    }
}
