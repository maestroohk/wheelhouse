"use client";

import { useEffect, useState } from "react";
import { ago, api, statusLabel, type LogEntry, type PullRequest, type PullRequests, type Repo, type Settings, type Status } from "@/lib/api";

function Shell({ title, sub, onClose, children, foot }: { title: string; sub?: string; onClose: () => void; children: React.ReactNode; foot?: React.ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="scrim" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <section className="drawer" role="dialog" aria-label={title} style={{ width: "min(560px, 100%)" }}>
        <header className="drawer-head">
          <div className="row">
            <h2 className="grow">{title}</h2>
            <button className="btn quiet" onClick={onClose}>Close · Esc</button>
          </div>
          {sub && <span className="mono small faint" style={{ overflowWrap: "anywhere" }}>{sub}</span>}
        </header>
        <div className="drawer-body">{children}</div>
        <footer className="drawer-foot">{foot}</footer>
      </section>
    </div>
  );
}

/** "Where was I?" for one repo: its recent commits and missions on one timeline. */
export function Logbook({ repoId, onClose, onOpenMission, onTarget }: { repoId: string; onClose: () => void; onOpenMission: (id: string) => void; onTarget: () => void }) {
  const [data, setData] = useState<{ repo: Repo; entries: LogEntry[] } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.logbook(repoId).then((d) => { if (live) setData(d); }, (e: Error) => { if (live) setError(e.message); });
    return () => { live = false; };
  }, [repoId]);

  const repo = data?.repo;
  return (
    <Shell
      title={repo?.name ?? "Logbook"}
      sub={repo?.path}
      onClose={onClose}
      foot={<button className="btn primary" onClick={onTarget}>Give this repo an instruction</button>}
    >
      {error && <p className="error">{error}</p>}
      {repo && (
        <>
          <div className="meta">
            <span>on {repo.branch ?? "no branch"}</span>
            <span style={repo.dirty ? { color: "var(--amber)" } : undefined}>{repo.dirty ? `${repo.dirty} uncommitted` : "clean"}</span>
            {repo.ahead > 0 && <span>{repo.ahead} unpushed</span>}
            {repo.behind > 0 && <span>{repo.behind} behind</span>}
            {!repo.hasUpstream && <span>never published</span>}
            {repo.stacks.map((s) => <span key={s}>{s}</span>)}
          </div>
          <div className="section">
            <span className="cap">What happened here last</span>
            <ul className="timeline">
              {data!.entries.length === 0 && <li><time /> <span className="faint">Nothing yet.</span></li>}
              {data!.entries.map((e, i) => (
                <li key={i} style={{ gridTemplateColumns: "70px minmax(0, 1fr)" }} className={e.kind === "mission" ? "brief" : ""}>
                  <time>{ago(e.at)}</time>
                  {e.kind === "mission" ? (
                    <button style={{ border: 0, background: "none", padding: 0, textAlign: "left", font: "inherit", color: "var(--helm)" }} onClick={() => onOpenMission(e.missionId!)}>
                      Agent · {statusLabel[e.who as Status] ?? e.who} · {e.text}
                    </button>
                  ) : (
                    <span style={{ overflowWrap: "anywhere" }}>{e.text} <span className="faint">· {e.who}</span></span>
                  )}
                </li>
              ))}
            </ul>
          </div>
        </>
      )}
      {!data && !error && <p className="faint">Reading the log…</p>}
    </Shell>
  );
}

function PrRow({ p }: { p: PullRequest }) {
  return (
    <li className="pr">
      <a href={p.url} target="_blank" rel="noreferrer" className="pr-title">{p.title}</a>
      <span className="meta">
        <span>{p.repo}#{p.number}</span>
        {p.branch && <span>{p.branch}</span>}
        {p.draft && <span>draft</span>}
      </span>
      <span className="row" style={{ gap: 6 }}>
        {p.checks !== "none" && <span className={`tag ${p.checks === "passing" ? "ok" : p.checks === "failing" ? "bad" : "run"}`}>{p.checks === "failing" ? `${p.failing} failing` : `checks ${p.checks}`}</span>}
        {p.review && <span className={`tag ${p.review === "approved" ? "ok" : p.review === "changes requested" ? "wait" : ""}`}>{p.review}</span>}
      </span>
    </li>
  );
}

