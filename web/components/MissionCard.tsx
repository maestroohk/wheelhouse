"use client";

import { useState } from "react";
import { duration, isActive, modeInfo, money, statusLabel, stepLabel, type Mission, type Status } from "@/lib/api";

export const statusTone = (s: Status) =>
  s === "running" || s === "checking" || s === "queued" || s === "shipping" ? "run" : s === "needs_input" || s === "review" ? "wait" : s === "failed" ? "bad" : s === "done" || s === "kept" || s === "shipped" ? "ok" : "";

export function DiffStat({ m }: { m: Mission }) {
  if (m.files.length === 0) return null;
  const added = m.files.reduce((n, f) => n + f.added, 0);
  const removed = m.files.reduce((n, f) => n + f.removed, 0);
  return (
    <span className="diffstat num">
      {m.files.length} {m.files.length === 1 ? "file" : "files"} <span className="add">+{added}</span> <span className="del">−{removed}</span>
    </span>
  );
}

export function CheckPills({ m }: { m: Mission }) {
  return (
    <>
      {m.checks.map((c) => (
        <span key={c.name} className={`tag ${c.status === "passed" ? "ok" : c.status === "failed" ? "bad" : ""}`} title={c.command}>
          {c.name} {c.status}
        </span>
      ))}
    </>
  );
}

/** The no-mistakes pipeline as a strip: one segment per step, so where it stands is read at a glance. */
export function Pipeline({ m }: { m: Mission }) {
  if (!m.ship || m.ship.steps.length === 0) return null;
  return (
    <ol className="pipe" aria-label="Shipping steps">
      {m.ship.steps.map((s) => (
        <li key={s.step} className={s.status} title={`${stepLabel[s.step] ?? s.step}: ${s.status.replace(/_/g, " ")}`}>
          {stepLabel[s.step] ?? s.step}
        </li>
      ))}
    </ol>
  );
}

/** A button that asks once more before doing something that can't be undone. */
export function Confirm({ label, sure, onConfirm, disabled, tone = "danger", title }: { label: string; sure: string; onConfirm: () => void; disabled?: boolean; tone?: string; title?: string }) {
  const [armed, setArmed] = useState(false);
  return (
    <button
      className={`btn ${tone}${armed ? " armed" : ""}`}
      title={title}
      disabled={disabled}
      onBlur={() => setArmed(false)}
      onClick={() => (armed ? (setArmed(false), onConfirm()) : setArmed(true))}
    >
      {armed ? sure : label}
    </button>
  );
}

type Props = {
  m: Mission;
  now: number;
  busy: boolean;
  onOpen: () => void;
  onReply: (text: string) => void;
  onCancel: () => void;
  onKeep: () => void;
  onDiscard: () => void;
  onArchive: () => void;
  onShip: () => void;
  onPage: () => void;
};

