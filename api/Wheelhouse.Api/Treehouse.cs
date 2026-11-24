namespace Wheelhouse.Api;

/// <summary>
/// Isolated working folders come from treehouse (github.com/kunchenguid/treehouse) when it is installed: a pool of
/// reusable git worktrees that arrive with dependencies already restored, and whose return step ends any process still
/// holding files open. Wheelhouse falls back to a plain `git worktree` only when treehouse isn't there.
/// </summary>
public static class Treehouse
{
    static bool? available;

    public static async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (available is { } known) return known;
        var r = await Proc.RunAsync("treehouse", new[] { "--version" }, null, TimeSpan.FromSeconds(15), ct);
        available = r.Ok;
        return r.Ok;
    }

    /// <summary>Leases a worktree for the repo and returns its folder, or null with the reason.</summary>
    public static async Task<(string? Path, string? Error)> LeaseAsync(string repo, string holder, CancellationToken ct)
    {
        // --lease prints only the folder on stdout; banners and setup output go to stderr.
        var r = await Proc.RunAsync("treehouse", new[] { "get", "--lease", "--lease-holder", holder }, repo, TimeSpan.FromMinutes(15), ct);
        var path = r.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (r.Ok && path != null && Directory.Exists(path)) return (Path.GetFullPath(path), null);
        return (null, Text.Clip(Text.Tail(r.Stderr.Length > 0 ? r.Stderr : r.Stdout, 6), 400));
    }

    public static async Task<bool> ReturnAsync(string repo, string worktree, CancellationToken ct)
    {
        var r = await Proc.RunAsync("treehouse", new[] { "return", worktree, "--force" }, repo, TimeSpan.FromMinutes(3), ct);
        return r.Ok;
    }
}
