"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { API, api, isActive, money, needsYou, type Mission, type Mode, type Plan, type Repo, type Settings } from "@/lib/api";
import Chart from "@/components/Chart";
import Fleet from "@/components/Fleet";
import Helm from "@/components/Helm";
import MissionCard from "@/components/MissionCard";
import MissionDrawer from "@/components/MissionDrawer";
import PlanCard from "@/components/PlanCard";
import { Logbook, SettingsPanel } from "@/components/Panels";

const remember = {
  get<T>(key: string, fallback: T): T {
    try {
      const raw = localStorage.getItem(`wheelhouse.${key}`);
      return raw ? (JSON.parse(raw) as T) : fallback;
    } catch {
      return fallback;
    }
  },
  set(key: string, value: unknown) {
    try { localStorage.setItem(`wheelhouse.${key}`, JSON.stringify(value)); } catch {}
  },
};

export default function Bridge() {
  const [repos, setRepos] = useState<Repo[]>([]);
  const [missions, setMissions] = useState<Mission[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [offline, setOffline] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [targets, setTargets] = useState<string[]>([]);
  const [mode, setMode] = useState<Mode>("scout");
  const [openId, setOpenId] = useState<string | null>(null);
  const [logbookId, setLogbookId] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const upsert = useCallback((m: Mission) => {
    setMissions((list) => {
      const i = list.findIndex((x) => x.id === m.id);
      if (i < 0) return [m, ...list];
      const copy = list.slice();
      copy[i] = m;
      return copy;
    });
  }, []);

  const upsertPlan = useCallback((p: Plan) => {
    setPlans((list) => (list.some((x) => x.id === p.id) ? list.map((x) => (x.id === p.id ? p : x)) : [p, ...list]));
  }, []);

  const load = useCallback(async () => {
    try {
      const s = await api.state();
      setRepos(s.repos);
      setMissions(s.missions);
      setPlans(s.plans ?? []);
      setSettings(s.settings);
      setOffline(null);
    } catch (e) {
      setOffline((e as Error).message);
    }
    setLoaded(true);
  }, []);

  useEffect(() => {
    setTargets(remember.get<string[]>("targets", []));
    setMode(remember.get<Mode>("mode", "scout"));
    load();
  }, [load]);

  useEffect(() => remember.set("targets", targets), [targets]);
  useEffect(() => remember.set("mode", mode), [mode]);

  // Live updates. The browser reconnects on its own; a reconnect also reloads everything so nothing is missed.
  useEffect(() => {
    const es = new EventSource(`${API}/api/stream`);
    let dropped = false;
    es.onmessage = (e) => {
      const msg = JSON.parse(e.data) as { type: string; payload: unknown };
      if (msg.type === "mission") upsert(msg.payload as Mission);
      else if (msg.type === "repos") setRepos(msg.payload as Repo[]);
      else if (msg.type === "plan") upsertPlan(msg.payload as Plan);
      else if (msg.type === "plan-removed") setPlans((l) => l.filter((p) => p.id !== (msg.payload as { id: string }).id));
      else if (msg.type === "mission-removed") setMissions((l) => l.filter((m) => m.id !== (msg.payload as { id: string }).id));
    };
    es.onopen = () => { if (dropped) { dropped = false; load(); } };
    es.onerror = () => { dropped = true; setOffline("Lost contact with Wheelhouse. Trying again…"); };
    return () => es.close();
  }, [load, upsert, upsertPlan]);

  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  const act = useCallback(async (id: string, fn: () => Promise<Mission | unknown>) => {
    setBusy(id);
    setToast(null);
    try {
      const r = await fn();
      if (r && typeof r === "object" && "status" in r) upsert(r as Mission);
    } catch (e) {
      setToast((e as Error).message);
    }
    setBusy(null);
  }, [upsert]);

  const send = async (instruction: string) => {
    setToast(null);
    try {
      (await api.create(targets, instruction, mode)).forEach(upsert);
      return true;
    } catch (e) {
      setToast((e as Error).message);
      return false;
    }
  };

  const startPlan = async (goal: string) => {
    setToast(null);
    try {
      upsertPlan(await api.createPlan(goal, targets));
      return true;
    } catch (e) {
      setToast((e as Error).message);
      return false;
    }
  };

  const planAct = async (id: string, fn: () => Promise<Plan | { ok: boolean }>) => {
    setBusy(id);
    setToast(null);
    try {
      const r = await fn();
      if ("goal" in r) upsertPlan(r);
      else setPlans((l) => l.filter((p) => p.id !== id));
    } catch (e) {
      setToast((e as Error).message);
    }
    setBusy(null);
  };

  const refresh = async () => {
    setRefreshing(true);
    try { setRepos(await api.refreshRepos()); } catch (e) { setToast((e as Error).message); }
    setRefreshing(false);
  };

  const handlers = (m: Mission) => ({
    onOpen: () => setOpenId(m.id),
    onReply: (text: string) => act(m.id, () => api.reply(m.id, text)),
    onCancel: () => act(m.id, () => api.cancel(m.id)),
    onKeep: (message?: string) => act(m.id, () => api.keep(m.id, typeof message === "string" ? message : undefined)),
    onDiscard: () => act(m.id, () => api.discard(m.id)),
    onRecheck: () => act(m.id, () => api.recheck(m.id)),
    onShip: () => act(m.id, () => api.ship(m.id)),
    onPage: () => act(m.id, async () => { const { url } = await api.page(m.id); window.open(url, "_blank", "noopener"); }),
    onArchive: () => act(m.id, async () => { await api.archive(m.id); setMissions((l) => l.filter((x) => x.id !== m.id)); }),
  });

  const sorted = useMemo(() => missions.slice().sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)), [missions]);
  const waiting = sorted.filter((m) => needsYou(m.status));
  const underway = sorted.filter((m) => isActive(m.status));
  const finished = sorted.filter((m) => !needsYou(m.status) && !isActive(m.status));
  const costToday = missions.filter((m) => new Date(m.createdAt).toDateString() === new Date(now).toDateString()).reduce((n, m) => n + m.costUsd, 0);
  const open = openId ? missions.find((m) => m.id === openId) : undefined;

  useEffect(() => {
    document.title = waiting.length > 0 ? `(${waiting.length}) Wheelhouse` : "Wheelhouse";
  }, [waiting.length]);

  const card = (m: Mission) => <MissionCard key={m.id} m={m} now={now} busy={busy === m.id} {...handlers(m)} />;

  return (
    <div className="bridge">
      <header className="band">
        <span className="mark">
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
            <circle cx="12" cy="12" r="7" />
            <circle cx="12" cy="12" r="1.8" fill="currentColor" stroke="none" />
            <path d="M12 2v5M12 17v5M2 12h5M17 12h5M4.9 4.9l3.6 3.6M15.5 15.5l3.6 3.6M19.1 4.9l-3.6 3.6M8.5 15.5l-3.6 3.6" />
          </svg>
          Wheelhouse
        </span>
        {(offline || toast) && <span className="toast">{toast ?? offline}</span>}
        <div className="gauges">
          <span className="gauge"><b className="num">{underway.length}</b> working</span>
          <span className={`gauge${waiting.length ? " hot" : ""}`}><b className="num">{waiting.length}</b> need you</span>
          <span className="gauge"><b className="num">{money(costToday)}</b> today</span>
          <button className="btn quiet" onClick={() => setShowSettings(true)} disabled={!settings}>Settings</button>
        </div>
      </header>

      <div className="deck">
        <aside className="col chartroom" aria-label="Fleet">
          <Chart repos={repos} missions={missions} targets={targets} now={now} onToggle={(id) => setTargets((t) => (t.includes(id) ? t.filter((x) => x !== id) : [...t, id]))} onLogbook={setLogbookId} />
        <Fleet
          repos={repos}
          missions={missions}
          targets={targets}
          now={now}
          onToggle={(id) => setTargets((t) => (t.includes(id) ? t.filter((x) => x !== id) : [...t, id]))}
          onPin={(r) => api.pin(r.id, !r.pinned).then(setRepos, (e: Error) => setToast(e.message))}
          onLogbook={setLogbookId}
          onRefresh={refresh}
          refreshing={refreshing}
        />
        </aside>

        <main className="col" aria-label="Missions">
          {plans.length > 0 && (
            <>
              <div className="col-head">
                <span className="cap">Plans</span>
                <span className="count">{plans.length}</span>
              </div>
              {plans.map((p) => (
                <PlanCard
                  key={p.id}
                  plan={p}
                  missions={missions}
                  busy={busy === p.id}
                  onLaunch={(steps) => planAct(p.id, () => api.launchPlan(p.id, steps))}
                  onRevise={(text) => planAct(p.id, () => api.revisePlan(p.id, text))}
                  onRemove={() => planAct(p.id, () => api.removePlan(p.id))}
                  onOpenMission={setOpenId}
                />
              ))}
            </>
          )}
          <div className="col-head">
            <span className="cap">Under way</span>
            <span className="count">{underway.length}</span>
          </div>
          {underway.map(card)}
          {underway.length === 0 && (
            <div className="empty">
              <b>{loaded ? "No agents are working" : "Loading…"}</b>
              {loaded && !offline && <span>Pick one or more repos on the left, choose how much the agent may do, and type an instruction below. Each repo gets its own agent, working in parallel.</span>}
              {offline && <span>{offline}</span>}
            </div>
          )}
          {finished.length > 0 && (
            <>
              <div className="col-head" style={{ marginTop: 10 }}>
                <span className="cap">Finished</span>
                <span className="count">{finished.length}</span>
              </div>
              {finished.map(card)}
            </>
          )}
        </main>

        <aside className="col inbox" aria-label="Needs you">
          <div className="col-head">
            <span className="cap" style={waiting.length ? { color: "var(--amber)" } : undefined}>Needs you</span>
            <span className="count">{waiting.length}</span>
          </div>
          {waiting.map(card)}
          {waiting.length === 0 && (
            <div className="empty">
              <b>Nothing is waiting on you</b>
              <span>Questions from agents, changes ready to review and failures show up here.</span>
            </div>
          )}
        </aside>
      </div>

      <Helm repos={repos} targets={targets.filter((t) => repos.length === 0 || repos.some((r) => r.id === t))} mode={mode} onTargets={setTargets} onMode={setMode} onSend={send} onPlan={startPlan} />

      {open && <MissionDrawer key={open.id} m={open} busy={busy === open.id} onClose={() => setOpenId(null)} {...handlers(open)} />}
      {logbookId && (
        <Logbook
          repoId={logbookId}
          onClose={() => setLogbookId(null)}
          onOpenMission={(id) => { setLogbookId(null); setOpenId(id); }}
          onTarget={() => { setTargets((t) => (t.includes(logbookId) ? t : [...t, logbookId])); setLogbookId(null); document.getElementById("helm-input")?.focus(); }}
        />
      )}
      {showSettings && settings && <SettingsPanel settings={settings} onClose={() => setShowSettings(false)} onSaved={(s) => { setSettings(s); refresh(); }} />}
    </div>
  );
}
