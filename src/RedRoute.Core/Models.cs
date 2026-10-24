using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedRoute.Core;

public enum RouteMode { Off, On, Auto }
public enum ConnectionPhase { Off, Connecting, Connected, Direct, Error }
public sealed record VpnProfile(Guid Id, string Name, string Config)
{
    // WPF automation peers and debugger/log formatting must never stringify private .conf data.
    public override string ToString() => $"VpnProfile {{ Id = {Id}, Name = {Name} }}";
}
public sealed record SiteRule(string Name, string[] Domains, string[] IpRanges, string? ServiceId = null);
public sealed class ClientSettings
{
    public RouteMode Mode { get; set; } = RouteMode.Off;
    public List<VpnProfile> Profiles { get; set; } = [];
    public List<SiteRule> Sites { get; set; } = [];
    public bool AutoFailover { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public DateTimeOffset? DatabaseUpdated { get; set; }
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Profiles == null || Sites == null || Profiles.Count > 20 || Sites.Count > 500) throw new FormatException("Некорректные профили или правила.");
        if (Profiles.Any(p => p == null) || Sites.Any(s => s == null || s.Name == null || s.Domains == null || s.IpRanges == null)) throw new FormatException("Пустой профиль или правило.");
        if (Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count) throw new FormatException("Профили повторяются.");
        foreach (var p in Profiles)
        {
            if (p.Name == null || p.Name.Length is < 1 or > 80 || p.Name.Any(char.IsControl)) throw new FormatException("Некорректное имя профиля.");
            _ = ConfParser.Parse(p.Config);
        }
        if (Sites.Sum(s => s.Domains.Length + s.IpRanges.Length) > 30000) throw new FormatException("Слишком большой список адресов.");
        foreach (var s in Sites)
        {
            if (s.Name.Length is < 1 or > 253) throw new FormatException("Некорректное правило.");
            foreach (var d in s.Domains) _ = Domains.Normalize(d);
            foreach (var ip in s.IpRanges) _ = Domains.NormalizeCidr(ip);
        }
    }
}
public sealed record EventEntry(DateTimeOffset Time, string Code, string Message);
public sealed record ProbeResult(string Target, bool Success, long Milliseconds, string Result);
public sealed record EngineStatus(RouteMode Mode, ConnectionPhase Phase, string? Profile, string Message,
    DateTimeOffset? LastSuccess, int ConsecutiveFailures, IReadOnlyList<EventEntry> Events, IReadOnlyList<ProbeResult>? Checks = null);
public sealed record ServiceRequest(string Command, ClientSettings? Settings = null);
public sealed record ServiceResponse(bool Success, string? Error = null, EngineStatus? Status = null);
public static class Protocol
{
    public const string PipeName = "RED_ROUTE.Control.v1";
    public const string ServiceName = "RED_ROUTE";
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}
