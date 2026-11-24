"use client";

import { useMemo, useState } from "react";
import { ago, isActive, needsYou, type Mission, type Repo } from "@/lib/api";

type Props = {
  repos: Repo[];
  missions: Mission[];
  targets: string[];
  now: number;
  onToggle: (id: string) => void;
  onPin: (repo: Repo) => void;
  onLogbook: (id: string) => void;
  onRefresh: () => void;
  refreshing: boolean;
};

const DAY = 86_400_000;

export default function Fleet({ repos, missions, targets, now, onToggle, onPin, onLogbook, onRefresh, refreshing }: Props) {
  const [query, setQuery] = useState("");
  const [showQuiet, setShowQuiet] = useState(false);

  const byRepo = useMemo(() => {
    const map = new Map<string, { active: number; waiting: number }>();
    for (const m of missions) {
      const e = map.get(m.repoId) ?? { active: 0, waiting: 0 };
      if (isActive(m.status)) e.active++;
      if (needsYou(m.status)) e.waiting++;
      map.set(m.repoId, e);
    }
    return map;
  }, [missions]);

  const q = query.trim().toLowerCase();
  const visible = q ? repos.filter((r) => r.name.toLowerCase().includes(q)) : repos;
  const isLive = (r: Repo) =>
    byRepo.has(r.id) || targets.includes(r.id) || r.dirty > 0 || (r.lastCommitAt != null && now - new Date(r.lastCommitAt).getTime() < 14 * DAY);

  const pinned = visible.filter((r) => r.pinned);
  const live = visible.filter((r) => !r.pinned && isLive(r));
  const quiet = visible.filter((r) => !r.pinned && !isLive(r));

  const row = (r: Repo) => {
    const s = byRepo.get(r.id);
    const lamp = s?.waiting ? "wait" : s?.active ? "run" : "";
    const on = targets.includes(r.id);
    return (
      <div key={r.id} className={`vessel${on ? " targeted" : ""}`}>
        <i className={`lamp ${lamp}`} title={s?.waiting ? "Waiting on you" : s?.active ? "Agent working" : "Idle"} />
        <button
          className="name clip"
          style={{ border: 0, background: "none", padding: 0, textAlign: "left" }}
          onClick={() => onToggle(r.id)}
          aria-pressed={on}
          title={on ? "Remove from the next instruction" : "Send the next instruction here"}
        >
          {r.name}
        </button>
        <span className="tools">
          <button className="icon-btn" onClick={() => onLogbook(r.id)} title="Logbook: what happened here last">
            ≡
          </button>
          <button className={`icon-btn${r.pinned ? " on" : ""}`} onClick={() => onPin(r)} title={r.pinned ? "Unpin" : "Pin to the top"}>
            {r.pinned ? "★" : "☆"}
          </button>
        </span>
        <span className="sub">
          <span className="clip mono">{r.branch ?? "no branch"}</span>
          {r.dirty > 0 && <span className="warn nowrap">{r.dirty} changed</span>}
          {r.ahead > 0 && <span className="nowrap">↑{r.ahead}</span>}
          {r.behind > 0 && <span className="nowrap">↓{r.behind}</span>}
          {s?.active ? <span className="nowrap" style={{ color: "var(--helm)" }}>{s.active} working</span> : null}
          {!s?.active && r.dirty === 0 && r.lastCommitAt && <span className="nowrap">{ago(r.lastCommitAt, now)}</span>}
        </span>
      </div>
    );
  };

  return (
    <section className="fleet" aria-label="Fleet list">
      <div className="col-head" style={{ padding: "0 8px" }}>
        <span className="cap">All repos</span>
        <span className="count">{repos.length} repos</span>
        <span className="grow" />
        <button className="icon-btn" onClick={onRefresh} disabled={refreshing} title="Look for new repos and refresh status">
          {refreshing ? "…" : "↻"}
        </button>
      </div>
      <input id="fleet-search" className="fleet-search" type="text" placeholder="Find a repo" value={query} onChange={(e) => setQuery(e.target.value)} />
      {pinned.length > 0 && (
        <div className="fleet-group">
          <span className="cap">Pinned</span>
          {pinned.map(row)}
        </div>
      )}
      {live.length > 0 && (
        <div className="fleet-group">
          <span className="cap">Active lately</span>
          {live.map(row)}
        </div>
      )}
      {quiet.length > 0 && (
        <div className="fleet-group">
          <button className="cap" style={{ border: 0, background: "none", textAlign: "left" }} onClick={() => setShowQuiet(!showQuiet)}>
            {showQuiet || q ? "▾" : "▸"} Quiet · {quiet.length}
          </button>
          {(showQuiet || q) && quiet.map(row)}
        </div>
      )}
      {repos.length === 0 && (
        <div className="empty" style={{ margin: "0 6px" }}>
          <b>No repos found</b>
          <span>Set your projects folder in Settings.</span>
        </div>
      )}
    </section>
  );
}
