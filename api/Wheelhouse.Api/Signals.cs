using System.Collections.Concurrent;
using System.Text.Json;

namespace Wheelhouse.Api;

public record QuotaWindow(string Label, int PercentLeft, DateTimeOffset? ResetsAt);

/// <summary>How much of the agent subscription is left, so ten agents aren't launched into an empty tank.</summary>
public record QuotaInfo(bool Known, string? Plan, int PercentLeft, string? LimitedBy, DateTimeOffset? ResetsAt, int? RunwaySeconds, List<QuotaWindow> Windows, string? Note);

public record PullRequest(string Repo, string? RepoId, int Number, string Title, string Url, string Branch, bool Draft, string Checks, int Failing, string Review, bool Mine);

public record PullRequests(bool Known, List<PullRequest> Items, string? Note, DateTimeOffset At);

/// <summary>
/// Outside facts worth a glance before starting work: the agent quota, read by quota-axi
/// (github.com/kunchenguid/quota-axi), and the developer's open pull requests with their CI and review state, read
/// by the GitHub CLI. Both are cached and both degrade to "not known" with a plain reason.
/// </summary>
public sealed class Signals
{
    private readonly RepoRegistry repos;
    private readonly ILogger<Signals> log;
    private readonly SemaphoreSlim quotaGate = new(1, 1), prGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> slugs = new();
    private QuotaInfo? quota;
    private DateTimeOffset quotaAt;
    private PullRequests? prs;

    public Signals(RepoRegistry repos, ILogger<Signals> log)
    {
        this.repos = repos;
        this.log = log;
    }

    // ---------- quota ----------

    public async Task<QuotaInfo> QuotaAsync(CancellationToken ct)
    {
        if (quota != null && DateTimeOffset.Now - quotaAt < TimeSpan.FromSeconds(90)) return quota;
        await quotaGate.WaitAsync(ct);
        try
        {
            if (quota != null && DateTimeOffset.Now - quotaAt < TimeSpan.FromSeconds(90)) return quota;
            quota = await ReadQuotaAsync(ct);
            quotaAt = DateTimeOffset.Now;
            return quota;
        }
        finally { quotaGate.Release(); }
    }

    static QuotaInfo Unknown(string note) => new(false, null, 0, null, null, null, new List<QuotaWindow>(), note);

