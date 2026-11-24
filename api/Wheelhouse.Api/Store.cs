using System.Text.Json;
using System.Threading.Channels;

namespace Wheelhouse.Api;

public sealed class AppPaths
{
    public string DataDir { get; }
    public string MissionsDir => Path.Combine(DataDir, "missions");
    public string EventsDir => Path.Combine(DataDir, "events");
    public string WorktreesDir => Path.Combine(DataDir, "worktrees");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    public AppPaths()
    {
        DataDir = Environment.GetEnvironmentVariable("WHEELHOUSE_DATA")
            // The user profile, not AppData: packaged apps (the Claude desktop app among them) see a private copy of
            // AppData, so data written from an agent session there would be invisible to a normal terminal.
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wheelhouse");
        Directory.CreateDirectory(MissionsDir);
        Directory.CreateDirectory(EventsDir);
        Directory.CreateDirectory(WorktreesDir);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public static T? Read<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options) : default; }
        catch { return default; }
    }

    public static void Write<T>(string file, T value)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, file, true);
    }
}

public sealed class SettingsStore
{
    private readonly AppPaths paths;
    private readonly object gate = new();
    private Settings current;

    public SettingsStore(AppPaths paths)
    {
        this.paths = paths;
        current = Json.Read<Settings>(paths.SettingsFile) ?? new Settings();
        if (string.IsNullOrWhiteSpace(current.ProjectsRoot))
        {
            var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
            current.ProjectsRoot = Directory.Exists(guess) ? guess : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public Settings Get() { lock (gate) return current; }

    public Settings Update(Action<Settings> change)
    {
        lock (gate)
        {
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(current, Json.Options), Json.Options)!;
            change(copy);
            copy.MaxParallel = Math.Clamp(copy.MaxParallel, 1, 12);
            copy.MissionMinutes = Math.Clamp(copy.MissionMinutes, 2, 240);
            if (string.IsNullOrWhiteSpace(copy.ClaudePath)) copy.ClaudePath = "claude";
            if (string.IsNullOrWhiteSpace(copy.Model)) copy.Model = null;
            Json.Write(paths.SettingsFile, copy);
            current = copy;
            return copy;
        }
    }
}

/// <summary>Fan-out of server events to every open browser tab.</summary>
public sealed class EventBus
{
    private readonly List<Channel<string>> subs = new();

    public (ChannelReader<string> Reader, Action Unsubscribe) Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (subs) subs.Add(ch);
        return (ch.Reader, () => { lock (subs) subs.Remove(ch); ch.Writer.TryComplete(); });
    }

    public void Publish(string type, object payload)
    {
        var line = JsonSerializer.Serialize(new { type, payload }, Json.Wire);
        lock (subs)
            foreach (var s in subs) s.Writer.TryWrite(line);
    }
}
