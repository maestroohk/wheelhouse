using System.Text.Json;

namespace Wheelhouse.Api;

public record NightResult(string? Worktree, string Summary, string Notes, string? StopReason, bool Ok, string? Error);

/// <summary>
/// Unattended runs are done by gnhf (github.com/kunchenguid/gnhf): it loops an agent on one objective, commits each
/// successful iteration on its own gnhf/ branch in its own worktree, keeps notes between iterations and waits out
/// usage limits. Wheelhouse starts it, watches the commits arrive, and takes the finished branch in for review.
/// </summary>
public static class Gnhf
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

    /// <summary>gnhf --worktree puts each run in a folder beside the repo.</summary>
    public static string WorktreesDir(string repo) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(repo).TrimEnd('\\', '/'))!, Path.GetFileName(Path.GetFullPath(repo).TrimEnd('\\', '/')) + "-gnhf-worktrees");

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

    public static async Task<NightResult> RunAsync(string repo, string objective, int maxIterations, int hours, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (file, args) = Command();
        args.AddRange(new[] { "--agent", "claude", "--worktree", "--max-iterations", maxIterations.ToString(), "--prevent-sleep", "on", "--meteor-frequency", "0", objective });
        var r = await Proc.RunAsync(file, args, repo, TimeSpan.FromHours(hours), ct);
        var worktree = NewestWorktree(repo, started);
        var output = NoMistakes.Clean(r.Stdout);

        string notes = "", stop = "";
        if (worktree != null)
        {
            var runs = Path.Combine(worktree, ".gnhf", "runs");
            var run = Directory.Exists(runs) ? Directory.EnumerateDirectories(runs).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault() : null;
            if (run != null)
            {
                try { notes = File.ReadAllText(Path.Combine(run, "notes.md")); } catch { }
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "end-state.json")));
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Name.Contains("stop", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("status", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("error", StringComparison.OrdinalIgnoreCase))
                            if (p.Value.ValueKind is JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) stop += $"{p.Name}: {v}. ";
                }
                catch { }
            }
        }
        if (r.NotStarted) return new NightResult(null, "", "", null, false, "gnhf isn't installed or couldn't be started.");
        var error = worktree == null
            ? $"gnhf ended without leaving any commits. {Text.Clip(Text.Tail(r.Stderr.Length > 0 ? r.Stderr : output, 6), 500)}"
            : null;
        return new NightResult(worktree, Text.Clip(Text.Tail(output, 30), 3000), Text.Clip(notes, 12000), stop.Trim(), worktree != null, r.TimedOut ? $"Stopped after {hours} hours." : error);
    }
}
