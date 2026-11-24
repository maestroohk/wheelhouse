using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wheelhouse.Api;

public record AgentRequest(string Cwd, string Prompt, string Mode, string? ResumeSessionId, string? Model, TimeSpan Timeout);

public record AgentEvent(string Kind, string Text);

public record AgentResult(bool Ok, string Text, string? Error, double? CostUsd, string? SessionId);

/// <summary>
/// The plug-in point for coding agents. Wheelhouse only needs three things from one: run a prompt in a folder at a
/// given autonomy, report what it is doing as it goes, and be able to pick the same conversation up again.
/// </summary>
public interface IAgentRunner
{
    string Id { get; }
    Task<AgentResult> RunAsync(AgentRequest req, Action<AgentEvent> onEvent, CancellationToken ct);
}

public sealed class ClaudeCodeRunner : IAgentRunner
{
    static readonly string[] ReadTools = { "Read", "Grep", "Glob" };
    static readonly string[] EditTools = { "Read", "Grep", "Glob", "Edit", "Write" };
    static readonly string[] NeverTools = { "NotebookEdit", "WebFetch", "WebSearch", "Task", "Agent" };
    static readonly string[] ShellTools = { "Bash", "PowerShell" };

    // What an Engineer may run. Build and test tooling plus read-only git; nothing that publishes or deletes.
    static readonly string[] EngineerCommands =
    {
        "dotnet build*", "dotnet test*", "dotnet restore*", "dotnet format*", "dotnet run*",
        "npm run *", "npm test*", "npm ci*", "npm install*", "npx tsc*", "npx eslint*", "npx vitest*", "npx jest*",
        "pnpm run *", "pnpm test*", "pnpm install*",
        "python -m pytest*", "pytest*",
        "git status*", "git diff*", "git log*", "git show*",
    };

    private readonly SettingsStore settings;

    public ClaudeCodeRunner(SettingsStore settings) => this.settings = settings;

    public string Id => "claude-code";

