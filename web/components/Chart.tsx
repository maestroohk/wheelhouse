"use client";

import { useMemo, useState } from "react";
import { ago, isActive, needsYou, type Mission, type Repo } from "@/lib/api";

type Props = {
  repos: Repo[];
  missions: Mission[];
  targets: string[];
  now: number;
  onToggle: (id: string) => void;
  onLogbook: (id: string) => void;
};

const C = 200; // centre of the 400 x 400 scope
const RINGS = [
  { r: 46, label: "today" },
  { r: 92, label: "this week" },
  { r: 138, label: "this month" },
  { r: 182, label: "older" },
];
const DAY = 86_400_000;

function hash(s: string) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}

/** How far from the centre a repo sits: the centre is now, each ring out is further back in time. */
function distance(ageDays: number, working: boolean) {
  if (working) return 20;
  if (ageDays <= 1) return 28 + ageDays * 14;
  if (ageDays <= 7) return 50 + ((ageDays - 1) / 6) * 38;
  if (ageDays <= 30) return 96 + ((ageDays - 7) / 23) * 38;
  return 142 + Math.min(1, Math.log(ageDays / 30) / Math.log(12)) * 34;
}

type Blip = { repo: Repo; x: number; y: number; working: number; waiting: number; age: number };

/**
 * The fleet as a scope. Every repo is a blip: the closer to the centre, the more recently it was touched.
 * Its bearing never changes (it comes from the name), so a repo is always found in the same direction.
 */
