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
        try { service.DeleteSubscription(first.Id); throw new Exception("Expected active deletion rejection"); }
        catch (ProxyException) { check(service.Subscriptions.Items.Count == 2, "active subscription cannot be deleted"); }
        service.DeleteSubscription(second.Id);
        check(service.Subscriptions.Items.Count == 1 && service.Subscriptions.ActiveId == first.Id, "deleting inactive subscription leaves active proxy intact");

        var empty = Path.Combine(fixture.DirectoryPath, "empty-state");
        await using var fresh = new ProxyService(empty);
        var pending = await fresh.SaveSubscriptionEntryAsync(null, "首个订阅", original.Url, null, false);
        check(fresh.Profile is null && fresh.Subscriptions.ActiveId is null && fresh.Subscriptions.Items.Single().Id == pending.Id,
            "first subscription can be saved before a core is configured or started");
    }
}