    public async Task<AgentResult> RunAsync(AgentRequest req, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var s = settings.Get();
        // The prompt goes in on stdin: no quoting or command-line length problems on Windows.
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--append-system-prompt", Contract(req.Mode) };
        var model = req.Model ?? s.Model;
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model!); }
        if (!string.IsNullOrWhiteSpace(req.ResumeSessionId)) { args.Add("--resume"); args.Add(req.ResumeSessionId!); }

        var allowed = new List<string>();
        var denied = new List<string>(NeverTools);
        switch (req.Mode)
        {
            case MissionMode.Engineer:
                allowed.AddRange(EditTools);
                foreach (var tool in ShellTools)
                    allowed.AddRange(EngineerCommands.Select(c => $"{tool}({c})"));
                args.Add("--permission-mode"); args.Add("acceptEdits");
                break;
            case MissionMode.Builder:
                allowed.AddRange(EditTools);
                denied.AddRange(ShellTools);
                args.Add("--permission-mode"); args.Add("acceptEdits");
                break;
            default:
                allowed.AddRange(ReadTools);
                denied.AddRange(ShellTools);
                denied.Add("Edit"); denied.Add("Write");
                break;
        }
        args.Add("--allowedTools"); args.AddRange(allowed);
        args.Add("--disallowedTools"); args.AddRange(denied);

        string? resultText = null, error = null, sessionId = null;
        double? cost = null;
        var lastText = "";

        void OnLine(string line)
        {
            if (!line.StartsWith('{')) return;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString();
            if (type == "assistant" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
                    if (itemType == "tool_use")
                        onEvent(new AgentEvent("tool", Describe(item, req.Cwd)));
                    else if (itemType == "text" && item.TryGetProperty("text", out var tx))
                    {
                        lastText = tx.GetString() ?? lastText;
                        var first = FirstLine(lastText);
                        if (first.Length > 0) onEvent(new AgentEvent("say", first));
                    }
                }
            }
            else if (type == "result")
            {
                if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) resultText = r.GetString();
                if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                    error = resultText ?? (root.TryGetProperty("subtype", out var st) ? st.GetString() : "The agent reported an error");
            }
        }

        var proc = await Proc.RunAsync(s.ClaudePath, args, req.Cwd, req.Timeout, ct, OnLine, req.Prompt);
        if (proc.NotStarted)
            return new AgentResult(false, "", $"The Claude CLI wasn't found at '{s.ClaudePath}'. Install Claude Code or set its path in Settings.", null, sessionId);
        if (IsLoginError(resultText) || IsLoginError(error) || IsLoginError(proc.Stdout))
            return new AgentResult(false, "", LoginHelp, cost, null);
        if (proc.TimedOut)
            return new AgentResult(false, lastText, $"The agent did not finish within {req.Timeout.TotalMinutes:0} minutes. Send a follow-up to let it continue.", cost, sessionId);
        if (error != null)
            return new AgentResult(false, resultText ?? lastText, error, cost, sessionId);
        if (resultText == null)
        {
            var detail = string.IsNullOrWhiteSpace(proc.Stderr) ? proc.Stdout : proc.Stderr;
            return new AgentResult(false, lastText, $"The Claude CLI exited with code {proc.ExitCode}. {Text.Clip(detail.Trim(), 600)}", cost, sessionId);
        }
        return new AgentResult(true, resultText, null, cost, sessionId);
    }

    public const string LoginHelp =
        "Your Claude CLI isn't signed in. Open a terminal, run 'claude', type /login and finish the sign-in once. Then send the mission again.";

    static bool IsLoginError(string? text) =>
        text != null && (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.OrdinalIgnoreCase) && text.Length < 300);

    static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ');
            if (line.Length > 0 && !line.StartsWith("```")) return Text.Clip(line, 140);
        }
        return "";
    }

    static string Describe(JsonElement toolUse, string cwd)
    {
        var name = toolUse.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return name;
        string? Get(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var target = Get("file_path") ?? Get("pattern") ?? Get("command") ?? Get("path") ?? "";
        if (target.Length > 0 && Path.IsPathFullyQualified(target))
        {
            try { target = Path.GetRelativePath(cwd, target).Replace('\\', '/'); } catch { }
        }
        var verb = name switch
        {
            "Read" => "Reading",
            "Grep" => "Searching for",
            "Glob" => "Listing",
            "Edit" => "Editing",
            "Write" => "Writing",
            "Bash" or "PowerShell" => "Running",
            "TodoWrite" => "Updating its plan",
            _ => name,
        };
        return Text.Clip($"{verb} {target}".Trim(), 140);
    }

    static string Contract(string mode)
    {
        var ability = mode switch
        {
            MissionMode.Engineer => "You can read and edit files and run build, test and package commands. Verify your work by building and running the relevant tests before you report.",
            MissionMode.Builder => "You can read and edit files. You cannot run commands, so say plainly under risks that the change is unbuilt and untested.",
            _ => "You can only read. You cannot change files or run commands. Investigate and report what you found.",
        };
        return $$"""
            You are a crew agent working for a developer through Wheelhouse, a tool that runs many agents across many repositories at once. The developer reads a short card about your work, not your transcript, and is looking after several other projects at the same time.

            {{ability}}

            Rules:
            - Work only inside the current directory.
            - Never commit, push, switch branches or change git configuration. Wheelhouse handles git.
            - If a decision really belongs to the developer and you cannot sensibly continue without it, stop and ask it as a question with two to four short options. Otherwise pick the conventional option and mention it under risks.
            - Keep any explanation before the final block under 250 words.

            End your final message with this fenced block, containing valid JSON, with nothing after it:

            ```brief
            {"headline": "...", "changes": ["..."], "verified": ["..."], "risks": ["..."], "questions": [{"text": "...", "options": ["...", "..."]}]}
            ```

            - headline: at most 14 words, plain language, stating the outcome. For a read-only task it is the answer itself.
            - changes: at most 5 items, each under 20 words. What you changed; for a read-only task, what you found. Name files where useful.
            - verified: what you actually checked and the result. Empty if you checked nothing.
            - risks: what the developer should look at before accepting. Empty if nothing.
            - questions: empty unless you are blocked on a decision.
            """;
    }
}

public static partial class BriefParser
{
    [GeneratedRegex("```brief\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    public static Brief Parse(string text)
    {
        text ??= "";
        string? json = null;
        var detail = text;
        var matches = Fence().Matches(text);
        if (matches.Count > 0)
        {
            var m = matches[^1];
            json = m.Groups[1].Value;
            detail = text.Remove(m.Index, m.Length);
        }
        else
        {
            var at = text.LastIndexOf("{\"headline\"", StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            if (at >= 0 && end > at) { json = text[at..(end + 1)]; detail = text[..at]; }
        }

        Brief? brief = null;
        if (json != null)
        {
            try { brief = JsonSerializer.Deserialize<Brief>(json, Json.Options); } catch { }
        }
        brief ??= new Brief();
        brief.Changes ??= new();
        brief.Verified ??= new();
        brief.Risks ??= new();
        brief.Questions = (brief.Questions ?? new()).Where(q => !string.IsNullOrWhiteSpace(q.Text)).ToList();
        foreach (var q in brief.Questions) q.Options ??= new();
        brief.Detail = detail.Trim().TrimEnd('`').Trim();
        if (string.IsNullOrWhiteSpace(brief.Headline))
        {
            var first = brief.Detail.Split('\n').Select(l => l.Trim().TrimStart('#', ' ')).FirstOrDefault(l => l.Length > 0) ?? "Finished";
            brief.Headline = Text.Clip(first, 110);
        }
        return brief;
    }
}
