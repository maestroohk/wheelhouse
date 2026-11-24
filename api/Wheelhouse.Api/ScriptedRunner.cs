namespace Wheelhouse.Api;

/// <summary>
/// A stand-in agent for tests and UI work. It makes no model calls: it walks a fixed script so the rest of the
/// pipeline (worktree, brief, questions, checks, diff, keep and discard) can be exercised without a signed-in CLI.
/// Selected with WHEELHOUSE_AGENT=scripted. An instruction containing "ask" makes it stop with a question first.
/// </summary>
public sealed class ScriptedRunner : IAgentRunner
{
    public string Id => "scripted";

    static async Task<AgentResult> PlanAsync(AgentRequest req, Action<AgentEvent> onEvent, string session, CancellationToken ct)
    {
        var ids = req.Prompt.Split('\n').Where(l => l.StartsWith("- id: ")).Select(l => l[6..].Split('|')[0].Trim()).ToList();
        foreach (var id in ids)
        {
            onEvent(new AgentEvent("tool", $"Reading {id}/README.md"));
            await Task.Delay(600, ct);
        }
        if (req.Prompt.Contains("unclear", StringComparison.OrdinalIgnoreCase))
            return new AgentResult(true, "```plan\n{\"summary\": \"\", \"question\": \"Which behaviour do you want: a new command, or a change to the existing one?\", \"steps\": [], \"skipped\": []}\n```", null, 0.01, session);
        var revised = req.Prompt.Contains("Your previous plan");
        var take = ids.Count > 1 ? ids.Take(ids.Count - 1).ToList() : ids;
        var steps = string.Join(",", take.Select(id => $"{{\"repo\": \"{id}\", \"mode\": \"engineer\", \"spec\": \"Add multiply to the calculator in src/Calc/Calculator.cs{(revised ? " and a test for it" : "")}. Done when the solution builds.\", \"out_of_scope\": \"Existing operations.\", \"why\": \"Calculator has Add and Subtract only.\"}}"));
        var skipped = ids.Count > 1 ? $"{{\"repo\": \"{ids[^1]}\", \"why\": \"Nothing here is touched by the goal.\"}}" : "";
        return new AgentResult(true, $"I read each repository.\n\n```plan\n{{\"summary\": \"One change per repository that has a calculator.\", \"question\": \"\", \"steps\": [{steps}], \"skipped\": [{skipped}]}}\n```", null, 0.02, session);
    }

    public async Task<AgentResult> RunAsync(AgentRequest req, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var session = req.ResumeSessionId ?? Guid.NewGuid().ToString();
        if (req.Contract != null && req.Contract.Contains("```plan")) return await PlanAsync(req, onEvent, session, ct);
        async Task Step(string kind, string text)
        {
            onEvent(new AgentEvent(kind, text));
            await Task.Delay(700, ct);
        }

        await Step("tool", "Listing **/*.cs");
        await Step("tool", "Reading src/Calc/Calculator.cs");

        if (req.ResumeSessionId == null && req.Prompt.Contains("ask", StringComparison.OrdinalIgnoreCase))
            return new AgentResult(true, """
                I need one decision before I change anything.

                ```brief
                {"headline": "Blocked on how Divide should treat zero", "changes": [], "verified": [], "risks": [], "questions": [{"text": "What should Divide do when the divisor is zero?", "options": ["Throw DivideByZeroException", "Return 0"]}]}
                ```
                """, null, 0.01, session);

        if (req.Mode == MissionMode.Scout)
            return new AgentResult(true, """
                The project is a small calculator library with one test.

                ```brief
                {"headline": "A calculator library with Add and Subtract; only Add is tested", "changes": ["src/Calc/Calculator.cs has Add and Subtract", "tests/Calc.Tests covers Add only", "Subtract has no test"], "verified": ["Read every .cs file in the repo"], "risks": [], "questions": []}
                ```
                """, null, 0.01, session);

        var file = Path.Combine(req.Cwd, "src", "Calc", "Calculator.cs");
        if (File.Exists(file))
        {
            await Step("tool", "Editing src/Calc/Calculator.cs");
            var text = File.ReadAllText(file);
            if (req.ResumeSessionId != null && text.Contains("=> a * b\n"))
            {
                text = text.Replace("=> a * b\n", "=> a * b;\n");
                File.WriteAllText(file, text);
            }
            var broken = req.Prompt.Contains("break", StringComparison.OrdinalIgnoreCase);
            var body = broken
                ? "    public static int Multiply(int a, int b) => a * b\n"
                : "    public static int Multiply(int a, int b) => a * b;\n";
            var at = text.LastIndexOf('}');
            if (at > 0 && !text.Contains("Multiply")) File.WriteAllText(file, text[..at] + body + text[at..]);
        }
        else
        {
            await Step("tool", "Writing NOTES.md");
            File.WriteAllText(Path.Combine(req.Cwd, "NOTES.md"), $"Scripted change for: {req.Prompt}\n");
        }
        if (req.Mode == MissionMode.Engineer) await Step("tool", "Running dotnet build");

        return new AgentResult(true, """
            Added Multiply next to the existing operations.

            ```brief
            {"headline": "Calculator can now multiply", "changes": ["Added Multiply(int, int) to src/Calc/Calculator.cs"], "verified": [], "risks": ["No test was added for Multiply"], "questions": []}
            ```
            """, null, 0.02, session);
    }
}
