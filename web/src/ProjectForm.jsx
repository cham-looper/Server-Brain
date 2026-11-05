import { useEffect, useMemo, useState } from 'react';
import { FolderSearch, Plus, Trash2, ChevronDown, AlertOctagon, AlertTriangle, CheckCircle2, Wand2 } from 'lucide-react';
import { motion, AnimatePresence } from 'framer-motion';
import { Field, Segmented, Switch } from './ui.jsx';
import { MODES, TYPES, slugify, cx } from './util.js';
import { useStore } from './store.js';
import { get, isDesktopApp, pickFolder } from './api.js';

const COLORS = ['', '#8b7bff', '#35d6c4', '#60a5fa', '#34d399', '#fbbf24', '#fb923c', '#fb7185', '#e879f9'];

/** Live check of a port: duplicates among our projects + processes already listening. */
export function usePortCheck(port, selfId, extra = []) {
  const projects = useStore((s) => s.projects);
  const [listener, setListener] = useState(null);
  useEffect(() => {
    setListener(null);
    if (!port) return undefined;
    const t = setTimeout(async () => {
      try {
        const o = await get('/ports');
        const l = o.listening.find((x) => x.port === Number(port));
        setListener(l && !(l.project && l.project === selfId) ? l : null);
      } catch { /* ignore */ }
    }, 350);
    return () => clearTimeout(t);
  }, [port, selfId]);
  return useMemo(() => {
    if (!port) return null;
    const p = Number(port);
    const dup = projects.find((x) => x.config.id !== selfId && [x.config.port, ...(x.config.extraPorts || [])].includes(p));
    if (dup) return { level: 'warn', text: `Also assigned to “${dup.config.name}”. They can't run at the same time.` };
    const ex = extra.find((e) => e.port === p);
    if (ex) return { level: 'bad', text: ex.text };
    if (listener) {
      if (listener.project && listener.project !== selfId) return { level: 'bad', text: `Used right now by the running project “${listener.projectName}”.` };
      if (!listener.project) return { level: 'bad', text: `Already in use by ${listener.process} (PID ${listener.pid}).` };
    }
    return { level: 'ok', text: 'Port looks free.' };
  }, [port, projects, selfId, listener, extra]);
}

export function PortStatus({ check }) {
  if (!check) return null;
  const Ico = check.level === 'bad' ? AlertOctagon : check.level === 'warn' ? AlertTriangle : CheckCircle2;
  const color = check.level === 'bad' ? 'var(--bad)' : check.level === 'warn' ? 'var(--warn)' : 'var(--ok)';
  return <div className="hint" style={{ color, display: 'flex', gap: 6, alignItems: 'center' }}><Ico size={14} />{check.text}</div>;
}

export async function suggestPort(projectId, from) {
  const r = await get(`/ports/suggest?project=${projectId || ''}&from=${from || 3000}`);
  return r.port;
}

export function emptyConfig() {
  return {
    id: '', name: '', path: '', type: 'custom', command: '', stopCommand: '', env: {}, port: null, extraPorts: [],
    injectPort: false, portEnvName: 'PORT', urlPath: '/', mode: 'manual', idleMinutes: 15, restart: 'onFailure', maxRestarts: 5,
    priority: 'normal', memoryLimitMb: 0, startDelaySec: 0, startupTimeoutSec: 90, keepStdinOpen: false, openBrowserOnStart: false,
    rewriteHeaders: true, group: '', color: '', notes: '', dependsOn: [], order: 0,
    share: { mode: 'off', hostname: '', autoStart: false, requireCode: true, code: '' },
  };
}

