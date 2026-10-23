using System.Net;
using System.Text;
using System.Text.Json;
using RedRoute.Core;
using RedRoute.Platform;

internal static class CatalogNetworkChecks
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "catalog-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "catalog.json");
        var index = JsonSerializer.Serialize(new { version = 1, services = new[]
        {
            new { id = "youtube", name = "YouTube", src = "source/youtube.txt" },
            new { id = "netflix", name = "Netflix", src = "source/netflix.txt" },
            new { id = "category-porn", name = "18+", src = "source/adult.txt" }
        }});
        string Reply(string url) => url.Contains("git/ref/") ? "{\"object\":{\"sha\":\"" + sha + "\"}}"
            : url.EndsWith("catalog.json") ? index
            : url.EndsWith("adult.txt") ? "newly-hidden.example\n"
            : url.EndsWith("netflix.txt") ? "netflix.com\nfresh-netflix.example\n"
            : "youtube.com\ngooglevideo.com\nnewly-hidden.example\nimg.newly-hidden.example\n";
        using var client = new HttpClient(new Handler((r, _) => Task.FromResult(Ok(Reply(r.RequestUri!.AbsoluteUri)))));
        var catalog = new ServiceCatalog(path, client);
        await catalog.RefreshAsync([new("Old", ["netflix.com"], [], "netflix")], default);
        assert(catalog.GetById("netflix")!.Domains.Contains("fresh-netflix.example"), "Database refresh updates the selected service by its stable ID");
        assert(catalog.Search("newly-hidden").Count == 0 && catalog.GetById("youtube")!.Domains.Length == 2, "New adult domains from upstream are filtered before refreshed services become visible");
        assert(new ServiceCatalog(path).Search("newly-hidden").Count == 0, "Adult exclusions learned during refresh survive a restart");
        var oldBytes = File.ReadAllBytes(path);
        using var failedClient = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var failed = new ServiceCatalog(path, failedClient);
        try { await failed.RefreshAsync([], default); throw new Exception("HTTP failure accepted"); } catch (HttpRequestException) { }
        assert(File.ReadAllBytes(path).SequenceEqual(oldBytes) && failed.GetById("netflix")!.Domains.Contains("fresh-netflix.example"), "Failed database download preserves both the working catalogue and its disk cache");
        foreach (var badIndex in new[]
        {
            "{\"version\":1,\"services\":null}", "{\"version\":1,\"services\":[null]}",
            "{\"version\":1,\"services\":[{\"id\":\"youtube\",\"name\":null,\"src\":\"x\"}]}",
            "{\"version\":99,\"services\":[]}"
        })
        {
            using var malformed = new HttpClient(new Handler((r, _) => Task.FromResult(Ok(r.RequestUri!.AbsolutePath.EndsWith("catalog.json") ? badIndex : Reply(r.RequestUri.AbsoluteUri)))));
            try { await new ServiceCatalog(path, malformed).RefreshAsync([], default); throw new Exception("Malformed catalogue accepted"); }
            catch (IOException) { assert(File.ReadAllBytes(path).SequenceEqual(oldBytes), "Malformed remote catalogue cannot replace the working cache: " + badIndex); }
        }
        foreach (var source in new[] { "../secret", "/absolute", "https://elsewhere.example/x", "folder\\file", "source/%2e%2e/file", "source/file?redirect=1", "source/file#fragment" })
        {
            var badIndex = JsonSerializer.Serialize(new { version = 1, services = new[] { new { id = "youtube", name = "YouTube", src = source } } });
            using var malformed = new HttpClient(new Handler((r, _) => Task.FromResult(Ok(r.RequestUri!.AbsolutePath.EndsWith("catalog.json") ? badIndex : Reply(r.RequestUri.AbsoluteUri)))));
            try { await new ServiceCatalog(path, malformed).RefreshAsync([], default); throw new Exception("Unsafe source accepted"); }
            catch (IOException) { assert(File.ReadAllBytes(path).SequenceEqual(oldBytes), "Database rejects unsafe source path: " + source); }
        }
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = new List<string>();
            using var delayed = new HttpClient(new Handler(async (r, token) =>
            {
                var url = r.RequestUri!.AbsoluteUri; requested.Add(url);
                if (url.Contains("git/ref/")) { entered.SetResult(); await release.Task.WaitAsync(token); }
                return Ok(Reply(url));
            }));
            var selected = new List<SiteRule> { new("Netflix", ["netflix.com"], [], "netflix") };
            var operation = new ServiceCatalog(path, delayed).RefreshAsync(selected, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); selected.Clear(); release.SetResult(); await operation;
            assert(requested.Any(r => r.EndsWith("netflix.txt")), "Editing selected sites while the database downloads cannot corrupt or alter its in-flight snapshot");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var stalled = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Ok(""); })))
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
            try { await new ServiceCatalog(path, stalled).RefreshAsync([], cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Cancelling a stalled database request leaves no partial cache"); }
        }
    }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
