using System.Diagnostics;
using System.Text;

namespace Wheelhouse.Api;

public record ProcResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public bool NotStarted => ExitCode == -1 && !TimedOut && Stderr.StartsWith("Could not start");
}

public static class Proc
{
    // Wheelhouse may itself be started from inside an agent session. These must not leak into the agents it starts.
    static readonly string[] AgentEnvVars = { "CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT" };
    static readonly string[] AgentEnvPrefixes = { "CLAUDE_CODE_", "CLAUDE_AGENT_SDK_", "CLAUDE_PREVIEW_" };

    static bool IsLoopback(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback;

    public static async Task<ProcResult> RunAsync(
        string file,
        IEnumerable<string> args,
        string? cwd,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onLine = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        Action<string>? onErrorLine = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        // Build servers that outlive a command keep files locked on Windows, which stops a worktree being removed.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["UseSharedCompilation"] = "false";
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (AgentEnvVars.Contains(key) || AgentEnvPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                psi.Environment.Remove(key);
            else if (key == "ANTHROPIC_BASE_URL" && IsLoopback(psi.Environment[key]))
                psi.Environment.Remove(key);
        }

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (so) so.AppendLine(e.Data);
            try { onLine?.Invoke(e.Data); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (se) se.AppendLine(e.Data);
            try { onErrorLine?.Invoke(e.Data); } catch { }
        };

        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, "", $"Could not start {file}: {ex.Message}", false); }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (stdin != null)
        {
            try
            {
                await p.StandardInput.WriteAsync(stdin);
                p.StandardInput.Close();
            }
            catch { }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
            return new ProcResult(-1, so.ToString(), se.ToString(), true);
        }

        p.WaitForExit();
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString(), false);
    }

    public static Task<ProcResult> Git(string repo, CancellationToken ct, params string[] args)
    {
        var all = new List<string> { "-C", repo, "-c", "core.quotepath=off", "-c", "color.ui=never" };
        all.AddRange(args);
        return RunAsync("git", all, repo, TimeSpan.FromMinutes(2), ct);
    }
}

public static class Text
{
    public static string Slug(string s, int max = 40)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= max) break;
        }
        var r = sb.ToString().Trim('-');
        return r.Length == 0 ? "x" : r;
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    public static string Tail(string s, int lines)
    {
        var all = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Splits one row of a TOON table: comma separated, with double-quoted cells and backslash escapes.</summary>
    public static List<string> Csv(string row)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (quoted)
            {
                if (ch == '\\' && i + 1 < row.Length) { var n = row[++i]; cur.Append(n == 'n' ? '\n' : n); }
                else if (ch == '"') quoted = false;
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells;
    }

}