export default function MissionCard({ m, now, busy, onOpen, onReply, onCancel, onKeep, onDiscard, onArchive, onShip, onPage }: Props) {
  const [answer, setAnswer] = useState("");
  const active = isActive(m.status);
  const elapsed = m.activeSeconds + (m.turnStartedAt ? Math.max(0, (now - new Date(m.turnStartedAt).getTime()) / 1000) : 0);
  const brief = m.brief;
  const closed = m.status === "kept" || m.status === "discarded" || m.status === "shipped";

  return (
    <article className={`card${m.status === "needs_input" ? " ask" : ""}`}>
      <div className="card-top">
        <span className="repo clip">{m.repoName}</span>
        <span className="tag" title={modeInfo[m.mode].hint}>{modeInfo[m.mode].label}</span>
        {m.planId && <span className="tag" title="Launched from a cross-repo plan">Plan</span>}
        <span className="grow" />
        <span className={`tag ${statusTone(m.status)}`}>{statusLabel[m.status]}</span>
      </div>

      <button className="card-title" onClick={onOpen} title="Open the full mission">
        {brief && !active ? brief.headline : m.title}
      </button>
      {brief && !active && brief.headline !== m.title && <p className="small faint clip">Asked: {m.title}</p>}

      {active && (
        <>
          <div className="activity">
            <i className={`lamp ${m.status === "queued" ? "" : "run"}`} />
            <span className="clip">{m.activity || statusLabel[m.status]}</span>
          </div>
          {m.status === "shipping" && <Pipeline m={m} />}
          <div className="meta num">
            {m.steps > 0 && <span>step {m.steps}</span>}
            {m.status !== "queued" && <span>{duration(elapsed)}</span>}
            <span className="grow" />
            <button className="btn quiet" onClick={onCancel} disabled={busy}>Cancel</button>
          </div>
        </>
      )}

      {m.status === "needs_input" && brief && (
        <>
          {brief.questions.map((q, i) => (
            <div key={i} className="options">
              <p className="question">{q.text}</p>
              {q.options.map((o) => (
                <button key={o} className="btn option" disabled={busy} onClick={() => onReply(brief.questions.length > 1 ? `${q.text} → ${o}` : o)}>
                  {o}
                </button>
              ))}
            </div>
          ))}
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault();
              if (answer.trim()) { onReply(answer); setAnswer(""); }
            }}
          >
            <input type="text" className="grow" style={{ width: "auto" }} placeholder="Or answer in your own words" value={answer} onChange={(e) => setAnswer(e.target.value)} />
            <button className="btn" disabled={busy || !answer.trim()}>Send</button>
          </form>
        </>
      )}

      {!active && m.status !== "needs_input" && brief && brief.changes.length > 0 && (
        <ul className="bullets">
          {brief.changes.slice(0, 4).map((c, i) => <li key={i}>{c}</li>)}
        </ul>
      )}
      {!active && brief && brief.risks.length > 0 && m.status === "review" && (
        <ul className="bullets risk">
          {brief.risks.slice(0, 2).map((c, i) => <li key={i}>{c}</li>)}
        </ul>
      )}

      {!active && m.ship && <Pipeline m={m} />}
      {m.error && <p className="error">{m.error}</p>}
      {m.note && !m.error && <p className="small muted">{m.note}</p>}

      {!active && (m.files.length > 0 || m.checks.length > 0) && (
        <div className="row">
          <DiffStat m={m} />
          <CheckPills m={m} />
        </div>
      )}

      {!active && (
        <div className="row">
          {m.status === "review" && (
            <>
              <button className="btn primary" onClick={onOpen}>Review</button>
              <button className="btn go" onClick={onKeep} disabled={busy} title={`Commit to ${m.branch}. Nothing is pushed.`}>Keep</button>
              <Confirm tone="ship" label="Ship" sure="Push and open a PR?" onConfirm={onShip} disabled={busy} title="Commit, then review, test, push and open a pull request through no-mistakes" />
              <Confirm label="Discard" sure="Delete the branch?" onConfirm={onDiscard} disabled={busy} />
            </>
          )}
          {(m.status === "failed" || m.status === "cancelled") && (
            <>
              <button className="btn" onClick={() => onReply("Continue from where you stopped.")} disabled={busy}>Continue</button>
              {m.worktree && <Confirm label="Discard" sure="Delete the branch?" onConfirm={onDiscard} disabled={busy} />}
            </>
          )}
          {m.status === "done" && m.worktree && <Confirm label="Discard" sure="Delete the branch?" onConfirm={onDiscard} disabled={busy} />}
          {(m.brief || m.files.length > 0) && (
            <button className="btn quiet" onClick={onPage} disabled={busy} title="Open as a page you can read, point at and reply on">Read</button>
          )}
          <span className="grow" />
          <span className="meta num">
            {m.activeSeconds > 0 && <span>{duration(m.activeSeconds)}</span>}
            {m.costUsd > 0 && <span>{money(m.costUsd)}</span>}
          </span>
          {(closed || ((m.status === "done" || m.status === "failed" || m.status === "cancelled") && !m.worktree)) && (
            <button className="btn quiet" onClick={onArchive} disabled={busy} title="Remove from the list">Clear</button>
          )}
        </div>
      )}
    </article>
  );
}
