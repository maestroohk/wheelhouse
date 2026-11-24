using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Markdig;

namespace Wheelhouse.Api;

/// <summary>
/// Long agent output is read and acted on in Lavish Editor (github.com/kunchenguid/lavish-axi) instead of as a wall of
/// text: Wheelhouse writes one review page per mission, Lavish opens it so the developer can point at any part of it
/// and leave a note or press a button, and what they send comes back here through `lavish-axi poll`.
/// </summary>
public sealed class Lavish
{
    const string KeepToken = "WHEELHOUSE:KEEP";
    const string ShipToken = "WHEELHOUSE:SHIP";
    const string AnswerToken = "WHEELHOUSE:ANSWER:";

    static bool? available;
    static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    private readonly AppPaths paths;
    private readonly MissionService missions;
    private readonly ILogger<Lavish> log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> listening = new();

    public Lavish(AppPaths paths, MissionService missions, ILogger<Lavish> log)
    {
        this.paths = paths;
        this.missions = missions;
        this.log = log;
    }

    static (string File, List<string> Args) Command(params string[] args) => NodeTool.Command("lavish-axi", args);

    public static async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (available is { } known) return known;
        var (file, args) = Command("--version");
        var r = await Proc.RunAsync(file, args, null, TimeSpan.FromSeconds(40), ct);
        available = r.Ok;
        return r.Ok;
    }

    /// <summary>Writes the mission's review page, opens it in Lavish and starts listening for what comes back.</summary>
    public async Task<string> OpenAsync(string id, CancellationToken ct)
    {
        var m = missions.Get(id);
        if (!await AvailableAsync(ct)) throw new WheelhouseException("Review pages use Lavish Editor (lavish-axi), which isn't installed on this machine.");
        var (diff, clipped) = await missions.DiffAsync(id, ct);
        var dir = Path.Combine(paths.DataDir, "pages", m.Id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "review.html");
        await File.WriteAllTextAsync(file, Page(m, diff, clipped), new UTF8Encoding(false), ct);

        var (exe, args) = Command(file, "--reopen");
        var open = await Proc.RunAsync(exe, args, dir, TimeSpan.FromMinutes(2), ct);
        var url = open.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("url:"))?[4..].Trim().Trim('"');
        if (!open.Ok || url == null) throw new WheelhouseException($"Lavish couldn't open the page: {Text.Clip(Text.Tail(open.Stdout + open.Stderr, 3), 300)}");

        if (listening.TryRemove(id, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        listening[id] = cts;
        _ = Task.Run(() => ListenAsync(id, file, cts.Token));
        return url;
    }

    private async Task ListenAsync(string id, string file, CancellationToken ct)
    {
        string? reply = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (exe, args) = string.IsNullOrEmpty(reply) ? Command("poll", file) : Command("poll", file, "--agent-reply", reply);
                reply = null;
                var r = await Proc.RunAsync(exe, args, Path.GetDirectoryName(file), TimeSpan.FromHours(12), ct);
                var text = r.Stdout;
                if (text.Contains("status: feedback"))
                {
                    // Lavish also reports its own layout findings this way. Only what the developer sent is acted on.
                    if (text.Contains("\nprompts") && !text.Contains("\nprompts: []")) reply = await HandleAsync(id, text);
                    if (text.Contains("session_ended: true")) break;
                    continue;
                }
                if (text.Contains("status: waiting")) continue;
                break; // ended, browser closed, no session, or an error: stop listening
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "Stopped listening to the review page for {Id}", id); }
        finally { listening.TryRemove(id, out _); }
    }

    /// <summary>Turns what the developer sent from the page into the matching Wheelhouse action.</summary>
    private async Task<string> HandleAsync(string id, string poll)
    {
        // Only the prompts are the developer's words; the rest of the response is Lavish talking to an agent.
        var start = poll.IndexOf("\nprompts", StringComparison.Ordinal);
        var end = start < 0 ? -1 : new[] { "\nlayout_warnings", "\nartifact_failures", "\nnext_step:", "\ndom_snapshot:" }
            .Select(k => poll.IndexOf(k, start + 1, StringComparison.Ordinal)).Where(i => i > start).DefaultIfEmpty(poll.Length).Min();
        var prompts = start >= 0 ? poll[start..end].Trim() : "";
        if (prompts.Length == 0) return "";
        try
        {
            if (prompts.Contains(ShipToken)) { await missions.ShipAsync(id, null, CancellationToken.None); return "Wheelhouse: shipping it now. Progress is on the mission card."; }
            if (prompts.Contains(KeepToken)) { await missions.KeepAsync(id, null, CancellationToken.None); return "Wheelhouse: kept and committed on the mission's branch. Nothing was pushed."; }
            var at = prompts.IndexOf(AnswerToken, StringComparison.Ordinal);
            if (at >= 0)
            {
                var answer = prompts[(at + AnswerToken.Length)..].Split('\n')[0].Trim().Trim('"', ',');
                missions.Reply(id, answer);
                return $"Wheelhouse: answered \"{Text.Clip(answer, 80)}\". The agent is carrying on.";
            }
            missions.Reply(id, "Notes from my review of your work, sent from the review page. Each one says which part of the page it is about:\n\n" + Text.Clip(prompts, 8000));
            return "Wheelhouse: your notes went to the agent. This page shows the earlier version; open it again from the mission when the agent is done.";
        }
        catch (WheelhouseException ex) { return $"Wheelhouse couldn't do that: {ex.Message}"; }
        catch (Exception ex) { log.LogWarning(ex, "Review page action failed for {Id}", id); return "Wheelhouse couldn't do that. Open the mission in Wheelhouse to see why."; }
    }

    // ---------- the page ----------

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    static string List(string cap, IEnumerable<string> items, string cls = "")
    {
        var list = items.ToList();
        if (list.Count == 0) return "";
        return $"<section><h2>{E(cap)}</h2><ul class=\"{cls}\">{string.Concat(list.Select(i => $"<li>{E(i)}</li>"))}</ul></section>";
    }

    static string Page(Mission m, string diff, bool clipped)
    {
        var b = m.Brief ?? new Brief { Headline = m.Title };
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{E(m.RepoName)} · {E(m.Title)}</title><style>{Css}</style></head><body><main>");
        sb.Append($"<header><p class=\"cap\">{E(m.RepoName)} · {E(m.Branch ?? m.Mode)} · {E(m.Status.Replace('_', ' '))}</p><h1>{E(b.Headline)}</h1><p class=\"asked\">You asked: {E(m.Turns.FirstOrDefault(t => t.Role == "you")?.Text ?? m.Title)}</p></header>");

        sb.Append("<section class=\"how\"><p>Click any line, paragraph or file below to leave a note on it, or select text. <b>Send to Agent</b> hands your notes to the agent as one follow-up. The buttons act straight away.</p></section>");

        if (b.Questions.Count > 0 && m.Status == MissionStatus.NeedsInput)
        {
            sb.Append("<section class=\"ask\"><h2>Needs your answer</h2>");
            foreach (var q in b.Questions)
            {
                sb.Append($"<p class=\"q\">{E(q.Text)}</p><div class=\"row\">");
                foreach (var o in q.Options) sb.Append($"<button type=\"button\" data-send=\"{E(AnswerToken + o)}\">{E(o)}</button>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        sb.Append(List(m.Mode == MissionMode.Scout ? "Found" : "Changed", b.Changes));
        if (m.Checks.Count > 0)
        {
            sb.Append("<section><h2>Checked by Wheelhouse</h2><div class=\"row\">");
            foreach (var c in m.Checks) sb.Append($"<span class=\"pill {E(c.Status)}\" title=\"{E(c.Command)}\">{E(c.Name)} {E(c.Status)}</span>");
            sb.Append("</div>");
            foreach (var c in m.Checks.Where(c => c.Status == "failed")) sb.Append($"<pre>{E(c.Tail)}</pre>");
            sb.Append("</section>");
        }
        sb.Append(List("Checked by the agent", b.Verified));
        sb.Append(List("Look at before accepting", b.Risks, "risk"));
        if (m.Error != null) sb.Append($"<section><h2>Problem</h2><p class=\"err\">{E(m.Error)}</p></section>");

        if (!string.IsNullOrWhiteSpace(b.Detail))
            sb.Append($"<section><h2>Agent's notes</h2><div class=\"notes\">{Markdig.Markdown.ToHtml(b.Detail, Markdown)}</div></section>");

        if (!string.IsNullOrWhiteSpace(diff))
        {
            sb.Append($"<section><h2>Changes · {m.Files.Count} file{(m.Files.Count == 1 ? "" : "s")}</h2>");
            foreach (var (path, lines) in SplitDiff(diff))
            {
                var stat = m.Files.FirstOrDefault(f => f.Path == path);
                sb.Append($"<details open class=\"file\"><summary><span>{E(path)}</span>{(stat != null ? $"<span class=\"stat\"><i class=\"add\">+{stat.Added}</i> <i class=\"del\">−{stat.Removed}</i></span>" : "")}</summary><div class=\"diff\">");
                foreach (var l in lines.Take(1500))
                {
                    var cls = l.StartsWith("@@") ? "hunk" : l.StartsWith('+') ? "add" : l.StartsWith('-') ? "del" : "";
                    sb.Append($"<div class=\"{cls}\">{E(l.Length == 0 ? " " : l)}</div>");
                }
                if (lines.Count > 1500) sb.Append("<div class=\"hunk\">… the rest of this file is cut here</div>");
                sb.Append("</div></details>");
            }
            if (clipped) sb.Append("<p class=\"asked\">The diff is very large and was cut short.</p>");
            sb.Append("</section>");
        }

        if (m.Worktree != null && m.Files.Count > 0 && !MissionStatus.IsActive(m.Status))
        {
            sb.Append("<section class=\"act\"><h2>Decide</h2><div class=\"row\">");
            sb.Append($"<button type=\"button\" class=\"go\" data-send=\"{KeepToken}\">Keep: commit on {E(m.Branch)}</button>");
            sb.Append($"<button type=\"button\" data-send=\"{ShipToken}\">Ship: review, test, push and open a pull request</button>");
            sb.Append("</div><p class=\"asked\">Keep pushes nothing. Ship pushes the branch to GitHub. To discard, use the mission card in Wheelhouse.</p></section>");
        }

        sb.Append("</main><script>").Append(Script).Append("</script></body></html>");
        return sb.ToString();
    }

    static List<(string Path, List<string> Lines)> SplitDiff(string diff)
    {
        var files = new List<(string, List<string>)>();
        List<string>? cur = null;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git "))
            {
                var at = line.LastIndexOf(" b/", StringComparison.Ordinal);
                cur = new List<string>();
                files.Add((at > 0 ? line[(at + 3)..] : line[11..], cur));
                continue;
            }
            if (cur == null) continue;
            if (line.StartsWith("index ") || line.StartsWith("--- ") || line.StartsWith("+++ ") || line.StartsWith("new file") || line.StartsWith("deleted file") || line.StartsWith("similarity") || line.StartsWith("rename ")) continue;
            cur.Add(line);
        }
        return files;
    }

    // A button sends its token through Lavish when the page is open there; opened as a plain file it says so instead.
    const string Script = """
        document.querySelectorAll('[data-send]').forEach(function (b) {
          b.addEventListener('click', function () {
            var row = b.parentElement;
            if (window.lavish && window.lavish.queuePrompt) {
              window.lavish.queuePrompt(b.getAttribute('data-send'));
              row.querySelectorAll('button').forEach(function (x) { x.disabled = true; });
              b.classList.add('chosen');
              var p = document.createElement('p'); p.className = 'asked'; p.textContent = 'Queued. Press Send to Agent to do it.'; row.after(p);
            } else {
              var n = document.createElement('p'); n.className = 'asked'; n.textContent = 'Open this page from the mission in Wheelhouse to use the buttons.'; row.after(n);
            }
          });
        });
        """;

    const string Css = """
        :root{--chart:#e8eef4;--sheet:#f7fafc;--ink:#10233a;--ink2:#44566b;--ink3:#74869d;--rule:#c9d5e2;--helm:#1d4fd0;--amber:#a15f06;--amberw:#f8ebd2;--go:#0c7a55;--gow:#d9efe5;--stop:#b3262e;--stopw:#f9dfe0;color-scheme:light}
        @media (prefers-color-scheme:dark){:root{--chart:#0a1420;--sheet:#101c2b;--ink:#dfe8f2;--ink2:#a3b3c6;--ink3:#6b7f97;--rule:#1f3146;--helm:#7ea6ff;--amber:#e7b057;--amberw:#2f240e;--go:#5fcba2;--gow:#11301f;--stop:#ff8086;--stopw:#3a1a1d;color-scheme:dark}}
        *{box-sizing:border-box}body{margin:0;background:var(--chart);color:var(--ink);font:15px/1.6 "Segoe UI",system-ui,sans-serif}
        main{max-width:960px;margin:0 auto;padding:32px 20px 80px;display:grid;gap:26px;min-width:0}
        h1{font-size:clamp(1.5rem,3.4vw,2.1rem);line-height:1.3;margin:6px 0;padding-bottom:2px;overflow:visible;letter-spacing:-.015em;text-wrap:balance}
        h2{font:600 11.5px "Cascadia Mono",Consolas,monospace;letter-spacing:.09em;text-transform:uppercase;color:var(--ink3);margin:0 0 8px}
        section{min-width:0}p{margin:0}.cap{font:500 12px "Cascadia Mono",Consolas,monospace;color:var(--ink3);letter-spacing:.04em}
        .asked{color:var(--ink2);font-size:13.5px;margin-top:6px}.how{background:var(--sheet);border:1px solid var(--rule);border-radius:5px;padding:12px 14px;color:var(--ink2);font-size:13.5px}
        ul{margin:0;padding-left:20px;display:grid;gap:4px}ul.risk li{color:var(--amber)}
        .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
        button{font:inherit;font-weight:600;color:var(--ink);background:var(--sheet);border:1px solid var(--ink3);border-radius:5px;padding:8px 14px;cursor:pointer}
        button:hover{border-color:var(--ink)}button.go{background:var(--go);border-color:var(--go);color:var(--sheet)}button:disabled{opacity:.45;cursor:default}button.chosen{opacity:1;outline:2px solid var(--helm)}
        .ask{background:var(--amberw);border-radius:5px;padding:14px 16px}.ask h2{color:var(--amber)}.q{font-weight:600;margin-bottom:8px}
        .pill{font:500 11.5px "Cascadia Mono",Consolas,monospace;text-transform:uppercase;letter-spacing:.05em;padding:2px 8px;border-radius:3px;border:1px solid var(--rule)}
        .pill.passed{background:var(--gow);color:var(--go);border-color:transparent}.pill.failed{background:var(--stopw);color:var(--stop);border-color:transparent}
        .err{color:var(--stop)}pre{background:var(--sheet);border:1px solid var(--rule);border-radius:4px;padding:10px 12px;overflow:auto;font:12.5px/1.5 "Cascadia Mono",Consolas,monospace;margin:8px 0 0}
        .notes{background:var(--sheet);border:1px solid var(--rule);border-radius:5px;padding:4px 18px;overflow-wrap:anywhere}.notes h1,.notes h2,.notes h3{font:600 1.05rem "Segoe UI",system-ui,sans-serif;letter-spacing:0;text-transform:none;color:var(--ink);margin:16px 0 6px}
        .notes p{margin:10px 0}.notes ul,.notes ol{display:block}.notes table{border-collapse:collapse;display:block;overflow-x:auto}.notes td,.notes th{border:1px solid var(--rule);padding:4px 10px;text-align:left}.notes code{font:.9em "Cascadia Mono",Consolas,monospace}
        .file{border:1px solid var(--rule);border-radius:5px;background:var(--sheet);margin-bottom:10px;overflow:hidden;min-width:0}
        .file summary{padding:8px 12px;font:500 13px "Cascadia Mono",Consolas,monospace;cursor:pointer;display:flex;gap:12px;justify-content:space-between;overflow-wrap:anywhere}
        .stat i{font-style:normal}.stat .add,.diff .add{color:var(--go)}.stat .del,.diff .del{color:var(--stop)}
        .diff{overflow-x:auto;border-top:1px solid var(--rule);font:12.5px/1.55 "Cascadia Mono",Consolas,monospace}.diff div{white-space:pre;padding:0 12px;min-width:max-content}
        .diff .add{background:var(--gow)}.diff .del{background:var(--stopw)}.diff .hunk{color:var(--ink3)}
        .act{border-top:2px solid var(--ink);padding-top:16px}
        """;
}
