using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedRoute.Platform;

public sealed record ReleaseUpdate(Version Version, string Notes, Uri Page, Uri Download, long Size, string Sha256)
{
    public string FileName => $"RED_ROUTE-{Version.ToString(3)}-Setup.exe";
}

public sealed class GitHubUpdates
{
    public const string Repository = "cveloy102/RED_ROUTE";
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    public GitHubUpdates(HttpClient? client = null) => this.client = client ?? SharedClient;
    public static ReleaseUpdate? Parse(string json, Version installed)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) return null;
        if (version <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;
        var name = $"RED_ROUTE-{version.ToString(3)}-Setup.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded") continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (size <= 0 || size > 400L * 1024 * 1024 || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")
                || !string.Equals(asset.GetProperty("browser_download_url").GetString(), expected, StringComparison.Ordinal)) continue;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            if (notes.Length > 5000) notes = notes[..5000];
            return new(version, notes, new Uri($"https://github.com/{Repository}/releases/tag/{tag}"), new Uri(expected), size, digest[7..].ToLowerInvariant());
        }
        return null;
    }
    public async Task<ReleaseUpdate?> CheckAsync(Version installed, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("RED_ROUTE/" + installed.ToString(3)); request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var memory = new MemoryStream(); var buffer = new byte[32768]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0) { if (memory.Length + count > 1024 * 1024) throw new IOException("Слишком большой ответ GitHub."); await memory.WriteAsync(buffer.AsMemory(0, count), deadline.Token); }
        return Parse(System.Text.Encoding.UTF8.GetString(memory.ToArray()), installed);
    }
    public async Task<string> DownloadAsync(ReleaseUpdate release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory); var target = Path.Combine(directory, release.FileName);
        if (File.Exists(target) && await VerifyAsync(target, release, token)) { progress?.Report(100); return target; }
        var temporary = Path.Combine(directory, release.FileName + "." + Guid.NewGuid().ToString("N") + ".part");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.Download); request.Headers.UserAgent.ParseAdd("RED_ROUTE-updater");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new IOException("Размер установщика изменился. Повторите проверку обновлений.");
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    total += count; if (total > release.Size) throw new IOException("Размер установщика не совпал.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token); progress?.Report(total * 95.0 / release.Size);
                }
            }
            if (!await VerifyAsync(temporary, release, deadline.Token)) throw new IOException("Проверка установщика не пройдена. Файл не будет запущен.");
            File.Move(temporary, target, true); progress?.Report(100); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<bool> VerifyAsync(string path, ReleaseUpdate release, CancellationToken token)
    {
        if (new FileInfo(path).Length != release.Size) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
