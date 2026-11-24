"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { modeInfo, type Mode, type Playbook, type Repo } from "@/lib/api";

type Props = {
  repos: Repo[];
  playbooks: Playbook[];
  targets: string[];
  mode: Mode;
  onTargets: (ids: string[]) => void;
  onMode: (m: Mode) => void;
  onSend: (instruction: string) => Promise<boolean>;
  onPlan: (goal: string) => Promise<boolean>;
  onSavePlaybook: (name: string, text: string, mode: Mode) => Promise<boolean>;
  onRemovePlaybook: (id: string) => void;
};

const MODES: Mode[] = ["scout", "builder", "engineer", "night"];
const BLANK = /\{\{[^}]*\}\}/;

/**
 * The one place instructions are typed. `@name` picks repos; `/` picks a saved instruction (a playbook).
 * The same instruction goes to each picked repo as its own mission.
 */
export default function Helm({ repos, playbooks, targets, mode, onTargets, onMode, onSend, onPlan, onSavePlaybook, onRemovePlaybook }: Props) {
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [pick, setPick] = useState(0);
  const [naming, setNaming] = useState<string | null>(null);
  const box = useRef<HTMLTextAreaElement>(null);

  // The @word being typed at the caret, if any.
  const mention = useMemo(() => {
    const m = /(^|\s)@([\w.\-/]*)$/.exec(text);
    return m ? m[2].toLowerCase() : null;
  }, [text]);

  // A leading slash with nothing else yet means "show me the playbooks".
  const slash = useMemo(() => {
    const m = /^\/([^\n]*)$/.exec(text);
    return m ? m[1].toLowerCase().trim() : null;
  }, [text]);

  const repoMatches = useMemo(() => {
    if (mention === null) return [];
    return repos.filter((r) => !targets.includes(r.id) && r.name.toLowerCase().includes(mention)).slice(0, 7);
  }, [mention, repos, targets]);

  const playMatches = useMemo(() => {
    if (slash === null) return [];
    return playbooks.filter((p) => p.name.toLowerCase().includes(slash) || p.id.includes(slash)).slice(0, 9);
  }, [slash, playbooks]);

  const listLength = playMatches.length || repoMatches.length;

  useEffect(() => setPick(0), [mention, slash]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        box.current?.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  useEffect(() => {
    const el = box.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = text ? `${Math.min(180, el.scrollHeight + 2)}px` : "";
  }, [text]);

  const chooseRepo = (r: Repo) => {
    onTargets([...targets, r.id]);
    setText(text.replace(/(^|\s)@[\w.\-/]*$/, "$1"));
    box.current?.focus();
  };

  const choosePlaybook = (p: Playbook) => {
    setText(p.text);
    onMode(p.mode);
    // Put the caret on the first blank so it can be typed over straight away.
    requestAnimationFrame(() => {
      const el = box.current;
      if (!el) return;
      el.focus();
      const blank = BLANK.exec(p.text);
      if (blank) el.setSelectionRange(blank.index, blank.index + blank[0].length);
    });
  };

  const run = async (action: (t: string) => Promise<boolean>) => {
    const instruction = text.trim();
    if (!instruction || sending) return;
    setSending(true);
    const ok = await action(instruction);
    setSending(false);
    if (ok) setText("");
  };

  const send = () => { if (targets.length > 0 && !blankLeft) run(onSend); };

  const names = new Map(repos.map((r) => [r.id, r.name]));
  const blankLeft = BLANK.test(text);
  const ready = text.trim().length > 0 && targets.length > 0 && slash === null && !blankLeft;

  return (
    <footer className="helm">
      {playMatches.length > 0 && (
        <div className="suggest wide" role="listbox" aria-label="Playbooks">
          {playMatches.map((p, i) => (
            <div key={p.id} className={`suggest-row${i === pick ? " on" : ""}`}>
              <button role="option" aria-selected={i === pick} onMouseDown={(e) => { e.preventDefault(); choosePlaybook(p); }}>
                <span className="grow clip">{p.name}</span>
                <span className="faint">{modeInfo[p.mode].label}</span>
              </button>
              {!p.builtIn && (
                <button className="icon-btn" title="Remove this playbook" onMouseDown={(e) => { e.preventDefault(); onRemovePlaybook(p.id); }}>×</button>
              )}
            </div>
          ))}
        </div>
      )}
      {playMatches.length === 0 && repoMatches.length > 0 && (
        <div className="suggest" role="listbox" aria-label="Repos">
          {repoMatches.map((r, i) => (
            <div key={r.id} className={`suggest-row${i === pick ? " on" : ""}`}>
              <button role="option" aria-selected={i === pick} onMouseDown={(e) => { e.preventDefault(); chooseRepo(r); }}>
                <span className="grow clip">{r.name}</span>
                <span className="faint">{r.branch}</span>
              </button>
            </div>
          ))}
        </div>
      )}
      <div className="helm-meta">
        <span className="cap">To</span>
        {targets.length === 0 && <span className="small faint">Click repos in the chart, or type @ to pick one. Type / for a saved instruction.</span>}
        {targets.map((id) => (
          <span key={id} className="chip">
            {names.get(id) ?? id}
            <button onClick={() => onTargets(targets.filter((t) => t !== id))} aria-label={`Remove ${names.get(id) ?? id}`}>×</button>
          </span>
        ))}
        {targets.length > 1 && <button className="btn quiet small" onClick={() => onTargets([])}>Clear</button>}
        {blankLeft && <span className="small" style={{ color: "var(--amber)" }}>Fill in the {"{{blank}}"} first</span>}
        {naming === null && text.trim().length >= 20 && slash === null && (
          <button className="btn quiet small" onClick={() => setNaming("")} title="Keep this instruction to reuse with /">Save as playbook</button>
        )}
        {naming !== null && (
          <form
            className="row"
            onSubmit={async (e) => {
              e.preventDefault();
              if (naming.trim() && (await onSavePlaybook(naming.trim(), text.trim(), mode))) setNaming(null);
            }}
          >
            <input type="text" autoFocus style={{ width: 190, padding: "3px 8px" }} placeholder="Name it" value={naming} onChange={(e) => setNaming(e.target.value)} onKeyDown={(e) => { if (e.key === "Escape") setNaming(null); }} aria-label="Playbook name" />
            <button className="btn small" disabled={!naming.trim()}>Save</button>
          </form>
        )}
        <span className="grow" />
        <span className="small faint">{modeInfo[mode].hint}</span>
        <span className="seg" role="radiogroup" aria-label="How much the agent may do">
          {MODES.map((m) => (
            <button key={m} role="radio" aria-checked={mode === m} className={mode === m ? "on" : ""} onClick={() => onMode(m)}>
              {modeInfo[m].label}
            </button>
          ))}
        </span>
      </div>
      <div className="helm-line">
        <textarea
          id="helm-input"
          ref={box}
          rows={1}
          className="grow"
          placeholder="Give an instruction…  ( / for playbooks, @ for repos, Enter to send, Ctrl K to focus )"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (listLength > 0) {
              if (e.key === "ArrowDown") { e.preventDefault(); setPick((pick + 1) % listLength); return; }
              if (e.key === "ArrowUp") { e.preventDefault(); setPick((pick + listLength - 1) % listLength); return; }
              if (e.key === "Enter" || e.key === "Tab") {
                e.preventDefault();
                if (playMatches.length > 0) choosePlaybook(playMatches[pick]);
                else chooseRepo(repoMatches[pick]);
                return;
              }
              if (e.key === "Escape") { setText(""); return; }
            }
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
          }}
        />
        <button
          className="btn"
          style={{ padding: "10px 14px" }}
          disabled={!text.trim() || sending || slash !== null || blankLeft}
          onClick={() => run(onPlan)}
          title={targets.length > 0 ? "A planner reads the picked repos and writes a task for each. Nothing starts until you approve it." : "A planner reads your active repos and writes a task for each one the goal touches. Nothing starts until you approve it."}
        >
          Plan first
        </button>
        <button className="btn primary" style={{ padding: "10px 18px" }} disabled={!ready || sending} onClick={send}>
          {sending ? "Sending…" : targets.length > 1 ? `Send to ${targets.length}` : "Send"}
        </button>
      </div>
    </footer>
  );
}
