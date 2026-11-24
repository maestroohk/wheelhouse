using System.Text.RegularExpressions;

namespace Wheelhouse.Api;

public sealed class ShipStep
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "";
    public int Findings { get; set; }
    public long DurationMs { get; set; }
}

public sealed class ShipFinding
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string File { get; set; } = "";
    public string Description { get; set; } = "";
    public string Action { get; set; } = "";
}

/// <summary>Where a mission's trip through the no-mistakes pipeline stands.</summary>
public sealed class ShipState
{
    public string? RunId { get; set; }
    public string? Outcome { get; set; }
    public string? Error { get; set; }
    public string? PrUrl { get; set; }
    /// <summary>The step waiting for a decision, when the pipeline is parked at a gate.</summary>
    public string? GateStep { get; set; }
    public List<ShipStep> Steps { get; set; } = new();
    public List<ShipFinding> Findings { get; set; } = new();
    public string Raw { get; set; } = "";

    public bool Pushed => Steps.Any(s => s.Step == "push" && s.Status == "completed");
}

/// <summary>
/// Shipping is done by no-mistakes (github.com/kunchenguid/no-mistakes): rebase, review, test, document, lint, push,
/// pull request, CI. Wheelhouse starts it with `no-mistakes axi run`, turns each gate into a Needs-you card, and
/// answers with `no-mistakes axi respond`. Nothing here reimplements the pipeline.
/// </summary>
public static partial class NoMistakes
{
    static bool? available;
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(90);

