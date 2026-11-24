# Wheelhouse

One room to steer coding agents across every repo you own.

You give short instructions. Agents work in parallel, one per repo, each in its own isolated copy. You get back a card you can read in ten seconds and a decision you can make with one click. Nothing touches your working copy and nothing is pushed.

## Run it

```powershell
.\dev.ps1
```

This builds and starts the API on http://localhost:5299 and the UI on http://localhost:3518, then opens the browser. Ctrl+C stops both.

Needs: .NET 10 SDK, Node 20+, git, and the Claude Code CLI signed in (`claude`, then `/login`, once).

## How it works

- **Fleet** (left). Every git repo under your projects folder, up to two folders deep. Each line shows the branch, uncommitted files, ahead/behind, and whether an agent is working there or waiting on you. Pin the ones you're active on. `≡` opens a repo's logbook: its recent commits and missions on one timeline.
- **Helm** (bottom). Click repos in the fleet or type `@name` to choose where the instruction goes. One instruction sent to five repos becomes five missions.
- **Three levels of autonomy.**
  - *Scout* reads and reports. It runs in the repo itself and can change nothing.
  - *Builder* edits files on an isolated branch. It can't run commands.
  - *Engineer* edits and can run build, test and package commands on an isolated branch.
- **Missions** (middle). What each agent is doing right now, in a few words.
- **Needs you** (right). Questions from agents with option buttons, changes ready for review, and failures. Answering resumes the same agent with its memory intact.
- **Review.** Open a mission to see its brief, the diff per file, and check results. *Keep* commits on the mission's `wheelhouse/*` branch as you. *Discard* deletes the branch and folder. Or send a follow-up and the agent revises.
- **Checks.** After an agent changes files, Wheelhouse runs the repo's own checks itself and puts the result on the card: `dotnet build` and `dotnet test`, a TypeScript type check where packages are installed, and `pytest` where the repo has a virtualenv.
- **Taking in outside work.** Work done by another agent in a git worktree can be handed in (`POST /api/missions/adopt`) and gets the same checks and review.

## Built on

Wheelhouse uses existing tools where they exist instead of rewriting them.

| Job | Tool | State |
|---|---|---|
| Isolated, pre-warmed mission folders | [treehouse](https://github.com/kunchenguid/treehouse) | In use. Falls back to plain `git worktree` if it isn't installed |
| Review, test, push, pull request, CI gate | [no-mistakes](https://github.com/kunchenguid/no-mistakes) (`no-mistakes axi run`) | Next |
| Unattended overnight runs | [gnhf](https://github.com/kunchenguid/gnhf) | Next |
| Agents other than Claude Code | [acpx](https://github.com/kunchenguid/acpx) / Agent Client Protocol | Later |

## Where things live

| What | Where |
|---|---|
| Missions, settings | `%USERPROFILE%\.wheelhouse` |
| Isolated working folders | `%USERPROFILE%\.wheelhouse\worktrees` |
| Mission branches | `wheelhouse/<title>-<id>` in each repo |

## Layout

```
api/Wheelhouse.Api   .NET 10 minimal API
  AgentRunner.cs     IAgentRunner + the Claude Code runner, and the brief contract agents must follow
  MissionService.cs  queue, worktrees, agent turns, checks, keep/discard
  RepoRegistry.cs    repo discovery and status
  Checks.cs          build/test detection
  Program.cs         HTTP endpoints and the event stream
web/                 Next.js UI
docs/features.html   feature plan
```

Adding another agent (Codex, Gemini CLI, a local model) means implementing `IAgentRunner` and registering it in `Program.cs`.

## Not built yet

Playbooks, cross-repo planning, fix-until-green, push and pull request, a `wh` command line, unattended overnight runs. See `docs/features.html`.
