"use client";

import { useEffect, useState } from "react";
import { modeInfo, statusLabel, type Mission, type Mode, type Plan, type PlanStepEdit } from "@/lib/api";
import { Confirm, statusTone } from "@/components/MissionCard";

type Props = {
  plan: Plan;
  missions: Mission[];
  busy: boolean;
  onLaunch: (steps: PlanStepEdit[]) => void;
  onRevise: (text: string) => void;
  onRemove: () => void;
  onOpenMission: (id: string) => void;
};

const STEP_MODES: Mode[] = ["scout", "builder", "engineer"];

/** One goal split across repos. The developer reads, edits and ticks the tasks; nothing runs until Launch. */
export default function PlanCard({ plan, missions, busy, onLaunch, onRevise, onRemove, onOpenMission }: Props) {
  const [edits, setEdits] = useState<Record<string, PlanStepEdit>>({});
  const [note, setNote] = useState("");

  // A fresh draft from the planner replaces whatever was being edited.
  useEffect(() => {
    setEdits(Object.fromEntries(plan.steps.map((s) => [s.repoId, { repoId: s.repoId, include: s.include, mode: s.mode, spec: s.spec }])));
  }, [plan.id, plan.updatedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const set = (repoId: string, change: Partial<PlanStepEdit>) => setEdits((e) => ({ ...e, [repoId]: { ...e[repoId], ...change } }));
  const chosen = plan.steps.filter((s) => edits[s.repoId]?.include ?? s.include).length;
  const byId = new Map(missions.map((m) => [m.id, m]));

  return (
    <article className="card plan">
      <div className="card-top">
        <span className="tag run">Plan</span>
        <span className="repo clip">{plan.steps.length + plan.skipped.length || plan.candidates.length} repos considered</span>
        <span className="grow" />
        <span className={`tag ${plan.status === "ready" ? "wait" : plan.status === "failed" ? "bad" : plan.status === "launched" ? "ok" : "run"}`}>
          {plan.status === "drafting" ? "Planning" : plan.status === "ready" ? "Ready for you" : plan.status === "launched" ? "Launched" : "Failed"}
        </span>
      </div>
      <p className="card-title">{plan.goal}</p>

      {plan.status === "drafting" && (
        <div className="activity">
          <i className="lamp run" />
          <span className="clip">{plan.activity || "Reading the repos"}</span>
        </div>
      )}

      {plan.error && <p className="error">{plan.error}</p>}
      {plan.status !== "drafting" && plan.summary && <p className="small muted">{plan.summary}</p>}
      {plan.question && plan.status === "ready" && (
        <p className="question" style={{ color: "var(--amber)" }}>The planner asks: {plan.question}</p>
      )}

      {plan.status === "ready" && plan.steps.length > 0 && (
        <ul className="steps">
          {plan.steps.map((s) => {
            const e = edits[s.repoId] ?? { repoId: s.repoId, include: s.include, mode: s.mode, spec: s.spec };
            return (
              <li key={s.repoId} className={e.include ? "" : "off"}>
                <label className="step-head">
                  <input type="checkbox" checked={e.include} onChange={(ev) => set(s.repoId, { include: ev.target.checked })} />
                  <span className="mono">{s.repoName}</span>
                </label>
                <select value={e.mode ?? s.mode} onChange={(ev) => set(s.repoId, { mode: ev.target.value as Mode })} disabled={!e.include} aria-label={`How ${s.repoName} is done`} title={modeInfo[(e.mode ?? s.mode) as Mode].hint}>
                  {STEP_MODES.map((m) => <option key={m} value={m}>{modeInfo[m].label}</option>)}
                </select>
                <textarea rows={2} value={e.spec ?? s.spec} onChange={(ev) => set(s.repoId, { spec: ev.target.value })} disabled={!e.include} aria-label={`Task for ${s.repoName}`} />
                {(s.why || s.outOfScope) && (
                  <p className="small faint step-why">
                    {s.why}
                    {s.why && s.outOfScope ? " " : ""}
                    {s.outOfScope && <>Leaves alone: {s.outOfScope}</>}
                  </p>
                )}
              </li>
            );
          })}
        </ul>
      )}

      {plan.status === "launched" && (
        <ul className="steps launched">
          {plan.steps.filter((s) => s.missionId).map((s) => {
            const m = byId.get(s.missionId!);
            return (
              <li key={s.repoId}>
                <button className="step-link mono" onClick={() => m && onOpenMission(m.id)} disabled={!m}>{s.repoName}</button>
                <span className={`tag ${m ? statusTone(m.status) : ""}`}>{m ? statusLabel[m.status] : "Cleared"}</span>
                <span className="small muted clip">{m?.activity || m?.brief?.headline || s.spec}</span>
              </li>
            );
          })}
        </ul>
      )}

      {plan.status !== "drafting" && plan.skipped.length > 0 && (
        <details className="small faint">
          <summary style={{ cursor: "pointer" }}>Left out: {plan.skipped.map((s) => s.repoName).join(", ")}</summary>
          <ul className="bullets" style={{ marginTop: 6 }}>
            {plan.skipped.map((s) => <li key={s.repoId}><span className="mono">{s.repoName}</span>: {s.why}</li>)}
          </ul>
        </details>
      )}

      {(plan.status === "ready" || plan.status === "failed") && (
        <form
          className="row"
          onSubmit={(ev) => {
            ev.preventDefault();
            if (note.trim()) { onRevise(note); setNote(""); }
          }}
        >
          <input type="text" className="grow" style={{ width: "auto" }} placeholder={plan.question ? "Answer the planner" : "Tell the planner what to change in this plan"} value={note} onChange={(ev) => setNote(ev.target.value)} />
          <button className="btn" disabled={busy || !note.trim()}>Plan again</button>
        </form>
      )}

      <div className="row">
        {plan.status === "ready" && plan.steps.length > 0 && (
          <button className="btn primary" disabled={busy || chosen === 0} onClick={() => onLaunch(Object.values(edits))}>
            Launch {chosen} {chosen === 1 ? "mission" : "missions"}
          </button>
        )}
        <span className="grow" />
        {plan.status === "launched" ? (
          <button className="btn quiet" onClick={onRemove} disabled={busy} title="Remove the plan from the list. Its missions stay.">Clear</button>
        ) : (
          <Confirm label={plan.status === "drafting" ? "Stop" : "Dismiss"} sure="Drop this plan?" onConfirm={onRemove} disabled={busy} />
        )}
      </div>
    </article>
  );
}
