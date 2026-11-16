namespace RedRoute.Core;

public static class SettingsCommands
{
    // IPC input is deserialized JSON: a missing list must be a validation error, not a service crash.
    public static ClientSettings UpdateProfiles(ClientSettings current, ClientSettings update) => update.Profiles == null
        ? throw new FormatException("Нет списка профилей.")
        : new()
    {
        Mode = update.Profiles.Count == 0 ? RouteMode.Off : current.Mode,
        Profiles = [.. update.Profiles], AutoFailover = update.AutoFailover,
        StartWithWindows = update.StartWithWindows, Sites = [.. current.Sites], DatabaseUpdated = current.DatabaseUpdated
    };
    public static ClientSettings RefreshServices(ClientSettings current, Func<string, SiteRule?> resolve)
    {
        var result = UpdateRules(current, new ClientSettings
        {
            Sites = current.Sites.Select(old => old.ServiceId is { } id ? resolve(id) ?? old : old).ToList(),
            DatabaseUpdated = current.DatabaseUpdated
        });
        result.Validate();
        return result;
    }
    public static ClientSettings UpdateRules(ClientSettings current, ClientSettings update) => update.Sites == null
        ? throw new FormatException("Нет списка правил.")
        : new()
    {
        Mode = current.Mode, Profiles = [.. current.Profiles], AutoFailover = current.AutoFailover,
        StartWithWindows = current.StartWithWindows, Sites = [.. update.Sites], DatabaseUpdated = update.DatabaseUpdated
    };
}
