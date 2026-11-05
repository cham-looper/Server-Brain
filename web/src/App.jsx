import { useCallback, useEffect, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { LayoutDashboard, Network, Globe, Activity, Settings, Command, WifiOff, LifeBuoy } from 'lucide-react';
import Help from './Help.jsx';
import Welcome from './Welcome.jsx';
import { Aurora, Toasts, Confirm, spring } from './ui.jsx';
import Dashboard from './Dashboard.jsx';
import AddProject from './AddProject.jsx';
import ProjectDrawer from './ProjectDrawer.jsx';
import CommandPalette from './CommandPalette.jsx';
import { PortsPage, PublicPage, ActivityPage, SettingsPage } from './Pages.jsx';
import { useStore, connect, toast, hasProblem } from './store.js';
import { post } from './api.js';
import { killProcess, removeProject } from './actions.js';
import { useHotkey } from './hooks.js';
import { cx, fmtMb } from './util.js';

const NAV = [
  ['/', 'Dashboard', LayoutDashboard], ['/ports', 'Ports', Network], ['/public', 'Public access', Globe],
  ['/activity', 'Activity', Activity], ['/settings', 'Settings', Settings], ['/help', 'Help & guide', LifeBuoy],
];

function useRoute() {
  const read = () => (location.hash.replace(/^#/, '') || '/');
  const [route, setRoute] = useState(read);
  useEffect(() => { const h = () => setRoute(read()); window.addEventListener('hashchange', h); return () => window.removeEventListener('hashchange', h); }, []);
  return [route, (r) => { location.hash = r; }];
}

export default function App() {
  const [route, go] = useRoute();
  const connected = useStore((s) => s.connected);
  const ready = useStore((s) => s.ready);
  const projects = useStore((s) => s.projects);
  const system = useStore((s) => s.system);
  const version = useStore((s) => s.version);
  const [adding, setAdding] = useState(false);
  const [drawer, setDrawer] = useState({ id: null, tab: 'overview' });
  const [palette, setPalette] = useState(false);
  const [confirmDel, setConfirmDel] = useState(null);
  const [confirmKill, setConfirmKill] = useState(null);
  const settings = useStore((s) => s.settings);
  const [welcomeHidden, setWelcomeHidden] = useState(false);
  const showWelcome = ready && !!settings && !settings.onboardingDone && !welcomeHidden;

  useEffect(() => { connect(); }, []);
  useHotkey('k', () => setPalette((o) => !o), { ctrl: true });

  const openDrawer = useCallback((p, tab = 'overview') => setDrawer({ id: p.config.id, tab }), []);
  const closeDrawer = useCallback(() => setDrawer((d) => ({ ...d, id: null })), []);
  const problems = projects.filter(hasProblem).length;
  const running = projects.filter((p) => p.runtime.status === 'running').length;

  const quit = async () => {
    try { await post('/app/quit'); toast('info', 'Server Brain is closing…'); } catch (e) { toast('error', 'Could not quit', e.message); }
  };

  return (
    <>
      <Aurora />
      <AnimatePresence>
        {ready && !connected && (
          <motion.div className="conn" initial={{ opacity: 0, y: -20 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -20 }}><WifiOff size={14} style={{ verticalAlign: -2 }} /> Reconnecting to Server Brain…</motion.div>
        )}
      </AnimatePresence>
      <div className="app">
        <nav className="sidebar" aria-label="Main">
          <div className="brand"><img src="/logo.svg" alt="" /><div><b>Server Brain</b><span>v{version || '1.0'}</span></div></div>
          {NAV.map(([path, label, Icon]) => (
            <button key={path} className={cx('nav', route === path && 'active')} onClick={() => go(path)}>
              {route === path && <motion.span layoutId="nav-pill" className="pill" transition={spring} />}
              <Icon size={18} /><span>{label}</span>
              {path === '/' && projects.length > 0 && <span className={cx('badge', problems && 'bad')}>{problems || projects.length}</span>}
            </button>
          ))}
          <button className="nav" onClick={() => setPalette(true)}><Command size={18} /><span>Quick actions</span><span className="badge">Ctrl K</span></button>
          <div className="side-foot">
            <div className="row" style={{ justifyContent: 'space-between' }}><span>Running</span><b style={{ color: 'var(--text)' }}>{running}/{projects.length}</b></div>
            <div className="row" style={{ justifyContent: 'space-between' }}><span>PC memory</span><b style={{ color: 'var(--text)' }}>{fmtMb(system.usedMb)}</b></div>
          </div>
        </nav>

        <main className="main">
          <AnimatePresence mode="wait" initial={false}>
            <motion.div key={route} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -8 }} transition={{ duration: 0.18 }}>
              {route === '/' && <Dashboard onAdd={() => setAdding(true)} onOpenDrawer={openDrawer} onDelete={setConfirmDel} onConfirmKill={setConfirmKill} />}
              {route === '/ports' && <PortsPage onConfirmKill={setConfirmKill} />}
              {route === '/public' && <PublicPage onOpenDrawer={openDrawer} />}
              {route === '/activity' && <ActivityPage />}
              {route === '/settings' && <SettingsPage onQuit={quit} go={go} />}
              {route === '/help' && <Help go={go} onAdd={() => setAdding(true)} />}
            </motion.div>
          </AnimatePresence>
        </main>
      </div>

      <AddProject open={adding} onClose={() => setAdding(false)} />
      <ProjectDrawer id={drawer.id} tab={drawer.tab} setTab={(tab) => setDrawer((d) => ({ ...d, tab }))} onClose={closeDrawer} onDelete={(p) => setConfirmDel(p)} onConfirmKill={setConfirmKill} />
      <CommandPalette open={palette} onClose={() => setPalette(false)} go={go} onAdd={() => setAdding(true)} onOpenDrawer={openDrawer} />
      <Toasts />
      <Welcome open={showWelcome} onClose={() => setWelcomeHidden(true)} onAdd={() => setAdding(true)} go={go} />

      <Confirm open={!!confirmDel} onClose={() => setConfirmDel(null)} danger confirmLabel="Remove" title={`Remove ${confirmDel?.config.name}?`}
        message="It will be stopped and removed from Server Brain. The project's files are not touched."
        onConfirm={() => { removeProject(confirmDel); if (drawer.id === confirmDel?.config.id) closeDrawer(); }} />
      <Confirm open={!!confirmKill} onClose={() => setConfirmKill(null)} danger confirmLabel="Stop process" title={`Stop ${confirmKill?.processName || 'process'}?`}
        message={`This force-stops ${confirmKill?.processName} (PID ${confirmKill?.pid}) to free port ${confirmKill?.port}. Unsaved work in that program may be lost.`}
        onConfirm={async () => { const k = confirmKill; if (await killProcess(k.port, k.pid, k.processName)) k.onDone?.(); }} />
    </>
  );
}
