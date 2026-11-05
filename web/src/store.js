import { useSyncExternalStore } from 'react';
import { api } from './api.js';

const HISTORY = 48;

let state = {
  ready: false,
  connected: false,
  version: '',
  projects: [],          // [{config, runtime}]
  settings: null,
  env: null,
  system: { cpu: 0, usedMb: 0, totalMb: 0, running: 0, total: 0 },
  activity: [],
  history: {},           // id -> {cpu:[], mem:[]}
  sysHistory: [],        // cpu samples
  toasts: [],
  installs: {},          // tool id -> {id,status,message} from winget installs
};

const listeners = new Set();
const emit = () => listeners.forEach((l) => l());
const set = (patch) => { state = { ...state, ...(typeof patch === 'function' ? patch(state) : patch) }; emit(); };

export const useStore = (selector = (s) => s) => useSyncExternalStore(
  (cb) => { listeners.add(cb); return () => listeners.delete(cb); },
  () => selector(state),
);
export const getState = () => state;

let toastId = 1;
export function toast(level, title, message, ttl = 5200) {
  const id = toastId++;
  set((s) => ({ toasts: [...s.toasts.slice(-3), { id, level, title, message }] }));
  if (ttl) setTimeout(() => dismissToast(id), ttl);
}
export const dismissToast = (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) }));

function upsertProject(p) {
  set((s) => {
    const i = s.projects.findIndex((x) => x.config.id === p.config.id);
    const projects = s.projects.slice();
    if (i >= 0) {
      // keep the latest conflicts if the server sends none (conflicts come in their own event)
      projects[i] = p;
    } else {
      projects.push(p);
      projects.sort((a, b) => (a.config.order - b.config.order) || a.config.name.localeCompare(b.config.name));
    }
    return { projects };
  });
}

function onState(d) {
  const history = { ...state.history };
  set({
    ready: true,
    version: d.version,
    projects: d.projects,
    settings: d.settings,
    env: d.env,
    system: { ...state.system, ...d.system },
    activity: d.activity || [],
    history,
  });
  applyTheme(d.settings);
}

function onMetrics(m) {
  set((s) => {
    const byId = new Map(m.projects.map((p) => [p.id, p]));
    const history = { ...s.history };
    const projects = s.projects.map((p) => {
      const live = byId.get(p.config.id);
      if (!live) {
        if (p.runtime.cpu || p.runtime.memMb) return { ...p, runtime: { ...p.runtime, cpu: 0, memMb: 0 } };
        return p;
      }
      const h = history[p.config.id] || { cpu: [], mem: [] };
      history[p.config.id] = {
        cpu: [...h.cpu, live.cpu].slice(-HISTORY),
        mem: [...h.mem, live.memMb].slice(-HISTORY),
      };
      return { ...p, runtime: { ...p.runtime, cpu: live.cpu, memMb: live.memMb, processCount: live.procs, effectivePort: live.port ?? p.runtime.effectivePort } };
    });
    return {
      projects,
      history,
      system: { ...s.system, ...m.system },
      sysHistory: [...s.sysHistory, m.system.cpu].slice(-HISTORY),
    };
  });
}

function applyTheme(settings) {
  if (!settings) return;
  const root = document.documentElement;
  const dark = settings.theme === 'system' ? matchMedia('(prefers-color-scheme: dark)').matches : settings.theme !== 'light';
  root.dataset.theme = dark ? 'dark' : 'light';
  root.dataset.accent = settings.accent || 'violet';
  root.dataset.motion = settings.reduceMotion ? 'reduce' : settings.lowPowerUi ? 'low' : 'full';
  try { localStorage.setItem('sb.theme', JSON.stringify({ theme: settings.theme, accent: settings.accent })); } catch { /* private mode */ }
}

let es;
let retry = 0;
export function connect() {
  if (es) return;
  const open = () => {
    es = new EventSource('/api/events');
    es.addEventListener('open', () => { retry = 0; set({ connected: true }); });
    es.addEventListener('state', (e) => onState(JSON.parse(e.data)));
    es.addEventListener('project', (e) => upsertProject(JSON.parse(e.data)));
    es.addEventListener('removed', (e) => { const { id } = JSON.parse(e.data); set((s) => ({ projects: s.projects.filter((p) => p.config.id !== id) })); });
    es.addEventListener('metrics', (e) => onMetrics(JSON.parse(e.data)));
    es.addEventListener('conflicts', (e) => {
      const map = JSON.parse(e.data);
      set((s) => ({ projects: s.projects.map((p) => (map[p.config.id] ? { ...p, runtime: { ...p.runtime, conflicts: map[p.config.id] } } : p)) }));
    });
    es.addEventListener('activity', (e) => set((s) => ({ activity: [JSON.parse(e.data), ...s.activity].slice(0, 300) })));
    es.addEventListener('activity-cleared', () => set({ activity: [] }));
    es.addEventListener('settings', (e) => { const st = JSON.parse(e.data); set({ settings: st }); applyTheme(st); });
    es.addEventListener('install', (e) => {
      const i = JSON.parse(e.data);
      set((s) => ({ installs: { ...s.installs, [i.id]: i } }));
      if (i.status === 'done') toast('ok', 'Installed', i.message, 6000);
      if (i.status === 'error') toast('error', 'Install failed', i.message, 9000);
    });
    es.addEventListener('toast', (e) => { const t = JSON.parse(e.data); toast(t.level, t.title, t.message, 7000); });
    es.addEventListener('error', () => {
      set({ connected: false });
      es.close(); es = undefined;
      setTimeout(open, Math.min(1000 * 2 ** retry++, 8000));
    });
  };
  open();
}

export async function refresh() {
  try { onState(await api('GET', '/state')); } catch { /* the SSE stream will retry */ }
}

// ---- derived helpers
export const isUp = (p) => p.runtime.status === 'running' || p.runtime.status === 'starting';
export const hasProblem = (p) =>
  ['crashed', 'failed', 'blocked'].includes(p.runtime.status) || p.runtime.conflicts?.some((c) => c.severity === 'error');
