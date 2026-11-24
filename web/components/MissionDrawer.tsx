"use client";

import { useEffect, useState } from "react";
import { api, duration, isActive, modeInfo, money, statusLabel, type Mission, type MissionEvent } from "@/lib/api";
import DiffView from "@/components/DiffView";
import { CheckPills, Confirm, DiffStat, statusTone } from "@/components/MissionCard";

type Props = {
  m: Mission;
  busy: boolean;
  onClose: () => void;
  onReply: (text: string) => void;
  onCancel: () => void;
  onKeep: (message?: string) => void;
  onDiscard: () => void;
  onRecheck?: () => void;
};

type Tab = "brief" | "changes" | "activity";

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });

export default function MissionDrawer({ m, busy, onClose, onReply, onCancel, onKeep, onDiscard, onRecheck }: Props) {
  const [tab, setTab] = useState<Tab>(m.status === "review" ? "changes" : "brief");
  const [events, setEvents] = useState<MissionEvent[]>([]);
  const [reply, setReply] = useState("");
  const [message, setMessage] = useState("");
  const active = isActive(m.status);
  const brief = m.brief;
  const canReply = !active && m.status !== "kept" && m.status !== "discarded";
  const canKeep = !active && !!m.worktree && m.files.length > 0;

  useEffect(() => {
    if (tab !== "activity") return;
    let live = true;
    api.mission(m.id).then((r) => { if (live) setEvents(r.events); }, () => {});
    return () => { live = false; };
  }, [m.id, m.updatedAt, tab]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const send = () => {
    if (!reply.trim()) return;
    onReply(reply);
    setReply("");
  };

  return (
    <div className="scrim" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <section className="drawer" role="dialog" aria-label={m.title}>
        <header className="drawer-head">
          <div className="row">
            <span className="mono muted">{m.repoName}</span>
            <span className="tag" title={modeInfo[m.mode].hint}>{modeInfo[m.mode].label}</span>
            <span className={`tag ${statusTone(m.status)}`}>{statusLabel[m.status]}</span>
            <span className="grow" />
            <button className="btn quiet" onClick={onClose}>Close · Esc</button>
          </div>
          <h2>{m.title}</h2>
          <div className="meta num">
            {m.branch && <span title="The isolated branch this mission works on">{m.branch}</span>}
            {m.baseBranch && <span>from {m.baseBranch}@{m.baseCommit?.slice(0, 7)}</span>}
            {m.steps > 0 && <span>{m.steps} steps</span>}
            {m.activeSeconds > 0 && <span>{duration(m.activeSeconds)}</span>}
            {m.costUsd > 0 && <span>{money(m.costUsd)}</span>}
          </div>
          <div className="tabs">
            <button className={tab === "brief" ? "on" : ""} onClick={() => setTab("brief")}>Brief</button>
            {m.mode !== "scout" && <button className={tab === "changes" ? "on" : ""} onClick={() => setTab("changes")}>Changes{m.files.length ? ` · ${m.files.length}` : ""}</button>}
            <button className={tab === "activity" ? "on" : ""} onClick={() => setTab("activity")}>Activity</button>
          </div>
        </header>

        <div className="drawer-body">
          {tab === "brief" && (
            <>
              {active && (
                <div className="activity">
                  <i className={`lamp ${m.status === "queued" ? "" : "run"}`} />
                  <span>{m.activity || statusLabel[m.status]}</span>
                  <span className="grow" />
                  <button className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
                </div>
              )}
              {m.error && <p className="error">{m.error}</p>}
              {brief && (
                <>
                  <p className="headline">{brief.headline}</p>
                  {brief.questions.length > 0 && m.status === "needs_input" && (
                    <div className="section">
                      <span className="cap">Needs your answer</span>
                      {brief.questions.map((q, i) => (
                        <div key={i} className="options">
                          <p className="question">{q.text}</p>
                          <div className="row">
                            {q.options.map((o) => (
                              <button key={o} className="btn option" disabled={busy} onClick={() => onReply(brief.questions.length > 1 ? `${q.text} → ${o}` : o)}>{o}</button>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                  {brief.changes.length > 0 && (
                    <div className="section">
                      <span className="cap">{m.mode === "scout" ? "Found" : "Changed"}</span>
                      <ul className="bullets">{brief.changes.map((c, i) => <li key={i}>{c}</li>)}</ul>
                    </div>
                  )}
                  {(brief.verified.length > 0 || m.checks.length > 0) && (
                    <div className="section">
                      <span className="cap">Verified</span>
                      {m.checks.length > 0 && <div className="row"><CheckPills m={m} /><span className="small faint">run by Wheelhouse</span></div>}
                      {brief.verified.length > 0 && <ul className="bullets">{brief.verified.map((c, i) => <li key={i}>{c}</li>)}</ul>}
                    </div>
                  )}
                  {brief.risks.length > 0 && (
                    <div className="section">
                      <span className="cap">Look at before accepting</span>
                      <ul className="bullets risk">{brief.risks.map((c, i) => <li key={i}>{c}</li>)}</ul>
                    </div>
                  )}
                  {brief.detail && (
                    <details className="section">
                      <summary className="cap" style={{ cursor: "pointer" }}>Agent&apos;s full notes</summary>
                      <div className="notes">{brief.detail}</div>
                    </details>
                  )}
                </>
              )}
              {m.note && <p className="muted">{m.note}</p>}
              <div className="section">
                <span className="cap">Conversation</span>
                {m.turns.map((t, i) => (
                  <div key={i} className={`turn ${t.role}`}>
                    <span className="who">{t.role}</span>
                    <p>{t.text}</p>
                  </div>
                ))}
              </div>
            </>
          )}

          {tab === "changes" && (
            <>
              <div className="row">
                <DiffStat m={m} />
                <CheckPills m={m} />
                {m.files.length === 0 && <span className="faint">No file changes yet.</span>}
                <span className="grow" />
                {!active && m.worktree && onRecheck && (
                  <button className="btn quiet" onClick={onRecheck} disabled={busy} title="Build and test this mission's folder again">Run checks again</button>
                )}
              </div>
              {m.worktree && <p className="small faint mono" style={{ overflowWrap: "anywhere" }}>Folder: {m.worktree}</p>}
              {m.checks.filter((c) => c.status === "failed").map((c) => (
                <div key={c.name} className="check">
                  <span className="cap">{c.name} failed · {c.command}</span>
                  <pre>{c.tail}</pre>
                </div>
              ))}
              {m.files.length > 0 && <DiffView missionId={m.id} version={m.updatedAt} />}
            </>
          )}

          {tab === "activity" && (
            <ul className="timeline">
              {events.length === 0 && <li><time /> <span className="faint">Nothing recorded yet.</span></li>}
              {events.map((e, i) => (
                <li key={i} className={e.kind}>
                  <time>{clock(e.at)}</time>
                  <span style={{ overflowWrap: "anywhere" }}>{e.text}</span>
                </li>
              ))}
            </ul>
          )}
        </div>

        <footer className="drawer-foot">
          {canKeep && (
            <div className="row">
              <input type="text" className="grow" style={{ width: "auto" }} placeholder={`Commit message (default: ${brief?.headline ?? m.title})`} value={message} onChange={(e) => setMessage(e.target.value)} />
              <button className="btn go" disabled={busy} onClick={() => onKeep(message || undefined)} title={`Commit to ${m.branch}. Nothing is pushed.`}>Keep and commit</button>
              <Confirm label="Discard" sure="Delete the branch?" onConfirm={onDiscard} disabled={busy} />
            </div>
          )}
          {canReply && (
            <div className="row">
              <textarea
                rows={1}
                className="grow"
                style={{ width: "auto" }}
                placeholder={m.status === "needs_input" ? "Answer in your own words" : "Ask for a change or a follow-up. The agent keeps its memory of this mission."}
                value={reply}
                onChange={(e) => setReply(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } }}
              />
              <button className="btn primary" disabled={busy || !reply.trim()} onClick={send}>Send</button>
            </div>
          )}
          {!canReply && !canKeep && <span className="small faint">{active ? "The agent is working. You can close this; it carries on." : "This mission is closed."}</span>}
        </footer>
      </section>
    </div>
  );
}
