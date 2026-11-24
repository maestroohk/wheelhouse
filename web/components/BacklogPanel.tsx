"use client";

import { useState } from "react";
import { type BacklogTask, type Repo } from "@/lib/api";
import { Confirm } from "@/components/MissionCard";

type Props = {
  tasks: BacklogTask[];
  repos: Repo[];
  busy: string | null;
  onStart: (id: string) => void;
  onDone: (id: string) => void;
  onRemove: (id: string) => void;
};

/** Things noted for later, per repo. Nothing here is running; Start turns a task into a mission. */
export default function BacklogPanel({ tasks, repos, busy, onStart, onDone, onRemove }: Props) {
  const [open, setOpen] = useState<string | null>(null);
  const names = new Map(repos.map((r) => [r.id, r.name]));
  if (tasks.length === 0) return null;

  return (
    <>
      <div className="col-head" style={{ marginTop: 10 }}>
        <span className="cap">Later</span>
        <span className="count">{tasks.length}</span>
      </div>
      <ul className="backlog">
        {tasks.map((t) => {
          const flying = t.state === "in_flight";
          return (
            <li key={t.id} className={flying ? "flying" : ""}>
              <span className="repo mono clip" title={t.repo}>{names.get(t.repo) ?? (t.repo || "no repo")}</span>
              <button className="task-title" onClick={() => setOpen(open === t.id ? null : t.id)} aria-expanded={open === t.id} title={t.body ? "Show the notes" : undefined}>
                {t.title}
              </button>
              <span className="row" style={{ gap: 4, flexWrap: "nowrap" }}>
                {flying ? (
                  <span className="tag run">Started</span>
                ) : (
                  <button className="btn" disabled={busy === t.id || !t.repo} onClick={() => onStart(t.id)} title={t.kind === "scout" ? "Start as a Scout mission" : "Start as an Engineer mission"}>
                    Start
                  </button>
                )}
                <button className="btn quiet" disabled={busy === t.id} onClick={() => onDone(t.id)} title="Mark as done without starting anything">Done</button>
                <Confirm label="×" sure="Remove?" onConfirm={() => onRemove(t.id)} disabled={busy === t.id} />
              </span>
              {open === t.id && (
                <p className="task-body small muted">
                  {t.body || "No notes."}
                  <span className="faint"> · {t.kind === "scout" ? "investigate" : "change"} · noted {t.created}</span>
                </p>
              )}
            </li>
          );
        })}
      </ul>
    </>
  );
}
