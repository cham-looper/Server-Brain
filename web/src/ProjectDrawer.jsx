import { useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import {
  X, Play, Square, ExternalLink, Copy, Download, Trash2, Search, ArrowDownToLine, WrapText, RefreshCw, Globe, ShieldCheck, KeyRound, Eye, EyeOff, Cloud, AlertTriangle, Link2,
} from 'lucide-react';
import { Drawer, TypeIcon, StatusPill, Sparkline, Segmented, Switch, Field, spring } from './ui.jsx';
import { ConflictAlerts } from './ProjectCard.jsx';
import ProjectForm from './ProjectForm.jsx';
import { get, post, put, del } from './api.js';
import { useStore, toast } from './store.js';
import { startProject, stopProject, openProject, patchProject, urlFor } from './actions.js';
import { copy, fmtMb, fmtUptime, timeAgo, ansiToParts, localHost, cx } from './util.js';
import { useNow } from './hooks.js';

const TABS = [['overview', 'Overview'], ['logs', 'Logs'], ['share', 'Public access'], ['settings', 'Settings']];

function CopyRow({ label, value, icon: Icon = Link2 }) {
  if (!value) return null;
  return (
    <div style={{ marginBottom: 10 }}>
      <div className="label" style={{ marginBottom: 4 }}>{label}</div>
      <div className="codebox"><Icon size={14} color="var(--muted)" /><span>{value}</span>
        <button className="btn sm icon ghost" onClick={async () => (await copy(value)) && toast('ok', 'Copied', value, 1800)} aria-label="Copy"><Copy size={14} /></button>
        {/^https?:/.test(value) && <button className="btn sm icon ghost" onClick={() => window.open(value, '_blank', 'noopener')} aria-label="Open"><ExternalLink size={14} /></button>}
      </div>
    </div>
  );
}

function Overview({ p, onConfirmKill }) {
  const env = useStore((s) => s.env);
  const hist = useStore((s) => s.history[p.config.id]);
  const activity = useStore((s) => s.activity);
  const now = useNow(1000);
  const { config: c, runtime: r } = p;
  const host = localHost(p, env);
  const mine = activity.filter((a) => a.projectId === c.id).slice(0, 8);
  return (
    <div>
      <ConflictAlerts p={p} onConfirmKill={onConfirmKill} />
      <div className="cols3" style={{ margin: '14px 0' }}>
        <div className="card-in"><div className="label">CPU</div><div style={{ fontSize: 26, fontWeight: 700 }}>{r.cpu.toFixed(1)}%</div><div style={{ height: 48 }}><Sparkline data={hist?.cpu} max={Math.max(20, ...(hist?.cpu || [0]))} height={48} /></div></div>
        <div className="card-in"><div className="label">Memory</div><div style={{ fontSize: 26, fontWeight: 700 }}>{fmtMb(r.memMb)}</div><div style={{ height: 48 }}><Sparkline data={hist?.mem} color="var(--a2)" height={48} /></div></div>
        <div className="card-in"><div className="label">Uptime</div><div style={{ fontSize: 26, fontWeight: 700 }}>{r.startedAt ? fmtUptime(r.startedAt, now) : '—'}</div><div className="hint">{r.processCount || 0} process{r.processCount === 1 ? '' : 'es'}{r.pid ? ` · PID ${r.pid}` : ''}</div></div>
      </div>
      <div className="section">
        <h3>Addresses</h3>
        <CopyRow label="Friendly name" value={host && 'http://' + host + (c.urlPath !== '/' ? c.urlPath : '')} />
        <CopyRow label="Direct" value={r.directUrl} />
        <CopyRow label="Public" value={r.tunnel?.status === 'connected' ? r.tunnel.url : null} icon={Globe} />
        {!host && !r.directUrl && <div className="hint">Set a port (or start the project so it can be detected) to get an address.</div>}
      </div>
      <div className="section">
        <h3>Details</h3>
        <dl className="kv">
          <dt>Command</dt><dd className="mono">{c.command}</dd>
          <dt>Folder</dt><dd className="mono">{c.path}</dd>
          <dt>Ports</dt><dd className="mono">{[c.port, ...(c.extraPorts || [])].filter(Boolean).join(', ') || 'detected on start'}</dd>
          <dt>Run mode</dt><dd>{c.mode === 'always' ? 'Always on (starts with Windows)' : c.mode === 'onDemand' ? `On demand (stops after ${c.idleMinutes} idle min)` : 'Manual'}</dd>
          <dt>Restart policy</dt><dd>{c.restart === 'never' ? 'Never' : c.restart === 'always' ? 'Always' : 'On failure'} (max {c.maxRestarts})</dd>
          {r.exitCode != null && r.status !== 'running' && <><dt>Last exit code</dt><dd>{r.exitCode}</dd></>}
        </dl>
      </div>
      <div className="section">
        <h3>Recent activity</h3>
        {mine.length === 0 ? <div className="hint">Nothing yet.</div> : (
          <div className="tl">{mine.map((a, i) => (
            <div key={a.ts + '-' + i} className="tl-item"><span className={cx('dot', a.level === 'error' ? 'bad' : a.level === 'warn' ? 'warn' : a.level === 'ok' ? 'ok' : '')} style={{ animation: 'none' }} />
              <div>{a.message}</div><div className="when">{timeAgo(a.ts, now)}</div></div>
          ))}</div>
        )}
      </div>
    </div>
  );
}

function Logs({ p }) {
  const id = p.config.id;
  const [lines, setLines] = useState([]);
  const [follow, setFollow] = useState(true);
  const [wrap, setWrap] = useState(true);
  const [q, setQ] = useState('');
  const box = useRef(null);
  const lastSeq = useRef(0);

  useEffect(() => {
    let es; let dead = false;
    (async () => {
      try { const d = await get(`/projects/${id}/logs?tail=1000`); if (dead) return; setLines(d.lines); lastSeq.current = d.lines.at(-1)?.seq || 0; } catch { /* none yet */ }
      if (dead) return;
      es = new EventSource(`/api/projects/${id}/logs/stream`);
      es.addEventListener('line', (e) => {
        const l = JSON.parse(e.data);
        if (l.seq <= lastSeq.current) return;
        lastSeq.current = l.seq;
        setLines((a) => (a.length > 2500 ? [...a.slice(-2000), l] : [...a, l]));
      });
    })();
    return () => { dead = true; es?.close(); };
  }, [id]);

  useEffect(() => { if (follow && box.current) box.current.scrollTop = box.current.scrollHeight; }, [lines, follow]);

  const shown = useMemo(() => {
    const f = q.trim().toLowerCase();
    const arr = f ? lines.filter((l) => l.text.toLowerCase().includes(f)) : lines;
    return arr.slice(-1200);
  }, [lines, q]);

  const hl = (text) => {
    const parts = ansiToParts(text);
    const f = q.trim();
    return parts.map((pt, i) => {
      if (!f) return <span key={i} style={pt.s}>{pt.t}</span>;
      const idx = pt.t.toLowerCase().indexOf(f.toLowerCase());
      if (idx < 0) return <span key={i} style={pt.s}>{pt.t}</span>;
      return <span key={i} style={pt.s}>{pt.t.slice(0, idx)}<mark>{pt.t.slice(idx, idx + f.length)}</mark>{pt.t.slice(idx + f.length)}</span>;
    });
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', gap: 10 }}>
      <div className="row">
        <div className="search" style={{ flex: 1, maxWidth: 'none' }}><Search size={15} /><input className="input" placeholder="Filter log…" value={q} onChange={(e) => setQ(e.target.value)} /></div>
        <button className={cx('btn icon', follow && 'primary')} title="Follow new output" onClick={() => setFollow((f) => !f)}><ArrowDownToLine size={16} /></button>
        <button className={cx('btn icon', wrap && 'primary')} title="Wrap lines" onClick={() => setWrap((f) => !f)}><WrapText size={16} /></button>
        <a className="btn icon" title="Download" href={`/api/projects/${id}/logs/download`}><Download size={16} /></a>
        <button className="btn icon" title="Clear" onClick={async () => { await del(`/projects/${id}/logs`); setLines([]); }}><Trash2 size={16} /></button>
      </div>
      <div className="term" ref={box} style={{ whiteSpace: wrap ? 'pre-wrap' : 'pre' }} onScroll={(e) => { const el = e.currentTarget; const atEnd = el.scrollHeight - el.scrollTop - el.clientHeight < 30; if (!atEnd && follow) setFollow(false); }}>
        {shown.length === 0 && <span className="faint">No output yet. Start the project to see its console here.</span>}
        {shown.map((l) => (
          <span key={l.seq} className={cx('ln', l.err && (/error|exception|traceback|fatal|fail|denied|EADDRINUSE|crit|panic/i.test(l.text) ? 'err' : 'stderr'), l.sys && 'sys')}>
            <span className="ts">{new Date(l.ts).toLocaleTimeString([], { hour12: false })}</span>{hl(l.text)}
          </span>
        ))}
      </div>
    </div>
  );
}

function Share({ p }) {
  const env = useStore((s) => s.env);
  const { config: c, runtime: r } = p;
  const [token, setToken] = useState('');
  const [host, setHost] = useState(c.share.hostname || '');
  const [reveal, setReveal] = useState(false);
  const t = r.tunnel || { status: 'off' };
  const haveCf = !!env?.tools?.cloudflared;
  const setShare = (patch, extra = {}) => patchProject(p, { share: { ...c.share, ...patch }, ...extra });
  const startTunnel = async () => { try { await post(`/projects/${c.id}/tunnel/start`); } catch (e) { toast('error', 'Could not open the tunnel', e.message); } };
  return (
    <div>
      <div className="alert info" style={{ marginBottom: 16 }}>
        <ShieldCheck size={18} color="var(--info)" />
        <div>Public sharing uses a Cloudflare Tunnel: your PC makes an outbound connection, so you open no router ports. Visitors reach <b>only this project</b>, never the dashboard. Keep the access code on unless the project has its own login.</div>
      </div>
      {!haveCf && <div className="alert warn" style={{ marginBottom: 16 }}><AlertTriangle size={18} color="var(--warn)" /><div>cloudflared was not found. Install it with <span className="mono">winget install Cloudflare.cloudflared</span> or set its path in Settings.</div></div>}

      <Field label="Sharing mode">
        <Segmented value={c.share.mode} onChange={(mode) => setShare({ mode })} options={[
          { id: 'off', label: 'Off' }, { id: 'quick', label: 'Quick link (random URL)', hint: 'No account or domain needed. The address changes each time.' },
          { id: 'named', label: 'My own domain', hint: 'A fixed address on a domain in your Cloudflare account.' }]} />
      </Field>

      {c.share.mode === 'named' && (
        <div className="card-in" style={{ marginBottom: 16 }}>
          <div className="hint" style={{ marginBottom: 10 }}>In Cloudflare Zero Trust → Networks → Tunnels, create a tunnel, add a public hostname pointing to <span className="mono">http://localhost:{env?.proxyPort || 80}</span>, and paste the tunnel token here.</div>
          <div className="cols2">
            <Field label="Public hostname"><input className="input mono" placeholder="chat.yourdomain.com" value={host} onChange={(e) => setHost(e.target.value)} onBlur={() => host !== c.share.hostname && setShare({ hostname: host.trim() })} /></Field>
            <Field label={c.share.hasToken ? 'Tunnel token (saved, encrypted)' : 'Tunnel token'}>
              <div className="row"><input className="input mono" type={reveal ? 'text' : 'password'} placeholder={c.share.hasToken ? '••••••••••••' : 'eyJh…'} value={token} onChange={(e) => setToken(e.target.value)} />
                <button className="btn icon" onClick={() => setReveal((v) => !v)} aria-label="Show">{reveal ? <EyeOff size={16} /> : <Eye size={16} />}</button></div>
            </Field>
          </div>
          <div className="row"><button className="btn sm" disabled={!token} onClick={async () => { if (await setShare({}, { shareToken: token })) { setToken(''); toast('ok', 'Token saved', 'Stored encrypted for your Windows account.'); } }}>Save token</button>
            {c.share.hasToken && <button className="btn sm danger" onClick={() => setShare({}, { clearShareToken: true })}>Remove token</button>}</div>
        </div>
      )}

      {c.share.mode !== 'off' && (
        <>
          <div className="card-in" style={{ marginBottom: 16 }}>
            <div className="row" style={{ marginBottom: 10 }}>
              <span className={cx('dot', t.status === 'connected' ? 'ok' : t.status === 'starting' ? 'warn' : t.status === 'error' ? 'bad' : '')} />
              <b>{t.status === 'connected' ? 'Public' : t.status === 'starting' ? 'Connecting…' : t.status === 'error' ? 'Tunnel problem' : 'Tunnel is off'}</b>
              <span className="grow" />
              {t.status === 'off' || t.status === 'error' ? <button className="btn primary sm" disabled={!haveCf} onClick={startTunnel}><Cloud size={14} />Go public</button> : <button className="btn sm danger" onClick={() => post(`/projects/${c.id}/tunnel/stop`)}>Stop sharing</button>}
            </div>
            {t.error && <div className="hint" style={{ color: 'var(--bad)', marginBottom: 8 }}>{t.error}</div>}
            <CopyRow label="Public address" value={t.url} icon={Globe} />
            {c.mode === 'onDemand' && <div className="hint">This project is On-demand, so the first public visitor wakes it up (a few seconds). It sleeps again when idle.</div>}
          </div>

          <div className="card-in">
            <div className="setting" style={{ paddingTop: 0 }}><div className="t"><b>Require an access code</b><span>Visitors see a code screen first. Strongly recommended.</span></div><Switch on={c.share.requireCode} onChange={(v) => setShare({ requireCode: v })} label="require code" /></div>
            {c.share.requireCode && (
              <div style={{ margin: '10px 0' }}>
                <div className="label" style={{ marginBottom: 4 }}>Access code</div>
                <div className="codebox"><KeyRound size={14} color="var(--muted)" /><span style={{ letterSpacing: 3, fontWeight: 700 }}>{c.share.code}</span>
                  <button className="btn sm icon ghost" onClick={async () => (await copy(c.share.code)) && toast('ok', 'Code copied', undefined, 1500)}><Copy size={14} /></button>
                  <button className="btn sm icon ghost" title="New code" onClick={async () => { await post(`/projects/${c.id}/code/regenerate`); toast('ok', 'New code created', 'Old links stop working.'); }}><RefreshCw size={14} /></button></div>
              </div>
            )}
            <div className="setting" style={{ paddingBottom: 0 }}><div className="t"><b>Open the tunnel automatically</b><span>Whenever Server Brain starts (also works for On-demand projects).</span></div><Switch on={c.share.autoStart} onChange={(v) => setShare({ autoStart: v })} label="auto start tunnel" /></div>
          </div>
        </>
      )}
    </div>
  );
}

function Settings({ p, onClose, onDelete }) {
  const [cfg, setCfg] = useState(() => structuredClone(p.config));
  const [errors, setErrors] = useState([]);
  const [busy, setBusy] = useState(false);
  const dirty = JSON.stringify(cfg) !== JSON.stringify(p.config);
  const save = async () => {
    setBusy(true); setErrors([]);
    try { await put(`/projects/${p.config.id}`, cfg); toast('ok', 'Saved', p.runtime.status === 'running' ? 'Changes apply the next time it starts.' : undefined); }
    catch (e) { setErrors(e.data?.errors || [e.message]); } finally { setBusy(false); }
  };
  return (
    <div>
      <ProjectForm value={cfg} onChange={setCfg} editing />
      {errors.map((e) => <div key={e} className="hint" style={{ color: 'var(--bad)' }}>• {e}</div>)}
      <div className="row" style={{ marginTop: 18, position: 'sticky', bottom: -20, padding: '12px 0', background: 'linear-gradient(transparent, var(--surface-solid) 40%)' }}>
        <button className="btn danger" onClick={() => onDelete(p)}><Trash2 size={15} />Remove</button>
        <span className="grow" />
        {dirty && <span className="hint">Unsaved changes</span>}
        <button className="btn primary" disabled={!dirty || busy} onClick={save}>{busy ? 'Saving…' : 'Save changes'}</button>
      </div>
    </div>
  );
}

export default function ProjectDrawer({ id, tab, setTab, onClose, onDelete, onConfirmKill }) {
  const p = useStore((s) => s.projects.find((x) => x.config.id === id));
  const open = !!id && !!p;
  const [last, setLast] = useState(null);
  useEffect(() => { if (p) setLast(p); }, [p]);
  const show = p || last;
  const up = show && (show.runtime.status === 'running' || show.runtime.status === 'starting');
  return (
    <Drawer open={open} onClose={onClose}>
      {show && (
        <>
          <div className="modal-h" style={{ padding: '18px 20px 14px' }}>
            <TypeIcon type={show.config.type} color={show.config.color} />
            <div className="grow"><h2 style={{ fontSize: 19 }}>{show.config.name}</h2><div className="purl">{show.config.path}</div></div>
            <StatusPill status={show.runtime.status} />
            <button className={cx('btn sm', up ? '' : 'primary')} onClick={() => (up ? stopProject(show) : startProject(show))}>{up ? <><Square size={14} />Stop</> : <><Play size={14} />Start</>}</button>
            <button className="btn sm icon" onClick={() => openProject(show)} aria-label="Open"><ExternalLink size={15} /></button>
            <button className="btn ghost icon" onClick={onClose} aria-label="Close"><X size={18} /></button>
          </div>
          <div className="tabs">
            {TABS.map(([k, label]) => (
              <button key={k} className={cx('tab', tab === k && 'on')} onClick={() => setTab(k)}>{label}{tab === k && <motion.span layoutId="drawer-tab" className="ul" transition={spring} />}</button>
            ))}
          </div>
          <div className="modal-b" style={{ display: 'flex', flexDirection: 'column' }}>
            <div style={{ flex: 1, minHeight: 0 }}>
              {tab === 'overview' && <Overview p={show} onConfirmKill={onConfirmKill} />}
              {tab === 'logs' && <Logs key={show.config.id} p={show} />}
              {tab === 'share' && <Share p={show} />}
              {tab === 'settings' && <Settings key={show.config.id} p={show} onClose={onClose} onDelete={onDelete} />}
            </div>
          </div>
        </>
      )}
    </Drawer>
  );
}
