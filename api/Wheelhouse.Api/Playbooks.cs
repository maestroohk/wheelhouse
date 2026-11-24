namespace Wheelhouse.Api;

/// <summary>A saved instruction. Text may hold blanks written as {{what}} for the developer to fill in.</summary>
public sealed class Playbook
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public string Mode { get; set; } = MissionMode.Scout;
    public bool BuiltIn { get; set; }
}

public record SavePlaybookRequest(string Name, string Text, string? Mode);

public sealed class Playbooks
{
    static readonly Playbook[] BuiltIns =
    {
        new()
        {
            Id = "where-was-i", Name = "Where was I?", Mode = MissionMode.Scout, BuiltIn = true,
            Text = "I am coming back to this repo after a while. From the uncommitted changes, the last ten commits, any TODO or plan files, and half-finished code, tell me what I was in the middle of, what state it is in, and the next two or three concrete steps to finish it.",
        },
        new()
        {
            Id = "review-my-changes", Name = "Review my uncommitted work", Mode = MissionMode.Scout, BuiltIn = true,
            Text = "Review the uncommitted changes in this repo as a careful colleague would before I commit. Report real bugs, things that will break at the edges, and anything left half done. Leave style alone unless it hides a bug.",
        },
        new()
        {
            Id = "add-tests", Name = "Add tests for…", Mode = MissionMode.Engineer, BuiltIn = true,
            Text = "Add tests for {{what}}. Follow how the existing tests in this repo are written and where they live. Cover the normal case, the edges and the failure cases. Do not change the code under test unless a test exposes a real bug, and say so if it does.",
        },
        new()
        {
            Id = "fix-failing-tests", Name = "Fix the failing tests", Mode = MissionMode.Engineer, BuiltIn = true,
            Text = "Run this repo's tests. For each failure, find the cause and fix it in the code. Do not weaken, skip or delete a test to make it pass; if a test is itself wrong, fix the test and say why.",
        },
        new()
        {
            Id = "update-packages", Name = "Update packages safely", Mode = MissionMode.Engineer, BuiltIn = true,
            Text = "Update this repo's packages to the newest patch and minor versions, keeping how versions are pinned. Leave major versions alone and list them for me. Build and run the tests afterwards and fix anything the updates broke.",
        },
        new()
        {
            Id = "explain-repo", Name = "Explain this repo", Mode = MissionMode.Scout, BuiltIn = true,
            Text = "Explain this repo to a developer joining it today: what it does, how it is laid out, how to build, run and test it, and the three or four files to read first. Point at real files.",
        },
        new()
        {
            Id = "security-pass", Name = "Security pass", Mode = MissionMode.Scout, BuiltIn = true,
            Text = "Look through this repo for security problems that matter in practice: secrets in the code or history, unsafe handling of input, missing authorisation checks, risky defaults, and packages with known problems. Rank what you find by how bad it is and say where it is.",
        },
        new()
        {
            Id = "refresh-readme", Name = "Bring the README up to date", Mode = MissionMode.Builder, BuiltIn = true,
            Text = "Bring this repo's README up to date with what the code actually does now: what it is, how to run it, how to test it. Keep its existing voice and structure. Remove what is no longer true and do not invent features.",
        },
        new()
        {
            Id = "do-this", Name = "Do this in the repo…", Mode = MissionMode.Engineer, BuiltIn = true,
            Text = "{{what you want done}}\n\nKeep the change as small as the task allows, follow the conventions already in this repo, and build and test before you report.",
        },
    };

    private readonly string file;
    private readonly object gate = new();
    private List<Playbook> own;

    public Playbooks(AppPaths paths)
    {
        file = Path.Combine(paths.DataDir, "playbooks.json");
        own = Json.Read<List<Playbook>>(file) ?? new List<Playbook>();
    }

    /// <summary>The developer's own playbooks first, then the ones that come with Wheelhouse.</summary>
    public List<Playbook> All()
    {
        lock (gate) return own.Concat(BuiltIns).ToList();
    }

    public Playbook Save(SavePlaybookRequest req)
    {
        var name = (req.Name ?? "").Trim();
        var text = (req.Text ?? "").Trim();
        if (name.Length == 0) throw new WheelhouseException("Give the playbook a name.");
        if (text.Length == 0) throw new WheelhouseException("The playbook has no instruction in it.");
        lock (gate)
        {
            var id = Text.Slug(name, 40);
            var existing = own.FirstOrDefault(p => p.Id == id);
            var pb = existing ?? new Playbook { Id = id };
            pb.Name = Text.Clip(name, 60);
            pb.Text = text;
            pb.Mode = MissionMode.IsValid(req.Mode) ? req.Mode! : MissionMode.Scout;
            if (existing == null) own.Insert(0, pb);
            Json.Write(file, own);
            return pb;
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (own.RemoveAll(p => p.Id == id) == 0) throw new WheelhouseException("Only playbooks you saved can be removed.");
            Json.Write(file, own);
        }
    }
}
