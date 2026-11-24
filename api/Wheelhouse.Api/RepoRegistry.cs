namespace Wheelhouse.Api;

public record LogEntry(DateTimeOffset At, string Kind, string Text, string? Who, string? MissionId);

/// <summary>Finds every git repo under the projects folder and keeps a cheap status line for each.</summary>
public sealed class RepoRegistry
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", "packages", "dist", "out" };

    private readonly SettingsStore settings;
    private readonly AppPaths paths;
    private readonly EventBus bus;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<RepoInfo> cache = new();
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;
    private string cachedRoot = "";

    public RepoRegistry(SettingsStore settings, AppPaths paths, EventBus bus)
    {
        this.settings = settings;
        this.paths = paths;
        this.bus = bus;
    }

    public async Task<List<RepoInfo>> GetAsync(bool force, CancellationToken ct)
    {
        var s = settings.Get();
        if (!force && cachedRoot == s.ProjectsRoot && DateTimeOffset.Now - cachedAt < TimeSpan.FromSeconds(45))
            return WithPins(cache, s);
        await gate.WaitAsync(ct);
        try
        {
            if (!force && cachedRoot == s.ProjectsRoot && DateTimeOffset.Now - cachedAt < TimeSpan.FromSeconds(45))
                return WithPins(cache, s);
            var found = Discover(s.ProjectsRoot);
            var result = new RepoInfo[found.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, found.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
            {
                result[i] = await DescribeAsync(s.ProjectsRoot, found[i], token);
            });
            cache = result.ToList();
            cachedAt = DateTimeOffset.Now;
            cachedRoot = s.ProjectsRoot;
            var pinned = WithPins(cache, s);
            bus.Publish("repos", pinned);
            return pinned;
        }
        finally { gate.Release(); }
    }

    public async Task<RepoInfo?> FindAsync(string id, CancellationToken ct) =>
        (await GetAsync(false, ct)).FirstOrDefault(r => r.Id == id);

    public void Invalidate() => cachedAt = DateTimeOffset.MinValue;

    static List<RepoInfo> WithPins(List<RepoInfo> repos, Settings s) =>
        repos.Select(r => r with { Pinned = s.Pinned.Contains(r.Id) })
            .OrderByDescending(r => r.Pinned)
            .ThenByDescending(r => r.LastCommitAt ?? DateTimeOffset.MinValue)
            .ToList();

    private List<string> Discover(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        root = Path.GetFullPath(root);
        void Walk(string dir, int depth)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) { found.Add(dir); return; }
            if (depth >= 2) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { return; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                if (sub.StartsWith(paths.DataDir, StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    static async Task<RepoInfo> DescribeAsync(string root, string path, CancellationToken ct)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        var name = rel == "." ? Path.GetFileName(path) : rel;
        string? branch = null;
        int dirty = 0, ahead = 0, behind = 0;
        var upstream = false;
        var st = await Proc.Git(path, ct, "status", "--porcelain=v2", "--branch");
        if (st.Ok)
        {
            foreach (var line in st.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("# branch.head ")) branch = line[14..].Trim();
                else if (line.StartsWith("# branch.upstream ")) upstream = true;
                else if (line.StartsWith("# branch.ab "))
                {
                    var parts = line[12..].Trim().Split(' ');
                    if (parts.Length == 2) { int.TryParse(parts[0].TrimStart('+'), out ahead); int.TryParse(parts[1].TrimStart('-'), out behind); }
                }
                else if (!line.StartsWith('#')) dirty++;
            }
        }
        string? subject = null;
        DateTimeOffset? at = null;
        var log = await Proc.Git(path, ct, "log", "-1", "--format=%ct%x09%s");
        if (log.Ok)
        {
            var parts = log.Stdout.Trim().Split('\t', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], out var unix)) { at = DateTimeOffset.FromUnixTimeSeconds(unix); subject = parts[1]; }
        }
        return new RepoInfo(Text.Slug(name, 60), name, path, false, branch, dirty, ahead, behind, upstream, subject, at, Stacks(path));
    }

    static List<string> Stacks(string path)
    {
        var s = new List<string>();
        bool Any(string pattern, int depth = 2)
        {
            try { return Directory.EnumerateFiles(path, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true }).Any(f => !f.Contains("node_modules")); }
            catch { return false; }
        }
        if (Any("*.sln", 1) || Any("*.slnx", 1) || Any("*.csproj", 3)) s.Add(".NET");
        if (Any("package.json")) s.Add("Node");
        if (Any("pyproject.toml") || Any("requirements.txt")) s.Add("Python");
        return s;
    }

    public async Task<List<LogEntry>> CommitsAsync(RepoInfo repo, int count, CancellationToken ct)
    {
        var log = await Proc.Git(repo.Path, ct, "log", $"-{count}", "--format=%ct%x09%an%x09%s");
        var list = new List<LogEntry>();
        if (!log.Ok) return list;
        foreach (var line in log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('\t', 3);
            if (p.Length == 3 && long.TryParse(p[0], out var unix))
                list.Add(new LogEntry(DateTimeOffset.FromUnixTimeSeconds(unix), "commit", p[2].Trim(), p[1], null));
        }
        return list;
    }
}
