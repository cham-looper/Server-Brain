import { useCallback, useEffect, useMemo, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Folder, FolderOpen, HardDrive, ChevronRight, ScanSearch, FolderPlus, ArrowLeft, Sparkles, AlertOctagon, Wand2, Loader2, FolderSearch } from 'lucide-react';
import { Modal, Check_, Segmented, spring } from './ui.jsx';
import ProjectForm, { emptyConfig, suggestPort } from './ProjectForm.jsx';
import { get, post, pickFolder, isDesktopApp } from './api.js';
import { toast, useStore } from './store.js';
import { MODES, TYPES, cx } from './util.js';
import { TypeIcon } from './ui.jsx';

function FolderBrowser({ value, onPick, onChoose, chooseLabel, onPath }) {
  const [data, setData] = useState(null);
  const [path, setPath] = useState(value || '');
  const [typed, setTyped] = useState(value || '');
  const [err, setErr] = useState('');
  const load = useCallback(async (p) => {
    try {
      const d = await get('/fs?path=' + encodeURIComponent(p || ''));
      setData(d); setErr(d.error || ''); setPath(d.path || ''); setTyped(d.path || ''); onPath?.(d.path || '');
    } catch (e) { setErr(e.message); }
  }, [onPath]);
  useEffect(() => { load(value || ''); /* eslint-disable-next-line */ }, []);
  const crumbs = useMemo(() => {
    if (!path) return [];
    const parts = path.split(/[\\/]/).filter(Boolean);
    const out = []; let acc = '';
    parts.forEach((p, i) => { acc = i === 0 ? p + (path.includes(':') ? '\\' : '/') : acc.replace(/[\\/]$/, '') + (path.includes('\\') ? '\\' : '/') + p; out.push({ name: p, path: acc }); });
    return out;
  }, [path]);
  return (
    <div className="fb">
      <div className="fb-path">
        <input className="input mono" value={typed} placeholder="Type or paste a folder path, e.g. D:\Local-AI-Studios" onChange={(e) => setTyped(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && load(typed)} />
        <button className="btn" onClick={() => load(typed)}>Go</button>
        {isDesktopApp && <button className="btn" onClick={async () => { const p = await pickFolder(); if (p) load(p); }}><FolderSearch size={16} />Browse…</button>}
      </div>
      <div style={{ padding: '6px 10px', borderBottom: '1px solid var(--border)' }} className="crumbs">
        <button onClick={() => load('')}>This PC</button>
        {crumbs.map((c) => <span key={c.path} style={{ display: 'contents' }}><ChevronRight size={12} /><button onClick={() => load(c.path)}>{c.name}</button></span>)}
      </div>
      <div className="fb-list">
        {err && <div className="hint" style={{ color: 'var(--bad)', padding: 8 }}>{err}</div>}
        {!path && data?.drives?.map((d) => (
          <button key={d.path} className="fb-item" onClick={() => load(d.path)}><HardDrive size={18} color="var(--a2)" /><b>{d.name}</b><span className="muted">{d.label}</span></button>
        ))}
        {path && data?.parent && <button className="fb-item" onClick={() => load(data.parent)}><ArrowLeft size={18} /><span>Up one level</span></button>}
        {path && data?.dirs?.map((d) => (
          <button key={d.path} className="fb-item" onClick={() => load(d.path)} onDoubleClick={() => onPick?.(d.path)}>
            {d.project ? <FolderOpen size={18} color="var(--a1)" /> : <Folder size={18} color="var(--faint)" />}
            <span>{d.name}</span>{d.added ? <span className="tag" style={{ color: 'var(--muted)' }}>added</span> : d.project ? <span className="tag">looks like a project</span> : null}
          </button>
        ))}
        {path && data && data.dirs.length === 0 && <div className="hint" style={{ padding: 10 }}>No sub-folders here.</div>}
      </div>
      {onChoose && (
        <div className="fb-path" style={{ borderTop: '1px solid var(--border)', borderBottom: 0, justifyContent: 'flex-end' }}>
          <button className="btn primary" disabled={!path} onClick={() => onChoose(path)}>{chooseLabel}</button>
        </div>
      )}
    </div>
  );
}

export default function AddProject({ open, onClose, onAdded }) {
  const [step, setStep] = useState('pick');          // pick | browse-scan | browse-one | bulk | single
  const [busy, setBusy] = useState(false);
  const [scanRoot, setScanRoot] = useState('');
  const [items, setItems] = useState([]);
  const [defaultMode, setDefaultMode] = useState('manual');
  const [cfg, setCfg] = useState(emptyConfig());
  const [sug, setSug] = useState(null);
  const [errors, setErrors] = useState([]);
  const projects = useStore((s) => s.projects);

  useEffect(() => { if (open) { setStep('pick'); setErrors([]); setItems([]); setSug(null); setCfg(emptyConfig()); } }, [open]);

  const scan = async (path) => {
    setBusy(true); setScanRoot(path);
    try {
      const r = await post('/scan', { path });
      setItems(r.suggestions.map((s) => ({
        ...s, selected: !s.alreadyAdded, command: (s.commands.find((c) => c.recommended) || s.commands[0])?.command || '',
        stopCommand: (s.commands.find((c) => c.recommended) || s.commands[0])?.stopCommand || '', mode: 'manual', port: s.port,
      })));
      setStep('bulk');
    } catch (e) { toast('error', 'Scan failed', e.message); } finally { setBusy(false); }
  };

  const detectOne = async (path) => {
    setBusy(true);
    try {
      const s = await post('/detect', { path });
      setSug(s);
      const best = s.commands.find((c) => c.recommended) || s.commands[0];
      setCfg({
        ...emptyConfig(), id: s.id, name: s.name, path: s.path, type: best && best.kind !== 'script' ? best.kind : s.type, command: best?.command || '', stopCommand: best?.stopCommand || '',
        port: s.port, extraPorts: s.extraPorts || [], portEnvName: s.portEnvName || 'PORT',
      });
      setStep('single');
    } catch (e) { toast('error', 'Could not read that folder', e.message); } finally { setBusy(false); }
  };

  // ---- bulk helpers
  const patchItem = (i, patch) => setItems((arr) => arr.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const selected = items.filter((x) => x.selected);
  const portUse = useMemo(() => {
    const m = new Map();
    selected.forEach((x) => { if (x.port) m.set(x.port, [...(m.get(x.port) || []), x.name]); });
    return m;
  }, [selected]);
  const existingPorts = useMemo(() => {
    const m = new Map();
    projects.forEach((p) => [p.config.port, ...(p.config.extraPorts || [])].filter(Boolean).forEach((pt) => m.set(pt, p.config.name)));
    return m;
  }, [projects]);
  const issue = (x) => {
    if (!x.selected || !x.port) return null;
    const others = (portUse.get(x.port) || []).filter((n) => n !== x.name);
    if (others.length) return `Same port as ${others.join(', ')}`;
    if (existingPorts.has(x.port)) return `Port already assigned to ${existingPorts.get(x.port)}`;
    return x.notes?.find((n) => n.startsWith(`Port ${x.port} is currently in use`)) || null;
  };
  const clashes = selected.filter(issue).length;

  const autoFix = async () => {
    const used = new Set([...existingPorts.keys()]);
    selected.forEach((x) => { if (x.port && !issue(x)) used.add(x.port); });
    const next = [...items];
    for (let i = 0; i < next.length; i++) {
      const x = next[i];
      if (!x.selected || !x.port || !issue(x)) continue;
      let p = await suggestPort(null, x.port + 1);
      while (used.has(p)) p = await suggestPort(null, p + 1);
      used.add(p);
      next[i] = { ...x, port: p, injectPort: true, notes: (x.notes || []).filter((n) => !n.startsWith('Port ')) };
    }
    setItems(next.map((x) => ({ ...x })));
    toast('ok', 'Ports re-assigned', 'Clashing ports were moved to free ones and are passed to each project via its port variable.');
  };

  const importSelected = async () => {
    setBusy(true);
    let ok = 0; const failed = [];
    for (const x of selected) {
      const body = {
        ...emptyConfig(), id: x.id, name: x.name, path: x.path, type: x.type, command: x.command, stopCommand: x.stopCommand || '',
        port: x.port || null, extraPorts: x.extraPorts || [], portEnvName: x.portEnvName || 'PORT', injectPort: !!x.injectPort, mode: x.mode || defaultMode,
      };
      try { await post('/projects', body); ok++; } catch (e) { failed.push(`${x.name}: ${e.message}`); }
    }
    setBusy(false);
    if (ok) toast('ok', `Added ${ok} project${ok > 1 ? 's' : ''}`, 'Start them from the dashboard. Nothing was changed inside their folders.');
    if (failed.length) toast('error', 'Some projects were not added', failed.join('\n'), 9000);
    if (ok) { onAdded?.(); onClose(); }
  };

  const saveSingle = async () => {
    setBusy(true); setErrors([]);
    try { await post('/projects', cfg); toast('ok', 'Project added', `${cfg.name} is ready. Press Start.`); onAdded?.(cfg.id); onClose(); }
    catch (e) { setErrors(e.data?.errors || [e.message]); } finally { setBusy(false); }
  };

  const titles = { pick: 'Add projects', 'browse-scan': 'Scan a folder of projects', 'browse-one': 'Choose the project folder', bulk: 'Projects found', single: 'Add a project' };
  const footer = step === 'bulk' ? (
    <>
      <span className="muted grow">{selected.length} selected{clashes ? <b style={{ color: 'var(--bad)' }}> · {clashes} port clash{clashes > 1 ? 'es' : ''}</b> : ''}</span>
      {clashes > 0 && <button className="btn" onClick={autoFix}><Wand2 size={16} />Fix clashing ports</button>}
      <button className="btn" onClick={() => setStep('browse-scan')}>Back</button>
      <button className="btn primary" disabled={!selected.length || busy} onClick={importSelected}>{busy ? <Loader2 size={16} className="spin" /> : <FolderPlus size={16} />}Add {selected.length || ''} project{selected.length === 1 ? '' : 's'}</button>
    </>
  ) : step === 'single' ? (
    <>
      <div className="grow">{errors.map((e) => <div key={e} className="hint" style={{ color: 'var(--bad)' }}>• {e}</div>)}</div>
      <button className="btn" onClick={() => setStep('browse-one')}>Back</button>
      <button className="btn primary" disabled={busy} onClick={saveSingle}>{busy ? 'Saving…' : 'Add project'}</button>
    </>
  ) : null;

  return (
    <Modal open={open} onClose={onClose} title={titles[step]} subtitle={step === 'bulk' ? `in ${scanRoot}` : step === 'pick' ? 'Server Brain reads your folders to suggest a start command and port. It never changes them.' : undefined} footer={footer}>
      <AnimatePresence mode="wait" initial={false}>
        <motion.div key={step} initial={{ opacity: 0, x: 24 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -24 }} transition={{ duration: 0.18 }}>
          {step === 'pick' && (
            <div className="cols2">
              <button className="glass" style={{ padding: 24, textAlign: 'left', cursor: 'pointer', border: '1px solid var(--border)' }} onClick={() => setStep('browse-scan')}>
                <ScanSearch size={34} color="var(--a1)" /><h3 style={{ margin: '12px 0 6px', fontSize: 17 }}>Scan a folder of projects</h3>
                <div className="muted">Point at a parent folder such as <span className="mono">D:\Local-AI-Studios</span> and add many projects at once, with port clashes flagged before you import.</div>
                <span className="chip ok" style={{ marginTop: 12 }}><Sparkles size={12} />Fastest way</span>
              </button>
              <button className="glass" style={{ padding: 24, textAlign: 'left', cursor: 'pointer', border: '1px solid var(--border)' }} onClick={() => setStep('browse-one')}>
                <FolderPlus size={34} color="var(--a2)" /><h3 style={{ margin: '12px 0 6px', fontSize: 17 }}>Add one project</h3>
                <div className="muted">Choose a single project folder. Node.js, Python, PHP, Docker, static sites and custom commands are all supported.</div>
              </button>
            </div>
          )}
          {step === 'browse-scan' && (
            <>
              <p className="muted" style={{ marginTop: 0 }}>Choose the <b>parent</b> folder. Every sub-folder that looks runnable will be listed.</p>
              <FolderBrowser value={scanRoot} chooseLabel={busy ? 'Scanning…' : 'Scan this folder'} onChoose={scan} onPick={scan} />
            </>
          )}
          {step === 'browse-one' && (
            <>
              <p className="muted" style={{ marginTop: 0 }}>Choose the folder of the project (double-click a folder to select it).</p>
              <FolderBrowser value={cfg.path} chooseLabel={busy ? 'Reading…' : 'Use this folder'} onChoose={detectOne} onPick={detectOne} />
            </>
          )}
          {step === 'bulk' && (
            <>
              <div className="row" style={{ marginBottom: 12 }}>
                <span className="muted">New projects start as</span>
                <Segmented small value={defaultMode} onChange={(m) => { setDefaultMode(m); setItems((a) => a.map((x) => ({ ...x, mode: m }))); }} options={MODES} />
                <span className="grow" />
                <button className="btn sm" onClick={() => setItems((a) => a.map((x) => ({ ...x, selected: !x.alreadyAdded })))}>Select all</button>
                <button className="btn sm" onClick={() => setItems((a) => a.map((x) => ({ ...x, selected: false })))}>None</button>
              </div>
              {items.length === 0 && <div className="empty" style={{ padding: 30 }}><p>Nothing runnable was found directly inside this folder.</p></div>}
              {items.map((x, i) => {
                const is = issue(x);
                return (
                  <div key={x.path} className={cx('sug', x.selected ? 'on' : 'off')}>
                    <Check_ on={x.selected} onChange={(v) => patchItem(i, { selected: v })} />
                    <div style={{ minWidth: 0 }}>
                      <div className="row" style={{ marginBottom: 8 }}>
                        <TypeIcon type={x.type} size={16} />
                        <b style={{ fontSize: 15 }}>{x.name}</b>
                        <span className="chip">{TYPES[x.type] || x.type}</span>
                        {x.alreadyAdded && <span className="chip">already added</span>}
                        <span className="grow" />
                        <span className="faint mono" style={{ fontSize: 11.5, overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: 260, whiteSpace: 'nowrap' }}>{x.path}</span>
                      </div>
                      <div className="row">
                        <select className="select mono" style={{ flex: 1, fontSize: 12.5 }} value={x.command} onChange={(e) => { const cm = x.commands.find((c) => c.command === e.target.value); patchItem(i, { command: e.target.value, stopCommand: cm?.stopCommand || '' }); }}>
                          {x.commands.map((c) => <option key={c.command} value={c.command}>{c.label}  —  {c.command}</option>)}
                        </select>
                        <input className="input mono" style={{ width: 96 }} type="number" placeholder="port" value={x.port ?? ''} onChange={(e) => patchItem(i, { port: e.target.value ? Number(e.target.value) : null })} />
                      </div>
                      {is && <div className="hint" style={{ color: 'var(--bad)', marginTop: 6, display: 'flex', gap: 6, alignItems: 'center' }}><AlertOctagon size={13} />{is}. Change the port or fix it with the button below.</div>}
                      {x.commands.find((c) => c.command === x.command)?.note && <div className="hint" style={{ marginTop: 4 }}>• {x.commands.find((c) => c.command === x.command).note}</div>}
                      {x.notes?.filter((n) => !n.startsWith('Port ')).slice(0, 2).map((n) => <div key={n} className="hint" style={{ marginTop: 2, color: 'var(--warn)' }}>• {n}</div>)}
                    </div>
                  </div>
                );
              })}
            </>
          )}
          {step === 'single' && <ProjectForm value={cfg} onChange={setCfg} suggestion={sug} />}
        </motion.div>
      </AnimatePresence>
    </Modal>
  );
}
