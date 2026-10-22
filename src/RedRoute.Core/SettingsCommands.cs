namespace RedRoute.Core;

public static class SettingsCommands
{
    public static ClientSettings UpdateRules(ClientSettings current, ClientSettings update) => new()
    {
        Mode = current.Mode, Profiles = [.. current.Profiles], AutoFailover = current.AutoFailover,
        StartWithWindows = current.StartWithWindows, Sites = [.. update.Sites], DatabaseUpdated = update.DatabaseUpdated
    };
}
