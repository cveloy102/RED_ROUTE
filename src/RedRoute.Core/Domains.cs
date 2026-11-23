using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RedRoute.Core;

public static partial class Domains
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Введите домен или ссылку на сайт.");
        value = value.Trim();
        // "*.example.com" means the same as example.com: a rule always covers subdomains.
        if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
        else if (value.StartsWith('.')) value = value[1..];
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
        // IPAddress.TryParse also accepts legacy forms: "192.168.1" is 192.168.0.1 and "10" is 0.0.0.10.
        // Silently storing a different network is worse than rejecting the input.
        if (parts.Length > 2 || !(parts[0].Contains(':') || StrictIpv4().IsMatch(parts[0])) || !IPAddress.TryParse(parts[0], out var ip))
            throw new FormatException("Некорректный IP-адрес или подсеть. IPv4 записывается четырьмя числами, например 192.168.1.0/24.");
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) throw new FormatException("IPv6-адрес с зоной интерфейса не поддерживается в правиле подсети.");
        var bits = max;
        if (parts.Length == 2 && (!Digits().IsMatch(parts[1]) || !int.TryParse(parts[1], out bits) || bits < 0 || bits > max)) throw new FormatException("Некорректная маска подсети.");
        return $"{ip}/{bits}";
    }
    [System.Text.RegularExpressions.GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex StrictIpv4();
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Digits();
}
