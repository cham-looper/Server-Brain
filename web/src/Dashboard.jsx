import { useMemo, useState } from 'react';
import { motion, AnimatePresence, LayoutGroup } from 'framer-motion';
import { Plus, Search, Play, Square, LayoutGrid, List, Activity, Cpu, MemoryStick, ShieldAlert, Rocket, Layers, AlertOctagon } from 'lucide-react';
import { Counter, Sparkline, Ring, Segmented, spring } from './ui.jsx';
import ProjectCard from './ProjectCard.jsx';
import { useStore, hasProblem, isUp, toast } from './store.js';
import { post } from './api.js';
import { greeting, cx } from './util.js';
import { useHotkey } from './hooks.js';

function Stat({ icon: Icon, label, children, spark, sparkColor, tone, delay = 0 }) {
  return (
    <motion.div className={cx('glass stat', tone)} initial={{ opacity: 0, y: 16 }} animate={{ opacity: 1, y: 0 }} transition={{ ...spring, delay }}>
      <div className="k"><Icon size={14} />{label}</div>
      <div className="v">{children}</div>
      {spark && <div className="spark"><Sparkline data={spark} color={sparkColor} height={46} max={100} /></div>}
    </motion.div>
  );
}

export default function Dashboard({ onAdd, onOpenDrawer, onEdit, onDelete, onConfirmKill }) {
  const projects = useStore((s) => s.projects);
  const system = useStore((s) => s.system);
  const sysHistory = useStore((s) => s.sysHistory);
  const ready = useStore((s) => s.ready);
  const [q, setQ] = useState('');
  const [filter, setFilter] = useState('all');
  const [view, setView] = useState(() => { try { return localStorage.getItem('sb.view') || 'grid'; } catch { return 'grid'; } });
  const changeView = (v) => { setView(v); try { localStorage.setItem('sb.view', v); } catch { /* ignore */ } };
  useHotkey('/', () => document.getElementById('search')?.focus());

  const running = projects.filter(isUp).length;
  const problems = projects.filter(hasProblem);
  const always = projects.filter((p) => p.config.mode === 'always').length;
  const memPct = system.totalMb ? (system.usedMb / system.totalMb) * 100 : 0;
  const myMem = projects.reduce((a, p) => a + (p.runtime.memMb || 0), 0);

  const clashes = useMemo(() => {
    const m = new Map();
    projects.forEach((p) => p.runtime.conflicts?.filter((c) => c.kind === 'duplicate').forEach((c) => {
      const o = projects.find((x) => x.config.id === c.otherProjectId);
      if (!o) return;
      const key = c.port + ':' + [p.config.id, o.config.id].sort().join('|');
      m.set(key, { port: c.port, a: p.config.name, b: o.config.name, error: c.severity === 'error' });
    }));
    return [...m.values()];
  }, [projects]);

  const list = projects.filter((p) => {
    if (q && !(p.config.name + p.config.id + p.config.group + p.config.path).toLowerCase().includes(q.toLowerCase())) return false;
    if (filter === 'running') return isUp(p);
    if (filter === 'stopped') return !isUp(p);
    if (filter === 'problems') return hasProblem(p);
    return true;
  });

  const bulk = async (what) => {
    try { await post(what === 'start' ? '/start-all' : '/stop-all'); toast('info', what === 'start' ? 'Starting always-on projects…' : 'Stopping everything…'); }
    catch (e) { toast('error', 'Failed', e.message); }
  };

  return (
    <div className="page">
      <div className="topbar">
        <div>
          <h1>{greeting()} 👋</h1>
          <div className="sub">{!ready ? 'Connecting to the engine…' : projects.length === 0 ? 'Add your first project to get started.' : `${running} of ${projects.length} project${projects.length === 1 ? '' : 's'} running${always ? ` · ${always} start${always === 1 ? 's' : ''} with Windows` : ''}`}</div>
        </div>
        <div className="grow" />
        {projects.length > 0 && <>
          <button className="btn" onClick={() => bulk('start')}><Rocket size={16} />Start always-on</button>
          <button className="btn" onClick={() => bulk('stop')} disabled={running === 0}><Square size={15} />Stop all</button>
        </>}
        <button className="btn primary" onClick={onAdd}><Plus size={17} />Add project</button>
      </div>

      <div className="stats">
        <Stat icon={Layers} label="Running" delay={0}><Counter value={running} /><small> / {projects.length}</small></Stat>
        <Stat icon={Cpu} label="System CPU" delay={0.05} spark={sysHistory} sparkColor="var(--a1)"><Counter value={system.cpu} decimals={0} /><small>%</small></Stat>
        <Stat icon={MemoryStick} label="Memory" delay={0.1}>
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <div><Counter value={system.usedMb / 1024} decimals={1} /><small> / {(system.totalMb / 1024).toFixed(0)} GB</small>
              <div className="hint" style={{ fontSize: 11.5, fontWeight: 400, letterSpacing: 0 }}>projects use {myMem >= 1024 ? (myMem / 1024).toFixed(1) + ' GB' : Math.round(myMem) + ' MB'}</div></div>
            <Ring value={memPct} size={54} color="var(--a2)"><span style={{ fontSize: 12 }}>{Math.round(memPct)}%</span></Ring>
          </div>
        </Stat>
        <Stat icon={ShieldAlert} label="Needs attention" delay={0.15} tone={problems.length ? 'bad' : ''}><Counter value={problems.length} />{problems.length === 0 && <small> all clear</small>}</Stat>
      </div>

      <AnimatePresence>
        {clashes.length > 0 && (
          <motion.div className="alert bad" style={{ marginBottom: 18 }} initial={{ opacity: 0, height: 0, marginBottom: 0 }} animate={{ opacity: 1, height: 'auto', marginBottom: 18 }} exit={{ opacity: 0, height: 0, marginBottom: 0 }}>
            <AlertOctagon size={18} color="var(--bad)" />
            <div><b>Port conflicts</b>
              {clashes.map((c) => <div key={c.port + c.a + c.b}>Port <b className="mono">{c.port}</b> is assigned to both <b>{c.a}</b> and <b>{c.b}</b>{c.error ? ' — they cannot both run.' : '.'}</div>)}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {projects.length > 0 && (
        <div className="toolbar">
          <div className="search"><Search size={16} /><input id="search" className="input" placeholder="Search projects…   ( / )" value={q} onChange={(e) => setQ(e.target.value)} /></div>
          <Segmented small value={filter} onChange={setFilter} options={[
            { id: 'all', label: `All ${projects.length}` }, { id: 'running', label: `Running ${running}` },
            { id: 'stopped', label: 'Stopped' }, { id: 'problems', label: `Problems ${problems.length}` }]} />
          <div className="grow" />
          <Segmented small value={view} onChange={changeView} options={[{ id: 'grid', label: 'Cards' }, { id: 'list', label: 'List' }]} />
        </div>
      )}

      {!ready && <div className="grid">{[0, 1, 2].map((i) => <div key={i} className="skeleton" />)}</div>}

      {ready && projects.length === 0 && (
        <div className="empty">
          <div className="orbit"><i /><i /><i /><img src="/logo.svg" alt="" /></div>
          <h2>Your servers, one calm place</h2>
          <p>Add your Node.js, Python or PHP projects once. Server Brain starts them with Windows, keeps them alive, warns you about port clashes and opens them by name in your browser.</p>
          <div className="row" style={{ justifyContent: 'center' }}>
            <button className="btn primary" onClick={onAdd}><Plus size={17} />Add your first project</button>
            <a className="btn" href="#/help">Open the guide</a>
          </div>
        </div>
      )}

      <LayoutGroup>
        <div className={view === 'grid' ? 'grid' : 'list'}>
          <AnimatePresence mode="popLayout">
            {list.map((p) => (
              <ProjectCard key={p.config.id} p={p} view={view} onOpenDrawer={onOpenDrawer} onEdit={(x) => onOpenDrawer(x, 'settings')} onDelete={onDelete} onConfirmKill={onConfirmKill} />
            ))}
          </AnimatePresence>
        </div>
      </LayoutGroup>
      {ready && projects.length > 0 && list.length === 0 && <div className="empty" style={{ padding: 40 }}><p>No projects match.</p></div>}
    </div>
  );
}
