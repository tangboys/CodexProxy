using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexProxy.Core;

public sealed record Subscription(string Id, string Name, string Url)
{
    [JsonIgnore] public string Host => new Uri(Url).Host;
    [JsonIgnore] public bool Active { get; init; }
    [JsonIgnore] public string StatusText => Active ? UiText.Get("使用中") : "";
    public override string ToString() => Name; // Do not expose the URL in UI automation or debug display.
}

public sealed record SubscriptionCatalog
{
    public string? ActiveId { get; init; }
    public List<Subscription> Items { get; init; } = [];

    public static SubscriptionCatalog Load(string directory, Profile? profile)
    {
        var file = Path.Combine(directory, "subscriptions.json");
        var catalog = File.Exists(file) ? JsonSerializer.Deserialize<SubscriptionCatalog>(File.ReadAllText(file))! : new();
        if (profile is null) return catalog with { ActiveId = null };
        if (catalog.Items.Any(s => s.Id == catalog.ActiveId && s.Url == profile.Url)) return catalog;
        // Migrate the original single profile, and recognize later CLI setup changes.
        var active = catalog.Items.FirstOrDefault(s => s.Url == profile.Url);
        if (active is null)
        {
            active = new(Guid.NewGuid().ToString("N"), catalog.Items.Count == 0 ? UiText.Get("默认订阅") : string.Format(UiText.Get("订阅 {0}"), catalog.Items.Count + 1), profile.Url);
            catalog = catalog with { Items = [.. catalog.Items, active] };
        }
        catalog = catalog with { ActiveId = active.Id };
        catalog.Save(directory);
        return catalog;
    }

    public void Save(string directory)
    {
        Profile.PrivateDirectory(directory);
        Profile.WriteJson(Path.Combine(directory, "subscriptions.json"), this);
    }
}