export default function ProjectForm({ value, onChange, editing, suggestion }) {
  const projects = useStore((s) => s.projects);
  const env = useStore((s) => s.env);
  const [advanced, setAdvanced] = useState(false);
  const c = value;
  const set = (patch) => onChange({ ...c, ...patch });
  const portCheck = usePortCheck(c.port, editing ? c.id : null);
  const extraText = (c.extraPorts || []).join(', ');
  const envRows = Object.entries(c.env || {});
  const setEnv = (rows) => set({ env: Object.fromEntries(rows.filter(([k]) => k !== '' || true)) });
  const idPreview = c.id || slugify(c.name);

  const browse = async () => {
    const path = await pickFolder();
    if (path) set({ path });
  };

  return (
    <div>
      <div className="cols2">
        <Field label="Name">
          <input className="input" value={c.name} placeholder="My Project" onChange={(e) => set({ name: e.target.value, ...(editing ? {} : { id: slugify(e.target.value) }) })} />
        </Field>
        <Field label="Short name (web address)" hint={env?.proxyPort ? `http://${idPreview}.localhost${env.proxyPort === 80 ? '' : ':' + env.proxyPort}` : 'Name routing is switched off in Settings.'}>
          <input className="input mono" value={c.id} disabled={editing} placeholder="my-project" onChange={(e) => set({ id: slugify(e.target.value) })} />
        </Field>
      </div>

      <Field label="Project folder" hint="Server Brain only runs the start command inside this folder. It never edits files in it.">
        <div className="row">
          <input className="input mono" value={c.path} placeholder="D:\Projects\my-project" onChange={(e) => set({ path: e.target.value })} />
          {isDesktopApp && <button type="button" className="btn" onClick={browse}><FolderSearch size={16} />Browse…</button>}
        </div>
      </Field>

      <Field label="Start command" hint="Runs through the Windows command line inside the folder. Anything you would type in a terminal works.">
        <input className="input mono" value={c.command} placeholder="npm start" onChange={(e) => set({ command: e.target.value })} />
        {suggestion?.commands?.length > 1 && (
          <div className="row wrap" style={{ marginTop: 6 }}>
            <span className="hint">Detected:</span>
            {suggestion.commands.map((cm) => (
              <button key={cm.command} type="button" className={cx('chip mono', c.command === cm.command && 'ok')} style={{ cursor: 'pointer' }} title={cm.note || ''}
                onClick={() => set({ command: cm.command, stopCommand: cm.stopCommand || c.stopCommand, type: cm.kind === 'script' ? c.type : cm.kind })}>{cm.label}</button>
            ))}
          </div>
        )}
        {suggestion?.notes?.map((n, i) => <div key={i} className="hint" style={{ color: 'var(--warn)' }}>• {n}</div>)}
        {suggestion?.commands?.find((x) => x.command === c.command)?.note && <div className="hint">• {suggestion.commands.find((x) => x.command === c.command).note}</div>}
      </Field>

      <div className="cols3">
        <Field label="Port" error={null}>
          <div className="row">
            <input className="input mono" type="number" min="1" max="65535" value={c.port ?? ''} placeholder="auto-detect" onChange={(e) => set({ port: e.target.value ? Number(e.target.value) : null })} />
            <button type="button" className="btn icon" title="Find a free port" onClick={async () => set({ port: await suggestPort(editing ? c.id : null, (c.port || 3000) + (c.port ? 1 : 0)) })}><Wand2 size={16} /></button>
          </div>
          <PortStatus check={portCheck} />
        </Field>
        <Field label="Type">
          <select className="select" value={c.type} onChange={(e) => set({ type: e.target.value })}>
            {Object.entries(TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </Field>
        <Field label="Open at path">
          <input className="input mono" value={c.urlPath} onChange={(e) => set({ urlPath: e.target.value })} />
        </Field>
      </div>

      <Field label="When should it run?" hint={MODES.find((m) => m.id === c.mode)?.hint}>
        <div className="row wrap">
          <Segmented value={c.mode} onChange={(mode) => set({ mode })} options={MODES} />
          <AnimatePresence initial={false}>
            {c.mode === 'onDemand' && (
              <motion.div initial={{ opacity: 0, width: 0 }} animate={{ opacity: 1, width: 'auto' }} exit={{ opacity: 0, width: 0 }} style={{ overflow: 'hidden' }}>
                <div className="row" style={{ whiteSpace: 'nowrap' }}><span className="muted">stop after</span>
                  <input className="input mono" style={{ width: 70 }} type="number" min="1" value={c.idleMinutes} onChange={(e) => set({ idleMinutes: Number(e.target.value) })} /><span className="muted">idle min</span></div>
              </motion.div>
            )}
          </AnimatePresence>
        </div>
      </Field>

      <button type="button" className="btn ghost sm" onClick={() => setAdvanced((a) => !a)} style={{ marginBottom: 12 }}>
        <motion.span animate={{ rotate: advanced ? 180 : 0 }} style={{ display: 'flex' }}><ChevronDown size={16} /></motion.span>Advanced options
      </button>

      <AnimatePresence initial={false}>
        {advanced && (
          <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} style={{ overflow: 'hidden' }}>
            <div className="cols2">
              <Field label="Extra ports (comma separated)" hint="Other ports this project opens. They are included in conflict checks.">
                <input className="input mono" defaultValue={extraText} key={extraText} placeholder="8766, 8768"
                  onBlur={(e) => set({ extraPorts: e.target.value.split(/[ ,]+/).map(Number).filter((n) => n > 0 && n < 65536) })} />
              </Field>
              <Field label="Stop command (optional)" hint="Runs before the process tree is killed, e.g. docker compose down.">
                <input className="input mono" value={c.stopCommand} onChange={(e) => set({ stopCommand: e.target.value })} />
              </Field>
            </div>

            <div className="setting"><div className="t"><b>Pass the port to the project</b><span>Sets an environment variable so the project listens on the port above (only works if the project reads it).</span></div>
              {c.injectPort && <input className="input mono" style={{ width: 150 }} value={c.portEnvName} onChange={(e) => set({ portEnvName: e.target.value })} />}
              <Switch on={c.injectPort} onChange={(v) => set({ injectPort: v })} label="Pass port" /></div>

            <div className="section" style={{ marginTop: 14 }}>
              <h3>Environment variables</h3>
              {envRows.map(([k, v], i) => (
                <div key={`${i}|${k}|${v}`} className="envrow">
                  <input className="input mono" placeholder="NAME" defaultValue={k} onBlur={(e) => { const rows = [...envRows]; rows[i] = [e.target.value.trim(), rows[i][1]]; setEnv(rows); }} />
                  <input className="input mono" placeholder="value" defaultValue={v} onBlur={(e) => { const rows = [...envRows]; rows[i] = [rows[i][0], e.target.value]; setEnv(rows); }} />
                  <button type="button" className="btn icon" onClick={() => setEnv(envRows.filter((_, j) => j !== i))} aria-label="Remove"><Trash2 size={15} /></button>
                </div>
              ))}
              <button type="button" className="btn sm" onClick={() => setEnv([...envRows, [`VAR_${envRows.length + 1}`, '']])}><Plus size={14} />Add variable</button>
            </div>

            <div className="cols3">
              <Field label="If it crashes">
                <select className="select" value={c.restart} onChange={(e) => set({ restart: e.target.value })}>
                  <option value="onFailure">Restart on failure</option><option value="always">Always restart</option><option value="never">Never restart</option>
                </select>
              </Field>
              <Field label="Max restarts"><input className="input mono" type="number" min="0" value={c.maxRestarts} onChange={(e) => set({ maxRestarts: Number(e.target.value) })} /></Field>
              <Field label="Startup timeout (s)" hint="Before warning that the port never opened."><input className="input mono" type="number" min="10" value={c.startupTimeoutSec} onChange={(e) => set({ startupTimeoutSec: Number(e.target.value) })} /></Field>
              <Field label="CPU priority">
                <select className="select" value={c.priority} onChange={(e) => set({ priority: e.target.value })}>
                  <option value="normal">Normal</option><option value="belownormal">Below normal (lighter)</option><option value="idle">Idle (lightest)</option>
                </select>
              </Field>
              <Field label="Memory limit (MB)" hint="0 = none. Restarts the project if it grows past this."><input className="input mono" type="number" min="0" value={c.memoryLimitMb} onChange={(e) => set({ memoryLimitMb: Number(e.target.value) })} /></Field>
              <Field label="Start delay (s)" hint="Extra wait when starting with Windows."><input className="input mono" type="number" min="0" value={c.startDelaySec} onChange={(e) => set({ startDelaySec: Number(e.target.value) })} /></Field>
            </div>

            <Field label="Start these first (dependencies)">
              <div className="row wrap">
                {projects.filter((p) => p.config.id !== c.id).map((p) => {
                  const on = c.dependsOn?.includes(p.config.id);
                  return <button type="button" key={p.config.id} className={cx('chip', on && 'ok')} style={{ cursor: 'pointer' }}
                    onClick={() => set({ dependsOn: on ? c.dependsOn.filter((x) => x !== p.config.id) : [...(c.dependsOn || []), p.config.id] })}>{p.config.name}</button>;
                })}
                {projects.filter((p) => p.config.id !== c.id).length === 0 && <span className="hint">No other projects yet.</span>}
              </div>
            </Field>

            <div className="setting"><div className="t"><b>Open the browser when it is ready</b><span>Also when it starts with Windows. (Your own start scripts may open a tab by themselves.)</span></div><Switch on={c.openBrowserOnStart} onChange={(v) => set({ openBrowserOnStart: v })} label="open on start" /></div>
            <div className="setting"><div className="t"><b>Adjust headers under the friendly name</b><span>Makes apps that check Origin/redirects behave behind http://name.localhost.</span></div><Switch on={c.rewriteHeaders} onChange={(v) => set({ rewriteHeaders: v })} label="rewrite headers" /></div>
            <div className="setting"><div className="t"><b>Keep standard input open</b><span>Only for programs that wait for console input. Normally off so “Press any key” scripts do not hang.</span></div><Switch on={c.keepStdinOpen} onChange={(v) => set({ keepStdinOpen: v })} label="keep stdin" /></div>

            <div className="cols2" style={{ marginTop: 14 }}>
              <Field label="Group / label"><input className="input" value={c.group} placeholder="e.g. AI Studios" onChange={(e) => set({ group: e.target.value })} /></Field>
              <Field label="Accent color">
                <div className="row wrap">
                  {COLORS.map((col) => (
                    <button type="button" key={col || 'auto'} onClick={() => set({ color: col })} aria-label={col || 'automatic'}
                      style={{ width: 24, height: 24, borderRadius: 8, border: c.color === col ? '2px solid var(--text)' : '2px solid transparent', background: col || 'linear-gradient(135deg,var(--a1),var(--a2))' }} />
                  ))}
                </div>
              </Field>
            </div>
            <Field label="Notes"><textarea className="textarea" rows={2} value={c.notes} onChange={(e) => set({ notes: e.target.value })} /></Field>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
