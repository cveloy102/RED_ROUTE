using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using RedRoute.Core;
using RedRoute.Platform;

internal static class ReleaseValidationChecks
{
    public static async Task RunAsync(string config, Action<bool, string> assert)
    {
        var failures = new List<string>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); assert(true, name); }
            catch (Exception e) { failures.Add(name + ": " + e.GetType().Name); Console.WriteLine("FAIL: " + name + " (" + e.GetType().Name + ")"); }
        }
        void Require(bool value) { if (!value) throw new InvalidOperationException("Unexpected result"); }
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in new[]
        {
            ("null cached service", "{\"Services\":{\"broken\":null}}"),
            ("null cached service name", "{\"Services\":{\"broken\":{\"Name\":null,\"Domains\":[],\"IpRanges\":[]}}}"),
            ("null hidden domain", "{\"HiddenDomains\":[null],\"Services\":{}}"),
            ("truncated cached JSON", "{\"Services\":{")
        })
            await Check("Damaged catalogue cannot prevent startup: " + name, () =>
            {
                var path = Path.Combine(folder, "catalog.json"); File.WriteAllText(path, json);
                Require(new ServiceCatalog(path).Find("youtube.com")?.ServiceId == "youtube"); return Task.CompletedTask;
            });
        await Check("Domain lookup prefers the precise service over a broad category", () =>
        {
            Require(new ServiceCatalog(Path.Combine(folder, "missing.json")).Find("netflix.com")?.ServiceId == "netflix"); return Task.CompletedTask;
        });
        foreach (var json in new[] { "null", "[]", "{}", "{\"draft\":null}", "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v0.9.0\",\"assets\":null}" })
            await Check("Malformed update metadata is safely ignored: " + json, () =>
            {
                Require(GitHubUpdates.Parse(json, new Version(0, 1, 12)) == null); return Task.CompletedTask;
            });
        foreach (var field in new[] { "RandomTrailers", "randomtrailers", "DISABLECOOKIES" })
            await Check("Invalid AWG boolean is rejected during import: " + field, () =>
            {
                var invalid = config.Replace("Jc = 4", "Jc = 4\n" + field + " = invalid");
                try { ConfParser.Parse(invalid); } catch (FormatException) { return Task.CompletedTask; }
                throw new InvalidOperationException("Invalid profile accepted");
            });
        foreach (var header in new[] { "4294967296", "9999999999", "10-1", "0-4294967296" })
            await Check("Invalid AWG header range is rejected: " + header, () =>
            {
                try { ConfParser.Parse(config.Replace("H1 = 5", "H1 = " + header)); } catch (FormatException) { return Task.CompletedTask; }
                throw new InvalidOperationException("Invalid header accepted");
            });
        foreach (var size in new[] { int.MinValue, -1, 0, Protocol.MaximumMessageBytes + 1, int.MaxValue })
            await Check("IPC rejects invalid frame length " + size, async () =>
            {
                var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size);
                using var stream = new MemoryStream(header);
                try { await PipeProtocol.ReadAsync<ServiceRequest>(stream, default); } catch (IOException) { return; }
                throw new InvalidOperationException("Invalid frame accepted");
            });
        await Check("IPC reads a frame fragmented into single-byte packets", async () =>
        {
            using var serialized = new MemoryStream(); await PipeProtocol.WriteAsync(serialized, new ServiceRequest("status"), default);
            using var fragmented = new FragmentedStream(serialized.ToArray());
            Require((await PipeProtocol.ReadAsync<ServiceRequest>(fragmented, default)).Command == "status");
        });
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 3, 0 }, new byte[] { 10, 0, 0, 0, 123 } })
            await Check("IPC rejects truncated input of " + bytes.Length + " bytes", async () =>
            {
                using var input = new MemoryStream(bytes);
                try { await PipeProtocol.ReadAsync<ServiceRequest>(input, default); } catch (EndOfStreamException) { return; }
                throw new InvalidOperationException("Truncated message accepted");
            });
        foreach (var text in new[] { "null", "[]", "{", "{\"Command\":42}" })
            await Check("Malformed IPC JSON becomes a recoverable transport error", async () =>
            {
                var data = Encoding.UTF8.GetBytes(text); var bytes = new byte[data.Length + 4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, data.Length); data.CopyTo(bytes, 4);
                using var input = new MemoryStream(bytes);
                try { await PipeProtocol.ReadAsync<ServiceRequest>(input, default); } catch (IOException) { return; }
                throw new Exception("Invalid IPC accepted");
            });
        if (failures.Count > 0) throw new Exception("Release regressions: " + string.Join("; ", failures));
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
