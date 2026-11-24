export const API = process.env.NEXT_PUBLIC_WHEELHOUSE_API ?? "http://localhost:5299";

export type Repo = {
  id: string;
  name: string;
  path: string;
  pinned: boolean;
  branch: string | null;
  dirty: number;
  ahead: number;
  behind: number;
  hasUpstream: boolean;
  lastSubject: string | null;
  lastCommitAt: string | null;
  stacks: string[];
};

export type Question = { text: string; options: string[] };

export type Brief = {
  headline: string;
  changes: string[];
  verified: string[];
  risks: string[];
  questions: Question[];
  detail: string;
};

export type FileChange = { path: string; added: number; removed: number };
export type Check = { name: string; command: string; status: "passed" | "failed" | "skipped"; seconds: number; tail: string };
export type Turn = { role: "you" | "agent"; text: string; at: string };
export type MissionEvent = { at: string; kind: string; text: string };

export type Status =
  | "queued"
  | "running"
  | "checking"
  | "shipping"
  | "shipped"
  | "needs_input"
  | "review"
  | "done"
  | "failed"
  | "cancelled"
  | "kept"
  | "discarded";

export type Mode = "scout" | "builder" | "engineer" | "night";

export type ShipStep = { step: string; status: string; findings: number; durationMs: number };
export type ShipFinding = { id: string; severity: string; file: string; description: string; action: string };
export type ShipState = { runId: string | null; outcome: string | null; error: string | null; prUrl: string | null; gateStep: string | null; steps: ShipStep[]; findings: ShipFinding[] };

export type Mission = {
  id: string;
  groupId: string | null;
  repoId: string;
  repoName: string;
  repoPath: string;
  title: string;
  mode: Mode;
  agent: string;
  status: Status;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  turnStartedAt: string | null;
  updatedAt: string;
  branch: string | null;
  worktree: string | null;
  workspace: string | null;
  baseBranch: string | null;
  baseCommit: string | null;
  activity: string;
  steps: number;
  costUsd: number;
  activeSeconds: number;
  brief: Brief | null;
  files: FileChange[];
  checks: Check[];
  turns: Turn[];
  commitSha: string | null;
  error: string | null;
  note: string | null;
  ship: ShipState | null;
};

export type Settings = {
  projectsRoot: string;
  claudePath: string;
  model: string | null;
  maxParallel: number;
  missionMinutes: number;
  runChecks: boolean;
  useTreehouse: boolean;
  nightIterations: number;
  nightHours: number;
  pinned: string[];
};

export type State = { repos: Repo[]; missions: Mission[]; settings: Settings; costToday: number };
export type LogEntry = { at: string; kind: "commit" | "mission"; text: string; who: string | null; missionId: string | null };

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API}/api${path}`, {
      ...init,
      headers: init?.body ? { "Content-Type": "application/json" } : undefined,
      cache: "no-store",
    });
  } catch {
    throw new Error("Wheelhouse isn't running. Start it with .\\dev.ps1");
  }
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      const body = await res.json();
      if (body?.error) message = body.error;
    } catch {}
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

const post = <T,>(path: string, body: unknown = {}) => call<T>(path, { method: "POST", body: JSON.stringify(body) });

export const api = {
  state: () => call<State>("/state"),
  refreshRepos: () => post<Repo[]>("/repos/refresh"),
  pin: (id: string, pinned: boolean) => post<Repo[]>(`/repos/${id}/pin`, { pinned }),
  logbook: (id: string) => call<{ repo: Repo; entries: LogEntry[] }>(`/repos/${id}/logbook`),
  create: (repoIds: string[], instruction: string, mode: Mode) => post<Mission[]>("/missions", { repoIds, instruction, mode }),
  mission: (id: string) => call<{ mission: Mission; events: MissionEvent[] }>(`/missions/${id}`),
  diff: (id: string) => call<{ diff: string; clipped: boolean }>(`/missions/${id}/diff`),
  reply: (id: string, text: string) => post<Mission>(`/missions/${id}/reply`, { text }),
  cancel: (id: string) => post<Mission>(`/missions/${id}/cancel`),
  keep: (id: string, message?: string) => post<Mission>(`/missions/${id}/keep`, { message }),
  discard: (id: string) => post<Mission>(`/missions/${id}/discard`),
  recheck: (id: string) => post<Mission>(`/missions/${id}/checks`),
  ship: (id: string, message?: string) => post<Mission>(`/missions/${id}/ship`, { message }),
  page: (id: string) => post<{ url: string }>(`/missions/${id}/page`),
  tools: () => call<{ treehouse: boolean; noMistakes: boolean; gnhf: boolean; lavish: boolean }>("/tools"),
  archive: (id: string) => call<{ ok: boolean }>(`/missions/${id}`, { method: "DELETE" }),
  saveSettings: (s: Settings) => call<Settings>("/settings", { method: "PUT", body: JSON.stringify(s) }),
};

export const isActive = (s: Status) => s === "queued" || s === "running" || s === "checking" || s === "shipping";
export const needsYou = (s: Status) => s === "needs_input" || s === "review" || s === "failed";

export const statusLabel: Record<Status, string> = {
  queued: "Queued",
  running: "Working",
  checking: "Running checks",
  shipping: "Shipping",
  shipped: "Shipped",
  needs_input: "Needs an answer",
  review: "Ready to review",
  done: "Done",
  failed: "Failed",
  cancelled: "Cancelled",
  kept: "Kept",
  discarded: "Discarded",
};

export const modeInfo: Record<Mode, { label: string; hint: string }> = {
  scout: { label: "Scout", hint: "Reads and reports. Changes nothing." },
  builder: { label: "Builder", hint: "Edits files on an isolated branch. Runs no commands." },
  engineer: { label: "Engineer", hint: "Edits, builds and tests on an isolated branch." },
  night: { label: "Unattended", hint: "Keeps working on its own for hours, one commit per step. You review the branch after." },
};

export function ago(iso: string | null, now = Date.now()): string {
  if (!iso) return "";
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  const d = Math.floor(s / 86400);
  return d < 30 ? `${d}d ago` : `${Math.floor(d / 30)}mo ago`;
}

export function duration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return m < 60 ? `${m}m ${String(s % 60).padStart(2, "0")}s` : `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, "0")}m`;
}

export const stepLabel: Record<string, string> = {
  intent: "Goal", rebase: "Rebase", review: "Review", test: "Tests", document: "Docs", lint: "Lint", push: "Push", pr: "PR", ci: "CI",
};

export const money = (n: number) => (n > 0 ? `$${n.toFixed(n < 1 ? 3 : 2)}` : "$0");
