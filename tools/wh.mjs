#!/usr/bin/env node
// wh: drive Wheelhouse from an agent or a script. Built to the AXI rules (github.com/kunchenguid/axi):
// compact TOON on stdout, live content when run bare, errors that say what to run next, no prompts,
// unknown flags rejected. Exit codes: 0 ok, 1 error, 2 usage error.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const BASE = (process.env.WHEELHOUSE_URL ?? "http://localhost:5299").replace(/\/$/, "") + "/api";
const ACTIVE = new Set(["queued", "running", "checking", "shipping"]);
const WAITING = new Set(["needs_input", "review", "failed"]);

// ---------- output ----------

const out = [];
const q = (v) => {
  if (typeof v === "number") return String(v);
  const s = String(v ?? "");
  return s === "" || /[,:"\n\r\[\]{}#]|^\s|\s$|^-/.test(s) || /^(true|false|null|-?\d+(\.\d+)?)$/.test(s)
    ? '"' + s.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r?\n/g, "\\n") + '"'
    : s;
};
const kv = (k, v, indent = "") => { if (v !== undefined && v !== null && v !== "") out.push(`${indent}${k}: ${q(v)}`); };
const list = (k, items, indent = "") => { if (items?.length) out.push(`${indent}${k}[${items.length}]: ${items.map(q).join(",")}`); };
const table = (k, rows, fields, empty) => {
  if (!rows.length) { out.push(`${k}: ${empty}`); return; }
  out.push(`${k}[${rows.length}]{${fields.join(",")}}:`);
  for (const r of rows) out.push("  " + fields.map((f) => q(r[f])).join(","));
};
const help = (lines) => { out.push(`help[${lines.length}]:`); for (const l of lines) out.push("  " + q(l)); };
const clip = (s, n) => { s = String(s ?? "").replace(/\s+/g, " ").trim(); return s.length > n ? s.slice(0, n - 1) + "…" : s; };
const flush = (code = 0) => { process.stdout.write(out.join("\n") + "\n"); process.exit(code); };
const fail = (message, hint, code = 1) => { out.length = 0; out.push(`error: ${q(message)}`); if (hint) out.push(`help: ${q(hint)}`); flush(code); };

// ---------- input ----------

const COMMANDS = {
  repos: { flags: { all: "bool" }, usage: "wh repos [--all]", about: "Repos in the fleet. By default only the ones in play (touched in 30 days, uncommitted work, or with missions)." },
  missions: { flags: { repo: "str", state: "str" }, usage: "wh missions [--repo <id>] [--state waiting|working|all]", about: "Missions, newest first. Default: the ones working or waiting on the developer." },
  send: { flags: { say: "str", mode: "str" }, usage: 'wh send <repo>... --say "<instruction>" [--mode scout|builder|engineer|night]', about: "Start one mission per repo. scout reads only (default); builder edits; engineer edits, builds and tests; night runs unattended." },
  show: { flags: { diff: "bool", full: "bool" }, usage: "wh show <mission> [--diff] [--full]", about: "One mission: state, brief, checks, files. --diff adds the change itself (first 6000 characters unless --full)." },
  wait: { flags: { "timeout-s": "str" }, usage: "wh wait <mission> [--timeout-s 900]", about: "Block until the mission stops working, then show it." },
  reply: { flags: { say: "str" }, usage: 'wh reply <mission> --say "<answer or change request>"', about: "Answer a mission's question or ask its agent for a revision. The agent keeps its memory." },
  adopt: { flags: { repo: "str", worktree: "str", title: "str", say: "str", "brief-file": "str", agent: "str" }, usage: 'wh adopt --repo <id> --worktree <path> --title "<title>" [--brief-file brief.json] [--say "<what was asked>"]', about: "Hand in work you did yourself in a git worktree of a fleet repo. Wheelhouse runs its checks and puts it up for the developer's review." },
  checks: { flags: {}, usage: "wh checks <mission>", about: "Run the repo's build and tests on the mission again." },
  keep: { flags: { message: "str" }, usage: 'wh keep <mission> [--message "<commit message>"]', about: "Commit the mission's work on its own branch. Pushes nothing. Only on the developer's say-so." },
  discard: { flags: {}, usage: "wh discard <mission>", about: "Delete the mission's branch and folder. Only on the developer's say-so." },
  cancel: { flags: {}, usage: "wh cancel <mission>", about: "Stop a working mission. Its work so far is kept for review." },
  ship: { flags: { "yes-push": "bool" }, usage: "wh ship <mission> --yes-push", about: "Commit and send through review, tests, push and pull request. This pushes to the remote, so it needs --yes-push and the developer's say-so." },
  later: { flags: { say: "str", kind: "str" }, usage: 'wh later <repo>... --say "<what to do>" [--kind change|investigate]', about: "Note something to do in a repo later, without starting anything. It goes on the developer's Later list (kept by tasks-axi)." },
  backlog: { flags: { repo: "str" }, usage: "wh backlog [--repo <id>]", about: "What is noted for later, per repo." },
  start: { flags: { mode: "str" }, usage: "wh start <task> [--mode scout|builder|engineer|night]", about: "Start a noted task as a mission in its repo." },
  plan: { flags: { goal: "str" }, usage: 'wh plan --goal "<goal>" [<repo>...]', about: "Have the planner split one goal into a task per repo. Nothing starts until it is launched." },
  plans: { flags: {}, usage: "wh plans", about: "Cross-repo plans and their tasks." },
  launch: { flags: {}, usage: "wh launch <plan>", about: "Start the missions of a plan that is ready, as drafted." },
};

