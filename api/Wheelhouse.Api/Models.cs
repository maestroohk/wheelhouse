namespace Wheelhouse.Api;

public record RepoInfo(
    string Id,
    string Name,
    string Path,
    bool Pinned,
    string? Branch,
    int Dirty,
    int Ahead,
    int Behind,
    bool HasUpstream,
    string? LastSubject,
    DateTimeOffset? LastCommitAt,
    List<string> Stacks);

public sealed class Question
{
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = new();
}

/// <summary>The fixed-shape report every mission ends in. This is what the developer reads instead of a transcript.</summary>
public sealed class Brief
{
    public string Headline { get; set; } = "";
    public List<string> Changes { get; set; } = new();
    public List<string> Verified { get; set; } = new();
    public List<string> Risks { get; set; } = new();
    public List<Question> Questions { get; set; } = new();
    public string Detail { get; set; } = "";
}

public record FileChange(string Path, int Added, int Removed);

public record CheckResult(string Name, string Command, string Status, double Seconds, string Tail);

public record Turn(string Role, string Text, DateTimeOffset At);

public record MissionEvent(DateTimeOffset At, string Kind, string Text);

public static class MissionStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Checking = "checking";
    public const string Shipping = "shipping";
    public const string Shipped = "shipped";
    public const string NeedsInput = "needs_input";
    public const string Review = "review";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Kept = "kept";
    public const string Discarded = "discarded";

    public static bool IsActive(string s) => s is Queued or Running or Checking or Shipping;
}

public static class MissionMode
{
    public const string Scout = "scout";       // read only, works in the repo itself
    public const string Builder = "builder";   // read + edit, isolated worktree
    public const string Engineer = "engineer"; // read + edit + build/test commands, isolated worktree

    public const string Night = "night";       // unattended: gnhf loops an agent and commits each step on its own branch

    public static bool IsValid(string? m) => m is Scout or Builder or Engineer or Night;
}

public sealed class Mission
{
    public string Id { get; set; } = "";
    public string? GroupId { get; set; }
    public string RepoId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Mode { get; set; } = MissionMode.Scout;
    public string Agent { get; set; } = "claude-code";
    public string Status { get; set; } = MissionStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? TurnStartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Branch { get; set; }
    public string? Worktree { get; set; }
    /// <summary>Where the folder came from: "treehouse" (leased from its pool) or "git" (a plain worktree).</summary>
    public string? Workspace { get; set; }
    public string? BaseBranch { get; set; }
    public string? BaseCommit { get; set; }
    public string? SessionId { get; set; }
    public string? PendingPrompt { get; set; }
    public string Activity { get; set; } = "";
    public int Steps { get; set; }
    public double CostUsd { get; set; }
    public double ActiveSeconds { get; set; }
    public Brief? Brief { get; set; }
    public List<FileChange> Files { get; set; } = new();
    public List<CheckResult> Checks { get; set; } = new();
    public List<Turn> Turns { get; set; } = new();
    public string? CommitSha { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }
    public ShipState? Ship { get; set; }
    public string? PageUrl { get; set; }
}

public record CreateMissionRequest(List<string> RepoIds, string Instruction, string? Mode);
public record ReplyRequest(string Text);
public record AdoptRequest(string RepoId, string? Title, string Worktree, string? Instruction, string? Mode, string? Agent, Brief? Brief);
public record KeepRequest(string? Message);
public record PinRequest(bool Pinned);

public sealed class Settings
{
    public string ProjectsRoot { get; set; } = "";
    public string ClaudePath { get; set; } = "claude";
    public string? Model { get; set; }
    public int MaxParallel { get; set; } = 3;
    public int MissionMinutes { get; set; } = 30;
    public bool RunChecks { get; set; } = true;
    public bool UseTreehouse { get; set; } = true;
    public int NightIterations { get; set; } = 8;
    public int NightHours { get; set; } = 8;
    /// <summary>The agent gnhf drives: claude, codex, copilot and so on, or acp:&lt;target or command&gt;.</summary>
    public string NightAgent { get; set; } = "claude";
    public List<string> Pinned { get; set; } = new();
}

public sealed class WheelhouseException(string message) : Exception(message);
