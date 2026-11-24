# Working on Wheelhouse

- Build the API: `dotnet build api/Wheelhouse.Api -nologo -v q`
- Type-check the UI: `npx tsc --noEmit` in `web/` (run npm from PowerShell; npm is broken in Git Bash on this machine)
- Run both: `.\dev.ps1` (API on 5299, UI on 3518)
- To test without touching real data, set `WHEELHOUSE_DATA` to a temp folder and `WHEELHOUSE_URL` to another port, and point `projectsRoot` in that folder's `settings.json` at a folder of disposable repos.

Rules for changes:

- Text shown in the UI is for the developer using it: plain words, no internal terms. Errors meant for the screen are thrown as `WheelhouseException`.
- Agents never commit, push or switch branches. Only `MissionService` runs git commands that change a repo, and only on `wheelhouse/*` branches.
- The brief (`Brief` in `Models.cs`, contract text in `AgentRunner.cs`) is the product. Keep it short and fixed in shape.

# Driving Wheelhouse from an agent

The API on `http://localhost:5299/api` is the whole interface:

- `GET /state` — repos, missions, settings
- `POST /missions` `{"repoIds":["repo-id"],"instruction":"…","mode":"scout|builder|engineer"}`
- `POST /missions/adopt` `{"repoId":"…","title":"…","worktree":"C:\\path\\to\\worktree","instruction":"…","agent":"who did it","brief":{"headline":"…","changes":[],"verified":[],"risks":[]}}` — hand in work you did yourself in a git worktree of a fleet repo (uncommitted, on its own branch). Wheelhouse runs the checks and puts it up for review like any mission.
- `POST /missions/{id}/checks` — run build and tests again
- `GET /missions/{id}` — mission and its activity
- `GET /missions/{id}/diff`
- `POST /missions/{id}/reply` `{"text":"…"}` — answer a question or ask for a revision
- `POST /missions/{id}/keep` `{"message":"…"}`, `POST /missions/{id}/discard`, `POST /missions/{id}/cancel`
- `GET /stream` — server-sent events: `mission`, `repos`, `mission-removed`
