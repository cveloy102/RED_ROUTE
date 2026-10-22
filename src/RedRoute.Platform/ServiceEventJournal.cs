using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public sealed class ServiceEventJournal
{
    private readonly string path;
    public ServiceEventJournal(string? directory = null) => path = Path.Combine(directory ?? SecureStore.ServiceDirectory, "connection-events.json");
    public IReadOnlyList<EventEntry> Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 512000) return [];
            return (JsonSerializer.Deserialize<EventEntry[]>(File.ReadAllText(path), Protocol.Json) ?? [])
                .Where(e => e != null && e.Code is { Length: < 80 } && e.Message is { Length: < 2048 }).TakeLast(150).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public void Save(IReadOnlyList<EventEntry> events)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(events.TakeLast(150), Protocol.Json));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logging must not interrupt VPN. */ }
    }
}
