using System.Text.RegularExpressions;

namespace RedRoute.Core;

/// <summary>An engine failure whose message was sanitized and may be shown to the user.</summary>
public sealed class EngineException(string message) : IOException(message);

public static partial class EngineLog
{
    /// <summary>
    /// Reduces a raw core log line to its error category, e.g. "start service: initialize inbound[1]".
    /// Addresses, hostnames and quoted values are removed: raw lines can contain destinations or keys.
    /// </summary>
    public static string? Summarize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = Ansi().Replace(line, "");
        var level = Level().Match(line);
        if (!level.Success) return null;
        var text = line[(level.Index + level.Length)..];
        text = Prefix().Replace(text, "");
        var parts = text.Split(": ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        text = string.Join(": ", parts.Take(3));
        text = Quoted().Replace(text, "\"…\"");
        text = Address().Replace(text, "…");
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 160) text = text[..160] + "…";
        return text.Length == 0 ? null : text;
    }
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")] private static partial Regex Ansi();
    [GeneratedRegex(@"\b(FATAL|ERROR)\b(\[\d+\])?", RegexOptions.CultureInvariant)] private static partial Regex Level();
    [GeneratedRegex(@"^[\s\]\[0-9:+\-T.]*", RegexOptions.CultureInvariant)] private static partial Regex Prefix();
    [GeneratedRegex("\"[^\"]*\"|'[^']*'", RegexOptions.CultureInvariant)] private static partial Regex Quoted();
    // IPv4, bracketed/plain IPv6 (with optional port) and dotted hostnames.
    [GeneratedRegex(@"\[?[0-9a-fA-F]{0,4}(:[0-9a-fA-F]{0,4}){2,}\]?(:\d+)?|\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b|\b([A-Za-z0-9-]+\.)+[A-Za-z]{2,}(:\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex Address();
}

/// <summary>Requires several consecutive IPv6 results that differ from the active policy before a rebuild.</summary>
public sealed class Ipv6ChangeDetector(int required = 3)
{
    private int mismatches;
    public void Reset() => mismatches = 0;
    public bool Observe(bool measured, bool active)
    {
        if (measured == active) { mismatches = 0; return false; }
        return ++mismatches >= required;
    }
}
