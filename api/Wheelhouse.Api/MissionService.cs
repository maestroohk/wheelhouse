using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wheelhouse.Api;

/// <summary>
/// Owns the life of a mission: queue, isolated worktree, agent turn, brief, checks, then keep or discard.
/// A mission is a conversation with one agent about one repo; every reply resumes the same agent session.
/// </summary>
public sealed class MissionService : IHostedService
{
    const int MaxDiff = 400_000;
    const int MaxEvents = 400;

    private readonly AppPaths paths;
    private readonly SettingsStore settings;
    private readonly RepoRegistry repos;
    private readonly EventBus bus;
    private readonly IAgentRunner runner;
    private readonly ILogger<MissionService> log;

    private readonly ConcurrentDictionary<string, Mission> missions = new();
    private readonly ConcurrentDictionary<string, List<MissionEvent>> events = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    private readonly ConcurrentDictionary<string, byte> dirty = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> repoGitLocks = new();
    private readonly object pumpGate = new();

    /// <summary>Raised each time a mission is saved, for parts of Wheelhouse that follow missions (the backlog).</summary>
    public event Action<Mission>? Changed;
    private readonly CancellationTokenSource stopping = new();
    private Task? flusher;

    public MissionService(AppPaths paths, SettingsStore settings, RepoRegistry repos, EventBus bus, IAgentRunner runner, ILogger<MissionService> log)
    {
        this.paths = paths;
        this.settings = settings;
        this.repos = repos;
        this.bus = bus;
        this.runner = runner;
        this.log = log;
    }

