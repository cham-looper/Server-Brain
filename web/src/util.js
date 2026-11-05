export const STATUS = {
  running: { label: 'Running', tone: 'ok' },
  starting: { label: 'Starting', tone: 'warn' },
  stopping: { label: 'Stopping', tone: 'warn' },
  waiting: { label: 'Restarting', tone: 'warn' },
  stopped: { label: 'Stopped', tone: 'idle' },
  crashed: { label: 'Crashed', tone: 'bad' },
  failed: { label: 'Failed', tone: 'bad' },
  blocked: { label: 'Port conflict', tone: 'bad' },
};

export const MODES = [
  { id: 'manual', label: 'Manual', hint: 'Starts when you click Start and runs until you stop it (once).' },
  { id: 'always', label: 'Always on', hint: 'Starts with Windows and is kept running (restarted if it crashes).' },
  { id: 'onDemand', label: 'On demand', hint: 'Starts the first time you open its address and stops itself when idle. Uses no hardware while unused.' },
];

export const TYPES = {
  node: 'Node.js', python: 'Python', php: 'PHP', docker: 'Docker', static: 'Static site', custom: 'Custom',
};

export function fmtMb(mb) {
  if (mb == null) return '—';
  if (mb >= 1024) return (mb / 1024).toFixed(1) + ' GB';
  return Math.round(mb) + ' MB';
}

export function fmtUptime(startedAt, now = Date.now()) {
  if (!startedAt) return '—';
  let s = Math.max(0, Math.floor((now - startedAt) / 1000));
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60); s -= m * 60;
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m ${s}s`;
  return `${s}s`;
}

export function timeAgo(ts, now = Date.now()) {
  const s = Math.max(0, Math.floor((now - ts) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return new Date(ts).toLocaleDateString();
}

export function greeting() {
  const h = new Date().getHours();
  return h < 5 ? 'Still up' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

export function localHost(p, env) {
  const port = env?.proxyPort;
  if (!port) return null;
  return `${p.config.id}.localhost${port === 80 ? '' : ':' + port}`;
}

export function slugify(s) {
  const r = (s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
  return r || 'project';
}

export async function copy(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch {
    const ta = document.createElement('textarea');
    ta.value = text; document.body.appendChild(ta); ta.select();
    try { document.execCommand('copy'); return true; } catch { return false; } finally { ta.remove(); }
  }
}

export const cx = (...a) => a.filter(Boolean).join(' ');

// minimal ANSI → spans for the log viewer
const ANSI = /\x1b\[([0-9;]*)m/g;
const COLORS = ['#6b7280', '#fb7185', '#34d399', '#fbbf24', '#60a5fa', '#c084fc', '#22d3ee', '#e5e7eb'];
export function ansiToParts(text) {
  const parts = [];
  let last = 0, style = {}, m;
  ANSI.lastIndex = 0;
  while ((m = ANSI.exec(text))) {
    if (m.index > last) parts.push({ t: text.slice(last, m.index), s: style });
    last = ANSI.lastIndex;
    const codes = m[1] === '' ? [0] : m[1].split(';').map(Number);
    for (const c of codes) {
      if (c === 0) style = {};
      else if (c === 1) style = { ...style, fontWeight: 700 };
      else if (c >= 30 && c <= 37) style = { ...style, color: COLORS[c - 30] };
      else if (c >= 90 && c <= 97) style = { ...style, color: COLORS[c - 90] };
      else if (c === 39) { const { color, ...rest } = style; style = rest; }
    }
  }
  if (last < text.length) parts.push({ t: text.slice(last), s: style });
  return parts.length ? parts : [{ t: text, s: {} }];
}
