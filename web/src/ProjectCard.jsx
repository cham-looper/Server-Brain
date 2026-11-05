import { useState } from 'react';
import { motion } from 'framer-motion';
import {
  Play, Square, RotateCw, ExternalLink, Terminal, MoreHorizontal, Settings2, Copy, FolderOpen, Trash2, Globe, AlertOctagon,
  AlertTriangle, Zap, Infinity as Inf, Moon, Cpu, MemoryStick, Clock, Wand2, Skull,
} from 'lucide-react';
import { TypeIcon, StatusPill, Segmented, Sparkline, Menu, spring } from './ui.jsx';
import { MODES, fmtMb, fmtUptime, copy, cx, localHost } from './util.js';
import { useStore, toast } from './store.js';
import { post } from './api.js';
import { startProject, stopProject, restartProject, openProject, patchProject, killProcess, moveToFreePort, urlFor } from './actions.js';
import { useNow } from './hooks.js';
import { installTool } from './SystemCheck.jsx';

const MODE_ICON = { manual: Zap, always: Inf, onDemand: Moon };

export function ConflictAlerts({ p, compact, onConfirmKill }) {
  const cs = p.runtime.conflicts || [];
  const blocked = p.runtime.status === 'blocked';
  const installs = useStore((s) => s.installs);
  const missing = p.runtime.missingTool;
  const inst = missing ? installs[missing] : null;
  const errs = cs.filter((c) => c.severity === 'error');
  const warns = cs.filter((c) => c.severity !== 'error');
  const shown = [...errs, ...warns].slice(0, compact ? 2 : 6);
  if (missing) {
    return (
      <div className="alert bad"><AlertOctagon size={16} color="var(--bad)" />
        <div style={{ flex: 1 }}><b>{p.runtime.missingToolName || missing} is not installed.</b> This project needs it and cannot start without it.
          {inst?.message && inst.status !== 'idle' && <div className="hint" style={{ marginTop: 4, color: inst.status === 'error' ? 'var(--bad)' : 'var(--a2)' }}>{inst.message}</div>}
          <div className="acts">
            <button className="btn sm primary" disabled={inst?.status === 'running'} onClick={() => installTool(missing)}>{inst?.status === 'running' ? 'Installing…' : `Install ${p.runtime.missingToolName || missing}`}</button>
            {inst?.status === 'done' && <button className="btn sm" onClick={() => startProject(p)}>Start again</button>}
            <a className="btn sm" href="#/help">Help</a>
          </div>
        </div></div>
    );
  }
  if (!shown.length && !p.runtime.warning && !(p.runtime.error && ['crashed', 'failed', 'blocked', 'waiting'].includes(p.runtime.status))) return null;
  return (
    <>
      {shown.map((c, i) => (
        <motion.div key={c.kind + c.port + i} className={cx('alert', c.severity === 'error' ? 'bad' : 'warn')} initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }}>
          {c.severity === 'error' ? <AlertOctagon size={16} color="var(--bad)" /> : <AlertTriangle size={16} color="var(--warn)" />}
          <div style={{ flex: 1 }}>
            {c.message}
            <div className="acts">
              {c.kind === 'in-use' && c.pid && !c.otherProjectId && (
                <button className="btn sm danger" onClick={() => onConfirmKill(c)}><Skull size={13} />Free port (stop {c.processName})</button>
              )}
              {c.kind !== 'reserved' && (c.kind === 'in-use' || c.kind === 'duplicate') && (
                <button className="btn sm" onClick={() => moveToFreePort(p)}><Wand2 size={13} />Use a free port</button>
              )}
              {blocked && c.kind === 'in-use' && <button className="btn sm" onClick={() => startProject(p, { force: true })}>Start anyway</button>}
            </div>
          </div>
        </motion.div>
      ))}
      {p.runtime.warning && !shown.length && (
        <div className="alert warn"><AlertTriangle size={16} color="var(--warn)" /><div style={{ flex: 1 }}>{p.runtime.warning}
          {p.runtime.detectedPort && p.runtime.detectedPort !== p.config.port && (
            <div className="acts"><button className="btn sm" onClick={async () => { try { await post(`/projects/${p.config.id}/use-detected-port`); toast('ok', 'Port updated', `Now using port ${p.runtime.detectedPort}.`); } catch (e) { toast('error', 'Failed', e.message); } }}>Use port {p.runtime.detectedPort}</button></div>
          )}
        </div></div>
      )}
      {p.runtime.error && ['crashed', 'failed', 'waiting'].includes(p.runtime.status) && (
        <div className="alert bad"><AlertOctagon size={16} color="var(--bad)" /><div>{p.runtime.error}</div></div>
      )}
    </>
  );
}

