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
    private sealed record CachedCatalog(DateTimeOffset Updated, string Commit, Dictionary<string, SiteRule> Services, string[]? HiddenDomains = null);
    private sealed record SeedService(string id, string name, string[] domains);
    private sealed record Seed(SeedService[] services);
    private readonly string cachePath;
    private readonly HttpClient? httpClient;
    private HashSet<string> hiddenDomains = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SiteRule> services = new(StringComparer.OrdinalIgnoreCase);
    private CatalogChoice[]? searchIndex;
    public DateTimeOffset? Updated { get; private set; }
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    { ["youtube.com"] = "youtube", ["youtu.be"] = "youtube", ["t.me"] = "telegram", ["telegram.org"] = "telegram", ["chatgpt.com"] = "openai", ["openai.com"] = "openai", ["discord.com"] = "discord", ["github.com"] = "github" };
    public ServiceCatalog(string? cacheFile = null, HttpClient? httpClient = null)
    {
        this.httpClient = httpClient;
        cachePath = cacheFile ?? Path.Combine(SecureStore.UserDirectory, "catalog.json");
        using (var stream = typeof(ServiceCatalog).Assembly.GetManifestResourceStream("RedRoute.Platform.Resources.hidden-domains.txt")!)
        using (var reader = new StreamReader(stream))
            AddHiddenDomains(hiddenDomains, reader.ReadToEnd().Split('\n'));
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
                    // Validate the complete cache before it can replace any bundled entries.
                    foreach (var rule in cached.Services.Values) new ClientSettings { Sites = [rule] }.Validate();
                    AddHiddenDomains(hiddenDomains, cached.HiddenDomains ?? []);
                    foreach (var item in cached.Services.Values.Where(s => IsHiddenService(s.ServiceId ?? "", s.Name)))
                        AddHiddenDomains(hiddenDomains, item.Domains);
                    foreach (var item in cached.Services)
                    {
                        services[item.Key] = item.Value;
                    }
                    Updated = cached.Updated;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException) { }
        FilterCatalog(services, hiddenDomains);
    }
    private static bool IsHiddenService(string id, string name) =>
        new[] { "porn", "adult", "nsfw", "hentai", "18+" }.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase) || name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    private static void AddHiddenDomains(HashSet<string> destination, IEnumerable<string> values)
    {
        foreach (var value in values)
            try { destination.Add(Domains.Normalize(value)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { }
    }
    private static bool IsHiddenDomain(string domain, HashSet<string> excluded)
    {
        for (var suffix = domain; ;)
        {
            if (excluded.Contains(suffix)) return true;
            var dot = suffix.IndexOf('.');
            if (dot < 0) return false;
            suffix = suffix[(dot + 1)..];
        }
    }
    private static void FilterCatalog(Dictionary<string, SiteRule> catalog, HashSet<string> excluded)
    {
        foreach (var (id, rule) in catalog.ToArray())
        {
            if (IsHiddenService(id, rule.Name)) { catalog.Remove(id); continue; }
            var visible = rule.Domains.Where(d => !IsHiddenDomain(d, excluded)).ToArray();
            if (visible.Length == 0 && rule.IpRanges.Length == 0) catalog.Remove(id);
            else catalog[id] = rule with { Domains = visible };
        }
    }
    public SiteRule? Find(string domain)
    {
        domain = Domains.Normalize(domain);
        if (IsHiddenDomain(domain, hiddenDomains)) return null;
        if (Aliases.TryGetValue(domain, out var id) && services.TryGetValue(id, out var rule)) return rule;
        return services.Values.Select(s => new { Rule = s, Match = s.Domains.Where(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal)).Select(d => d.Length).DefaultIfEmpty(0).Max() })
            .Where(s => s.Match > 0).OrderByDescending(s => s.Match)
            .ThenBy(s => s.Rule.ServiceId?.StartsWith("category-", StringComparison.OrdinalIgnoreCase) == true)
            .ThenBy(s => s.Rule.Domains.Length).Select(s => s.Rule).FirstOrDefault();
    }
    public SiteRule? GetById(string id) => services.GetValueOrDefault(id);
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
        var selectedIds = selected.Where(s => s.ServiceId != null).Select(s => s.ServiceId!).ToArray();
        using var ownedClient = httpClient == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) } : null;
        var client = httpClient ?? ownedClient!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3)); token = deadline.Token;
        using var reference = JsonDocument.Parse(await Download(client, "https://api.github.com/repos/Ground-Zerro/Geo-Aggregator/git/ref/heads/main", 300000, token));
        if (reference.RootElement.ValueKind != JsonValueKind.Object || !reference.RootElement.TryGetProperty("object", out var obj)
            || obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("sha", out var shaValue) || shaValue.ValueKind != JsonValueKind.String)
            throw new IOException("Некорректная версия базы.");
        var sha = shaValue.GetString()!;
        if (sha.Length != 40 || sha.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Некорректная версия базы.");
        var prefix = "https://raw.githubusercontent.com/Ground-Zerro/Geo-Aggregator/" + sha + "/";
        var catalog = JsonSerializer.Deserialize<CatalogRoot>(await Download(client, prefix + "db/catalog.json", 600000, token)) ?? throw new IOException("Пустой каталог.");
        if (catalog.version != 1 || catalog.services == null || catalog.services.Length > 2000
            || catalog.services.Any(s => s == null || string.IsNullOrWhiteSpace(s.id) || string.IsNullOrWhiteSpace(s.name) || string.IsNullOrWhiteSpace(s.src)
                || s.id.Length > 200 || s.name.Length > 253 || s.name.Any(char.IsControl))) throw new IOException("Неизвестный формат базы.");
        if (catalog.services.Select(s => s.id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.services.Length) throw new IOException("Повторяющиеся сервисы базы.");
        var ids = Aliases.Values.Concat(selectedIds).Distinct();
        var next = new Dictionary<string, SiteRule>(services, StringComparer.OrdinalIgnoreCase);
        var nextHidden = new HashSet<string>(hiddenDomains, StringComparer.OrdinalIgnoreCase);
        // Refresh the exclusion list too, before merging any updated services.
        foreach (var item in catalog.services.Where(s => IsHiddenService(s.id, s.name)))
        {
            ValidateSource(item.src);
            var text = System.Text.Encoding.UTF8.GetString(await Download(client, prefix + item.src, 1000000, token));
            AddHiddenDomains(nextHidden, ParseService(item.name, item.id, text.Split('\n')).Domains);
        }
        foreach (var id in ids)
        {
            var item = catalog.services.FirstOrDefault(s => s.id == id);
            if (item == null || IsHiddenService(item.id, item.name)) continue;
            ValidateSource(item.src);
            var text = System.Text.Encoding.UTF8.GetString(await Download(client, prefix + item.src, 1000000, token));
            next[id] = ParseService(item.name, id, text.Split('\n'));
        }
        FilterCatalog(next, nextHidden);
        var snapshot = new CachedCatalog(DateTimeOffset.UtcNow, sha, next, nextHidden.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
        var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot), token); token.ThrowIfCancellationRequested(); File.Move(tmp, cachePath, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        services = next; hiddenDomains = nextHidden; searchIndex = null; Updated = snapshot.Updated;
    }
    private static void ValidateSource(string source)
    {
        if (source.Length > 300 || source.Contains("..") || source.StartsWith('/') || source.Contains(':') || source.Contains('\\')
            || source.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.'))) throw new IOException("Небезопасный путь базы.");
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(25)); token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("RED_ROUTE/0.1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("База слишком большая.");
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0) { if (buffer.Length + count > limit) throw new IOException("База слишком большая."); await buffer.WriteAsync(chunk.AsMemory(0, count), token); }
        return buffer.ToArray();
    }
}