/** Open pull requests: the ones needing something from the developer first. */
export function PullRequestPanel({ data, onClose, onRefresh, refreshing }: { data: PullRequests; onClose: () => void; onRefresh: () => void; refreshing: boolean }) {
  const attention = data.items.filter((p) => p.mine && (p.checks === "failing" || p.review === "changes requested"));
  const review = data.items.filter((p) => !p.mine);
  const rest = data.items.filter((p) => p.mine && !attention.includes(p));
  const group = (title: string, items: PullRequest[]) =>
    items.length > 0 && (
      <div className="section">
        <span className="cap">{title} · {items.length}</span>
        <ul className="prs">{items.map((p) => <PrRow key={p.url} p={p} />)}</ul>
      </div>
    );
  return (
    <Shell
      title="Pull requests"
      sub={data.known ? `Read from GitHub ${ago(data.at)}` : undefined}
      onClose={onClose}
      foot={<button className="btn" onClick={onRefresh} disabled={refreshing}>{refreshing ? "Reading…" : "Read again"}</button>}
    >
      {!data.known && <p className="muted">{data.note}</p>}
      {data.known && data.items.length === 0 && <p className="muted">You have no open pull requests and nothing is waiting on your review.</p>}
      {group("Needs you", attention)}
      {group("Waiting on your review", review)}
      {group("Open", rest)}
    </Shell>
  );
}

export function SettingsPanel({ settings, onClose, onSaved }: { settings: Settings; onClose: () => void; onSaved: (s: Settings) => void }) {
  const [draft, setDraft] = useState<Settings>(settings);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => setDraft({ ...draft, [k]: v });

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      onSaved(await api.saveSettings(draft));
      onClose();
    } catch (e) {
      setError((e as Error).message);
    }
    setSaving(false);
  };

  return (
    <Shell
      title="Settings"
      onClose={onClose}
      foot={
        <div className="row">
          {error && <span className="toast grow">{error}</span>}
          <span className="grow" />
          <button className="btn primary" onClick={save} disabled={saving}>{saving ? "Saving…" : "Save"}</button>
        </div>
      }
    >
      <div className="form">
        <div className="field">
          <label htmlFor="set-root">Projects folder</label>
          <input id="set-root" type="text" value={draft.projectsRoot} onChange={(e) => set("projectsRoot", e.target.value)} />
          <span className="hint">Every git repo up to two folders deep becomes part of the fleet.</span>
        </div>
        <div className="field">
          <label htmlFor="set-par">Agents working at once</label>
          <input id="set-par" type="number" min={1} max={12} value={draft.maxParallel} onChange={(e) => set("maxParallel", Number(e.target.value))} />
          <span className="hint">Extra missions wait in a queue.</span>
        </div>
        <div className="field">
          <label htmlFor="set-min">Time limit per turn (minutes)</label>
          <input id="set-min" type="number" min={2} max={240} value={draft.missionMinutes} onChange={(e) => set("missionMinutes", Number(e.target.value))} />
        </div>
        <div className="field">
          <label htmlFor="set-night">Unattended runs: most steps, and most hours</label>
          <div className="row">
            <input id="set-night" type="number" min={1} max={200} style={{ width: 110 }} value={draft.nightIterations} onChange={(e) => set("nightIterations", Number(e.target.value))} />
            <input id="set-night-hours" type="number" min={1} max={24} style={{ width: 110 }} value={draft.nightHours} onChange={(e) => set("nightHours", Number(e.target.value))} aria-label="Most hours" />
          </div>
          <span className="hint">An unattended run stops at whichever comes first. Run by gnhf.</span>
        </div>
        <div className="field">
          <label htmlFor="set-night-agent">Unattended runs: agent</label>
          <input id="set-night-agent" type="text" value={draft.nightAgent} onChange={(e) => set("nightAgent", e.target.value)} />
          <span className="hint">Any agent gnhf supports: claude, codex, copilot, opencode, or acp: followed by a target.</span>
        </div>
        <div className="field">
          <label htmlFor="set-model">Model</label>
          <input id="set-model" type="text" placeholder="Claude Code's default" value={draft.model ?? ""} onChange={(e) => set("model", e.target.value || null)} />
          <span className="hint">A Claude Code model name or alias, such as sonnet or opus. Leave empty for the default.</span>
        </div>
        <div className="field">
          <label htmlFor="set-claude">Claude CLI</label>
          <input id="set-claude" type="text" value={draft.claudePath} onChange={(e) => set("claudePath", e.target.value)} />
          <span className="hint">The command or full path used to start Claude Code.</span>
        </div>
        <label className="row" htmlFor="set-checks">
          <input id="set-checks" type="checkbox" checked={draft.runChecks} onChange={(e) => set("runChecks", e.target.checked)} />
          <span>Run build and tests after an agent changes files</span>
        </label>
        <div className="field">
          <label htmlFor="set-fix">When checks fail, send it back to the agent this many times</label>
          <input id="set-fix" type="number" min={0} max={5} style={{ width: 110 }} value={draft.fixAttempts} onChange={(e) => set("fixAttempts", Number(e.target.value))} />
          <span className="hint">The agent gets the failing output and tries again before you are asked to look. 0 turns this off.</span>
        </div>
        <label className="row" htmlFor="set-treehouse">
          <input id="set-treehouse" type="checkbox" checked={draft.useTreehouse} onChange={(e) => set("useTreehouse", e.target.checked)} />
          <span>Get mission folders from treehouse when it is installed (they arrive with packages restored)</span>
        </label>
      </div>
    </Shell>
  );
}