function parse(argv) {
  const [command, ...rest] = argv;
  const spec = COMMANDS[command];
  if (!spec) fail(`unknown command ${command}`, `commands: ${Object.keys(COMMANDS).join(", ")}. Run \`wh\` to see what is going on, or \`wh <command> --help\`.`, 2);
  const flags = {}, args = [];
  for (let i = 0; i < rest.length; i++) {
    const a = rest[i];
    if (a === "--help" || a === "-h") { out.push(`usage: ${q(spec.usage)}`, `about: ${q(spec.about)}`); flush(0); }
    if (a.startsWith("--")) {
      const [name, inline] = a.slice(2).split(/=(.*)/s);
      const kind = spec.flags[name];
      if (!kind) fail(`unknown flag --${name} for \`${command}\``, `usage: ${spec.usage}`, 2);
      if (kind === "bool") flags[name] = true;
      else {
        const value = inline ?? rest[++i];
        if (value === undefined) fail(`--${name} needs a value`, `usage: ${spec.usage}`, 2);
        flags[name] = value;
      }
    } else args.push(a);
  }
  return { command, spec, flags, args };
}

const need = (value, what, spec) => { if (!value) fail(`${what} is required`, `usage: ${spec.usage}`, 2); return value; };

// ---------- api ----------

async function api(pathname, method = "GET", body) {
  let res;
  try {
    res = await fetch(BASE + pathname, { method, headers: body ? { "Content-Type": "application/json" } : undefined, body: body ? JSON.stringify(body) : undefined });
  } catch {
    fail("Wheelhouse isn't running", "Start it with .\\dev.ps1 in the wheelhouse folder, or set WHEELHOUSE_URL if it runs elsewhere.");
  }
  if (res.ok) return res.json();
  let message = `the request failed (${res.status})`;
  try { const j = await res.json(); if (j?.error) message = j.error; } catch {}
  fail(message, res.status === 400 && /doesn't exist|wasn't found/.test(message) ? "Run `wh missions --state all` or `wh repos --all` for valid ids." : undefined);
}

const row = (m) => ({ id: m.id, repo: m.repoId, state: m.status, summary: clip(ACTIVE.has(m.status) ? m.activity || m.title : m.brief?.questions?.[0]?.text || m.error || m.brief?.headline || m.title, 90) });

