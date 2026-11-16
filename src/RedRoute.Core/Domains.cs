using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RedRoute.Core;

public static class Domains
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Введите домен или ссылку на сайт.");
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) throw new FormatException("Введите домен или ссылку на сайт.");
        if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new FormatException("Введите домен или ссылку http/https.");
        string host;
        try { host = new IdnMapping().GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new FormatException("Некорректный международный домен."); }
        if (IPAddress.TryParse(host, out _) || host.Length > 253 || !host.Contains('.')) throw new FormatException("Нужен домен сайта, например youtube.com.");
        foreach (var label in host.Split('.'))
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new FormatException("Некорректный домен.");
        return host;
    }
    public static string NormalizeCidr(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Введите IP-адрес или подсеть.");
        var parts = value.Trim().Split('/');
        // IPAddress.TryParse also accepts legacy IPv4 forms ("10.1" -> 10.0.0.1, "010.0.0.1" -> 8.0.0.1 as octal).
        // A rule must never silently target a different address than the one the user typed.
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)
            || ip.AddressFamily == AddressFamily.InterNetwork && !IsDottedDecimal(parts[0]))
            throw new FormatException("Некорректный IP-адрес или подсеть.");
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) throw new FormatException("IPv6-адрес с зоной интерфейса не поддерживается в правиле подсети.");
        var bits = max;
        if (parts.Length == 2 && (parts[1].Length is < 1 or > 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out bits) || bits > max))
            throw new FormatException("Некорректная маска подсети.");
        return $"{ip}/{bits}";
    }
    private static bool IsDottedDecimal(string value)
    {
        var octets = value.Split('.');
        return octets.Length == 4 && octets.All(o => o.Length is >= 1 and <= 3 && o.All(char.IsAsciiDigit)
            && (o.Length == 1 || o[0] != '0') && int.Parse(o, CultureInfo.InvariantCulture) <= 255);
    }
}
