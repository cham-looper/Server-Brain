import { useEffect, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import {
  Search, RefreshCw, Skull, Globe, Cloud, Copy, Download, Upload, Trash2, FolderOpen, Power, CheckCircle2, XCircle, ExternalLink, AlertOctagon, Info, Cpu,
} from 'lucide-react';
import { Segmented, Switch, spring, StatusPill, TypeIcon, Modal, Field } from './ui.jsx';
import { get, post, put, del } from './api.js';
import { useStore, toast } from './store.js';
import { cx, copy, timeAgo, localHost } from './util.js';
import { killProcess, moveToFreePort } from './actions.js';
import { useNow } from './hooks.js';

const rise = (i = 0) => ({ initial: { opacity: 0, y: 14 }, animate: { opacity: 1, y: 0 }, transition: { ...spring, delay: Math.min(i, 12) * 0.03 } });

/* ───────────────────────── Ports ───────────────────────── */
export function PortsPage({ onConfirmKill }) {
  const projects = useStore((s) => s.projects);
  const [data, setData] = useState(null);
  const [q, setQ] = useState('');
  const [onlyMine, setOnlyMine] = useState(false);
  const load = async () => { try { setData(await get('/ports')); } catch (e) { toast('error', 'Could not read ports', e.message); } };
  useEffect(() => { load(); const t = setInterval(load, 4000); return () => clearInterval(t); }, []);

  const rows = (data?.listening || []).filter((r) => {
    if (onlyMine && !r.project && !r.configuredFor?.length) return false;
    const s = `${r.port} ${r.process} ${r.projectName || ''} ${r.pid} ${(r.configuredFor || []).join(' ')}`.toLowerCase();
    return s.includes(q.toLowerCase());
  });
  const planned = data?.planned || [];
  const plannedBy = useMemo(() => {
    const m = new Map();
    planned.forEach((p) => m.set(p.port, [...(m.get(p.port) || []), p]));
    return m;
  }, [planned]);
  const dupPorts = [...plannedBy.entries()].filter(([, v]) => v.length > 1);

  return (
    <div className="page">
      <div className="topbar">
        <div><h1>Ports</h1><div className="sub">Everything listening on this PC, matched to your projects. Conflicts are caught before a project starts.</div></div>
        <div className="grow" />
        <button className="btn" onClick={load}><RefreshCw size={15} />Refresh</button>
      </div>

      {dupPorts.length > 0 && (
        <motion.div className="alert bad" style={{ marginBottom: 18 }} {...rise()}>
          <AlertOctagon size={18} color="var(--bad)" />
          <div><b>{dupPorts.length} port{dupPorts.length > 1 ? 's are' : ' is'} assigned to more than one project</b>
            {dupPorts.map(([port, v]) => (
              <div key={port} style={{ marginTop: 4 }}>Port <b className="mono">{port}</b>: {v.map((x) => x.projectName).join(' and ')}
                <span className="row wrap" style={{ display: 'inline-flex', marginLeft: 10 }}>
                  {v.slice(1).map((x) => { const proj = projects.find((p) => p.config.id === x.project); return proj ? <button key={x.project} className="btn sm" onClick={() => moveToFreePort(proj)}>Move {x.projectName} to a free port</button> : null; })}
                </span></div>
            ))}
          </div>
        </motion.div>
      )}

      <div className="toolbar">
        <div className="search"><Search size={16} /><input className="input" placeholder="Filter by port, process or project…" value={q} onChange={(e) => setQ(e.target.value)} /></div>
        <div className="row"><Switch on={onlyMine} onChange={setOnlyMine} label="only mine" /><span className="muted">Only my projects</span></div>
      </div>

      <table className="table">
        <thead><tr><th>Port</th><th>Process</th><th>PID</th><th>Project</th><th /></tr></thead>
        <tbody>
          {!data && <tr><td colSpan={5}>Reading…</td></tr>}
          {rows.map((r, i) => {
            const clash = !r.project && r.configuredFor?.length > 0;
            return (
              <motion.tr key={r.port} {...rise(i)} className={clash ? 'hl' : ''}>
                <td><b className="mono" style={{ fontSize: 15 }}>{r.port}</b></td>
                <td><div>{r.process}</div><div className="faint mono" style={{ fontSize: 11, maxWidth: 420, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={r.path}>{r.path}</div></td>
                <td className="mono">{r.pid}</td>
                <td>
                  {r.project ? <span className="chip ok">{r.projectName}</span>
                    : clash ? <span className="chip bad"><AlertOctagon size={12} />Blocks {r.configuredFor.join(', ')}</span>
                    : r.isSelf ? <span className="chip">Server Brain</span> : <span className="faint">not managed</span>}
                </td>
                <td style={{ textAlign: 'right' }}>
                  {!r.project && !r.protectedPid && !r.isSelf && <button className="btn sm danger" onClick={() => onConfirmKill({ port: r.port, pid: r.pid, processName: r.process, onDone: load })}><Skull size={13} />Stop process</button>}
                </td>
              </motion.tr>
            );
          })}
        </tbody>
      </table>
      {data && rows.length === 0 && <div className="empty" style={{ padding: 30 }}><p>Nothing matches.</p></div>}

      <div className="section" style={{ marginTop: 28 }}>
        <h3>Ports assigned to your projects</h3>
        <div className="grid" style={{ gridTemplateColumns: 'repeat(auto-fill,minmax(260px,1fr))' }}>
          {planned.map((x, i) => (
            <motion.div key={x.port + x.project} {...rise(i)} className="card-in row">
              <b className="mono" style={{ fontSize: 16 }}>{x.port}</b><span className="grow" style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{x.projectName}</span>
              {plannedBy.get(x.port).length > 1 ? <span className="chip bad">shared</span> : x.listening ? <span className="chip ok">open</span> : <span className="chip">idle</span>}
            </motion.div>
          ))}
          {planned.length === 0 && <div className="hint">No ports assigned yet.</div>}
        </div>
      </div>
    </div>
  );
}

/* ───────────────────────── Public access ───────────────────────── */
export function PublicPage({ onOpenDrawer }) {
  const projects = useStore((s) => s.projects);
  const env = useStore((s) => s.env);
  const cf = env?.tools?.cloudflared;
  const shared = projects.filter((p) => p.config.share.mode !== 'off');
  const others = projects.filter((p) => p.config.share.mode === 'off');
  return (
    <div className="page">
      <div className="topbar">
        <div><h1>Public access</h1><div className="sub">Share a project with the world through a Cloudflare Tunnel. No router ports, no static IP.</div></div>
      </div>
      <div className="cols3" style={{ marginBottom: 22 }}>
        <motion.div className="glass card-in" {...rise(0)}>{cf ? <CheckCircle2 color="var(--ok)" /> : <XCircle color="var(--bad)" />}<h3 style={{ margin: '8px 0 4px' }}>cloudflared</h3><div className="muted" style={{ fontSize: 12.5, wordBreak: 'break-all' }}>{cf || 'Not found. Install: winget install Cloudflare.cloudflared'}</div></motion.div>
        <motion.div className="glass card-in" {...rise(1)}><Cloud color="var(--a2)" /><h3 style={{ margin: '8px 0 4px' }}>Quick link</h3><div className="muted" style={{ fontSize: 12.5 }}>A random <span className="mono">*.trycloudflare.com</span> address. Free, no account. Changes every time.</div></motion.div>
        <motion.div className="glass card-in" {...rise(2)}><Globe color="var(--a1)" /><h3 style={{ margin: '8px 0 4px' }}>Your domain</h3><div className="muted" style={{ fontSize: 12.5 }}>A permanent name like <span className="mono">chat.yourdomain.com</span> using a Cloudflare tunnel token.</div></motion.div>
      </div>

      <div className="section"><h3>Shared projects</h3>
        {shared.length === 0 && <div className="hint">Nothing is shared. Pick a project below and open its “Public access” tab.</div>}
        <div className="list">
          {shared.map((p, i) => (
            <motion.div key={p.config.id} {...rise(i)} className="lrow" style={{ gridTemplateColumns: '44px 1.2fr 1.6fr auto' }}>
              <TypeIcon type={p.config.type} color={p.config.color} size={20} />
              <div><div className="pname">{p.config.name}</div><div className="row"><StatusPill status={p.runtime.status} /></div></div>
              <div style={{ minWidth: 0 }}>
                {p.runtime.tunnel?.status === 'connected' ? (
                  <div className="codebox"><Globe size={14} color="var(--ok)" /><span>{p.runtime.tunnel.url}</span><button className="btn sm icon ghost" onClick={async () => (await copy(p.runtime.tunnel.url)) && toast('ok', 'Copied', p.runtime.tunnel.url, 1500)}><Copy size={14} /></button></div>
                ) : <span className="muted">{p.runtime.tunnel?.status === 'starting' ? 'Connecting…' : p.runtime.tunnel?.error || 'Tunnel is off'}</span>}
                {p.config.share.requireCode && <div className="hint" style={{ marginTop: 4 }}>Access code: <b className="mono">{p.config.share.code}</b></div>}
              </div>
              <div className="row">
                {p.runtime.tunnel?.status === 'connected' || p.runtime.tunnel?.status === 'starting'
                  ? <button className="btn sm danger" onClick={() => post(`/projects/${p.config.id}/tunnel/stop`)}>Stop sharing</button>
                  : <button className="btn sm primary" disabled={!cf} onClick={() => post(`/projects/${p.config.id}/tunnel/start`).catch((e) => toast('error', 'Tunnel', e.message))}>Go public</button>}
                <button className="btn sm" onClick={() => onOpenDrawer(p, 'share')}>Options</button>
              </div>
            </motion.div>
          ))}
        </div>
      </div>

      {others.length > 0 && (
        <div className="section"><h3>Not shared</h3>
          <div className="row wrap">{others.map((p) => <button key={p.config.id} className="chip" style={{ cursor: 'pointer' }} onClick={() => onOpenDrawer(p, 'share')}>{p.config.name}</button>)}</div>
        </div>
      )}
    </div>
  );
}

/* ───────────────────────── Activity ───────────────────────── */
export function ActivityPage() {
  const activity = useStore((s) => s.activity);
  const [lvl, setLvl] = useState('all');
  const now = useNow(15000);
  const items = activity.filter((a) => lvl === 'all' || a.level === lvl);
  return (
    <div className="page">
      <div className="topbar">
        <div><h1>Activity</h1><div className="sub">Starts, stops, crashes, restarts and port problems, newest first.</div></div>
        <div className="grow" />
        <Segmented small value={lvl} onChange={setLvl} options={[{ id: 'all', label: 'All' }, { id: 'error', label: 'Errors' }, { id: 'warn', label: 'Warnings' }, { id: 'ok', label: 'Success' }]} />
        <button className="btn" onClick={() => del('/activity')}><Trash2 size={15} />Clear</button>
      </div>
      <div className="glass" style={{ padding: '14px 20px' }}>
        {items.length === 0 && <div className="empty" style={{ padding: 30 }}><p>Nothing to show.</p></div>}
        <div className="tl">
          {items.map((a, i) => (
            <motion.div key={a.ts + '-' + i} className="tl-item" {...rise(i)}>
              <span className={cx('dot', a.level === 'error' ? 'bad' : a.level === 'warn' ? 'warn' : a.level === 'ok' ? 'ok' : '')} />
              <div>{a.projectName && <b>{a.projectName}: </b>}{a.message}</div>
              <div className="when">{timeAgo(a.ts, now)} · {new Date(a.ts).toLocaleString()}</div>
            </motion.div>
          ))}
        </div>
      </div>
    </div>
  );
}

/* ───────────────────────── Settings ───────────────────────── */
function SettingRow({ title, desc, children }) {
  return <div className="setting"><div className="t"><b>{title}</b><span>{desc}</span></div>{children}</div>;
}

export function SettingsPage({ onQuit, go }) {
  const s = useStore((x) => x.settings);
  const env = useStore((x) => x.env);
  const version = useStore((x) => x.version);
  const [draft, setDraft] = useState(null);
  const [resetOpen, setResetOpen] = useState(false);
  const [resetText, setResetText] = useState('');
  useEffect(() => { if (s && !draft) setDraft(s); }, [s, draft]);
  if (!draft || !env) return <div className="page"><div className="skeleton" /></div>;
  const save = async (patch) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    try {
      const r = await put('/settings', next);
      if (r.restartRequired) toast('warn', 'Restart needed', 'Port changes apply the next time Server Brain starts.', 7000);
    } catch (e) { toast('error', 'Could not save settings', e.message); }
  };
  const importFile = async (file) => {
    if (!file) return;
    try {
      const text = await file.text();
      JSON.parse(text);
      const res = await fetch('/api/import?replace=false', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: text });
      const d = await res.json();
      if (!res.ok) throw new Error(d.error || 'Import failed');
      toast('ok', 'Imported', `${d.imported} project${d.imported === 1 ? '' : 's'} added (existing ones were kept).`);
    } catch (e) { toast('error', 'Import failed', e.message); }
  };
  const tools = [['Node.js', env.tools.node], ['npm', env.tools.npm], ['Python', env.tools.python], ['PHP', env.tools.php], ['Docker', env.tools.docker], ['cloudflared', env.tools.cloudflared]];

  return (
    <div className="page" style={{ maxWidth: 920 }}>
      <div className="topbar"><div><h1>Settings</h1><div className="sub">Server Brain {version} · MIT licensed</div></div></div>

      <motion.div className="glass card-in section" {...rise(0)}><h3>Startup</h3>
        <SettingRow title="Start Server Brain with Windows" desc="Runs hidden in the tray at login, then starts your Always-on projects one by one."><Switch on={draft.startWithWindows} onChange={(v) => save({ startWithWindows: v })} label="autostart" /></SettingRow>
        <SettingRow title="Stagger between projects" desc="Seconds to wait between starting each always-on project, so login stays snappy."><input className="input mono" style={{ width: 90 }} type="number" min="0" value={draft.staggerSeconds} onChange={(e) => setDraft({ ...draft, staggerSeconds: Number(e.target.value) })} onBlur={() => save({})} /></SettingRow>
        <SettingRow title="Open the browser after Start" desc="When you press Start, open the project as soon as it is ready."><Switch on={draft.autoOpenBrowser} onChange={(v) => save({ autoOpenBrowser: v })} label="auto open" /></SettingRow>
        <SettingRow title="Default idle time (On-demand)" desc="Minutes without visitors before an On-demand project stops itself."><input className="input mono" style={{ width: 90 }} type="number" min="1" value={draft.defaultIdleMinutes} onChange={(e) => setDraft({ ...draft, defaultIdleMinutes: Number(e.target.value) })} onBlur={() => save({})} /></SettingRow>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(1)}><h3>Notifications</h3>
        <SettingRow title="Desktop notifications" desc="Windows toasts when a project crashes, hits a port conflict or a tunnel goes live."><Switch on={draft.notifications} onChange={(v) => save({ notifications: v })} label="notifications" /></SettingRow>
        <SettingRow title="Confirm before quitting" desc="Asks first when projects are still running."><Switch on={draft.confirmExit} onChange={(v) => save({ confirmExit: v })} label="confirm exit" /></SettingRow>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(2)}><h3>Network</h3>
        <SettingRow title="Friendly names (http://name.localhost)" desc={`Routes names to projects, wakes On-demand projects and powers public sharing. Currently on port ${env.proxyPort || 'off'}.`}><Switch on={draft.proxyEnabled} onChange={(v) => save({ proxyEnabled: v })} label="proxy" /></SettingRow>
        <SettingRow title="Names port" desc="80 lets you type http://name.localhost with no port. Needs a restart."><input className="input mono" style={{ width: 90 }} type="number" value={draft.proxyPort} onChange={(e) => setDraft({ ...draft, proxyPort: Number(e.target.value) })} onBlur={() => save({})} /></SettingRow>
        <SettingRow title="Dashboard port" desc={`The dashboard is at http://127.0.0.1:${env.dashboardPort} (local only). Needs a restart.`}><input className="input mono" style={{ width: 90 }} type="number" value={draft.dashboardPort} onChange={(e) => setDraft({ ...draft, dashboardPort: Number(e.target.value) })} onBlur={() => save({})} /></SettingRow>
        <SettingRow title="cloudflared path" desc="Leave empty to find it automatically."><input className="input mono" style={{ width: 300 }} value={draft.cloudflaredPath} placeholder={env.tools.cloudflared || 'not found'} onChange={(e) => setDraft({ ...draft, cloudflaredPath: e.target.value })} onBlur={() => save({})} /></SettingRow>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(3)}><h3>Appearance</h3>
        <SettingRow title="Theme" desc="Dark, light, or follow Windows."><Segmented small value={draft.theme} onChange={(theme) => save({ theme })} options={[{ id: 'dark', label: 'Dark' }, { id: 'light', label: 'Light' }, { id: 'system', label: 'System' }]} /></SettingRow>
        <SettingRow title="Accent color" desc="Used for highlights and buttons.">
          <div className="row">{[['violet', '#8b7bff'], ['cyan', '#22d3ee'], ['emerald', '#34d399'], ['rose', '#fb7185'], ['amber', '#fbbf24']].map(([k, col]) => (
            <button key={k} aria-label={k} onClick={() => save({ accent: k })} style={{ width: 28, height: 28, borderRadius: 10, background: col, border: draft.accent === k ? '3px solid var(--text)' : '3px solid transparent' }} />
          ))}</div></SettingRow>
        <SettingRow title="Reduce motion" desc="Turns off most animations."><Switch on={draft.reduceMotion} onChange={(v) => save({ reduceMotion: v })} label="reduce motion" /></SettingRow>
        <SettingRow title="Low-power interface" desc="Stops the animated background to save battery and GPU."><Switch on={draft.lowPowerUi} onChange={(v) => save({ lowPowerUi: v })} label="low power" /></SettingRow>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(4)}><h3>Backup &amp; data</h3>
        <SettingRow title="Export configuration" desc="Saves all projects and settings to a JSON file (secrets are not included)."><a className="btn" href="/api/export"><Download size={15} />Export</a></SettingRow>
        <SettingRow title="Import configuration" desc="Adds the projects from an exported file. Existing projects are kept."><label className="btn" style={{ cursor: 'pointer' }}><Upload size={15} />Import<input type="file" accept=".json,application/json" hidden onChange={(e) => importFile(e.target.files?.[0])} /></label></SettingRow>
        <SettingRow title="Back up now" desc="Saves a copy of your projects and settings in the backups folder. Server Brain also does this automatically before every update."><button className="btn" onClick={async () => { try { const r = await post('/backup'); toast('ok', 'Backup saved', r.path, 9000); } catch (e) { toast('error', 'Backup failed', e.message); } }}>Back up</button></SettingRow>
        <SettingRow title="Data folder" desc={env.dataDir}><button className="btn" onClick={() => post('/reveal', { path: env.dataDir })}><FolderOpen size={15} />Show</button></SettingRow>
        <SettingRow title="Reset Server Brain" desc="Removes all projects and restores default settings, as if freshly installed. A backup is saved first. Your project folders are not touched."><button className="btn danger" onClick={() => { setResetText(''); setResetOpen(true); }}><Trash2 size={15} />Reset…</button></SettingRow>
        <SettingRow title="Show the welcome tour again" desc="Replays the first-run setup (including the PC check)."><button className="btn" onClick={async () => { await save({ onboardingDone: false }); go?.('/'); }}>Show tour</button></SettingRow>
      </motion.div>

      <Modal open={resetOpen} onClose={() => setResetOpen(false)} small title="Reset Server Brain?"
        footer={<><button className="btn" onClick={() => setResetOpen(false)}>Cancel</button>
          <button className="btn danger" disabled={resetText !== 'RESET'} onClick={async () => { try { const r = await post('/reset', { confirm: 'RESET' }); setResetOpen(false); toast('ok', 'Reset done', `Backup saved: ${r.backup}`, 9000); setDraft(null); } catch (e) { toast('error', 'Reset failed', e.message); } }}>Reset everything</button></>}>
        <p className="muted" style={{ marginTop: 0 }}>All projects will be stopped and removed from Server Brain and settings return to defaults. A backup file is saved first so you can import it again. The projects' own folders are never touched.</p>
        <Field label="Type RESET to confirm"><input className="input mono" value={resetText} onChange={(e) => setResetText(e.target.value)} placeholder="RESET" /></Field>
      </Modal>

      <motion.div className="glass card-in section" {...rise(5)}><h3>Check your PC</h3>
        <SettingRow title="Installed programs" desc="See whether Node.js, Python, PHP, Docker, cloudflared and WebView2 are present, and install missing ones."><button className="btn" onClick={() => go?.('/help')}>Open check</button></SettingRow>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(5)}><h3>Detected tools</h3>
        <div className="cols3">{tools.map(([n, p]) => (
          <div key={n} className="row" style={{ alignItems: 'flex-start' }}>{p ? <CheckCircle2 size={16} color="var(--ok)" style={{ marginTop: 3 }} /> : <XCircle size={16} color="var(--faint)" style={{ marginTop: 3 }} />}
            <div style={{ minWidth: 0 }}><b>{n}</b><div className="faint mono" style={{ fontSize: 11, wordBreak: 'break-all' }}>{p || 'not on PATH'}</div></div></div>
        ))}</div>
      </motion.div>

      <motion.div className="glass card-in section" {...rise(6)}><h3>About</h3>
        <p className="muted" style={{ marginTop: 0 }}>Server Brain {version}. A lightweight manager that runs your local projects. Open source under the MIT License. It never modifies the projects it runs.</p>
        <div className="row"><button className="btn danger" onClick={onQuit}><Power size={15} />Quit Server Brain</button><span className="hint">Stops all managed projects.</span></div>
      </motion.div>
    </div>
  );
}
