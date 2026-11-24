"use client";

import { useEffect, useMemo, useState } from "react";
import { api } from "@/lib/api";

type FileDiff = { path: string; lines: string[]; added: number; removed: number };

function split(diff: string): FileDiff[] {
  const files: FileDiff[] = [];
  let cur: FileDiff | null = null;
  for (const line of diff.split(/\r?\n/)) {
    if (line.startsWith("diff --git ")) {
      const m = / b\/(.+)$/.exec(line);
      cur = { path: m ? m[1] : line.slice(11), lines: [], added: 0, removed: 0 };
      files.push(cur);
      continue;
    }
    if (!cur) continue;
    if (line.startsWith("index ") || line.startsWith("--- ") || line.startsWith("+++ ") || line.startsWith("new file") || line.startsWith("deleted file") || line.startsWith("similarity") || line.startsWith("rename ")) continue;
    if (line.startsWith("+")) cur.added++;
    else if (line.startsWith("-")) cur.removed++;
    cur.lines.push(line);
  }
  return files;
}

export default function DiffView({ missionId, version }: { missionId: string; version: string }) {
  const [diff, setDiff] = useState<string | null>(null);
  const [clipped, setClipped] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.diff(missionId).then(
      (r) => { if (live) { setDiff(r.diff); setClipped(r.clipped); setError(null); } },
      (e: Error) => { if (live) setError(e.message); },
    );
    return () => { live = false; };
  }, [missionId, version]);

  const files = useMemo(() => (diff ? split(diff) : []), [diff]);

  if (error) return <p className="error">{error}</p>;
  if (diff === null) return <p className="faint">Loading the changes…</p>;
  if (files.length === 0) return <p className="faint">No file changes.</p>;

  return (
    <div className="section">
      {files.map((f, i) => (
        <details key={f.path} className="file" open={files.length <= 6 || i < 3}>
          <summary>
            <span className="path grow">{f.path}</span>
            <span className="diffstat num"><span className="add">+{f.added}</span> <span className="del">−{f.removed}</span></span>
          </summary>
          <div className="diff">
            {f.lines.length === 0 && <div className="hunk">Binary or empty file</div>}
            {f.lines.map((l, n) => (
              <div key={n} className={l.startsWith("@@") ? "hunk" : l.startsWith("+") ? "add" : l.startsWith("-") ? "del" : undefined}>{l || " "}</div>
            ))}
          </div>
        </details>
      ))}
      {clipped && <p className="small faint">The diff is very large and was cut short. Open the branch in your editor to see all of it.</p>}
    </div>
  );
}
