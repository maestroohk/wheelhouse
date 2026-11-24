# Working on Wheelhouse

- Build the API: `dotnet build api/Wheelhouse.Api -nologo -v q`
- Type-check the UI: `npx tsc --noEmit` in `web/` (run npm from PowerShell; npm is broken in Git Bash on this machine)
- Run both: `.\dev.ps1` (API on 5299, UI on 3518)
- Unattended runs can be tested without a signed-in agent: set `nightAgent` in the test `settings.json` to `acp:node <path to a script that runs acp-mock>`. gnhf rejects a result with fields it didn't ask for; return exactly `success`, `summary`, `key_changes_made`, `key_learnings`.
- To test without touching real data, set `WHEELHOUSE_DATA` to a temp folder and `WHEELHOUSE_URL` to another port, and point `projectsRoot` in that folder's `settings.json` at a folder of disposable repos.

Rules for changes:

- npm-installed tools (gnhf, lavish-axi, tasks-axi) are started through `NodeTool.Command`, which runs the package's script with node. Never pass developer-typed text through `cmd /c`: quotes get mangled and `&` runs a second command.

- Text shown in the UI is for the developer using it: plain words, no internal terms. Errors meant for the screen are thrown as `WheelhouseException`.
- Agents never commit, push or switch branches. Only `MissionService` runs git commands that change a repo, and only on `wheelhouse/*` branches. The exceptions are the tools it hands off to: gnhf commits on its own `gnhf/*` branch during an unattended run, and no-mistakes pushes when the developer presses Ship.
- The brief (`Brief` in `Models.cs`, contract text in `AgentRunner.cs`) is the product. Keep it short and fixed in shape.

# Driving Wheelhouse from an agent

Use `wh` (in this folder: `wh.cmd` on Windows, `./wh` elsewhere; it needs only Node). It prints compact output, says what to run next, and never prompts.

- `wh` shows what is working and what is waiting on the developer. Start here.
- `wh repos` lists repo ids. `wh send <repo>... --say "…" --mode scout|builder|engineer|night` starts a mission per repo.
- `wh wait <id>` blocks until a mission stops, then shows its brief, checks and files. `wh show <id> --diff` adds the change.
- `wh reply <id> --say "…"` answers a question or asks for a revision.
- `wh adopt --repo <id> --worktree <path> --title "…" [--brief-file brief.json]` hands in work you did yourself in a git worktree (uncommitted, on its own branch), so it gets Wheelhouse's checks and the developer's review.
- `wh later <repo>... --say "…"` notes something for later without starting it; `wh backlog` lists what is noted; `wh start <task>` starts one. Use `later` for follow-ups you notice while doing something else, so they are not lost and not started unasked.
- `wh plan --goal "…" [<repo>...]`, `wh plans`, `wh launch <plan>` for a goal that spans repos.
- `wh keep`, `wh discard`, `wh ship --yes-push` and `wh launch` are the developer's decisions. Do them only when the developer has said so.
- `wh <command> --help` for any command. Set `WHEELHOUSE_URL` if Wheelhouse is not on `http://localhost:5299`.

`wh` is a thin layer over the HTTP API on `http://localhost:5299/api`, which is the whole interface:

- `GET /state` — repos, missions, settings
- `POST /missions` `{"repoIds":["repo-id"],"instruction":"…","mode":"scout|builder|engineer|night"}` (`night` is an unattended gnhf run)
- `POST /missions/adopt` `{"repoId":"…","title":"…","worktree":"C:\\path\\to\\worktree","instruction":"…","agent":"who did it","brief":{"headline":"…","changes":[],"verified":[],"risks":[]}}` — hand in work you did yourself in a git worktree of a fleet repo (uncommitted, on its own branch). Wheelhouse runs the checks and puts it up for review like any mission.
- `POST /missions/{id}/checks` — run build and tests again
- `GET /missions/{id}` — mission and its activity
- `GET /missions/{id}/diff`
- `POST /missions/{id}/reply` `{"text":"…"}` — answer a question or ask for a revision
- `POST /missions/{id}/keep` `{"message":"…"}`, `POST /missions/{id}/discard`, `POST /missions/{id}/cancel`
- `POST /missions/{id}/ship` `{"message":"…"}` — commit and run the no-mistakes pipeline; this pushes, so only on the developer's say-so
- `POST /missions/{id}/page` — write the review page and open it in Lavish; returns its URL
- `GET /backlog`, `POST /backlog` `{"repoIds":["…"],"text":"title\nnotes","kind":"engineer|scout"}`, `POST /backlog/{id}/start` `{"mode":"…"}`, `POST /backlog/{id}/done`, `DELETE /backlog/{id}`
- `POST /plans` `{"goal":"…","repoIds":[]}` — draft a cross-repo plan; `POST /plans/{id}/revise` `{"text":"…"}`; `POST /plans/{id}/launch` `{"steps":[{"repoId":"…","include":true,"mode":"engineer","spec":"…"}]}`; `DELETE /plans/{id}`
- `GET /tools` — which of treehouse, no-mistakes, gnhf and lavish-axi are installed
- `GET /stream` — server-sent events: `mission`, `repos`, `mission-removed`