export default function ProjectCard({ p, onOpenDrawer, onEdit, onDelete, onConfirmKill, view = 'grid' }) {
  const env = useStore((s) => s.env);
  const hist = useStore((s) => s.history[p.config.id]);
  const [busy, setBusy] = useState(false);
  const now = useNow(p.runtime.status === 'running' ? 1000 : 0);
  const { config: c, runtime: r } = p;
  const up = r.status === 'running' || r.status === 'starting';
  const transitional = r.status === 'stopping';
  const host = localHost(p, env);
  const ModeIcon = MODE_ICON[c.mode] || Zap;
  const bad = ['crashed', 'failed', 'blocked'].includes(r.status) || r.conflicts?.some((x) => x.severity === 'error');

  const run = async (fn) => { setBusy(true); try { await fn(); } finally { setBusy(false); } };
  const toggle = () => run(() => (up || r.status === 'waiting' ? stopProject(p) : startProject(p)));
  const onMove = (e) => {
    const b = e.currentTarget.getBoundingClientRect();
    e.currentTarget.style.setProperty('--mx', e.clientX - b.left + 'px');
    e.currentTarget.style.setProperty('--my', e.clientY - b.top + 'px');
  };

  const menu = (
    <Menu trigger={({ toggle: t }) => <button className="btn icon" onClick={t} aria-label="More"><MoreHorizontal size={18} /></button>}>
      <button onClick={() => onEdit(p)}><Settings2 size={16} />Edit settings</button>
      <button onClick={() => onOpenDrawer(p, 'logs')}><Terminal size={16} />View logs</button>
      <button onClick={async () => { const u = host ? 'http://' + host : urlFor(p); if (u && await copy(u)) toast('ok', 'Copied', u); }}><Copy size={16} />Copy address</button>
      <button onClick={() => post('/reveal', { path: c.path })}><FolderOpen size={16} />Show folder</button>
      <button onClick={() => onOpenDrawer(p, 'share')}><Globe size={16} />Public access…</button>
      <hr />
      <button className="danger" onClick={() => onDelete(p)}><Trash2 size={16} />Remove from Server Brain</button>
    </Menu>
  );

  const modeSel = (
    <Segmented small value={c.mode} onChange={(m) => patchProject(p, { mode: m })} options={MODES.map((m) => ({ ...m, label: m.id === 'onDemand' ? 'On demand' : m.id === 'always' ? 'Always' : 'Manual' }))} />
  );

  if (view === 'list') {
    return (
      <motion.div layout className={cx('lrow')} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={spring}>
        <TypeIcon type={c.type} color={c.color} size={20} />
        <div style={{ minWidth: 0, cursor: 'pointer' }} onClick={() => onOpenDrawer(p, 'overview')}>
          <div className="pname">{c.name}</div><div className="purl">{host || urlFor(p) || c.path}</div>
        </div>
        <StatusPill status={r.status} />
        <span className="chip mono">{r.effectivePort ? ':' + r.effectivePort : 'no port'}</span>
        <span className="muted" style={{ fontSize: 12.5 }}>{up ? `${r.cpu}% · ${fmtMb(r.memMb)}` : '—'}</span>
        <div>{modeSel}</div>
        <div className="row">
          <button className={cx('btn sm', up ? '' : 'primary')} disabled={busy || transitional} onClick={toggle}>{up ? <Square size={14} /> : <Play size={14} />}{up ? 'Stop' : 'Start'}</button>
          <button className="btn sm icon" onClick={() => run(() => openProject(p))} aria-label="Open"><ExternalLink size={15} /></button>
          {menu}
        </div>
      </motion.div>
    );
  }

  return (
    <motion.article layout onMouseMove={onMove}
      className={cx('pcard', r.status === 'running' && 'running', bad && 'bad')}
      initial={{ opacity: 0, y: 18, scale: 0.97 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, scale: 0.94 }} transition={spring}
      whileHover={{ y: -3 }}>
      <div className="phead">
        <TypeIcon type={c.type} color={c.color} />
        <div style={{ minWidth: 0, flex: 1, cursor: 'pointer' }} onClick={() => onOpenDrawer(p, 'overview')}>
          <div className="pname">{c.name}</div>
          <div className="purl" title={host ? 'http://' + host : ''}>{host || c.path}</div>
        </div>
        <StatusPill status={r.status} />
      </div>

      <div className="pmeta">
        <span className={cx('chip mono', r.conflicts?.some((x) => x.port === (r.effectivePort ?? c.port) && x.severity === 'error') && 'bad')}>{r.effectivePort ? ':' + r.effectivePort : c.port ? ':' + c.port : 'no port'}</span>
        <span className="chip"><ModeIcon size={12} />{MODES.find((m) => m.id === c.mode)?.label}</span>
        {r.tunnel?.status === 'connected' && <span className="chip ok"><Globe size={12} />Public</span>}
        {r.restartCount > 0 && <span className="chip warn"><RotateCw size={12} />{r.restartCount} restart{r.restartCount > 1 ? 's' : ''}</span>}
        {c.group && <span className="chip">{c.group}</span>}
      </div>

      {up ? (
        <>
          <div className="pstats">
            <div className="pstat"><div className="l"><Cpu size={10} /> CPU</div><div className="n">{r.cpu.toFixed(1)}%</div></div>
            <div className="pstat"><div className="l"><MemoryStick size={10} /> Memory</div><div className="n">{fmtMb(r.memMb)}</div></div>
            <div className="pstat"><div className="l"><Clock size={10} /> Uptime</div><div className="n">{fmtUptime(r.startedAt, now)}</div></div>
          </div>
          <div className="pspark"><Sparkline data={hist?.cpu} max={Math.max(20, ...(hist?.cpu || [0]))} height={38} /></div>
        </>
      ) : (
        c.notes ? <div className="muted" style={{ fontSize: 12.5, minHeight: 20 }}>{c.notes}</div> : null
      )}

      <ConflictAlerts p={p} compact onConfirmKill={onConfirmKill} />

      <div style={{ display: 'flex', justifyContent: 'center' }}>{modeSel}</div>

      <div className="pactions">
        <button className={cx('btn main', up || r.status === 'waiting' ? '' : 'primary')} disabled={busy || transitional} onClick={toggle}>
          {up || r.status === 'waiting' ? <><Square size={15} />Stop</> : <><Play size={15} />Start</>}
        </button>
        <button className="btn icon" title="Open in browser" onClick={() => run(() => openProject(p))}><ExternalLink size={17} /></button>
        <button className="btn icon" title="Restart" disabled={!up || busy} onClick={() => run(() => restartProject(p))}><RotateCw size={17} /></button>
        <button className="btn icon" title="Logs" onClick={() => onOpenDrawer(p, 'logs')}><Terminal size={17} /></button>
        {menu}
      </div>
    </motion.article>
  );
}