export default function Chart({ repos, missions, targets, now, onToggle, onLogbook }: Props) {
  const [hover, setHover] = useState<string | null>(null);

  const blips = useMemo(() => {
    const state = new Map<string, { working: number; waiting: number }>();
    for (const m of missions) {
      const e = state.get(m.repoId) ?? { working: 0, waiting: 0 };
      if (isActive(m.status)) e.working++;
      if (needsYou(m.status)) e.waiting++;
      state.set(m.repoId, e);
    }
    const placed = repos.map((repo) => {
      const s = state.get(repo.id) ?? { working: 0, waiting: 0 };
      const age = repo.lastCommitAt ? Math.max(0, (now - new Date(repo.lastCommitAt).getTime()) / DAY) : 400;
      // Something waiting on you belongs near the centre, whatever the last commit says.
      const felt = s.waiting > 0 ? Math.min(age, 0.9) : age;
      return { repo, ...s, age, d: distance(felt, s.working > 0), a: ((hash(repo.name) % 3600) / 3600) * Math.PI * 2 };
    });
    // Nudge neighbours apart along their ring so no two blips sit on top of each other.
    placed.sort((p, q) => p.d - q.d);
    for (let pass = 0; pass < 4; pass++)
      for (let i = 0; i < placed.length; i++)
        for (let j = i + 1; j < placed.length; j++) {
          const p = placed[i], q = placed[j];
          if (q.d - p.d > 20) break;
          const dx = Math.cos(p.a) * p.d - Math.cos(q.a) * q.d;
          const dy = Math.sin(p.a) * p.d - Math.sin(q.a) * q.d;
          if (Math.hypot(dx, dy) < 22) q.a += 24 / Math.max(q.d, 20);
        }
    return placed.map<Blip>((p) => ({ repo: p.repo, working: p.working, waiting: p.waiting, age: p.age, x: C + Math.cos(p.a) * p.d, y: C + Math.sin(p.a) * p.d }));
  }, [repos, missions, Math.floor(now / 60_000)]); // eslint-disable-line react-hooks/exhaustive-deps

  const focus = blips.find((b) => b.repo.id === hover);
  const thisWeek = blips.filter((b) => b.age <= 7).length;
  const dirty = repos.filter((r) => r.dirty > 0).length;

  return (
    <div className="scope">
      <svg viewBox="0 0 400 400" role="group" aria-label="Fleet chart: repos by how recently they were touched">
        <defs>
          <linearGradient id="sweep" x1="0" y1="0" x2="1" y2="0">
            <stop offset="0" stopColor="var(--sonar)" stopOpacity="0" />
            <stop offset="1" stopColor="var(--sonar)" stopOpacity="0.28" />
          </linearGradient>
        </defs>
        <circle cx={C} cy={C} r={RINGS[3].r + 8} className="scope-face" />
        {RINGS.map((ring) => (
          <g key={ring.label}>
            <circle cx={C} cy={C} r={ring.r} className="ring" />
            <text x={C} y={C - ring.r + 11} className="ring-label" textAnchor="middle">{ring.label}</text>
          </g>
        ))}
        <path d={`M${C - 190} ${C}H${C + 190}M${C} ${C - 190}V${C + 190}`} className="ring cross" />
        <g className="sweep">
          <path d={`M${C} ${C} L${C + 182} ${C} A182 182 0 0 0 ${C + 182 * Math.cos(-0.6)} ${C + 182 * Math.sin(-0.6)} Z`} fill="url(#sweep)" />
          <line x1={C} y1={C} x2={C + 182} y2={C} className="sweep-line" />
        </g>
        <circle cx={C} cy={C} r="2.5" className="now-dot" />

        {blips.map((b) => {
          const on = targets.includes(b.repo.id);
          const tone = b.waiting ? "wait" : b.working ? "run" : b.repo.dirty ? "dirty" : "idle";
          const labelled = on || b.working > 0 || b.waiting > 0 || b.repo.pinned || hover === b.repo.id || b.age <= 30;
          const left = b.x > 290;
          const size = 3.6 + Math.min(b.repo.dirty, 24) / 8;
          return (
            <g
              key={b.repo.id}
              className={`blip ${tone}${on ? " on" : ""}`}
              role="button"
              tabIndex={0}
              aria-pressed={on}
              aria-label={`${b.repo.name}${on ? ", selected" : ""}`}
              onClick={() => onToggle(b.repo.id)}
              onDoubleClick={() => onLogbook(b.repo.id)}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onToggle(b.repo.id); } }}
              onMouseEnter={() => setHover(b.repo.id)}
              onMouseLeave={() => setHover(null)}
              onFocus={() => setHover(b.repo.id)}
              onBlur={() => setHover(null)}
            >
              <circle cx={b.x} cy={b.y} r="11" className="hit" />
              {b.working > 0 && <circle cx={b.x} cy={b.y} r="6" className="ping" />}
              {on && <path className="reticle" d={`M${b.x - 9} ${b.y - 5}v-4h4M${b.x + 5} ${b.y - 9}h4v4M${b.x + 9} ${b.y + 5}v4h-4M${b.x - 5} ${b.y + 9}h-4v-4`} />}
              {b.waiting > 0 ? (
                <rect x={b.x - 4.5} y={b.y - 4.5} width="9" height="9" transform={`rotate(45 ${b.x} ${b.y})`} className="mark" />
              ) : (
                <circle cx={b.x} cy={b.y} r={size} className="mark" />
              )}
              {labelled && (
                <text x={left ? b.x - 10 : b.x + 10} y={b.y + 3.5} textAnchor={left ? "end" : "start"} className="blip-label">
                  {b.repo.name.length > 18 ? `${b.repo.name.slice(0, 17)}…` : b.repo.name}
                </text>
              )}
            </g>
          );
        })}
      </svg>
      <p className="readout" aria-live="polite">
        {focus ? (
          <>
            <b>{focus.repo.name}</b> · {focus.repo.branch ?? "no branch"}
            {focus.repo.dirty > 0 && <> · <span className="warn">{focus.repo.dirty} uncommitted</span></>}
            {focus.working > 0 && <> · {focus.working} working</>}
            {focus.waiting > 0 && <> · <span className="warn">{focus.waiting} need you</span></>}
            {focus.repo.lastCommitAt && <> · last commit {ago(focus.repo.lastCommitAt, now)}</>}
          </>
        ) : (
          <>{repos.length} repos · {thisWeek} touched this week · {dirty} with uncommitted work · click a blip to aim at it</>
        )}
      </p>
    </div>
  );
}
