using System.Net.Http;
using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public sealed class ServiceCatalog
{
    private sealed record Entry(string id, string name, string src);
    private sealed record CatalogRoot(int version, Entry[] services);
    private sealed record CachedCatalog(DateTimeOffset Updated, string Commit, Dictionary<string, SiteRule> Services);
    private sealed record SeedService(string id, string name, string[] domains);
    private sealed record Seed(SeedService[] services);
    private readonly string cachePath = Path.Combine(SecureStore.UserDirectory, "catalog.json");
    private Dictionary<string, SiteRule> services = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? Updated { get; private set; }
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    { ["youtube.com"] = "youtube", ["youtu.be"] = "youtube", ["t.me"] = "telegram", ["telegram.org"] = "telegram", ["chatgpt.com"] = "openai", ["openai.com"] = "openai", ["discord.com"] = "discord", ["github.com"] = "github" };
    public ServiceCatalog()
    {
        using (var stream = typeof(ServiceCatalog).Assembly.GetManifestResourceStream("RedRoute.Platform.Resources.seed.json"))
        {
            var seed = stream == null ? null : JsonSerializer.Deserialize<Seed>(stream);
            foreach (var entry in seed?.services ?? [])
            {
                var ips = entry.domains.Where(d => System.Net.IPAddress.TryParse(d.Split('/')[0], out _)).Select(Domains.NormalizeCidr).ToArray();
                var domains = entry.domains.Where(d => !System.Net.IPAddress.TryParse(d.Split('/')[0], out _)).Select(Domains.Normalize).ToArray();
                services[entry.id] = new(entry.name, domains, ips, entry.id);
            }
        }
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length < 4_000_000)
            {
                var cached = JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllText(cachePath));
                if (cached != null) { services = cached.Services; Updated = cached.Updated; }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }
    public SiteRule? Find(string domain)
    {
        if (Aliases.TryGetValue(domain, out var id) && services.TryGetValue(id, out var rule)) return rule;
        return services.Values.FirstOrDefault(s => s.Domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal)));
    }
    public IReadOnlyList<SiteRule> Suggest(string query)
    {
        query = query.Trim();
        if (query.Length < 2 || query.Length > 200) return [];
        return services.Values.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.ServiceId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || s.Domains.Any(d => d.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Take(6).ToArray();
    }
    public async Task RefreshAsync(IEnumerable<SiteRule> selected, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RED_ROUTE/0.1");
        using var reference = JsonDocument.Parse(await Download(client, "https://api.github.com/repos/Ground-Zerro/Geo-Aggregator/git/ref/heads/main", 300000, token));
        var sha = reference.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        if (sha.Length != 40 || sha.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Некорректная версия базы.");
        var prefix = "https://raw.githubusercontent.com/Ground-Zerro/Geo-Aggregator/" + sha + "/";
        var catalog = JsonSerializer.Deserialize<CatalogRoot>(await Download(client, prefix + "db/catalog.json", 600000, token)) ?? throw new IOException("Пустой каталог.");
        if (catalog.version != 1 || catalog.services.Length > 2000) throw new IOException("Неизвестный формат базы.");
        var ids = Aliases.Values.Concat(selected.Where(s => s.ServiceId != null).Select(s => s.ServiceId!)).Distinct();
        var next = new Dictionary<string, SiteRule>(services, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var item = catalog.services.FirstOrDefault(s => s.id == id);
            if (item == null) continue;
            if (item.src.Contains("..") || item.src.StartsWith('/') || item.src.Contains(':') || item.src.Contains('\\')) throw new IOException("Небезопасный путь базы.");
            var text = System.Text.Encoding.UTF8.GetString(await Download(client, prefix + item.src, 1000000, token));
            List<string> domains = []; List<string> ips = [];
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Split('#')[0].Trim(); if (line.Length == 0) continue;
                if (line.StartsWith("domain:", StringComparison.Ordinal)) line = line[7..];
                if (line.StartsWith("full:", StringComparison.Ordinal)) line = line[5..];
                if (line.StartsWith("regexp:", StringComparison.Ordinal) || line.Contains('@')) continue;
                if (System.Net.IPAddress.TryParse(line.Split('/')[0], out _)) ips.Add(Domains.NormalizeCidr(line));
                else domains.Add(Domains.Normalize(line));
            }
            if (domains.Count + ips.Count > 15000) throw new IOException("Слишком большой список сервиса.");
            next[id] = new(item.name, domains.Distinct().ToArray(), ips.Distinct().ToArray(), id);
        }
        var snapshot = new CachedCatalog(DateTimeOffset.UtcNow, sha, next);
        Directory.CreateDirectory(SecureStore.UserDirectory);
        var tmp = cachePath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot), token); File.Move(tmp, cachePath, true);
        services = next; Updated = snapshot.Updated;
    }
    private static async Task<byte[]> Download(HttpClient client, string url, int limit, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("База слишком большая.");
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0) { if (buffer.Length + count > limit) throw new IOException("База слишком большая."); await buffer.WriteAsync(chunk.AsMemory(0, count), token); }
        return buffer.ToArray();
    }
}
