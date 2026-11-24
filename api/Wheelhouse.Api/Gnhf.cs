using System.Text.RegularExpressions;

namespace Wheelhouse.Api;

/// <param name="StopReason">gnhf's own closing tally, such as "3 iterations, 3 good, 0 failed".</param>
public record NightResult(string? Worktree, string Summary, string Notes, string? StopReason, bool Ok, string? Error);

/// <summary>
/// Unattended runs are done by gnhf (github.com/kunchenguid/gnhf): it loops an agent on one objective, commits each
/// successful iteration on its own gnhf/ branch in its own worktree, keeps notes between iterations and waits out
/// usage limits. Wheelhouse starts it, watches the iterations arrive, and takes the finished branch in for review.
/// </summary>
public static partial class Gnhf
{
    static bool? available;

    static (string File, List<string> Prefix) Command() =>
        OperatingSystem.IsWindows() ? ("cmd.exe", new List<string> { "/d", "/c", "gnhf" }) : ("gnhf", new List<string>());

    public static async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (available is { } known) return known;
        var (file, args) = Command();
        args.Add("--version");
        var r = await Proc.RunAsync(file, args, null, TimeSpan.FromSeconds(30), ct);
        available = r.Ok;
        return r.Ok;
    }

    /// <summary>gnhf --worktree puts each run in a folder beside the repo: &lt;repo&gt;-gnhf-worktrees/&lt;run&gt;.</summary>
    public static string WorktreesDir(string repo)
    {
        var full = Path.GetFullPath(repo).TrimEnd('\\', '/');
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(full) + "-gnhf-worktrees");
    }

    public static string? NewestWorktree(string repo, DateTime since)
    {
        var dir = WorktreesDir(repo);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir)
            .Select(d => new DirectoryInfo(d))
            .Where(d => d.CreationTimeUtc >= since.AddSeconds(-5) && (File.Exists(Path.Combine(d.FullName, ".git")) || Directory.Exists(Path.Combine(d.FullName, ".git"))))
            .OrderByDescending(d => d.CreationTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    static string? RunDir(string worktree)
    {
        var runs = Path.Combine(worktree, ".gnhf", "runs");
        return Directory.Exists(runs) ? Directory.EnumerateDirectories(runs).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault() : null;
    }

    /// <summary>Which iteration gnhf is on, read from the per-iteration files it keeps (.gnhf/runs/&lt;run&gt;/iteration-N.jsonl).</summary>
    public static int Iteration(string worktree)
    {
        var run = RunDir(worktree);
        if (run == null) return 0;
        try { return Directory.EnumerateFiles(run, "iteration-*.jsonl").Count(); } catch { return 0; }
    }

    public static async Task<NightResult> RunAsync(string repo, string objective, string agent, int maxIterations, int hours, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (file, args) = Command();
        args.AddRange(new[]
        {
            "--agent", string.IsNullOrWhiteSpace(agent) ? "claude" : agent.Trim(),
            "--worktree", "--max-iterations", maxIterations.ToString(), "--prevent-sleep", "on", "--meteor-frequency", "0", objective,
        });
        var r = await Proc.RunAsync(file, args, repo, TimeSpan.FromHours(hours), ct);
        if (r.NotStarted) return new NightResult(null, "", "", null, false, "gnhf isn't installed or couldn't be started.");

        var worktree = NewestWorktree(repo, started);
        // gnhf redraws a full-screen display the whole time; only its closing summary is plain text.
        var output = NoMistakes.Clean(r.Stdout);
        var tally = Tally().Match(output);
        var stats = tally.Success ? $"{tally.Groups[1].Value} iterations, {tally.Groups[2].Value} good, {tally.Groups[3].Value} failed" : null;

        var notes = "";
        if (worktree != null && RunDir(worktree) is { } run)
        {
            try { notes = await File.ReadAllTextAsync(Path.Combine(run, "notes.md"), CancellationToken.None); } catch { }
        }
        if (worktree != null)
        {
            // gnhf removes a worktree that has no commits; one that is still there with none means it was cut short.
            var ours = await Proc.Git(repo, CancellationToken.None, "rev-parse", "HEAD");
            var count = await Proc.Git(worktree, CancellationToken.None, "rev-list", "--count", $"{ours.Stdout.Trim()}..HEAD");
            if (!int.TryParse(count.Stdout.Trim(), out var n) || n == 0) worktree = null;
        }
        var error = r.TimedOut ? $"Stopped after {hours} hours."
            : worktree == null ? "gnhf ended without committing any step. Its agent may not be signed in, or every step failed."
            : null;
        return new NightResult(worktree, Text.Clip(Text.Tail(output, 14), 2000), Text.Clip(notes, 12000), stats, worktree != null, error);
    }

    [GeneratedRegex(@"iterations\s+(\d+)\s+total\s+(\d+)\s+good\s+(\d+)\s+failed")]
    private static partial Regex Tally();
}
