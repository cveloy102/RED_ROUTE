namespace RedRoute.Core;

public static class SettingsCommands
{
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
    public static ClientSettings UpdateRules(ClientSettings current, ClientSettings update) => new()
    {
        Mode = current.Mode, Profiles = [.. current.Profiles], AutoFailover = current.AutoFailover,
        StartWithWindows = current.StartWithWindows, Sites = [.. update.Sites], DatabaseUpdated = update.DatabaseUpdated
    };
}
