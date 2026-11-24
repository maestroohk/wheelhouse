using System.Collections.Concurrent;
using System.Text.Json;

namespace Wheelhouse.Api;

/// <summary>
/// Tools that are installed as npm packages. On Windows their launchers are .cmd files, and passing free text (an
/// objective, a task title) through cmd.exe mangles quotes and lets characters like &amp; act as commands. So the
/// package's own script is run with node directly, and cmd is only the fallback when the package can't be found.
/// </summary>
public static class NodeTool
{
    static readonly ConcurrentDictionary<string, (string File, string[] Prefix)> cache = new();

    public static (string File, List<string> Args) Command(string package, params string[] args)
    {
        var (file, prefix) = cache.GetOrAdd(package, Resolve);
        var all = new List<string>(prefix);
        all.AddRange(args);
        return (file, all);
    }

    static (string, string[]) Resolve(string package)
    {
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "lib", "node_modules"),
                     "/usr/local/lib/node_modules",
                     "/usr/lib/node_modules",
                 })
        {
            try
            {
                var dir = Path.Combine(root, package);
                var manifest = Path.Combine(dir, "package.json");
                if (!File.Exists(manifest)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!doc.RootElement.TryGetProperty("bin", out var bin)) continue;
                var rel = bin.ValueKind == JsonValueKind.String ? bin.GetString()
                    : bin.TryGetProperty(package, out var named) ? named.GetString()
                    : bin.EnumerateObject().FirstOrDefault().Value.GetString();
                if (rel != null && File.Exists(Path.Combine(dir, rel))) return ("node", new[] { Path.Combine(dir, rel) });
            }
            catch { }
        }
        return OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/d", "/c", package }) : (package, Array.Empty<string>());
    }
}

public sealed class BacklogTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Kind { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Body { get; set; } = "";
    public string Created { get; set; } = "";
    public string Closed { get; set; } = "";
}

public record AddBacklogRequest(List<string> RepoIds, string Text, string? Kind);
public record StartBacklogRequest(string? Mode);

/// <summary>
/// The backlog: things to do in a repo later, noted without starting anything. It is kept by tasks-axi
/// (github.com/kunchenguid/tasks-axi) in one markdown file, so any other agent or tool that speaks tasks-axi sees and
/// changes the same list. Wheelhouse adds, starts, finishes and removes tasks through it and never edits the file.
/// </summary>
public sealed class Backlog
{
    static bool? available;

    private readonly AppPaths paths;
    private readonly MissionService missions;
    private readonly EventBus bus;
    private readonly ILogger<Backlog> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> settled = new();

    public Backlog(AppPaths paths, MissionService missions, EventBus bus, ILogger<Backlog> log)
    {
        this.paths = paths;
        this.missions = missions;
        this.bus = bus;
        this.log = log;
        // A task follows the mission it was started as: done when the work is kept, shipped or answered; back in the
        // queue when the work is thrown away.
        missions.Changed += m =>
        {
            if (m.BacklogId == null) return;
            var want = m.Status switch
            {
                MissionStatus.Kept or MissionStatus.Shipped => "done",
                MissionStatus.Done when m.Mode == MissionMode.Scout => "done",
                MissionStatus.Discarded => "reopen",
                _ => null,
            };
            if (want == null || settled.TryGetValue(m.Id, out var did) && did == want) return;
            settled[m.Id] = want;
            _ = Task.Run(async () =>
            {
                try { await RunAsync(new[] { want, m.BacklogId }, CancellationToken.None); await PublishAsync(); }
                catch (Exception ex) { log.LogWarning(ex, "Couldn't update backlog task {Task}", m.BacklogId); }
            });
        };
    }

    string FilePath => Path.Combine(paths.DataDir, "backlog.md");