    // ---------- lifecycle ----------

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(paths.MissionsDir, "*.json"))
        {
            var m = Json.Read<Mission>(file);
            if (m == null || string.IsNullOrEmpty(m.Id)) continue;
            if (MissionStatus.IsActive(m.Status))
            {
                m.Status = MissionStatus.Failed;
                m.Error = "Wheelhouse was restarted while this was running. Send a follow-up to let the agent continue.";
                m.Activity = "";
                m.PendingPrompt = null;
                Json.Write(file, m);
            }
            missions[m.Id] = m;
        }
        flusher = Task.Run(FlushLoopAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        stopping.Cancel();
        foreach (var cts in running.Values) cts.Cancel();
        if (flusher != null) { try { await flusher; } catch { } }
        FlushAll();
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token)) FlushAll();
        }
        catch (OperationCanceledException) { }
    }

    private void FlushAll()
    {
        foreach (var id in dirty.Keys.ToList())
        {
            dirty.TryRemove(id, out _);
            if (!missions.TryGetValue(id, out var m)) continue;
            try
            {
                lock (m)
                {
                    m.UpdatedAt = DateTimeOffset.Now;
                    Json.Write(Path.Combine(paths.MissionsDir, $"{id}.json"), m);
                    bus.Publish("mission", m);
                    try { Changed?.Invoke(m); } catch { }
                }
                if (events.TryGetValue(id, out var ev))
                    lock (ev) Json.Write(Path.Combine(paths.EventsDir, $"{id}.json"), ev);
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not save mission {Id}", id); }
        }
    }

    private void Touch(Mission m) => dirty[m.Id] = 1;

    private void Note(Mission m, string kind, string text)
    {
        var list = events.GetOrAdd(m.Id, _ => LoadEvents(m.Id));
        lock (list)
        {
            list.Add(new MissionEvent(DateTimeOffset.Now, kind, text));
            if (list.Count > MaxEvents) list.RemoveRange(0, list.Count - MaxEvents);
        }
        Touch(m);
    }

    private List<MissionEvent> LoadEvents(string id) =>
        Json.Read<List<MissionEvent>>(Path.Combine(paths.EventsDir, $"{id}.json")) ?? new List<MissionEvent>();

    // ---------- queries ----------

    public List<Mission> All() => missions.Values.OrderByDescending(m => m.CreatedAt).ToList();

    /// <summary>
    /// Missions as they are right now, for sending to a browser. Agents change missions on other threads, so each one
    /// is copied under its own lock; handing the live objects to the serialiser can fail halfway through a list.
    /// </summary>
    public List<System.Text.Json.JsonElement> Snapshot() => All().Select(Snapshot).ToList();

    public static System.Text.Json.JsonElement Snapshot(Mission m)
    {
        lock (m) return System.Text.Json.JsonSerializer.SerializeToElement(m, Json.Wire);
    }

    public Mission Get(string id) => missions.TryGetValue(id, out var m) ? m : throw new WheelhouseException("That mission doesn't exist.");

    public List<MissionEvent> Events(string id)
    {
        var list = events.GetOrAdd(id, LoadEvents);
        lock (list) return list.ToList();
    }

    public double CostToday() => missions.Values.Where(m => m.CreatedAt.LocalDateTime.Date == DateTime.Today).Sum(m => m.CostUsd);

    // ---------- commands ----------

    public async Task<List<Mission>> CreateAsync(CreateMissionRequest req, CancellationToken ct)
    {
        var instruction = (req.Instruction ?? "").Trim();
        if (instruction.Length == 0) throw new WheelhouseException("Type an instruction first.");
        if (req.RepoIds == null || req.RepoIds.Count == 0) throw new WheelhouseException("Pick at least one repo.");
        var mode = MissionMode.IsValid(req.Mode) ? req.Mode! : MissionMode.Scout;
        var all = await repos.GetAsync(false, ct);
        var group = req.RepoIds.Count > 1 ? Text.NewId() : null;
        var created = new List<Mission>();
        foreach (var id in req.RepoIds.Distinct())
        {
            var repo = all.FirstOrDefault(r => r.Id == id) ?? throw new WheelhouseException($"Repo '{id}' wasn't found. Refresh the fleet.");
            var m = new Mission
            {
                Id = Text.NewId(),
                GroupId = group,
                PlanId = req.PlanId,
                BacklogId = req.BacklogId,
                RepoId = repo.Id,
                RepoName = repo.Name,
                RepoPath = repo.Path,
                Title = Text.Clip(instruction.Split('\n')[0].Trim(), 90),
                Mode = mode,
                Agent = runner.Id,
                Status = MissionStatus.Queued,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                PendingPrompt = instruction,
                Activity = "Waiting for a free slot",
            };
            m.Turns.Add(new Turn("you", instruction, DateTimeOffset.Now));
            missions[m.Id] = m;
            Note(m, "status", "Queued");
            created.Add(m);
        }
        FlushAll();
        Pump();
        return created;
    }

    public Mission Reply(string id, string text)
    {
        var m = Get(id);
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new WheelhouseException("Type a reply first.");
        lock (m)
        {
            if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("The agent is still working. Wait for it to finish, or cancel it.");
            if (m.Status is MissionStatus.Kept or MissionStatus.Discarded) throw new WheelhouseException("This mission is closed. Start a new one.");
            if (m.Mode != MissionMode.Scout && m.Worktree != null && !Directory.Exists(m.Worktree))
                throw new WheelhouseException("The mission's folder no longer exists. Start a new mission.");
            if (m.Ship?.GateStep != null && m.Status == MissionStatus.NeedsInput)
            {
                m.Turns.Add(new Turn("you", text, DateTimeOffset.Now));
                var answer = text;
                Note(m, "you", Text.Clip(text, 200));
                StartShip(m, (wt, progress, ct) => AnswerGate(m, wt, answer, progress, ct));
                return m;
            }
            m.Turns.Add(new Turn("you", text, DateTimeOffset.Now));
            m.PendingPrompt = text;
            m.Status = MissionStatus.Queued;
            m.Error = null;
            m.FinishedAt = null;
            m.Activity = "Waiting for a free slot";
        }
        Note(m, "you", Text.Clip(text, 200));
        FlushAll();
        Pump();
        return m;
    }

    public Mission Cancel(string id)
    {
        var m = Get(id);
        if (running.TryGetValue(id, out var cts)) cts.Cancel();
        else
            lock (m)
            {
                if (m.Status == MissionStatus.Queued)
                {
                    m.Status = MissionStatus.Cancelled;
                    m.PendingPrompt = null;
                    m.Activity = "";
                    m.FinishedAt = DateTimeOffset.Now;
                }
            }
        Note(m, "status", "Cancelled by you");
        FlushAll();
        return m;
    }

    public async Task<Mission> KeepAsync(string id, string? message, CancellationToken ct)
    {
        var m = Get(id);
        if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("The agent is still working.");
        if (m.Worktree == null || !Directory.Exists(m.Worktree)) throw new WheelhouseException("There is nothing to keep on this mission.");
        var msg = string.IsNullOrWhiteSpace(message) ? DefaultCommitMessage(m) : message!.Trim();
        var add = await Proc.Git(m.Worktree, ct, "add", "-A");
        if (!add.Ok) throw new WheelhouseException($"Couldn't stage the changes: {add.Stderr.Trim()}");
        await CommitIfNeededAsync(m, msg, ct);
        var sha = await Proc.Git(m.Worktree, ct, "rev-parse", "--short", "HEAD");
        var removed = await RemoveWorktreeAsync(m, ct);
        lock (m)
        {
            m.CommitSha = sha.Stdout.Trim();
            m.Status = MissionStatus.Kept;
            m.Note = removed
                ? $"Committed as {m.CommitSha} on {m.Branch}. Not pushed, not merged."
                : $"Committed as {m.CommitSha} on {m.Branch}. The working folder was in use and couldn't be deleted yet: {m.Worktree}";
            if (removed) m.Worktree = null;
        }
        Note(m, "status", $"Kept as {m.CommitSha} on {m.Branch}");
        FlushAll();
        return m;
    }

    public async Task<Mission> DiscardAsync(string id, CancellationToken ct)
    {
        var m = Get(id);
        if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("Cancel the mission before discarding it.");
        var removed = true;
        if (m.Worktree != null) removed = await RemoveWorktreeAsync(m, ct);
        if (m.Branch != null) await Proc.Git(m.RepoPath, ct, "branch", "-D", m.Branch);
        lock (m)
        {
            m.Status = MissionStatus.Discarded;
            m.Note = removed ? "Branch deleted and its folder released." : $"Branch deleted. The folder was in use and couldn't be deleted yet: {m.Worktree}";
            if (removed) m.Worktree = null;
        }
        Note(m, "status", "Discarded");
        FlushAll();
        return m;
    }

    public void Archive(string id)
    {
        var m = Get(id);
        if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("Cancel the mission first.");
        if (m.Worktree != null && Directory.Exists(m.Worktree)) throw new WheelhouseException("Keep or discard the changes before clearing this mission.");
        missions.TryRemove(id, out _);
        events.TryRemove(id, out _);
        try { File.Delete(Path.Combine(paths.MissionsDir, $"{id}.json")); File.Delete(Path.Combine(paths.EventsDir, $"{id}.json")); } catch { }
        bus.Publish("mission-removed", new { id });
    }

    public async Task<(string Diff, bool Clipped)> DiffAsync(string id, CancellationToken ct)
    {
        var m = Get(id);
        ProcResult r;
        if (m.Worktree != null && Directory.Exists(m.Worktree) && m.BaseCommit != null)
        {
            if (!MissionStatus.IsActive(m.Status)) await Proc.Git(m.Worktree, ct, "add", "-A");
            r = await Proc.Git(m.Worktree, ct, "diff", "--cached", m.BaseCommit);
        }
        else if (m.CommitSha != null && m.BaseCommit != null)
            r = await Proc.Git(m.RepoPath, ct, "diff", m.BaseCommit, m.CommitSha);
        else
            return ("", false);
        var diff = r.Stdout;
        return diff.Length > MaxDiff ? (diff[..MaxDiff], true) : (diff, false);
    }

    // ---------- the run ----------

    private void Pump()
    {
        lock (pumpGate)
        {
            var max = settings.Get().MaxParallel;
            foreach (var m in missions.Values.Where(x => x.Status == MissionStatus.Queued && x.PendingPrompt != null).OrderBy(x => x.UpdatedAt))
            {
                if (running.Count >= max) break;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                if (!running.TryAdd(m.Id, cts)) continue;
                string prompt;
                lock (m)
                {
                    prompt = m.PendingPrompt!;
                    m.PendingPrompt = null;
                    m.Status = MissionStatus.Running;
                    m.StartedAt ??= DateTimeOffset.Now;
                    m.TurnStartedAt = DateTimeOffset.Now;
                    m.Activity = "Starting";
                }
                Touch(m);
                _ = Task.Run(() => m.Mode == MissionMode.Night && m.Worktree == null ? RunNightAsync(m, prompt, cts) : RunTurnAsync(m, prompt, cts));
            }
        }
    }

    private async Task RunTurnAsync(Mission m, string prompt, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            if (m.Mode != MissionMode.Scout && m.Worktree == null)
                await PrepareWorktreeAsync(m, ct);
            var cwd = m.Worktree ?? m.RepoPath;
            if (!Directory.Exists(cwd)) throw new WheelhouseException($"{cwd} doesn't exist any more.");

            Note(m, "status", m.SessionId == null ? "Agent started" : "Agent resumed");
            // An agent with no saved conversation (work taken in from outside) is told what happened so far.
            if (m.SessionId == null && m.Turns.Count > 1)
            {
                var earlier = string.Join("\n\n", m.Turns.Take(m.Turns.Count - 1).Select(t => (t.Role == "you" ? "Developer: " : "Agent reported: ") + t.Text));
                var state = m.Files.Count > 0 ? "\n\nThe folder already holds uncommitted work for this task in: " + string.Join(", ", m.Files.Take(30).Select(f => f.Path)) + ". Read it before changing anything." : "";
                prompt = "Earlier in this task:\n" + earlier + state + "\n\nNow: " + prompt;
            }
            var res = await runner.RunAsync(
                new AgentRequest(cwd, prompt, m.Mode == MissionMode.Night ? MissionMode.Engineer : m.Mode, m.SessionId, null, TimeSpan.FromMinutes(settings.Get().MissionMinutes)),
                ev =>
                {
                    lock (m)
                    {
                        if (ev.Kind == "tool") m.Steps++;
                        m.Activity = ev.Text;
                    }
                    Note(m, ev.Kind, ev.Text);
                },
                ct);

            // The agent's saved conversation can be gone (cleared, or it never really started). Start a new one rather than fail.
            if (!res.Ok && m.SessionId != null && (res.Error ?? "").Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
            {
                Note(m, "status", "The earlier conversation is gone. Starting a new one.");
                lock (m) m.SessionId = null;
                var recap = string.Join("\n\n", m.Turns.Select(t => (t.Role == "you" ? "Developer: " : "You reported: ") + t.Text));
                res = await runner.RunAsync(
                    new AgentRequest(cwd, "Earlier in this task:\n" + recap + "\n\nNow: " + prompt, m.Mode, null, null, TimeSpan.FromMinutes(settings.Get().MissionMinutes)),
                    ev => { lock (m) { if (ev.Kind == "tool") m.Steps++; m.Activity = ev.Text; } Note(m, ev.Kind, ev.Text); },
                    ct);
            }

            lock (m)
            {
                m.SessionId = res.SessionId ?? m.SessionId;
                m.CostUsd += res.CostUsd ?? 0;
            }

            if (!res.Ok)
            {
                lock (m)
                {
                    m.Status = MissionStatus.Failed;
                    m.Error = res.Error;
                    if (!string.IsNullOrWhiteSpace(res.Text)) m.Brief = BriefParser.Parse(res.Text);
                }
                Note(m, "error", res.Error ?? "Failed");
                if (m.Worktree != null) await RefreshFilesAsync(m, CancellationToken.None);
                return;
            }

            var brief = BriefParser.Parse(res.Text);
            lock (m)
            {
                m.Brief = brief;
                m.Turns.Add(new Turn("agent", brief.Headline, DateTimeOffset.Now));
            }
            Note(m, "brief", brief.Headline);

            if (m.Worktree != null) await RefreshFilesAsync(m, ct);

            if (brief.Questions.Count > 0)
            {
                lock (m) m.Status = MissionStatus.NeedsInput;
                return;
            }
            if (m.Mode == MissionMode.Scout || m.Files.Count == 0)
            {
                lock (m)
                {
                    m.Status = MissionStatus.Done;
                    if (m.Mode != MissionMode.Scout) m.Note = "The agent finished without changing any files.";
                }
                return;
            }

            await CheckAndOfferAsync(m, ct);
            QueueFixIfNeeded(m);
        }
        catch (OperationCanceledException)
        {
            lock (m) m.Status = MissionStatus.Cancelled;
            if (m.Worktree != null) { try { await RefreshFilesAsync(m, CancellationToken.None); } catch { } }
        }
        catch (Exception ex)
        {
            lock (m) { m.Status = MissionStatus.Failed; m.Error = ex is WheelhouseException ? ex.Message : $"Something went wrong: {ex.Message}"; }
            Note(m, "error", m.Error!);
            if (ex is not WheelhouseException) log.LogError(ex, "Mission {Id} failed", m.Id);
        }
        finally
        {
            running.TryRemove(m.Id, out _);
            cts.Dispose();
            lock (m)
            {
                m.Activity = m.Status == MissionStatus.Queued ? "Waiting for a free slot" : "";
                m.TurnStartedAt = null;
                m.FinishedAt = m.Status == MissionStatus.Queued ? null : DateTimeOffset.Now;
                m.ActiveSeconds += sw.Elapsed.TotalSeconds;
            }
            Note(m, "status", m.Status);
            FlushAll();
            repos.Invalidate();
            Pump();
        }
    }

    /// <summary>
    /// Fix until green: a failed check goes back to the agent with the failure output, without waiting for the
    /// developer, up to the number of attempts in Settings. After that the mission is shown as it is.
    /// </summary>
    private void QueueFixIfNeeded(Mission m)
    {
        var limit = settings.Get().FixAttempts;
        var failed = m.Checks.FirstOrDefault(c => c.Status == "failed");
        if (failed == null || m.Status != MissionStatus.Review || m.FixRounds >= limit) return;
        var prompt =
            $"Wheelhouse ran this repository's own checks on your change and \"{failed.Name}\" failed. Fix the cause in your change; do not weaken or remove the check or the tests.\n\n" +
            $"Command: {failed.Command}\n\nThe end of its output:\n{failed.Tail}";
        lock (m)
        {
            m.FixRounds++;
            m.Turns.Add(new Turn("you", $"Automatic: {failed.Name} failed, so it went back to the agent (attempt {m.FixRounds} of {limit}).", DateTimeOffset.Now));
            m.PendingPrompt = prompt;
            m.Status = MissionStatus.Queued;
        }
        Note(m, "status", $"{failed.Name} failed. Sent back to the agent, attempt {m.FixRounds} of {limit}");
    }

    /// <summary>Runs the repo's own checks against the mission's folder, then puts the mission up for review.</summary>
    private async Task CheckAndOfferAsync(Mission m, CancellationToken ct)
    {
        if (settings.Get().RunChecks && m.Worktree != null)
        {
            var plans = Checks.Detect(m.Worktree, m.RepoPath);
            lock (m) { m.Status = MissionStatus.Checking; m.Checks = new(); }
            foreach (var plan in plans)
            {
                lock (m) m.Activity = $"Running {plan.Name.ToLowerInvariant()}";
                Note(m, "check", $"Running {plan.Name.ToLowerInvariant()}");
                var result = await Checks.RunAsync(plan, m.Worktree, ct);
                lock (m) m.Checks.Add(result);
                Note(m, "check", $"{plan.Name} {result.Status} in {result.Seconds:0}s");
                if (result.Status == "failed" && plan.StopOnFail) break;
            }
            await RefreshFilesAsync(m, ct);
        }
        lock (m) m.Status = MissionStatus.Review;
    }

    /// <summary>
    /// Takes in work that was done outside Wheelhouse (another agent, or you) in a git worktree of a fleet repo, so it
    /// gets the same checks, card, diff review and keep or discard as a mission started here.
    /// </summary>
    public async Task<Mission> AdoptAsync(AdoptRequest req, CancellationToken ct)
    {
        var repo = await repos.FindAsync(req.RepoId, ct) ?? throw new WheelhouseException($"Repo '{req.RepoId}' wasn't found. Refresh the fleet.");
        if (string.IsNullOrWhiteSpace(req.Worktree) || !Directory.Exists(req.Worktree)) throw new WheelhouseException("That folder doesn't exist.");
        var worktree = Path.GetFullPath(req.Worktree);
        if (missions.Values.Any(x => x.Worktree != null && string.Equals(Path.GetFullPath(x.Worktree), worktree, StringComparison.OrdinalIgnoreCase)))
            throw new WheelhouseException("That folder already belongs to a mission.");
        if (string.Equals(worktree, Path.GetFullPath(repo.Path), StringComparison.OrdinalIgnoreCase))
            throw new WheelhouseException("That is the repo's own working copy. Wheelhouse only takes in separate worktrees.");
        var common = await Proc.Git(worktree, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var own = await Proc.Git(repo.Path, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!common.Ok || !own.Ok || !string.Equals(Path.GetFullPath(common.Stdout.Trim()), Path.GetFullPath(own.Stdout.Trim()), StringComparison.OrdinalIgnoreCase))
            throw new WheelhouseException($"That folder isn't a worktree of {repo.Name}.");
        var branch = await Proc.Git(worktree, ct, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branch.Ok || branch.Stdout.Trim() == "HEAD") throw new WheelhouseException("That folder isn't on a branch.");
        var baseBranch = await Proc.Git(repo.Path, ct, "rev-parse", "--abbrev-ref", "HEAD");
        var mergeBase = await Proc.Git(repo.Path, ct, "merge-base", "HEAD", branch.Stdout.Trim());
        if (!mergeBase.Ok) throw new WheelhouseException("Couldn't find where that branch started from.");

        var title = Text.Clip((req.Title ?? "").Trim(), 90);
        if (title.Length == 0) title = branch.Stdout.Trim();
        var brief = req.Brief ?? new Brief { Headline = title };
        if (string.IsNullOrWhiteSpace(brief.Headline)) brief.Headline = title;
        brief.Changes ??= new();
        brief.Verified ??= new();
        brief.Risks ??= new();
        brief.Questions = new();
        brief.Detail ??= "";
        var who = string.IsNullOrWhiteSpace(req.Agent) ? "another agent" : req.Agent!.Trim();
        var m = new Mission
        {
            Id = Text.NewId(),
            RepoId = repo.Id,
            RepoName = repo.Name,
            RepoPath = repo.Path,
            Title = title,
            Mode = req.Mode is MissionMode.Builder or MissionMode.Engineer ? req.Mode : MissionMode.Engineer,
            Agent = who,
            Status = MissionStatus.Checking,
            CreatedAt = DateTimeOffset.Now,
            StartedAt = DateTimeOffset.Now,
            TurnStartedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            Branch = branch.Stdout.Trim(),
            Worktree = worktree,
            BaseBranch = baseBranch.Ok ? baseBranch.Stdout.Trim() : null,
            BaseCommit = mergeBase.Stdout.Trim(),
            Brief = brief,
            Activity = "Taking in the work",
            Note = $"Done outside Wheelhouse by {who} and taken in for review.",
        };
        m.Turns.Add(new Turn("you", string.IsNullOrWhiteSpace(req.Instruction) ? title : req.Instruction!.Trim(), DateTimeOffset.Now));
        m.Turns.Add(new Turn("agent", brief.Headline, DateTimeOffset.Now));
        missions[m.Id] = m;
        Note(m, "status", $"Taken in from {m.Branch}");
        FlushAll();
        StartChecks(m);
        return m;
    }

    public Mission Recheck(string id)
    {
        var m = Get(id);
        if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("The mission is busy.");
        if (m.Worktree == null || !Directory.Exists(m.Worktree)) throw new WheelhouseException("There is no folder to check on this mission.");
        lock (m) { m.Status = MissionStatus.Checking; m.TurnStartedAt = DateTimeOffset.Now; m.Activity = "Running checks"; }
        FlushAll();
        StartChecks(m);
        return m;
    }

    private void StartChecks(Mission m)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        if (!running.TryAdd(m.Id, cts)) { cts.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await RefreshFilesAsync(m, cts.Token);
                await CheckAndOfferAsync(m, cts.Token);
            }
            catch (OperationCanceledException) { lock (m) m.Status = MissionStatus.Review; }
            catch (Exception ex)
            {
                lock (m) { m.Status = MissionStatus.Failed; m.Error = $"The checks couldn't run: {ex.Message}"; }
                log.LogError(ex, "Checks failed to run for {Id}", m.Id);
            }
            finally
            {
                running.TryRemove(m.Id, out _);
                cts.Dispose();
                lock (m) { m.Activity = ""; m.TurnStartedAt = null; m.FinishedAt = DateTimeOffset.Now; m.ActiveSeconds += sw.Elapsed.TotalSeconds; }
                Note(m, "status", m.Status);
                FlushAll();
            }
        });
    }

    // ---------- ship (no-mistakes) ----------

    private async Task CommitIfNeededAsync(Mission m, string message, CancellationToken ct)
    {
        await Proc.Git(m.Worktree!, ct, "add", "-A");
        var staged = await Proc.Git(m.Worktree!, ct, "diff", "--cached", "--quiet");
        if (staged.ExitCode == 0) return; // nothing new to commit
        var commit = await Proc.Git(m.Worktree!, ct, "commit", "-m", message);
        if (!commit.Ok) throw new WheelhouseException($"Couldn't commit: {Text.Clip((commit.Stderr + commit.Stdout).Trim(), 400)}");
    }

    /// <summary>Commits the mission's work and sends it through no-mistakes: review, tests, push, pull request, CI.</summary>
    public async Task<Mission> ShipAsync(string id, string? message, CancellationToken ct)
    {
        var m = Get(id);
        if (MissionStatus.IsActive(m.Status)) throw new WheelhouseException("The mission is busy.");
        if (m.Worktree == null || !Directory.Exists(m.Worktree) || m.Branch == null) throw new WheelhouseException("There is nothing to ship on this mission.");
        if (!await NoMistakes.AvailableAsync(ct)) throw new WheelhouseException("Shipping uses no-mistakes, which isn't installed on this machine.");
        var problem = await NoMistakes.EnsureInitAsync(m.RepoPath, ct);
        if (problem != null) throw new WheelhouseException(problem);
        await CommitIfNeededAsync(m, string.IsNullOrWhiteSpace(message) ? DefaultCommitMessage(m) : message!.Trim(), ct);
        var sha = await Proc.Git(m.Worktree, ct, "rev-parse", "--short", "HEAD");
        lock (m) m.CommitSha = sha.Stdout.Trim();

        var asked = string.Join(" ", m.Turns.Where(t => t.Role == "you").Select(t => t.Text.Trim()));
        var decided = m.Brief is { } b ? $" What was done: {b.Headline}. {string.Join(" ", b.Changes)}" : "";
        var intent = Text.Clip(asked + decided, 3000);
        Note(m, "status", $"Shipping {m.Branch} through no-mistakes");
        StartShip(m, (wt, progress, token) => NoMistakes.RunAsync(wt, intent, progress, token));
        return m;
    }

    private Task<ShipState> AnswerGate(Mission m, string worktree, string answer, Action<string> progress, CancellationToken ct)
    {
        var a = answer.Trim();
        var ids = m.Ship?.Findings.Select(f => f.Id) ?? Enumerable.Empty<string>();
        if (a.StartsWith("Approve", StringComparison.OrdinalIgnoreCase)) return NoMistakes.RespondAsync(worktree, "approve", null, null, progress, ct);
        if (a.StartsWith("Skip", StringComparison.OrdinalIgnoreCase)) return NoMistakes.RespondAsync(worktree, "skip", null, null, progress, ct);
        if (a.StartsWith("Fix", StringComparison.OrdinalIgnoreCase)) return NoMistakes.RespondAsync(worktree, "fix", ids, null, progress, ct);
        // Anything else is guidance for how to fix what was found.
        return NoMistakes.RespondAsync(worktree, "fix", ids, a, progress, ct);
    }

    private void StartShip(Mission m, Func<string, Action<string>, CancellationToken, Task<ShipState>> step)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        if (!running.TryAdd(m.Id, cts)) { cts.Dispose(); throw new WheelhouseException("The mission is busy."); }
        lock (m)
        {
            m.Status = MissionStatus.Shipping;
            m.TurnStartedAt = DateTimeOffset.Now;
            m.FinishedAt = null;
            m.Error = null;
            m.Activity = "Starting the pipeline";
        }
        FlushAll();
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var worktree = m.Worktree!;
            try
            {
                var state = await step(worktree, text =>
                {
                    lock (m)
                    {
                        m.Activity = text;
                        // Keep the strip of steps current while the pipeline runs, not only when it stops.
                        var (name, status) = NoMistakes.FromProgress(text);
                        if (name != null && m.Ship?.Steps.FirstOrDefault(x => x.Step == name) is { } live) live.Status = status;
                    }
                    Note(m, "ship", text);
                }, cts.Token);
                lock (m) m.Ship = state;
                if (state.GateStep != null)
                {
                    var label = NoMistakes.Label(state.GateStep);
                    var top = string.Join(" ", state.Findings.Take(3).Select(f => $"[{f.Severity}] {f.Description}"));
                    lock (m)
                    {
                        m.Brief ??= new Brief { Headline = m.Title };
                        m.Brief.Questions = new List<Question>
                        {
                            new()
                            {
                                Text = Text.Clip($"{label} stopped with {state.Findings.Count} finding{(state.Findings.Count == 1 ? "" : "s")}. {top}", 600),
                                Options = new List<string> { "Approve and continue", "Fix these", "Skip this step" },
                            },
                        };
                        m.Status = MissionStatus.NeedsInput;
                    }
                    Note(m, "ship", $"Waiting for your decision at {label.ToLowerInvariant()}");
                }
                else if (state.Pushed)
                {
                    lock (m) { if (m.Brief != null) m.Brief.Questions = new(); }
                    var released = await RemoveWorktreeAsync(m, CancellationToken.None);
                    lock (m)
                    {
                        m.Status = MissionStatus.Shipped;
                        m.Note = state.PrUrl != null ? $"Pushed {m.Branch}. Pull request: {state.PrUrl}" : $"Pushed {m.Branch}.";
                        if (released) m.Worktree = null;
                    }
                    Note(m, "ship", m.Note!);
                }
                else
                {
                    var failed = state.Steps.FirstOrDefault(x => x.Status == "failed")?.Step;
                    lock (m)
                    {
                        if (m.Brief != null) m.Brief.Questions = new();
                        m.Status = MissionStatus.Review;
                        m.Error = failed != null
                            ? $"Shipping stopped at {NoMistakes.Label(failed).ToLowerInvariant()}: {state.Error ?? state.Outcome}. Nothing was pushed."
                            : $"Shipping ended without pushing: {state.Error ?? state.Outcome ?? "no outcome reported"}.";
                    }
                    Note(m, "error", m.Error!);
                }
            }
            catch (OperationCanceledException)
            {
                await NoMistakes.AbortAsync(worktree);
                lock (m) { m.Status = MissionStatus.Review; m.Note = "Shipping was cancelled. Nothing more was pushed."; }
            }
            catch (Exception ex)
            {
                lock (m) { m.Status = MissionStatus.Review; m.Error = $"Shipping couldn't run: {ex.Message}"; }
                log.LogError(ex, "Ship failed for {Id}", m.Id);
            }
            finally
            {
                running.TryRemove(m.Id, out _);
                cts.Dispose();
                lock (m) { m.Activity = ""; m.TurnStartedAt = null; m.FinishedAt = DateTimeOffset.Now; m.ActiveSeconds += sw.Elapsed.TotalSeconds; }
                Note(m, "status", m.Status);
                FlushAll();
                repos.Invalidate();
            }
        });
    }

    // ---------- unattended (gnhf) ----------

    private async Task RunNightAsync(Mission m, string objective, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var sw = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (!await Gnhf.AvailableAsync(ct)) throw new WheelhouseException("Unattended runs use gnhf, which isn't installed on this machine.");
            var head = await Proc.Git(m.RepoPath, ct, "rev-parse", "HEAD");
            if (!head.Ok) throw new WheelhouseException($"{m.RepoName} has no commits yet.");
            var baseBranch = await Proc.Git(m.RepoPath, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (baseBranch.Stdout.Trim().StartsWith("gnhf/")) throw new WheelhouseException("The repo is on a gnhf branch. Switch it back to your own branch first.");
            var s = settings.Get();
            lock (m) { m.BaseCommit = head.Stdout.Trim(); m.BaseBranch = baseBranch.Stdout.Trim(); m.Activity = "gnhf is starting"; }
            Note(m, "status", $"Unattended run started with gnhf: up to {s.NightIterations} iterations, {s.NightHours} hours");

            // gnhf draws a full-screen display, so progress is read from what it does to git instead.
            var watcher = Task.Run(async () =>
            {
                var seen = "";
                while (!watching.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(6), watching.Token); } catch { break; }
                    var wt = Gnhf.NewestWorktree(m.RepoPath, started);
                    if (wt == null) continue;
                    var iteration = Gnhf.Iteration(wt);
                    var count = await Proc.Git(wt, CancellationToken.None, "rev-list", "--count", $"{m.BaseCommit}..HEAD");
                    int.TryParse(count.Stdout.Trim(), out var n);
                    if ($"{iteration}/{n}" == seen) continue;
                    seen = $"{iteration}/{n}";
                    var last = n > 0 ? (await Proc.Git(wt, CancellationToken.None, "log", "-1", "--format=%s")).Stdout.Trim() : "";
                    lock (m)
                    {
                        m.Steps = n;
                        m.Activity = $"Step {Math.Max(iteration, 1)} of up to {s.NightIterations}" + (n > 0 ? $" · {n} committed · last: {Text.Clip(last, 80)}" : "");
                    }
                    Note(m, "tool", m.Activity);
                }
            });

            var result = await Gnhf.RunAsync(m.RepoPath, objective, s.NightAgent, s.NightIterations, s.NightHours, ct);
            watching.Cancel();
            await watcher;

            if (result.Worktree == null)
            {
                lock (m) { m.Status = MissionStatus.Failed; m.Error = result.Error ?? "gnhf ended without leaving any work."; }
                Note(m, "error", m.Error!);
                return;
            }
            var branch = await Proc.Git(result.Worktree, ct, "rev-parse", "--abbrev-ref", "HEAD");
            var commits = await Proc.Git(result.Worktree, ct, "log", "--reverse", "--format=%s", $"{m.BaseCommit}..HEAD");
            var subjects = commits.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            lock (m)
            {
                m.Worktree = result.Worktree;
                m.Workspace = "gnhf";
                m.Branch = branch.Stdout.Trim();
                m.Steps = subjects.Length;
                m.Brief = new Brief
                {
                    Headline = $"Unattended run finished with {subjects.Length} committed step{(subjects.Length == 1 ? "" : "s")}",
                    Changes = subjects.TakeLast(5).Select(x => Text.Clip(x, 110)).ToList(),
                    Verified = result.StopReason is { Length: > 0 } stats ? new List<string> { $"gnhf reports: {stats}" } : new List<string>(),
                    Risks = new List<string> { "No one watched this run. Read the diff before keeping it." },
                    Detail = result.Notes.Length > 0 ? result.Notes : result.Summary,
                };
                if (result.Error != null) m.Note = result.Error;
                m.Turns.Add(new Turn("agent", m.Brief.Headline, DateTimeOffset.Now));
            }
            Note(m, "brief", m.Brief!.Headline);
            await RefreshFilesAsync(m, ct);
            await CheckAndOfferAsync(m, ct);
        }
        catch (OperationCanceledException)
        {
            watching.Cancel();
            var wt = Gnhf.NewestWorktree(m.RepoPath, started);
            lock (m)
            {
                m.Status = MissionStatus.Cancelled;
                if (wt != null) { m.Worktree = wt; m.Workspace = "gnhf"; }
            }
            if (wt != null)
            {
                var branch = await Proc.Git(wt, CancellationToken.None, "rev-parse", "--abbrev-ref", "HEAD");
                lock (m) m.Branch = branch.Stdout.Trim();
                try { await RefreshFilesAsync(m, CancellationToken.None); } catch { }
            }
        }
        catch (Exception ex)
        {
            watching.Cancel();
            lock (m) { m.Status = MissionStatus.Failed; m.Error = ex is WheelhouseException ? ex.Message : $"Something went wrong: {ex.Message}"; }
            Note(m, "error", m.Error!);
            if (ex is not WheelhouseException) log.LogError(ex, "Unattended mission {Id} failed", m.Id);
        }
        finally
        {
            running.TryRemove(m.Id, out _);
            cts.Dispose();
            lock (m) { m.Activity = ""; m.TurnStartedAt = null; m.FinishedAt = DateTimeOffset.Now; m.ActiveSeconds += sw.Elapsed.TotalSeconds; }
            Note(m, "status", m.Status);
            FlushAll();
            repos.Invalidate();
            Pump();
        }
    }

    private async Task PrepareWorktreeAsync(Mission m, CancellationToken ct)
    {
        var gitLock = repoGitLocks.GetOrAdd(m.RepoPath, _ => new SemaphoreSlim(1, 1));
        await gitLock.WaitAsync(ct);
        try
        {
            var head = await Proc.Git(m.RepoPath, ct, "rev-parse", "HEAD");
            if (!head.Ok) throw new WheelhouseException($"{m.RepoName} has no commits yet, so there is nothing to branch from.");
            var baseBranch = await Proc.Git(m.RepoPath, ct, "rev-parse", "--abbrev-ref", "HEAD");
            var branch = $"wheelhouse/{Text.Slug(m.Title, 36)}-{m.Id[..4]}";
            string? worktree = null;
            var workspace = "git";
            lock (m) m.Activity = "Preparing an isolated branch";
            if (settings.Get().UseTreehouse && await Treehouse.AvailableAsync(ct))
            {
                Note(m, "status", "Leasing a ready-made folder from treehouse. Your working copy isn't touched.");
                var (leased, why) = await Treehouse.LeaseAsync(m.RepoPath, $"wheelhouse-{m.Id}", ct);
                if (leased != null)
                {
                    // Start from exactly what the repo has checked out now, then put the mission on its own branch.
                    var at = await Proc.Git(leased, ct, "checkout", "--detach", head.Stdout.Trim());
                    var sw = at.Ok ? await Proc.Git(leased, ct, "switch", "-c", branch) : at;
                    if (sw.Ok) { worktree = leased; workspace = "treehouse"; }
                    else
                    {
                        await Treehouse.ReturnAsync(m.RepoPath, leased, CancellationToken.None);
                        Note(m, "status", $"treehouse folder couldn't be put on {branch}; using a plain worktree instead.");
                    }
                }
                else Note(m, "status", $"treehouse couldn't lease a folder ({why}); using a plain worktree instead.");
            }
            if (worktree == null)
            {
                worktree = Path.Combine(paths.WorktreesDir, $"{Text.Slug(m.RepoName, 30)}-{m.Id}");
                Note(m, "status", $"Creating {branch} in a separate folder. Your working copy isn't touched.");
                var add = await Proc.Git(m.RepoPath, ct, "worktree", "add", "-b", branch, worktree, "HEAD");
                if (!add.Ok) throw new WheelhouseException($"Couldn't create the branch: {Text.Clip(add.Stderr.Trim(), 300)}");
            }
            lock (m)
            {
                m.Branch = branch;
                m.Worktree = worktree;
                m.Workspace = workspace;
                m.BaseCommit = head.Stdout.Trim();
                m.BaseBranch = baseBranch.Ok ? baseBranch.Stdout.Trim() : null;
            }
        }
        finally { gitLock.Release(); }
    }

    private async Task RefreshFilesAsync(Mission m, CancellationToken ct)
    {
        if (m.Worktree == null || m.BaseCommit == null || !Directory.Exists(m.Worktree)) return;
        await Proc.Git(m.Worktree, ct, "add", "-A");
        var stat = await Proc.Git(m.Worktree, ct, "diff", "--cached", "--numstat", m.BaseCommit);
        var files = new List<FileChange>();
        if (stat.Ok)
            foreach (var line in stat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t', 3);
                if (p.Length != 3) continue;
                int.TryParse(p[0], out var a);
                int.TryParse(p[1], out var r);
                files.Add(new FileChange(p[2].Trim(), a, r));
            }
        lock (m) m.Files = files;
        Touch(m);
    }

    private async Task<bool> RemoveWorktreeAsync(Mission m, CancellationToken ct)
    {
        if (m.Worktree == null) return true;
        // A treehouse folder goes back to its pool (treehouse ends anything still running in it and resets it).
        // The folder stays on disk for the next mission; the mission's branch and commits stay in the repo.
        if (m.Workspace == "treehouse")
            return await Treehouse.ReturnAsync(m.RepoPath, m.Worktree, ct);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!Directory.Exists(m.Worktree)) break;
            var r = await Proc.Git(m.RepoPath, ct, "worktree", "remove", "--force", m.Worktree);
            if (r.Ok) break;
            await Task.Delay(800, ct);
        }
        await Proc.Git(m.RepoPath, ct, "worktree", "prune");
        if (Directory.Exists(m.Worktree))
        {
            // git can drop the worktree and still leave build output behind. Once git no longer knows the folder,
            // what remains is only this mission's leftovers, so it is removed here.
            var list = await Proc.Git(m.RepoPath, ct, "worktree", "list", "--porcelain");
            var known = list.Stdout.Replace('\\', '/').Contains(Path.GetFullPath(m.Worktree).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            for (var attempt = 0; !known && attempt < 3 && Directory.Exists(m.Worktree); attempt++)
            {
                try { Directory.Delete(m.Worktree, true); }
                catch { await Task.Delay(700, ct); }
            }
        }
        return !Directory.Exists(m.Worktree);
    }

    static string DefaultCommitMessage(Mission m)
    {
        var subject = m.Brief?.Headline is { Length: > 0 } h ? h.TrimEnd('.') : m.Title;
        var body = m.Brief?.Changes.Count > 0 ? "\n\n" + string.Join('\n', m.Brief.Changes.Select(c => $"- {c}")) : "";
        return Text.Clip(subject, 72) + body;
    }
}
