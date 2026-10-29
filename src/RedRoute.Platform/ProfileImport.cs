using System.Security.Cryptography;
using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public sealed record ProfileImportResult(IReadOnlyList<VpnProfile> Profiles, int Added, int Duplicates);

public static class ProfileImport
{
    public static async Task<ProfileImportResult> ReadAsync(ClientSettings current, IEnumerable<string> paths, CancellationToken token = default)
    {
        var profiles = current.Profiles.ToList();
        var identities = profiles.Select(p => Identity(ConfParser.Parse(p.Config))).ToHashSet(StringComparer.Ordinal);
        var added = 0; var duplicates = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 65536) throw new FormatException("Слишком большой .conf. Допустимо до 64 КБ.");
            var config = await File.ReadAllTextAsync(path, token);
            var parsed = ConfParser.Parse(config);
            if (!identities.Add(Identity(parsed))) { duplicates++; continue; }
            var name = Path.GetFileNameWithoutExtension(path).Trim();
            if (name.Length == 0) name = "Профиль";
            if (name.Length > 80) name = name[..80];
            profiles.Add(new(Guid.NewGuid(), name, config)); added++;
        }
        // Validate the complete result before changing or saving any existing settings.
        var candidate = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(current, Protocol.Json), Protocol.Json)!;
        candidate.Profiles = profiles; candidate.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, Protocol.Json);
        try { if (bytes.Length > Protocol.MaximumMessageBytes) throw new FormatException("Слишком большой объём настроек. Текущие профили сохранены без изменений."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new(profiles, added, duplicates);
    }
    private static string Identity(ParsedProfile parsed)
    {
        Dictionary<string, string> Normalize(Dictionary<string, string> values) => values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Key.Equals("RandomTrailers", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("DisableCookies", StringComparison.OrdinalIgnoreCase)
                ? ConfParser.ParseBoolean(p.Value, p.Key).ToString() : p.Value.Trim());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Interface = Normalize(parsed.Interface), Peer = Normalize(parsed.Peer) });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