    public static async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (available is { } known) return known;
        var (file, args) = NodeTool.Command("tasks-axi", "--version");
        var r = await Proc.RunAsync(file, args, null, TimeSpan.FromSeconds(30), ct);
        available = r.Ok;
        return r.Ok;
    }

    async Task<ProcResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        if (!await AvailableAsync(ct)) throw new WheelhouseException("The backlog uses tasks-axi, which isn't installed. Install it with: npm install -g tasks-axi");
        var all = args.ToList();
        all.Add("--file");
        all.Add(FilePath);
        var (file, full) = NodeTool.Command("tasks-axi", all.ToArray());
        await gate.WaitAsync(ct);
        try
        {
            var r = await Proc.RunAsync(file, full, paths.DataDir, TimeSpan.FromSeconds(40), ct);
            if (r.Ok) return r;
            var error = r.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("error:"))?[6..].Trim().Trim('"');
            throw new WheelhouseException(error ?? $"The backlog couldn't be changed: {Text.Clip(Text.Tail(r.Stdout + r.Stderr, 3), 300)}");
        }
        finally { gate.Release(); }
    }

    public async Task<List<BacklogTask>> ListAsync(CancellationToken ct)
    {
        if (!await AvailableAsync(ct) || !File.Exists(FilePath)) return new List<BacklogTask>();
        var r = await RunAsync(new[] { "list", "--fields", "body,created,closed", "--limit", "500" }, ct);
        var tasks = new List<BacklogTask>();
        string[]? fields = null;
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("tasks[") && line.Contains('{'))
            {
                fields = line[(line.IndexOf('{') + 1)..line.IndexOf('}')].Split(',');
                continue;
            }
            if (fields == null) continue;
            if (!line.StartsWith("  ")) { fields = null; continue; }
            var cells = Text.Csv(line.Trim());
            if (cells.Count < fields.Length) continue;
            string Cell(string name) { var i = Array.IndexOf(fields, name); var v = i >= 0 ? cells[i] : ""; return v == "-" ? "" : v; }
            tasks.Add(new BacklogTask { Id = Cell("id"), Title = Cell("title"), State = Cell("state"), Kind = Cell("kind"), Repo = Cell("repo"), Body = Cell("body"), Created = Cell("created"), Closed = Cell("closed") });
        }
        // What is still to do, newest first; finished tasks are kept by tasks-axi but not shown here.
        return tasks.Where(t => t.State != "done").OrderByDescending(t => t.State == "in_flight").ThenByDescending(t => t.Created).ToList();
    }

    public async Task PublishAsync()
    {
        try { bus.Publish("backlog", await ListAsync(CancellationToken.None)); } catch { }
    }

    public async Task<List<BacklogTask>> AddAsync(AddBacklogRequest req, CancellationToken ct)
    {
        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) throw new WheelhouseException("Type what to do first.");
        if (req.RepoIds == null || req.RepoIds.Count == 0) throw new WheelhouseException("Pick the repo this is for.");
        // The first line is the title; anything after it is kept as the task's notes.
        var lines = text.Split('\n', 2);
        var title = Text.Clip(lines[0].Trim(), 140);
        var body = lines.Length > 1 ? lines[1].Trim() : (lines[0].Trim().Length > 140 ? text : "");
        var kind = req.Kind == MissionMode.Scout ? "scout" : "ship";
        foreach (var repo in req.RepoIds.Distinct())
        {
            var args = new List<string> { "add", title, "--mint", "--repo", repo, "--kind", kind, "--json" };
            string? temp = null;
            if (body.Length > 0)
            {
                temp = Path.Combine(Path.GetTempPath(), $"wh-task-{Text.NewId()}.md");
                await File.WriteAllTextAsync(temp, body, ct);
                args.Add("--body-file");
                args.Add(temp);
            }
            try { await RunAsync(args, ct); }
            finally { if (temp != null) { try { File.Delete(temp); } catch { } } }
        }
        var all = await ListAsync(ct);
        bus.Publish("backlog", all);
        return all;
    }

    /// <summary>Starts a task as a mission in its repo and marks it in flight.</summary>
    public async Task<Mission> StartAsync(string id, string? mode, CancellationToken ct)
    {
        var task = (await ListAsync(ct)).FirstOrDefault(t => t.Id == id) ?? throw new WheelhouseException("That task isn't in the backlog any more.");
        if (task.Repo.Length == 0) throw new WheelhouseException("That task has no repo, so Wheelhouse doesn't know where to run it.");
        var chosen = MissionMode.IsValid(mode) ? mode! : task.Kind == "scout" ? MissionMode.Scout : MissionMode.Engineer;
        var instruction = task.Body.Length > 0 ? $"{task.Title}\n\n{task.Body}" : task.Title;
        var created = await missions.CreateAsync(new CreateMissionRequest(new List<string> { task.Repo }, instruction, chosen, null, task.Id), ct);
        if (task.State != "in_flight") await RunAsync(new[] { "start", id }, ct);
        await PublishAsync();
        return created[0];
    }

    public async Task<List<BacklogTask>> DoneAsync(string id, CancellationToken ct)
    {
        await RunAsync(new[] { "done", id }, ct);
        var all = await ListAsync(ct);
        bus.Publish("backlog", all);
        return all;
    }

    public async Task<List<BacklogTask>> RemoveAsync(string id, CancellationToken ct)
    {
        await RunAsync(new[] { "rm", id }, ct);
        var all = await ListAsync(ct);
        bus.Publish("backlog", all);
        return all;
    }
}