function showMission(m) {
  kv("mission", m.id);
  kv("repo", m.repoId);
  kv("state", m.status);
  kv("mode", m.mode);
  kv("asked", clip(m.turns.find((t) => t.role === "you")?.text ?? m.title, 200));
  if (ACTIVE.has(m.status)) kv("doing", m.activity);
  kv("branch", m.branch);
  kv("folder", m.worktree);
  kv("error", m.error);
  kv("note", m.note);
  const b = m.brief;
  if (b && !ACTIVE.has(m.status)) {
    kv("headline", b.headline);
    list("changes", b.changes);
    list("verified", b.verified);
    list("risks", b.risks);
    for (const question of b.questions ?? []) { kv("question", question.text); list("options", question.options); }
  }
  if (m.checks.length) table("checks", m.checks.map((c) => ({ name: c.name, result: c.status, seconds: c.seconds })), ["name", "result", "seconds"]);
  for (const c of m.checks.filter((c) => c.status === "failed")) kv(`${c.name.toLowerCase()}_output`, clip(c.tail, 1500));
  if (m.files.length) table("files", m.files.slice(0, 40).map((f) => ({ path: f.path, added: f.added, removed: f.removed })), ["path", "added", "removed"]);
  if (m.ship?.steps?.length) table("ship", m.ship.steps.map((s) => ({ step: s.step, state: s.status })), ["step", "state"]);
  const next = [];
  if (m.status === "needs_input") next.push(`Run \`wh reply ${m.id} --say "<answer>"\` to answer`);
  if (m.status === "review") next.push(`Run \`wh show ${m.id} --diff\` to read the change`, `Run \`wh reply ${m.id} --say "<what to change>"\` to have the agent revise it`, "Keeping, shipping and discarding are the developer's call");
  if (m.status === "failed" || m.status === "cancelled") next.push(`Run \`wh reply ${m.id} --say "Continue from where you stopped."\` to resume`);
  if (ACTIVE.has(m.status)) next.push(`Run \`wh wait ${m.id}\` to block until it stops`);
  if (next.length) help(next);
}

// ---------- commands ----------

async function home() {
  const s = await api("/state");
  const self = fileURLToPath(import.meta.url);
  kv("bin", self.startsWith(os.homedir()) ? "~" + self.slice(os.homedir().length).replace(/\\/g, "/") : self.replace(/\\/g, "/"));
  kv("description", "Wheelhouse runs coding agents across the developer's repos, each in an isolated folder, and puts their work up for the developer's review. Use it to start, follow and hand in work.");
  const working = s.missions.filter((m) => ACTIVE.has(m.status)), waiting = s.missions.filter((m) => WAITING.has(m.status));
  out.push(`fleet: ${s.repos.length} repos, ${working.length} missions working, ${waiting.length} waiting on the developer`);
  table("waiting", waiting.map(row), ["id", "repo", "state", "summary"], "nothing is waiting on the developer");
  table("working", working.map(row), ["id", "repo", "state", "summary"], "no agents are working");
  const ready = (s.plans ?? []).filter((p) => p.status === "ready");
  const noted = (s.backlog ?? []).filter((t) => t.state === "queued");
  if (noted.length) out.push(`later: ${noted.length} tasks noted for later (run \`wh backlog\`)`);
  const quota = await fetch(BASE + "/quota").then((r) => r.json()).catch(() => null);
  if (quota?.known) out.push(`agent_quota: ${quota.percentLeft}% left, limited by the ${quota.limitedBy} window${quota.percentLeft < 25 ? " (low: avoid starting several missions at once)" : ""}`);
  if (ready.length) table("plans_ready", ready.map((p) => ({ id: p.id, goal: clip(p.goal, 70), tasks: p.steps.length })), ["id", "goal", "tasks"]);
  help([
    "Run `wh repos` for repo ids",
    'Run `wh send <repo> --say "<instruction>" --mode engineer` to start a mission',
    "Run `wh show <id>` for a mission's brief, checks and files",
    'Run `wh adopt --repo <id> --worktree <path> --title "<title>"` to hand in work you did yourself',
  ]);
}

