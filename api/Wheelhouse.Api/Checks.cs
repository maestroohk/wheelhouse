using System.Diagnostics;

namespace Wheelhouse.Api;

public record CheckPlan(string Name, string File, string[] Args, bool StopOnFail = true, string? Subdir = null);

/// <summary>Finds and runs a repo's own build and test commands so the mission card can say pass or fail as a fact.</summary>
public static class Checks
{
    /// <param name="dir">The folder to check (a mission's worktree).</param>
    /// <param name="home">The repo's own folder, where an existing Python virtualenv may live. Nothing is ever installed.</param>
    public static List<CheckPlan> Detect(string dir, string? home = null)
    {
        var plans = new List<CheckPlan>();
        var sln = Top(dir, "*.slnx").Concat(Top(dir, "*.sln")).FirstOrDefault();
        var projects = Deep(dir, "*.csproj");
        if (sln != null || projects.Count > 0)
        {
            var target = sln ?? (projects.Count == 1 ? projects[0] : null);
            if (target != null)
            {
                var rel = Path.GetRelativePath(dir, target);
                plans.Add(new CheckPlan("Build", "dotnet", new[] { "build", rel, "-nologo", "-v", "q", "-nodeReuse:false" }));
                var hasTests = projects.Any(p =>
                {
                    try { var t = File.ReadAllText(p); return t.Contains("Microsoft.NET.Test.Sdk") || t.Contains("xunit", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
                if (hasTests)
                    plans.Add(new CheckPlan("Tests", "dotnet", new[] { "test", rel, "--no-build", "-nologo", "-v", "q" }));
            }
        }
        // No solution and several projects: build each one.
        if (sln == null && projects.Count > 1)
            foreach (var project in projects.Take(4))
                plans.Add(new CheckPlan($"Build {Path.GetFileNameWithoutExtension(project)}", "dotnet",
                    new[] { "build", Path.GetRelativePath(dir, project), "-nologo", "-v", "q", "-nodeReuse:false" }, false));

        // TypeScript: only where packages are already installed in the folder being checked.
        foreach (var sub in new[] { "", "web", "app", "client", "frontend", "ui" })
        {
            var where = sub.Length == 0 ? dir : Path.Combine(dir, sub);
            if (File.Exists(Path.Combine(where, "tsconfig.json")) && File.Exists(Path.Combine(where, "node_modules", "typescript", "bin", "tsc")))
                plans.Add(new CheckPlan(sub.Length == 0 ? "Type check" : $"Type check {sub}", "node",
                    new[] { Path.Combine("node_modules", "typescript", "bin", "tsc"), "--noEmit" }, false, sub.Length == 0 ? null : sub));
        }

        // Python: only when the repo already has a virtualenv to run pytest with.
        foreach (var sub in new[] { "", "research", "engine" })
        {
            var where = sub.Length == 0 ? dir : Path.Combine(dir, sub);
            if (!File.Exists(Path.Combine(where, "pytest.ini"))) continue;
            var python = new[] { home, dir }.Where(h => h != null)
                .Select(h => Path.Combine(h!, ".venv", "Scripts", "python.exe"))
                .FirstOrDefault(File.Exists);
            if (python != null)
                plans.Add(new CheckPlan("Python tests", python, new[] { "-m", "pytest", "-q", "--no-header", "-p", "no:cacheprovider" }, false, sub.Length == 0 ? null : sub));
            break;
        }
        return plans;
    }

    public static async Task<CheckResult> RunAsync(CheckPlan plan, string dir, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var r = await Proc.RunAsync(plan.File, plan.Args, plan.Subdir == null ? dir : Path.Combine(dir, plan.Subdir), TimeSpan.FromMinutes(12), ct);
        var command = $"{Path.GetFileNameWithoutExtension(plan.File)} {string.Join(' ', plan.Args)}";
        var output = string.IsNullOrWhiteSpace(r.Stdout) ? r.Stderr : r.Stdout;
        var status = r.NotStarted ? "skipped" : r.Ok ? "passed" : "failed";
        var tail = r.TimedOut ? "Stopped after 12 minutes." : Text.Tail(output, r.Ok ? 4 : 30);
        return new CheckResult(plan.Name, command, status, Math.Round(sw.Elapsed.TotalSeconds, 1), Text.Clip(tail, 4000));
    }

    static IEnumerable<string> Top(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    static List<string> Deep(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Where(f => !f.Contains("node_modules") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();
        }
        catch { return new List<string>(); }
    }
}
