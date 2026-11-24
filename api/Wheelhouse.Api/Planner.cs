using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wheelhouse.Api;

public sealed class PlanStep
{
    public string RepoId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string Mode { get; set; } = MissionMode.Engineer;
    /// <summary>What to do in this repo, in the planner's words. The developer can edit it before launching.</summary>
    public string Spec { get; set; } = "";
    public string OutOfScope { get; set; } = "";
    public string Why { get; set; } = "";
    public bool Include { get; set; } = true;
    public string? MissionId { get; set; }
}

public sealed class PlanSkip
{
    public string RepoId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string Why { get; set; } = "";
}

public static class PlanStatus
{
    public const string Drafting = "drafting";
    public const string Ready = "ready";
    public const string Launched = "launched";
    public const string Failed = "failed";
}

/// <summary>One goal that spans repos, split into one task per repo. Nothing runs until the developer launches it.</summary>
public sealed class Plan
{
    public string Id { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Status { get; set; } = PlanStatus.Drafting;
    public string Summary { get; set; } = "";
    public string? Question { get; set; }
    public string? Error { get; set; }
    public string Activity { get; set; } = "";
    public List<string> Candidates { get; set; } = new();
    public List<PlanStep> Steps { get; set; } = new();
    public List<PlanSkip> Skipped { get; set; } = new();
    public List<string> Revisions { get; set; } = new();
    public double CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public record CreatePlanRequest(string Goal, List<string>? RepoIds);
public record PlanStepEdit(string RepoId, bool Include, string? Mode, string? Spec);
public record LaunchPlanRequest(List<PlanStepEdit>? Steps);
public record RevisePlanRequest(string Text);

/// <summary>
/// The first mate: reads the candidate repos and turns one goal into a task per repo. Its rules follow the intake
/// rules of firstmate (github.com/kunchenguid/firstmate): keep the developer's ask as stated, one independently
/// checkable task per repo, investigate only where it is unclear what to build, and leave out repos the goal does
/// not touch. firstmate itself drives tmux on macOS and Linux, so its rules are reused here, not its scripts.
/// </summary>
public sealed partial class Planner
{
    const int MaxCandidates = 12;

    private readonly AppPaths paths;
    private readonly SettingsStore settings;
    private readonly RepoRegistry repos;
    private readonly MissionService missions;
    private readonly IAgentRunner runner;
    private readonly EventBus bus;
    private readonly ILogger<Planner> log;
    private readonly ConcurrentDictionary<string, Plan> plans = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> drafting = new();

    public Planner(AppPaths paths, SettingsStore settings, RepoRegistry repos, MissionService missions, IAgentRunner runner, EventBus bus, ILogger<Planner> log)
    {
        this.paths = paths;
        this.settings = settings;
        this.repos = repos;
        this.missions = missions;
        this.runner = runner;
        this.bus = bus;
        this.log = log;
        Directory.CreateDirectory(Dir);
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var p = Json.Read<Plan>(file);
            if (p == null || p.Id.Length == 0) continue;
            if (p.Status == PlanStatus.Drafting) { p.Status = PlanStatus.Failed; p.Error = "Wheelhouse was restarted while this plan was being drafted. Draft it again."; p.Activity = ""; }
            plans[p.Id] = p;
        }
    }

    string Dir => Path.Combine(paths.DataDir, "plans");

    public List<Plan> All() => plans.Values.OrderByDescending(p => p.CreatedAt).ToList();

    Plan Get(string id) => plans.TryGetValue(id, out var p) ? p : throw new WheelhouseException("That plan doesn't exist.");

    void Save(Plan p)
    {
        lock (p)
        {
            p.UpdatedAt = DateTimeOffset.Now;
            Json.Write(Path.Combine(Dir, $"{p.Id}.json"), p);
            bus.Publish("plan", p);
        }
    }

    // ---------- commands ----------

    public async Task<Plan> CreateAsync(CreatePlanRequest req, CancellationToken ct)
    {
        var goal = (req.Goal ?? "").Trim();
        if (goal.Length == 0) throw new WheelhouseException("Type the goal first.");
        var all = await repos.GetAsync(false, ct);
        List<RepoInfo> candidates;
        if (req.RepoIds is { Count: > 0 })
            candidates = all.Where(r => req.RepoIds.Contains(r.Id)).ToList();
        else
        {
            // No repos picked: consider the ones in play, which are the pinned ones and those touched in the last month.
            var month = DateTimeOffset.Now.AddDays(-30);
            candidates = all.Where(r => r.Pinned || r.Dirty > 0 || r.LastCommitAt >= month).ToList();
        }
        if (candidates.Count == 0) throw new WheelhouseException("There are no repos to plan across. Pick some in the fleet first.");
        if (candidates.Count > MaxCandidates)
            throw new WheelhouseException($"That is {candidates.Count} repos. Pick up to {MaxCandidates} in the fleet so the planner can read each one properly.");

        var plan = new Plan
        {
            Id = Text.NewId(),
            Goal = goal,
            Candidates = candidates.Select(r => r.Id).ToList(),
            CreatedAt = DateTimeOffset.Now,
            Activity = "Reading the repos",
        };
        plans[plan.Id] = plan;
        Save(plan);
        StartDraft(plan, candidates);
        return plan;
    }

    public async Task<Plan> ReviseAsync(string id, string text, CancellationToken ct)
    {
        var plan = Get(id);
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new WheelhouseException("Say what to change first.");
        if (plan.Status is PlanStatus.Drafting) throw new WheelhouseException("The planner is still working.");
        if (plan.Status is PlanStatus.Launched) throw new WheelhouseException("This plan has already been launched.");
        var all = await repos.GetAsync(false, ct);
        var candidates = all.Where(r => plan.Candidates.Contains(r.Id)).ToList();
        lock (plan) { plan.Revisions.Add(text); plan.Status = PlanStatus.Drafting; plan.Error = null; plan.Question = null; plan.Activity = "Revising the plan"; }
        Save(plan);
        StartDraft(plan, candidates);
        return plan;
    }

    public async Task<Plan> LaunchAsync(string id, LaunchPlanRequest req, CancellationToken ct)
    {
        var plan = Get(id);
        if (plan.Status != PlanStatus.Ready) throw new WheelhouseException("This plan isn't ready to launch.");
        lock (plan)
        {
            foreach (var edit in req.Steps ?? new())
            {
                var step = plan.Steps.FirstOrDefault(s => s.RepoId == edit.RepoId);
                if (step == null) continue;
                step.Include = edit.Include;
                if (MissionMode.IsValid(edit.Mode) && edit.Mode != MissionMode.Night) step.Mode = edit.Mode!;
                if (!string.IsNullOrWhiteSpace(edit.Spec)) step.Spec = edit.Spec!.Trim();
            }
        }
        var chosen = plan.Steps.Where(s => s.Include).ToList();
        if (chosen.Count == 0) throw new WheelhouseException("Tick at least one repo to launch.");
        foreach (var step in chosen)
        {
            var instruction = new StringBuilder(plan.Goal).Append("\n\nIn this repo: ").Append(step.Spec);
            if (step.OutOfScope.Length > 0) instruction.Append("\n\nLeave alone: ").Append(step.OutOfScope);
            var created = await missions.CreateAsync(new CreateMissionRequest(new List<string> { step.RepoId }, instruction.ToString(), step.Mode, plan.Id), ct);
            step.MissionId = created[0].Id;
        }
        lock (plan) { plan.Status = PlanStatus.Launched; plan.Activity = ""; }
        Save(plan);
        return plan;
    }

    public void Remove(string id)
    {
        Get(id);
        if (drafting.TryRemove(id, out var cts)) cts.Cancel();
        plans.TryRemove(id, out _);
        try { File.Delete(Path.Combine(Dir, $"{id}.json")); } catch { }
        bus.Publish("plan-removed", new { id });
    }

    // ---------- drafting ----------

    private void StartDraft(Plan plan, List<RepoInfo> candidates)
    {
        var cts = new CancellationTokenSource();
        if (drafting.TryRemove(plan.Id, out var old)) old.Cancel();
        drafting[plan.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var root = settings.Get().ProjectsRoot;
                var last = DateTimeOffset.MinValue;
                var result = await runner.RunAsync(
                    new AgentRequest(root, Prompt(plan, candidates, root), MissionMode.Scout, null, null, TimeSpan.FromMinutes(Math.Max(10, settings.Get().MissionMinutes)), Contract),
                    ev =>
                    {
                        lock (plan) plan.Activity = ev.Text;
                        if (DateTimeOffset.Now - last < TimeSpan.FromMilliseconds(700)) return;
                        last = DateTimeOffset.Now;
                        bus.Publish("plan", plan);
                    },
                    cts.Token);
                lock (plan) plan.CostUsd += result.CostUsd ?? 0;
                if (!result.Ok) { Fail(plan, result.Error ?? "The planner stopped without a plan."); return; }
                Apply(plan, result.Text, candidates);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Plan {Id} failed", plan.Id);
                Fail(plan, $"Something went wrong: {ex.Message}");
            }
            finally { drafting.TryRemove(plan.Id, out _); }
        });
    }

    void Fail(Plan plan, string error)
    {
        lock (plan) { plan.Status = PlanStatus.Failed; plan.Error = error; plan.Activity = ""; }
        Save(plan);
    }

    static string Prompt(Plan plan, List<RepoInfo> candidates, string root)
    {
        var sb = new StringBuilder();
        sb.Append("The developer's goal, in their own words:\n\n").Append(plan.Goal).Append("\n\n");
        sb.Append("Candidate repositories. Each is a folder under ").Append(root).Append(":\n");
        foreach (var r in candidates)
        {
            sb.Append($"- id: {r.Id} | folder: {r.Name} | stack: {(r.Stacks.Count > 0 ? string.Join(", ", r.Stacks) : "unknown")} | branch: {r.Branch ?? "none"}");
            if (r.LastSubject != null) sb.Append($" | last commit: \"{Text.Clip(r.LastSubject, 70)}\"");
            if (r.Dirty > 0) sb.Append($" | {r.Dirty} uncommitted files");
            sb.Append('\n');
        }
        if (plan.Steps.Count > 0 && plan.Revisions.Count > 0)
        {
            sb.Append("\nYour previous plan:\n");
            foreach (var s in plan.Steps) sb.Append($"- {s.RepoId} ({s.Mode}): {s.Spec}\n");
            foreach (var s in plan.Skipped) sb.Append($"- {s.RepoId}: skipped, {s.Why}\n");
            sb.Append("\nThe developer asked for these changes to the plan, oldest first:\n");
            foreach (var r in plan.Revisions) sb.Append("- ").Append(r).Append('\n');
            sb.Append("\nWrite the whole plan again with those changes applied.\n");
        }
        return sb.ToString();
    }

    void Apply(Plan plan, string text, List<RepoInfo> candidates)
    {
        var match = Fence().Matches(text).LastOrDefault();
        Draft? draft = null;
        if (match != null)
        {
            try { draft = JsonSerializer.Deserialize<Draft>(match.Groups[1].Value, Json.Options); } catch { }
        }
        if (draft == null) { Fail(plan, "The planner answered, but not with a plan Wheelhouse could read. Draft it again."); return; }

        RepoInfo? Find(string? key) =>
            key == null ? null : candidates.FirstOrDefault(r => string.Equals(r.Id, key.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, key.Trim(), StringComparison.OrdinalIgnoreCase));

        var steps = new List<PlanStep>();
        foreach (var s in draft.Steps ?? new())
        {
            var repo = Find(s.Repo);
            if (repo == null || string.IsNullOrWhiteSpace(s.Spec) || steps.Any(x => x.RepoId == repo.Id)) continue;
            steps.Add(new PlanStep
            {
                RepoId = repo.Id,
                RepoName = repo.Name,
                Mode = s.Mode is MissionMode.Scout or MissionMode.Builder or MissionMode.Engineer ? s.Mode : MissionMode.Engineer,
                Spec = s.Spec!.Trim(),
                OutOfScope = (s.OutOfScope ?? "").Trim(),
                Why = (s.Why ?? "").Trim(),
            });
        }
        var skipped = new List<PlanSkip>();
        foreach (var s in draft.Skipped ?? new())
        {
            var repo = Find(s.Repo);
            if (repo != null && steps.All(x => x.RepoId != repo.Id)) skipped.Add(new PlanSkip { RepoId = repo.Id, RepoName = repo.Name, Why = (s.Why ?? "").Trim() });
        }
        // A candidate the planner never mentioned is shown as skipped, so nothing disappears without a word.
        foreach (var r in candidates)
            if (steps.All(x => x.RepoId != r.Id) && skipped.All(x => x.RepoId != r.Id))
                skipped.Add(new PlanSkip { RepoId = r.Id, RepoName = r.Name, Why = "The planner didn't say." });

        lock (plan)
        {
            plan.Summary = Text.Clip((draft.Summary ?? "").Trim(), 400);
            plan.Question = string.IsNullOrWhiteSpace(draft.Question) ? null : draft.Question!.Trim();
            plan.Steps = steps;
            plan.Skipped = skipped;
            plan.Activity = "";
            plan.Error = null;
            plan.Status = PlanStatus.Ready;
            if (steps.Count == 0 && plan.Question == null) plan.Summary = plan.Summary.Length > 0 ? plan.Summary : "The planner found nothing to do for this goal in these repos.";
        }
        Save(plan);
    }

    sealed class Draft
    {
        public string? Summary { get; set; }
        public string? Question { get; set; }
        public List<DraftStep>? Steps { get; set; }
        public List<DraftStep>? Skipped { get; set; }
    }

    sealed class DraftStep
    {
        public string? Repo { get; set; }
        public string? Mode { get; set; }
        public string? Spec { get; set; }
        [JsonPropertyName("out_of_scope")] public string? OutOfScope { get; set; }
        public string? Why { get; set; }
    }

    [GeneratedRegex("```plan\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    public const string Contract = """
        You are the first mate for a developer who looks after many repositories through Wheelhouse. They give one goal; you read the candidate repositories and split the goal into one task per repository. Other agents will carry the tasks out, one agent per repository, in parallel, each seeing only its own repository and your task for it. You change nothing yourself and you can only read.

        For every candidate, read enough to decide: its README, its project files, and the code the goal touches. Then either write its task or skip it.

        Rules:
        - Keep the developer's ask as they stated it. Never widen it into a general goal, a clean-up, a consistency sweep or extra hardening they did not ask for.
        - One task per repository, and each task must be something that can be built and checked in that repository alone. If the goal needs one repository to change before another, say so in the later task's spec.
        - Skip a repository the goal does not apply to, and say why in one line. Skipping is normal.
        - Choose the mode for each task. "engineer" for a code change that should be built and tested, which is the usual case. "builder" for a change that needs no build, such as documentation. "scout" only where it is unclear what should be built there and someone has to investigate first; a scout changes nothing and reports back.
        - Write each spec for an agent that has not seen the other repositories: name the files and places you found, say what done looks like, and keep it under 120 words.
        - Say what the agent should leave alone in that repository when the ask is narrow.
        - If the goal is too unclear to plan at all, return no steps and one question for the developer.

        End your final message with this fenced block, containing valid JSON, with nothing after it:

        ```plan
        {"summary": "...", "question": "", "steps": [{"repo": "<id>", "mode": "engineer", "spec": "...", "out_of_scope": "...", "why": "..."}], "skipped": [{"repo": "<id>", "why": "..."}]}
        ```

        - summary: one or two plain sentences on how you split the goal.
        - question: empty unless you cannot plan without an answer.
        - repo: the id from the candidate list, exactly.
        - why: one line on what you found in that repository that makes the task necessary.
        """;
}
