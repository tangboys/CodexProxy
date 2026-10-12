using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexProxy.Core;

public sealed record NodeSnapshot(List<Node> Nodes, DateTimeOffset? CheckedAt)
{
    internal static string CacheKey(Profile profile) => "subscription-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Url)))[..16].ToLowerInvariant();

    internal static async Task<NodeSnapshot?> LoadAsync(string directory, Profile profile, CancellationToken cancellation)
    {
        var path = Path.Combine(directory, CacheKey(profile) + ".nodes.json");
        if (!File.Exists(path)) return null;
        var snapshot = JsonSerializer.Deserialize<NodeSnapshot>(await File.ReadAllTextAsync(path, cancellation).ConfigureAwait(false));
        return snapshot?.Nodes is null ? null : snapshot;
    }

    internal Task SaveAsync(string directory, Profile profile, CancellationToken cancellation) =>
        Profile.WriteJsonAsync(Path.Combine(directory, CacheKey(profile) + ".nodes.json"), new
        {
            Nodes = Nodes.Select(n => new { n.Name, n.Protocol, n.Available, n.Delay }),
            CheckedAt
        }, cancellation);

    internal NodeSnapshot ForSelection(string selected) => this with
    {
        Nodes = Nodes.Select(n => n with { Current = selected != CoreSession.Auto && n.Name == selected, Codex = null }).ToList()
    };
}
