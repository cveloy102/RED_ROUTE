using System.Net.Http;
using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public sealed record CatalogChoice(SiteRule Rule, bool IsService)
{
    public string Title => Rule.Name;
    public string Description => IsService ? $"Весь сервис · доменов: {Rule.Domains.Length}, подсетей: {Rule.IpRanges.Length}" : "Только этот домен и его поддомены";
}
public sealed class ServiceCatalog
{
    private sealed record Entry(string id, string name, string src);
    private sealed record CatalogRoot(int version, Entry[] services);
    private sealed record CachedCatalog(DateTimeOffset Updated, string Commit, Dictionary<string, SiteRule> Services);
    private sealed record SeedService(string id, string name, string[] domains);
    private sealed record Seed(SeedService[] services);
    private readonly string cachePath = Path.Combine(SecureStore.UserDirectory, "catalog.json");
    private Dictionary<string, SiteRule> services = new(StringComparer.OrdinalIgnoreCase);
    private CatalogChoice[]? searchIndex;
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
                services[entry.id] = ParseService(entry.name, entry.id, entry.domains);
            }
        }
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length < 4_000_000)
            {
                var cached = JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllText(cachePath));
                if (cached?.Services != null)
                {
                    foreach (var item in cached.Services)
                    {
                        new ClientSettings { Sites = [item.Value] }.Validate();
                        services[item.Key] = item.Value;
                    }
                    Updated = cached.Updated;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException) { }
    }
    public SiteRule? Find(string domain)
    {
        if (Aliases.TryGetValue(domain, out var id) && services.TryGetValue(id, out var rule)) return rule;
        return services.Values.FirstOrDefault(s => s.Domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal)));
    }
    public IReadOnlyList<SiteRule> Suggest(string query)
    {
        query = query.Trim();
        if (query.Length > 200) return [];
        return services.Values.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.ServiceId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || s.Domains.Any(d => d.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public IReadOnlyList<CatalogChoice> Search(string query)
    {
        query = query.Trim();
        if (query.Length > 200) return [];
        searchIndex ??= services.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new CatalogChoice(s, true))
            .Concat(services.Values.SelectMany(s => s.Domains).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(d => new CatalogChoice(new SiteRule(d, [d], []), false))).ToArray();
        if (query.Length == 0) return searchIndex;
        return searchIndex.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || (s.IsService && s.Rule.ServiceId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(s => s.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ThenByDescending(s => s.IsService).ToArray();
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
            next[id] = ParseService(item.name, id, text.Split('\n'));
        }
        var snapshot = new CachedCatalog(DateTimeOffset.UtcNow, sha, next);
        Directory.CreateDirectory(SecureStore.UserDirectory);
        var tmp = cachePath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot), token); File.Move(tmp, cachePath, true);
        services = next; searchIndex = null; Updated = snapshot.Updated;
    }
    private static SiteRule ParseService(string name, string id, IEnumerable<string> values)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ips = new HashSet<string>();
        foreach (var raw in values)
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0 || line.StartsWith("regexp:", StringComparison.Ordinal) || line.Contains('@')) continue;
            if (line.StartsWith("domain:", StringComparison.Ordinal)) line = line[7..];
            if (line.StartsWith("full:", StringComparison.Ordinal)) line = line[5..];
            try
            {
                if (System.Net.IPAddress.TryParse(line.Split('/')[0], out _)) ips.Add(Domains.NormalizeCidr(line));
                else domains.Add(Domains.Normalize(line));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { /* Skip unsupported upstream entries, not the complete catalogue. */ }
            if (domains.Count + ips.Count > 15000) throw new IOException("Слишком большой список сервиса.");
        }
        return new(name, domains.ToArray(), ips.ToArray(), id);
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