    async Task<QuotaInfo> ReadQuotaAsync(CancellationToken ct)
    {
        var (file, args) = NodeTool.Command("quota-axi", "--json", "--provider", "claude");
        var r = await Proc.RunAsync(file, args, null, TimeSpan.FromSeconds(45), ct);
        if (r.NotStarted || !r.Stdout.TrimStart().StartsWith('{'))
            return Unknown("The quota gauge uses quota-axi. Install it with: npm install -g quota-axi");
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            var provider = doc.RootElement.GetProperty("providers").EnumerateArray().FirstOrDefault();
            if (provider.ValueKind != JsonValueKind.Object || provider.TryGetProperty("notSetUp", out var absent) && absent.ValueKind == JsonValueKind.True)
                return Unknown("The Claude CLI isn't signed in, so its quota can't be read.");

            DateTimeOffset? Date(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

            var windows = new List<QuotaWindow>();
            if (provider.TryGetProperty("windows", out var ws))
                foreach (var w in ws.EnumerateArray())
                    if (w.TryGetProperty("percentRemaining", out var p) && p.ValueKind == JsonValueKind.Number)
                        windows.Add(new QuotaWindow(w.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "", (int)Math.Round(p.GetDouble()), Date(w, "resetsAt")));
            if (windows.Count == 0) return Unknown("quota-axi found the Claude CLI but no quota figures for it.");

            var left = windows.Min(w => w.PercentLeft);
            var limiting = windows.OrderBy(w => w.PercentLeft).First();
            int? runway = null;
            if (provider.TryGetProperty("quotaSemantics", out var sem) && sem.TryGetProperty("effectiveAvailability", out var avail))
                foreach (var a in avail.EnumerateArray())
                {
                    if (!a.TryGetProperty("scope", out var scope) || scope.GetString() != "all_models") continue;
                    if (a.TryGetProperty("effectivePercentRemaining", out var e) && e.ValueKind == JsonValueKind.Number) left = (int)Math.Round(e.GetDouble());
                    if (a.TryGetProperty("limitingWindowIds", out var ids) && ids.GetArrayLength() > 0)
                    {
                        var id = ids[0].GetString();
                        foreach (var w in ws.EnumerateArray())
                            if (w.TryGetProperty("id", out var wid) && wid.GetString() == id)
                                limiting = new QuotaWindow(w.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "", left, Date(w, "resetsAt"));
                    }
                    if (a.TryGetProperty("runway", out var run) && run.TryGetProperty("usableRunwaySeconds", out var secs) && secs.ValueKind == JsonValueKind.Number)
                        runway = (int)secs.GetDouble();
                }
            return new QuotaInfo(true, provider.TryGetProperty("plan", out var plan) ? plan.GetString() : null, left, limiting.Label, limiting.ResetsAt, runway, windows, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't read the quota");
            return Unknown("The quota report couldn't be read.");
        }
    }

    // ---------- pull requests ----------

    public async Task<PullRequests> PullRequestsAsync(bool force, CancellationToken ct)
    {
        if (!force && prs != null && DateTimeOffset.Now - prs.At < TimeSpan.FromMinutes(4)) return prs;
        await prGate.WaitAsync(ct);
        try
        {
            if (!force && prs != null && DateTimeOffset.Now - prs.At < TimeSpan.FromMinutes(4)) return prs;
            prs = await ReadPullRequestsAsync(ct);
            return prs;
        }
        finally { prGate.Release(); }
    }

    async Task<PullRequests> ReadPullRequestsAsync(CancellationToken ct)
    {
        PullRequests Unknown(string note) => new(false, new List<PullRequest>(), note, DateTimeOffset.Now);
        const string fields = "number,title,url,repository,isDraft";
        var mine = await Proc.RunAsync("gh", new[] { "search", "prs", "--author", "@me", "--state", "open", "--json", fields, "--limit", "40" }, null, TimeSpan.FromSeconds(40), ct);
        if (mine.NotStarted) return Unknown("Pull requests are read with the GitHub CLI (gh), which isn't installed.");
        if (!mine.Ok)
            return Unknown((mine.Stderr + mine.Stdout).Contains("auth login", StringComparison.OrdinalIgnoreCase)
                ? "The GitHub CLI isn't signed in. Run: gh auth login"
                : "GitHub couldn't be reached just now.");
        var asked = await Proc.RunAsync("gh", new[] { "search", "prs", "--review-requested", "@me", "--state", "open", "--json", fields, "--limit", "40" }, null, TimeSpan.FromSeconds(40), ct);

        var fleet = await repos.GetAsync(false, ct);
        await Parallel.ForEachAsync(fleet.Where(r => !slugs.ContainsKey(r.Path)), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (r, token) =>
        {
            var origin = await Proc.Git(r.Path, token, "config", "--get", "remote.origin.url");
            slugs[r.Path] = Slug(origin.Stdout.Trim());
        });
        string? RepoId(string nameWithOwner) =>
            fleet.FirstOrDefault(r => slugs.TryGetValue(r.Path, out var s) && string.Equals(s, nameWithOwner, StringComparison.OrdinalIgnoreCase))?.Id;

        var found = new List<(JsonElement Pr, bool Mine)>();
        using var mineDoc = JsonDocument.Parse(mine.Stdout);
        foreach (var p in mineDoc.RootElement.EnumerateArray()) found.Add((p.Clone(), true));
        if (asked.Ok)
        {
            using var askedDoc = JsonDocument.Parse(asked.Stdout);
            foreach (var p in askedDoc.RootElement.EnumerateArray()) found.Add((p.Clone(), false));
        }

        var items = new ConcurrentBag<PullRequest>();
        await Parallel.ForEachAsync(found.Take(40), new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct }, async (f, token) =>
        {
            var url = f.Pr.GetProperty("url").GetString()!;
            var repo = f.Pr.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "";
            string branch = "", review = "", checks = "none";
            var failing = 0;
            var detail = await Proc.RunAsync("gh", new[] { "pr", "view", url, "--json", "headRefName,reviewDecision,statusCheckRollup" }, null, TimeSpan.FromSeconds(40), token);
            if (detail.Ok)
            {
                try
                {
                    using var d = JsonDocument.Parse(detail.Stdout);
                    branch = d.RootElement.GetProperty("headRefName").GetString() ?? "";
                    review = (d.RootElement.TryGetProperty("reviewDecision", out var rd) ? rd.GetString() : "") switch
                    {
                        "APPROVED" => "approved",
                        "CHANGES_REQUESTED" => "changes requested",
                        "REVIEW_REQUIRED" => "awaiting review",
                        _ => "",
                    };
                    int pending = 0, total = 0;
                    if (d.RootElement.TryGetProperty("statusCheckRollup", out var roll) && roll.ValueKind == JsonValueKind.Array)
                        foreach (var c in roll.EnumerateArray())
                        {
                            total++;
                            string? S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                            var outcome = S("conclusion") is { Length: > 0 } con ? con : S("state") ?? S("status") ?? "";
                            if (outcome is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE") failing++;
                            else if (outcome is "PENDING" or "QUEUED" or "IN_PROGRESS" or "EXPECTED" or "WAITING" or "") pending++;
                        }
                    checks = total == 0 ? "none" : failing > 0 ? "failing" : pending > 0 ? "running" : "passing";
                }
                catch { }
            }
            items.Add(new PullRequest(repo, RepoId(repo), f.Pr.GetProperty("number").GetInt32(), f.Pr.GetProperty("title").GetString() ?? "", url, branch,
                f.Pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True, checks, failing, review, f.Mine));
        });
        var ordered = items
            .OrderByDescending(p => p.Mine && (p.Checks == "failing" || p.Review == "changes requested"))
            .ThenByDescending(p => !p.Mine)
            .ThenBy(p => p.Repo).ThenByDescending(p => p.Number).ToList();
        return new PullRequests(true, ordered, null, DateTimeOffset.Now);
    }

    /// <summary>owner/name from a GitHub remote URL in either its https or its ssh form.</summary>
    static string? Slug(string url)
    {
        var at = url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = url[(at + "github.com".Length)..].TrimStart(':', '/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];
        return rest.Count(c => c == '/') == 1 ? rest : null;
    }
}
