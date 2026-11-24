"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { modeInfo, type Mode, type Repo } from "@/lib/api";

type Props = {
  repos: Repo[];
  targets: string[];
  mode: Mode;
  onTargets: (ids: string[]) => void;
  onMode: (m: Mode) => void;
  onSend: (instruction: string) => Promise<boolean>;
};

const MODES: Mode[] = ["scout", "builder", "engineer", "night"];

/** The one place instructions are typed. `@name` picks repos; the same instruction goes to each as its own mission. */
export default function Helm({ repos, targets, mode, onTargets, onMode, onSend }: Props) {
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [pick, setPick] = useState(0);
  const box = useRef<HTMLTextAreaElement>(null);

  // The @word being typed at the caret, if any.
  const mention = useMemo(() => {
    const m = /(^|\s)@([\w.\-/]*)$/.exec(text);
    return m ? m[2].toLowerCase() : null;
  }, [text]);

  const matches = useMemo(() => {
    if (mention === null) return [];
    return repos.filter((r) => !targets.includes(r.id) && r.name.toLowerCase().includes(mention)).slice(0, 7);
  }, [mention, repos, targets]);

  useEffect(() => setPick(0), [mention]);

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

  const choose = (r: Repo) => {
    onTargets([...targets, r.id]);
    setText(text.replace(/(^|\s)@[\w.\-/]*$/, "$1"));
    box.current?.focus();
  };

  const send = async () => {
    const instruction = text.trim();
    if (!instruction || targets.length === 0 || sending) return;
    setSending(true);
    const ok = await onSend(instruction);
    setSending(false);
    if (ok) setText("");
  };

  const names = new Map(repos.map((r) => [r.id, r.name]));
  const ready = text.trim().length > 0 && targets.length > 0;

  return (
    <footer className="helm">
      {matches.length > 0 && (
        <div className="suggest" role="listbox">
          {matches.map((r, i) => (
            <button key={r.id} role="option" aria-selected={i === pick} className={i === pick ? "on" : ""} onMouseDown={(e) => { e.preventDefault(); choose(r); }}>
              <span className="grow clip">{r.name}</span>
              <span className="faint">{r.branch}</span>
            </button>
          ))}
        </div>
      )}
      <div className="helm-meta">
        <span className="cap">To</span>
        {targets.length === 0 && <span className="small faint">Click repos in the fleet, or type @ to pick one</span>}
        {targets.map((id) => (
          <span key={id} className="chip">
            {names.get(id) ?? id}
            <button onClick={() => onTargets(targets.filter((t) => t !== id))} aria-label={`Remove ${names.get(id) ?? id}`}>×</button>
          </span>
        ))}
        {targets.length > 1 && <button className="btn quiet small" onClick={() => onTargets([])}>Clear</button>}
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
          placeholder="Give an instruction…  (Ctrl K to focus, Enter to send, Shift Enter for a new line)"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (matches.length > 0) {
              if (e.key === "ArrowDown") { e.preventDefault(); setPick((pick + 1) % matches.length); return; }
              if (e.key === "ArrowUp") { e.preventDefault(); setPick((pick + matches.length - 1) % matches.length); return; }
              if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); choose(matches[pick]); return; }
            }
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
          }}
        />
        <button className="btn primary" style={{ padding: "10px 18px" }} disabled={!ready || sending} onClick={send}>
          {sending ? "Sending…" : targets.length > 1 ? `Send to ${targets.length}` : "Send"}
        </button>
      </div>
    </footer>
  );
}