    public static async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (available is { } known) return known;
        var r = await Proc.RunAsync("no-mistakes", new[] { "--version" }, null, TimeSpan.FromSeconds(20), ct);
        available = r.Ok;
        return r.Ok;
    }

    /// <summary>Sets up the gate for a repo the first time it ships. Local only: a bare repo and a git remote.</summary>
    public static async Task<string?> EnsureInitAsync(string repo, CancellationToken ct)
    {
        var remotes = await Proc.Git(repo, ct, "remote");
        var names = remotes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!names.Contains("origin")) return "This repo has no 'origin' remote, so there is nowhere to push to.";
        if (names.Contains("no-mistakes")) return null;
        var init = await Proc.RunAsync("no-mistakes", new[] { "init" }, repo, TimeSpan.FromMinutes(2), ct);
        return init.Ok ? null : $"no-mistakes couldn't set up its gate: {Text.Clip(Text.Tail(Clean(init.Stdout + init.Stderr), 4), 300)}";
    }

    public static async Task<ShipState> RunAsync(string worktree, string intent, Action<string> onProgress, CancellationToken ct)
    {
        var r = await Proc.RunAsync("no-mistakes", new[] { "axi", "run", "--intent", intent }, worktree, Patience, ct, l => Progress(l, onProgress), null, null, l => Progress(l, onProgress));
        return Parse(r);
    }

    public static async Task<ShipState> RespondAsync(string worktree, string action, IEnumerable<string>? findings, string? instructions, Action<string> onProgress, CancellationToken ct)
    {
        var args = new List<string> { "axi", "respond", "--action", action };
        var ids = findings?.Where(f => f.Length > 0).ToList() ?? new();
        if (action == "fix" && ids.Count > 0) { args.Add("--findings"); args.Add(string.Join(',', ids)); }
        if (action == "fix" && !string.IsNullOrWhiteSpace(instructions)) { args.Add("--instructions"); args.Add(instructions!); }
        var r = await Proc.RunAsync("no-mistakes", args, worktree, Patience, ct, l => Progress(l, onProgress), null, null, l => Progress(l, onProgress));
        return Parse(r);
    }

    public static Task AbortAsync(string worktree) =>
        Proc.RunAsync("no-mistakes", new[] { "axi", "abort" }, worktree, TimeSpan.FromMinutes(1), CancellationToken.None);

    // Progress lines look like "  review: running".
    static void Progress(string line, Action<string> onProgress)
    {
        var m = ProgressLine().Match(Clean(line));
        if (m.Success && Label(m.Groups[1].Value) != m.Groups[1].Value) onProgress($"{Label(m.Groups[1].Value)}: {m.Groups[2].Value}");
    }

    public static string Label(string step) => step switch
    {
        "intent" => "Reading the goal",
        "rebase" => "Rebasing",
        "review" => "Code review",
        "test" => "Tests",
        "document" => "Docs",
        "lint" => "Lint",
        "push" => "Push",
        "pr" => "Pull request",
        "ci" => "CI",
        _ => step,
    };

    static ShipState Parse(ProcResult r)
    {
        var text = Clean(r.Stdout);
        var state = new ShipState { Raw = Text.Clip(Text.Tail(text, 80), 6000) };
        if (r.NotStarted) { state.Outcome = "failed"; state.Error = "no-mistakes isn't installed or couldn't be started."; return state; }
        if (r.TimedOut) { state.Outcome = "failed"; state.Error = "no-mistakes didn't reach a decision within 90 minutes."; return state; }

        var lines = text.Split('\n');
        string? table = null;
        var inGate = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var indented = line.StartsWith(' ');

            if (!indented)
            {
                table = null;
                inGate = trimmed.StartsWith("gate:");
                if (trimmed.StartsWith("outcome:")) state.Outcome = Unquote(trimmed[8..]);
                else if (trimmed.StartsWith("error:")) state.Error = Unquote(trimmed[6..]);
                continue;
            }
            var head = TableHead().Match(trimmed);
            if (head.Success) { table = head.Groups[1].Value; continue; }
            if (table == "steps")
            {
                var c = Text.Csv(trimmed);
                if (c.Count >= 4) state.Steps.Add(new ShipStep { Step = c[0], Status = c[1], Findings = int.TryParse(c[2], out var n) ? n : 0, DurationMs = long.TryParse(c[3], out var d) ? d : 0 });
                else table = null;
                continue;
            }
            if (table == "findings")
            {
                var c = Text.Csv(trimmed);
                if (c.Count >= 5) state.Findings.Add(new ShipFinding { Id = c[0], Severity = c[1], File = c[2], Description = c[3], Action = c[4] });
                else table = null;
                continue;
            }
            if (trimmed.StartsWith("id:") && state.RunId == null && !inGate) state.RunId = Unquote(trimmed[3..]);
            if (inGate && trimmed.StartsWith("step:")) state.GateStep = Unquote(trimmed[5..]);
        }
        if (text.Split('\n').Any(l => l.StartsWith("gate:")) && state.GateStep == null)
            state.GateStep = state.Steps.FirstOrDefault(s => s.Status is "awaiting_approval" or "awaiting" or "gated")?.Step ?? "review";
        var pr = PrUrl().Match(text);
        if (pr.Success) state.PrUrl = pr.Value;
        if (state.Outcome == null && state.GateStep == null && !r.Ok)
        {
            state.Outcome = "failed";
            state.Error ??= Text.Clip(Text.Tail(Clean(r.Stderr.Length > 0 ? r.Stderr : r.Stdout), 4), 400);
        }
        return state;
    }

    static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : s;
    }

    public static string Clean(string s) =>
        string.Join('\n', Ansi().Replace(s, "").Replace("\r", "").Split('\n').Where(l => !l.Contains("new version of", StringComparison.OrdinalIgnoreCase) && !l.Contains("\" to update")));

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"^\s+([a-z]+):\s+([a-z_]+)\s*$")]
    private static partial Regex ProgressLine();
    [GeneratedRegex(@"^([a-z_]+)\[\d+\]\{[^}]*\}:$")]
    private static partial Regex TableHead();
    [GeneratedRegex(@"https://github\.com/[\w.\-]+/[\w.\-]+/pull/\d+")]
    private static partial Regex PrUrl();
}
