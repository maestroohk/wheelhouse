using Wheelhouse.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("WHEELHOUSE_URL") ?? "http://localhost:5299");

builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<RepoRegistry>();
if (Environment.GetEnvironmentVariable("WHEELHOUSE_AGENT") == "scripted")
    builder.Services.AddSingleton<IAgentRunner, ScriptedRunner>();
else
    builder.Services.AddSingleton<IAgentRunner, ClaudeCodeRunner>();
builder.Services.AddSingleton<MissionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MissionService>());
builder.Services.AddSingleton<Lavish>();
builder.Services.AddSingleton<Planner>();
builder.Services.AddSingleton<Playbooks>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Every WheelhouseException is a message meant for the person at the screen.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (WheelhouseException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

var api = app.MapGroup("/api");

api.MapGet("/health", () => new { ok = true, name = "wheelhouse", version = "0.1.0" });

api.MapGet("/tools", async (CancellationToken ct) => new { treehouse = await Treehouse.AvailableAsync(ct), noMistakes = await NoMistakes.AvailableAsync(ct), gnhf = await Gnhf.AvailableAsync(ct), lavish = await Lavish.AvailableAsync(ct) });

api.MapGet("/state", async (RepoRegistry repos, MissionService missions, SettingsStore settings, Planner planner, Playbooks playbooks, CancellationToken ct) => new
{
    repos = await repos.GetAsync(false, ct),
    missions = missions.Snapshot(),
    settings = settings.Get(),
    costToday = missions.CostToday(),
    plans = planner.Snapshot(),
    playbooks = playbooks.All(),
});

api.MapPost("/repos/refresh", async (RepoRegistry repos, CancellationToken ct) => await repos.GetAsync(true, ct));

api.MapPost("/repos/{id}/pin", async (string id, PinRequest req, SettingsStore settings, RepoRegistry repos, CancellationToken ct) =>
{
    settings.Update(s =>
    {
        s.Pinned.Remove(id);
        if (req.Pinned) s.Pinned.Add(id);
    });
    var list = await repos.GetAsync(false, ct);
    return list;
});

api.MapGet("/repos/{id}/logbook", async (string id, RepoRegistry repos, MissionService missions, CancellationToken ct) =>
{
    var repo = await repos.FindAsync(id, ct) ?? throw new WheelhouseException("That repo wasn't found.");
    var entries = await repos.CommitsAsync(repo, 12, ct);
    entries.AddRange(missions.All().Where(m => m.RepoId == id).Select(m => new LogEntry(
        m.FinishedAt ?? m.CreatedAt,
        "mission",
        m.Brief?.Headline is { Length: > 0 } h ? h : m.Title,
        m.Status,
        m.Id)));
    return new { repo, entries = entries.OrderByDescending(e => e.At).Take(20) };
});

api.MapPost("/missions", async (CreateMissionRequest req, MissionService missions, CancellationToken ct) => await missions.CreateAsync(req, ct));

api.MapPost("/missions/adopt", async (AdoptRequest req, MissionService missions, CancellationToken ct) => await missions.AdoptAsync(req, ct));
api.MapPost("/missions/{id}/checks", (string id, MissionService missions) => missions.Recheck(id));

api.MapGet("/missions/{id}", (string id, MissionService missions) => new { mission = MissionService.Snapshot(missions.Get(id)), events = missions.Events(id) });

api.MapGet("/missions/{id}/diff", async (string id, MissionService missions, CancellationToken ct) =>
{
    var (diff, clipped) = await missions.DiffAsync(id, ct);
    return new { diff, clipped };
});

api.MapPost("/missions/{id}/reply", (string id, ReplyRequest req, MissionService missions) => missions.Reply(id, req.Text));
api.MapPost("/missions/{id}/cancel", (string id, MissionService missions) => missions.Cancel(id));
api.MapPost("/missions/{id}/keep", async (string id, KeepRequest req, MissionService missions, CancellationToken ct) => await missions.KeepAsync(id, req.Message, ct));
api.MapPost("/missions/{id}/ship", async (string id, KeepRequest req, MissionService missions, CancellationToken ct) => await missions.ShipAsync(id, req.Message, ct));
api.MapPost("/missions/{id}/page", async (string id, Lavish lavish, CancellationToken ct) => new { url = await lavish.OpenAsync(id, ct) });
api.MapPost("/missions/{id}/discard", async (string id, MissionService missions, CancellationToken ct) => await missions.DiscardAsync(id, ct));
api.MapDelete("/missions/{id}", (string id, MissionService missions) => { missions.Archive(id); return new { ok = true }; });

api.MapPost("/plans", async (CreatePlanRequest req, Planner planner, CancellationToken ct) => await planner.CreateAsync(req, ct));
api.MapPost("/plans/{id}/revise", async (string id, RevisePlanRequest req, Planner planner, CancellationToken ct) => await planner.ReviseAsync(id, req.Text, ct));
api.MapPost("/plans/{id}/launch", async (string id, LaunchPlanRequest req, Planner planner, CancellationToken ct) => await planner.LaunchAsync(id, req, ct));
api.MapDelete("/plans/{id}", (string id, Planner planner) => { planner.Remove(id); return new { ok = true }; });

api.MapGet("/playbooks", (Playbooks playbooks) => playbooks.All());
api.MapPost("/playbooks", (SavePlaybookRequest req, Playbooks playbooks) => { playbooks.Save(req); return playbooks.All(); });
api.MapDelete("/playbooks/{id}", (string id, Playbooks playbooks) => { playbooks.Remove(id); return playbooks.All(); });

api.MapGet("/settings", (SettingsStore settings) => settings.Get());
api.MapPut("/settings", (Settings incoming, SettingsStore settings, RepoRegistry repos) =>
{
    if (!Directory.Exists(incoming.ProjectsRoot)) throw new WheelhouseException($"The folder {incoming.ProjectsRoot} doesn't exist.");
    var saved = settings.Update(s =>
    {
        s.ProjectsRoot = incoming.ProjectsRoot.Trim();
        s.ClaudePath = incoming.ClaudePath;
        s.Model = incoming.Model;
        s.MaxParallel = incoming.MaxParallel;
        s.MissionMinutes = incoming.MissionMinutes;
        s.RunChecks = incoming.RunChecks;
        s.UseTreehouse = incoming.UseTreehouse;
        s.FixAttempts = Math.Clamp(incoming.FixAttempts, 0, 5);
        s.NightIterations = Math.Clamp(incoming.NightIterations, 1, 200);
        s.NightHours = Math.Clamp(incoming.NightHours, 1, 24);
        s.NightAgent = string.IsNullOrWhiteSpace(incoming.NightAgent) ? "claude" : incoming.NightAgent.Trim();
    });
    repos.Invalidate();
    return saved;
});

api.MapGet("/stream", async (HttpContext ctx, EventBus bus) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var (reader, unsubscribe) = bus.Subscribe();
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(20));
        var beat = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
        var read = reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            var done = await Task.WhenAny(beat, read);
            if (done == beat)
            {
                await beat;
                await ctx.Response.WriteAsync(": beat\n\n", ctx.RequestAborted);
                beat = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
            }
            else
            {
                if (!await read) break;
                while (reader.TryRead(out var line))
                    await ctx.Response.WriteAsync($"data: {line}\n\n", ctx.RequestAborted);
                read = reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
            }
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally { unsubscribe(); }
});

app.Run();
