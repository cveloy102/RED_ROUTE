using System.Net;
using System.Text.RegularExpressions;

namespace RedRoute.Core;

public sealed record ParsedProfile(Dictionary<string, string> Interface, Dictionary<string, string> Peer)
{
    public bool IsAmnezia => Interface.Keys.Any(k => ConfParser.AwgFields.Contains(k));
    public string[] Addresses => Interface["Address"].Split(',').Select(s => Domains.NormalizeCidr(s)).ToArray();
    public (string Host, int Port) Endpoint
    {
        get
        {
            var uri = new Uri("udp://" + Peer["Endpoint"]);
            return (uri.Host.Trim('[', ']'), uri.Port);
        }
    }
}
public static partial class ConfParser
{
    public static readonly HashSet<string> AwgFields = new(StringComparer.OrdinalIgnoreCase)
    { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies" };
    public static ParsedProfile Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 65536 || text.Contains('\0')) throw new FormatException("Пустой или слишком большой .conf.");
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        var seenInterface = false; var seenPeer = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('['))
            {
                if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase) && !seenInterface) { seenInterface = true; current = iface; }
                else if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase) && !seenPeer) { seenPeer = true; current = peer; }
                else throw new FormatException("Поддерживается один раздел Interface и один Peer в каждом файле.");
                continue;
            }
            var equal = line.IndexOf('=');
            if (current == null || equal < 1) throw new FormatException("Некорректная строка .conf.");
            var key = line[..equal].Trim(); var value = line[(equal + 1)..].Split('#')[0].Trim();
            var allowed = current == iface
                ? AwgFields.Contains(key) || new[] { "PrivateKey", "Address", "DNS", "MTU", "ListenPort" }.Contains(key, StringComparer.OrdinalIgnoreCase)
                : new[] { "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive" }.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (!allowed) throw new FormatException($"Параметр {key} не поддерживается. Скрипты из конфигурации не выполняются.");
            if (!current.TryAdd(key, value)) throw new FormatException($"Параметр {key} повторяется.");
        }
        foreach (var key in new[] { "PrivateKey", "Address" }) if (!iface.ContainsKey(key)) throw new FormatException($"В Interface отсутствует {key}.");
        foreach (var key in new[] { "PublicKey", "Endpoint", "AllowedIPs" }) if (!peer.ContainsKey(key)) throw new FormatException($"В Peer отсутствует {key}.");
        ValidateKey(iface["PrivateKey"]); ValidateKey(peer["PublicKey"]);
        if (peer.TryGetValue("PresharedKey", out var psk)) ValidateKey(psk);
        var parsed = new ParsedProfile(iface, peer);
        _ = parsed.Addresses;
        if (!Uri.TryCreate("udp://" + peer["Endpoint"], UriKind.Absolute, out var endpoint) || endpoint.Port is < 1 or > 65535 || endpoint.Host.Length == 0 || endpoint.AbsolutePath != "/" || endpoint.UserInfo != "" || endpoint.Query != "" || endpoint.Fragment != "")
            throw new FormatException("Некорректный Endpoint: нужен адрес:порт.");
        if (Uri.CheckHostName(endpoint.Host.Trim('[', ']')) == UriHostNameType.Unknown) throw new FormatException("Некорректный адрес сервера.");
        foreach (var range in peer["AllowedIPs"].Split(',')) _ = Domains.NormalizeCidr(range);
        foreach (var (key, value) in iface)
        {
            if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase)) ValidateNumber(value, 1280, 9000, key);
            else if (key.Equals("ListenPort", StringComparison.OrdinalIgnoreCase)) ValidateNumber(value, 0, 65535, key);
            else if (new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4" }.Contains(key, StringComparer.OrdinalIgnoreCase)) ValidateNumber(value, 0, 65535, key);
            else if (key.Length == 2 && key[0] is 'H' or 'h' && "1234".Contains(key[1]))
            {
                _ = ParseRange(value, key);
            }
            else if (key.Length == 2 && key[0] is 'I' or 'i' && "12345".Contains(key[1]) && (value.Length > 4096 || value.Any(char.IsControl))) throw new FormatException("Некорректный I-параметр.");
            else if (key.Equals("HeaderProtectionKey", StringComparison.OrdinalIgnoreCase)) ValidateKey(value);
            else if (key.Equals("RandomTrailers", StringComparison.OrdinalIgnoreCase) || key.Equals("DisableCookies", StringComparison.OrdinalIgnoreCase)) _ = ParseBoolean(value, key);
            else if (new[] { "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts" }.Contains(key, StringComparer.OrdinalIgnoreCase)) _ = ParseRange(value, key);
        }
        if (parsed.IsAmnezia)
        {
            var ranges = Enumerable.Range(1, 4).Select(i => iface.TryGetValue("H" + i, out var h) ? ParseRange(h, "H" + i) : ((uint)i, (uint)i)).ToArray();
            for (var i = 0; i < ranges.Length; i++)
            for (var j = i + 1; j < ranges.Length; j++)
                if (ranges[i].Item1 <= ranges[j].Item2 && ranges[j].Item1 <= ranges[i].Item2) throw new FormatException("Диапазоны H1–H4 не должны пересекаться.");
            if (iface.ContainsKey("HeaderProtectionKey") && Enumerable.Range(1, 4).Any(i => !iface.TryGetValue("S" + i, out var s) || int.Parse(s) < 12))
                throw new FormatException("Для HeaderProtectionKey каждый параметр S1–S4 должен быть не меньше 12.");
            var minimum = iface.TryGetValue("Jmin", out var jmin) ? int.Parse(jmin) : 0;
            var maximum = iface.TryGetValue("Jmax", out var jmax) ? int.Parse(jmax) : 0;
            if (minimum > maximum) throw new FormatException("Jmin не должен быть больше Jmax.");
        }
        if (peer.TryGetValue("PersistentKeepalive", out var keep)) _ = ParseKeepalive(keep, parsed.IsAmnezia);
        return parsed;
    }
    private static void ValidateKey(string value)
    {
        try { if (Convert.FromBase64String(value).Length != 32) throw new FormatException(); }
        catch (FormatException) { throw new FormatException("Ключ в .conf должен содержать 32 байта в Base64."); }
    }
    public static bool ParseBoolean(string value, string key) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "1" => true,
        "false" or "off" or "0" => false,
        _ => throw new FormatException($"Некорректный {key}: используйте on/off, true/false или 1/0.")
    };
    public static string ParseKeepalive(string value, bool amnezia)
    {
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return "0";
        if (amnezia && value.Contains('-'))
        {
            var (first, last) = ParseRange(value, "PersistentKeepalive");
            if (last > 65535) throw new FormatException("PersistentKeepalive должен быть от 0 до 65535 секунд.");
            return $"{first}-{last}";
        }
        ValidateNumber(value, 0, 65535, "PersistentKeepalive");
        return int.Parse(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void ValidateNumber(string value, int min, int max, string key)
    {
        if (!int.TryParse(value, out var n) || n < min || n > max) throw new FormatException($"Некорректный {key}.");
    }
    private static (uint, uint) ParseRange(string value, string key)
    {
        var bounds = value.Split('-');
        if (!HeaderValue().IsMatch(value) || !uint.TryParse(bounds[0], out var first)
            || !uint.TryParse(bounds[^1], out var last) || last < first)
            throw new FormatException($"Некорректный диапазон {key}: нужны числа от 0 до 4294967295 по возрастанию.");
        return (first, last);
    }
    [GeneratedRegex(@"^\d{1,10}(-\d{1,10})?$", RegexOptions.CultureInvariant)] private static partial Regex HeaderValue();
}