async function run() {
  const argv = process.argv.slice(2);
  if (argv.length === 0) { await home(); flush(); }
  if (argv[0] === "--help" || argv[0] === "-h") {
    table("commands", Object.entries(COMMANDS).map(([name, c]) => ({ name, usage: c.usage })), ["name", "usage"]);
    help(["Run `wh` with no arguments to see what is going on now", "Run `wh <command> --help` for one command"]);
    flush();
  }
  const { command, spec, flags, args } = parse(argv);
  const s = ["repos", "missions", "plans", "backlog"].includes(command) ? await api("/state") : null;

  if (command === "repos") {
    const month = Date.now() - 30 * 86400000;
    const busy = new Set(s.missions.map((m) => m.repoId));
    const rows = s.repos
      .filter((r) => flags.all || r.pinned || r.dirty > 0 || busy.has(r.id) || (r.lastCommitAt && new Date(r.lastCommitAt).getTime() > month))
      .map((r) => ({ id: r.id, branch: r.branch ?? "", uncommitted: r.dirty, stack: r.stacks.join("+") }));
    table("repos", rows, ["id", "branch", "uncommitted", "stack"], "0 repos in play");
    if (!flags.all && rows.length < s.repos.length) help([`Run \`wh repos --all\` for all ${s.repos.length} repos`]);
  } else if (command === "missions") {
    const state = flags.state ?? "open";
    if (!["open", "waiting", "working", "all"].includes(state)) fail(`unknown state ${state}`, "valid: waiting, working, all", 2);
    const rows = s.missions
      .filter((m) => !flags.repo || m.repoId === flags.repo)
      .filter((m) => state === "all" || (state !== "working" && WAITING.has(m.status)) || (state !== "waiting" && ACTIVE.has(m.status)))
      .map(row);
    table("missions", rows, ["id", "repo", "state", "summary"], `0 ${state === "all" ? "" : state + " "}missions${flags.repo ? " in " + flags.repo : ""}`);
    if (rows.length) help(["Run `wh show <id>` for one mission"]);
    else if (state !== "all") help(["Run `wh missions --state all` to include finished ones"]);
  } else if (command === "send") {
    need(args.length, "at least one repo id", spec);
    need(flags.say, "--say", spec);
    const mode = flags.mode ?? "scout";
    if (!["scout", "builder", "engineer", "night"].includes(mode)) fail(`unknown mode ${mode}`, "valid: scout, builder, engineer, night", 2);
    const created = await api("/missions", "POST", { repoIds: args, instruction: flags.say, mode });
    table("started", created.map((m) => ({ id: m.id, repo: m.repoId, mode: m.mode })), ["id", "repo", "mode"]);
    help([`Run \`wh wait ${created[0].id}\` to block until it stops`, "Run `wh` to see everything that is working"]);
  } else if (command === "show" || command === "wait") {
    const id = need(args[0], "a mission id", spec);
    let { mission } = await api(`/missions/${id}`);
    if (command === "wait") {
      const limit = Number(flags["timeout-s"] ?? 900);
      if (!Number.isFinite(limit) || limit <= 0) fail("--timeout-s must be a number of seconds", `usage: ${spec.usage}`, 2);
      const until = Date.now() + limit * 1000;
      while (ACTIVE.has(mission.status) && Date.now() < until) {
        await new Promise((r) => setTimeout(r, 3000));
        ({ mission } = await api(`/missions/${id}`));
      }
      if (ACTIVE.has(mission.status)) kv("waited", `${limit}s and it is still working`);
    }
    showMission(mission);
    if (flags.diff) {
      const d = await api(`/missions/${id}/diff`);
      const max = flags.full ? Infinity : 6000;
      kv("diff", d.diff.length > max ? d.diff.slice(0, max) : d.diff || "no file changes");
      if (d.diff.length > max) help([`Run \`wh show ${id} --diff --full\` for all ${d.diff.length} characters of the diff`]);
    }
  } else if (command === "reply") {
    const id = need(args[0], "a mission id", spec);
    const m = await api(`/missions/${id}/reply`, "POST", { text: need(flags.say, "--say", spec) });
    kv("mission", m.id); kv("state", m.status);
    help([`Run \`wh wait ${m.id}\` to block until the agent stops`]);
  } else if (command === "adopt") {
    let brief;
    if (flags["brief-file"]) {
      try { brief = JSON.parse(fs.readFileSync(flags["brief-file"], "utf8")); }
      catch (e) { fail(`couldn't read ${flags["brief-file"]}: ${e.message}`, 'The file holds JSON like {"headline":"…","changes":["…"],"verified":["…"],"risks":["…"]}', 2); }
    }
    const m = await api("/missions/adopt", "POST", {
      repoId: need(flags.repo, "--repo", spec), worktree: path.resolve(need(flags.worktree, "--worktree", spec)),
      title: need(flags.title, "--title", spec), instruction: flags.say, agent: flags.agent, brief,
    });
    kv("mission", m.id); kv("state", m.status); kv("branch", m.branch);
    help([`Run \`wh wait ${m.id}\` to see what Wheelhouse's checks say`]);
  } else if (["checks", "keep", "discard", "cancel", "ship"].includes(command)) {
    const id = need(args[0], "a mission id", spec);
    if (command === "ship" && !flags["yes-push"]) fail("ship pushes to the remote and opens a pull request", `Only with the developer's say-so: wh ship ${id} --yes-push`, 2);
    const before = (await api(`/missions/${id}`)).mission;
    const done = { keep: "kept", discard: "discarded", cancel: "cancelled", ship: "shipped" }[command];
    if (before.status === done) { out.push(`mission: ${before.id} already ${done} (no-op)`); flush(); }
    const m = await api(`/missions/${id}/${command}`, "POST", command === "keep" || command === "ship" ? { message: flags.message } : {});
    kv("mission", m.id); kv("state", m.status); kv("note", m.note); kv("error", m.error);
    if (ACTIVE.has(m.status)) help([`Run \`wh wait ${m.id}\` to block until it stops`]);
  } else if (command === "later") {
    need(args.length, "at least one repo id", spec);
    const kind = flags.kind ?? "change";
    if (!["change", "investigate"].includes(kind)) fail(`unknown kind ${kind}`, "valid: change, investigate", 2);
    const all = await api("/backlog", "POST", { repoIds: args, text: need(flags.say, "--say", spec), kind: kind === "investigate" ? "scout" : "engineer" });
    out.push(`noted: ${args.length} ${args.length === 1 ? "task" : "tasks"} added, ${all.filter((t) => t.state === "queued").length} now waiting`);
    help(["Run `wh backlog` to see the list", "Starting a task is `wh start <task>`"]);
  } else if (command === "backlog") {
    const rows = (s.backlog ?? []).filter((t) => !flags.repo || t.repo === flags.repo).map((t) => ({ id: t.id, repo: t.repo, state: t.state === "in_flight" ? "started" : t.state, task: clip(t.title, 100) }));
    table("later", rows, ["id", "repo", "state", "task"], `0 tasks noted for later${flags.repo ? " in " + flags.repo : ""}`);
    help(rows.length ? ["Run `wh start <task>` to start one as a mission"] : ['Run `wh later <repo> --say "<what to do>"` to note one']);
  } else if (command === "start") {
    const id = need(args[0], "a task id", spec);
    if (flags.mode && !["scout", "builder", "engineer", "night"].includes(flags.mode)) fail(`unknown mode ${flags.mode}`, "valid: scout, builder, engineer, night", 2);
    const m = await api(`/backlog/${encodeURIComponent(id)}/start`, "POST", { mode: flags.mode });
    table("started", [{ id: m.id, repo: m.repoId, mode: m.mode }], ["id", "repo", "mode"]);
    help([`Run \`wh wait ${m.id}\` to block until it stops`]);
  } else if (command === "plan") {
    const p = await api("/plans", "POST", { goal: need(flags.goal, "--goal", spec), repoIds: args });
    kv("plan", p.id); kv("state", p.status);
    help(["Run `wh plans` in a minute to read the tasks it drafted"]);
  } else if (command === "plans") {
    if (!s.plans?.length) { out.push("plans: 0 plans"); help(['Run `wh plan --goal "<goal>" [<repo>...]` to draft one']); flush(); }
    for (const p of s.plans) {
      kv("plan", p.id); kv("state", p.status, "  "); kv("goal", clip(p.goal, 160), "  "); kv("question", p.question, "  "); kv("error", p.error, "  ");
      if (p.steps.length) {
        out.push(`  tasks[${p.steps.length}]{repo,mode,mission,task}:`);
        for (const t of p.steps) out.push("    " + [t.repoId, t.mode, t.missionId ?? "", clip(t.spec, 110)].map(q).join(","));
      }
      if (p.skipped.length) out.push(`  left_out[${p.skipped.length}]: ${p.skipped.map((x) => q(x.repoId)).join(",")}`);
    }
    if (s.plans.some((p) => p.status === "ready")) help(["Run `wh launch <plan>` to start a ready plan's missions (the developer's call)"]);
  } else if (command === "launch") {
    const p = await api(`/plans/${need(args[0], "a plan id", spec)}/launch`, "POST", {});
    table("started", p.steps.filter((t) => t.missionId).map((t) => ({ id: t.missionId, repo: t.repoId, mode: t.mode })), ["id", "repo", "mode"], "nothing was started");
  }
  flush();
}

run().catch((e) => fail(e?.message ?? String(e)));
