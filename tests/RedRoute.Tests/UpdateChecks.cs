using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedRoute.Platform;

internal static class UpdateChecks
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var payload = Encoding.ASCII.GetBytes("MZ-synthetic-installer-for-tests-only");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string Metadata(string version = "0.2.0", bool draft = false, bool preview = false, string? url = null, string? digest = null) => JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft, prerelease = preview, body = "Changes",
            assets = new[] { new { name = $"RED_ROUTE-{version}-Setup.exe", state = "uploaded", size = payload.Length, digest = digest ?? "sha256:" + hash,
                browser_download_url = url ?? $"https://github.com/cveloy102/RED_ROUTE/releases/download/v{version}/RED_ROUTE-{version}-Setup.exe" } }
        });
        var installed = new Version(0, 1, 9, 0);
        var release = GitHubUpdates.Parse(Metadata(), installed)!;
        assert(release.Version == new Version(0, 2, 0) && release.Sha256 == hash, "Updater selects newer stable release and SHA-256");
        assert(GitHubUpdates.Parse(Metadata("0.1.9"), installed) == null && GitHubUpdates.Parse(Metadata("0.1.8"), installed) == null, "Updater ignores equal versions and downgrades");
        assert(GitHubUpdates.Parse(Metadata(draft: true), installed) == null && GitHubUpdates.Parse(Metadata(preview: true), installed) == null, "Updater ignores draft and prerelease builds");
        assert(GitHubUpdates.Parse(Metadata(url: "https://example.org/installer.exe"), installed) == null, "Updater refuses assets outside the exact release repository path");
        assert(GitHubUpdates.Parse(Metadata(digest: ""), installed) == null, "Updater never offers a binary without a SHA-256 digest");
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "updater-" + Guid.NewGuid().ToString("N"));
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var client = new HttpClient(handler); var updater = new GitHubUpdates(client);
        var path = await updater.DownloadAsync(release, folder, null, default);
        assert(File.ReadAllBytes(path).SequenceEqual(payload), "Updater publishes only the completely verified download");
        await updater.DownloadAsync(release, folder, null, default);
        assert(handler.Requests == 1, "A previously downloaded installer is rehashed before safe reuse");
        File.WriteAllText(path, "broken"); await updater.DownloadAsync(release, folder, null, default);
        assert(handler.Requests == 2 && File.ReadAllBytes(path).SequenceEqual(payload), "Corrupt cached installer is replaced with verified content");
        var badFolder = folder + "-bad";
        try { await updater.DownloadAsync(release with { Sha256 = new string('0', 64) }, badFolder, null, default); throw new Exception("Corrupt update was accepted"); }
        catch (IOException) { assert(!Directory.EnumerateFiles(badFolder).Any(), "Failed checksum removes temporary download and leaves no runnable installer"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await updater.DownloadAsync(release, folder + "-cancelled", null, cancel.Token); throw new Exception("Cancelled download was accepted"); }
        catch (OperationCanceledException) { assert(!Directory.EnumerateFiles(folder + "-cancelled").Any(), "Cancelled update leaves no partial installer"); }
        using var missing = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        assert(await new GitHubUpdates(missing).CheckAsync(installed) == null, "No published release is a normal no-update state");
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(response(request)); }
    }
}
