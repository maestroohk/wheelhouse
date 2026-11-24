namespace Wheelhouse.Api;

/// <summary>
/// A stand-in agent for tests and UI work. It makes no model calls: it walks a fixed script so the rest of the
/// pipeline (worktree, brief, questions, checks, diff, keep and discard) can be exercised without a signed-in CLI.
/// Selected with WHEELHOUSE_AGENT=scripted. An instruction containing "ask" makes it stop with a question first.
/// </summary>
public sealed class ScriptedRunner : IAgentRunner
{
    public string Id => "scripted";

    public async Task<AgentResult> RunAsync(AgentRequest req, Action<AgentEvent> onEvent, CancellationToken ct)
    {
        var session = req.ResumeSessionId ?? Guid.NewGuid().ToString();
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
