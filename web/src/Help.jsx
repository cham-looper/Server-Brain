import { useMemo, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import {
  Search, ChevronDown, Rocket, Link2, Zap, LayoutDashboard, Network, Globe, MonitorCheck, History, LifeBuoy, Keyboard, Info, Copy, ExternalLink, Plus, Folder,
} from 'lucide-react';
import { SystemCheckPanel } from './SystemCheck.jsx';
import { useStore, toast } from './store.js';
import { post } from './api.js';
import { copy, cx } from './util.js';
import { spring } from './ui.jsx';

function Addr({ label, url, note }) {
  return (
    <div style={{ marginBottom: 10 }}>
      <div className="label" style={{ marginBottom: 4 }}>{label}</div>
      <div className="codebox"><Link2 size={14} color="var(--muted)" /><span>{url}</span>
        <button className="btn sm icon ghost" onClick={async () => (await copy(url)) && toast('ok', 'Copied', url, 1500)} aria-label="Copy"><Copy size={14} /></button>
        <button className="btn sm icon ghost" onClick={() => post('/open-external', { path: url })} aria-label="Open in browser"><ExternalLink size={14} /></button></div>
      {note && <div className="hint" style={{ marginTop: 4 }}>{note}</div>}
    </div>
  );
}

const Steps = ({ items }) => (
  <ol style={{ margin: '4px 0 0', paddingLeft: 20, display: 'grid', gap: 8 }}>{items.map((t, i) => <li key={i}>{t}</li>)}</ol>
);
const Table = ({ rows, head }) => (
  <table className="table" style={{ borderSpacing: '0 4px', marginTop: 4 }}>
    {head && <thead><tr>{head.map((h) => <th key={h}>{h}</th>)}</tr></thead>}
    <tbody>{rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j} style={j === 0 ? { fontWeight: 600, whiteSpace: 'nowrap' } : undefined}>{c}</td>)}</tr>)}</tbody>
  </table>
);
const Tip = ({ children }) => <div className="alert info" style={{ marginTop: 12 }}><Info size={16} color="var(--info)" /><div>{children}</div></div>;

function sections({ env, go, onAdd }) {
  const dash = `http://127.0.0.1:${env?.dashboardPort ?? 7777}`;
  const names = env?.proxyPort ? `.localhost${env.proxyPort === 80 ? '' : ':' + env.proxyPort}` : null;
  return [
    {
      id: 'start', icon: Rocket, title: 'Start here: set up in 5 minutes', kw: 'quick start first time setup begin add scan import install',
      body: (
        <>
          <Steps items={[
            <>Open <b>Help → Check your PC</b> (below) and install anything marked red that your projects need (Node.js, Python…).</>,
            <>Click <b>Add project → Scan a folder of projects</b>, choose the parent folder (for example <span className="mono">D:\Local-AI-Studios</span>) and press <b>Scan this folder</b>.</>,
            <>Untick what you do not want. Red text means two projects use the same port: press <b>Fix clashing ports</b>.</>,
            <>Press <b>Add</b>. Nothing inside your project folders is changed.</>,
            <>On each card pick <b>Manual</b>, <b>Always</b> or <b>On demand</b> (see “Run modes”), then press <b>Start</b>.</>,
          ]} />
          <div className="row" style={{ marginTop: 14 }}>
            <button className="btn primary" onClick={onAdd}><Plus size={16} />Add project</button>
            <button className="btn" onClick={() => go('/')}><LayoutDashboard size={16} />Go to dashboard</button>
          </div>
          <Tip>You only do this once. Afterwards you just open the dashboard (tray icon) whenever you want to start, stop or check something.</Tip>
        </>
      ),
    },
    {
      id: 'addresses', icon: Link2, title: 'Web addresses: dashboard and projects', kw: 'url port address link open browser localhost 7777 brain.localhost access web',
      body: (
        <>
          <p style={{ marginTop: 0 }}>The dashboard is a web page served by Server Brain on <b>this PC only</b>. You can use the app window or any browser.</p>
          <Addr label="Dashboard (main address)" url={dash} note={`Also works: http://localhost:${env?.dashboardPort ?? 7777} and http://brain.localhost (when the names port is 80).`} />
          <p>Every project also gets its own address:</p>
          <Table head={['Type', 'Looks like', 'Notes']} rows={[
            ['Friendly name', names ? `http://my-project${names}` : 'switched off in Settings', 'Type it in Chrome, Edge or Firefox. On-demand projects start by themselves.'],
            ['Direct', 'http://127.0.0.1:PORT', 'Always works, including from command-line tools.'],
            ['Public', 'https://xxxx.trycloudflare.com', 'Only after you switch on Public access for that project.'],
          ]} />
          <Tip>The dashboard is never reachable from other computers or phones, and never through a public tunnel. This is deliberate: it can start programs. If port {env?.dashboardPort ?? 7777} is taken, Server Brain uses the next free one; the tray menu → <b>Open in browser</b> always opens the right address.</Tip>
          <Tip><b>name.localhost</b> works in browsers only. Tools like curl, Node or Python on Windows need the direct 127.0.0.1 address.</Tip>
        </>
      ),
    },
    {
      id: 'modes', icon: Zap, title: 'Run modes: Manual, Always, On demand', kw: 'manual always on demand idle start with windows once auto restart',
      body: (
        <Table head={['Mode', 'Best for', 'What happens']} rows={[
          ['Manual', 'Things you use sometimes', 'Starts when you press Start and runs until you press Stop (“once”).'],
          ['Always', 'Things that must always be up', 'Starts automatically when you sign in to Windows (one after another, not all at once) and is restarted if it crashes.'],
          ['On demand', 'Heavy or rarely used apps (AI tools, GPU)', 'Uses no memory or GPU until you open its address. Starts in a few seconds and stops after the idle time (default 15 min).'],
        ]} />
      ),
    },
    {
      id: 'dashboard', icon: LayoutDashboard, title: 'Using the dashboard, window and tray', kw: 'card start stop restart logs tray close window quit menu shortcut ctrl k',
      body: (
        <>
          <Table head={['Control', 'What it does']} rows={[
            ['Start / Stop', 'Starts or stops the project. Start also opens it in your browser when ready (can be switched off in Settings).'],
            ['↗ Open', 'Opens the project in the browser (starts it first if needed).'],
            ['↻ Restart', 'Stops and starts it again.'],
            ['>_ Logs', 'Live console output with search, download and clear.'],
            ['⋯ menu', 'Edit settings, copy address, show folder, public access, remove.'],
            ['Ctrl + K', 'Quick actions: start, stop or open any project, jump to a page.'],
            ['/', 'Jump to the search box.'],
          ]} />
          <Tip><b>Closing the window only hides it.</b> Server Brain keeps running in the tray (bottom-right, maybe under the ^ arrow). Left-click the tray icon to reopen; right-click lists your projects with Start / Stop / Open. To really exit use tray → <b>Quit Server Brain</b>; that stops your projects.</Tip>
        </>
      ),
    },
    {
      id: 'ports', icon: Network, title: 'Port problems and how to fix them', kw: 'port conflict in use busy blocked free address already used 8765 7870',
      body: (
        <>
          <p style={{ marginTop: 0 }}>A <b>port</b> is like a door number. Only one program can use a door at a time. Server Brain checks when you add a project and again before every start.</p>
          <Table head={['You see', 'Meaning', 'Fix']} rows={[
            ['Yellow box: “also assigned to X”', 'Two projects are set to the same port.', 'Run only one at a time, or press “Use a free port”.'],
            ['Red box: “already in use by node.exe”', 'Another program holds the port right now.', '“Free port” stops that program (asks first; system programs are protected), or use a free port.'],
            ['Status “Port conflict”', 'The start was blocked to avoid a clash.', 'Fix as above, or “Start anyway”.'],
          ]} />
          <Tip>“Use a free port” gives the project its new port through its port variable (for example <span className="mono">APP_PORT</span>). If a project hard-codes its port in its code, it ignores that, and you have to change the port in the project itself. The <b>Ports</b> page shows everything listening on this PC.</Tip>
          <div className="row" style={{ marginTop: 12 }}><button className="btn" onClick={() => go('/ports')}><Network size={16} />Open Ports page</button></div>
        </>
      ),
    },
    {
      id: 'public', icon: Globe, title: 'Public access (share on the internet)', kw: 'public tunnel cloudflare share trycloudflare domain access code token',
      body: (
        <>
          <Steps items={[
            <>Open the project → <b>Public access</b> tab.</>,
            <>Choose <b>Quick link</b> (random address, no account) or <b>My own domain</b> (needs a Cloudflare tunnel token and hostname).</>,
            <>Press <b>Go public</b>, then copy the address and the <b>access code</b> and send both to the visitor.</>,
            <><b>Stop sharing</b> closes it again.</>,
          ]} />
          <Tip>Visitors can only reach that one project, never the dashboard, and must enter the access code. Keep the code on unless the project has its own login. Needs <b>cloudflared</b> (see “Check your PC”).</Tip>
        </>
      ),
    },
    {
      id: 'pc', icon: MonitorCheck, title: 'Check your PC (new computer or missing programs)', kw: 'new pc install node python php docker winget missing not installed prerequisites webview2 requirements',
      body: (
        <>
          <p style={{ marginTop: 0 }}>Server Brain runs your projects with the programs installed on this PC, exactly like a terminal would. On a new computer some may be missing. This check shows what is installed and can install the rest for you.</p>
          <SystemCheckPanel />
          <Tip><b>What if something is missing?</b> A project that needs a missing program will not start; its card says which program is missing and has an <b>Install</b> button. After installing, just press Start again. No restart of Server Brain is needed.</Tip>
        </>
      ),
    },
    {
      id: 'updates', icon: History, title: 'Updating, reinstalling, backup and reset', kw: 'update upgrade reinstall clean install backup restore reset export import uninstall settings data folder',
      body: (
        <>
          <Table head={['I want to…', 'Do this']} rows={[
            ['Update to a new version', 'Run the new installer and choose “Update (keep everything)”. Your projects and settings stay exactly as they are.'],
            ['Start completely fresh', 'Run the installer and choose “Clean install”, or use Settings → Reset. Your old setup is saved to a backup first.'],
            ['Back up my setup', 'Settings → Backup & reset → Back up now (or Export to a file you can keep).'],
            ['Move to another PC', 'Export on the old PC, install on the new one, then Settings → Import.'],
            ['Uninstall', 'Windows Settings → Apps → Server Brain. It asks whether to also delete your settings. Your project folders are never touched.'],
          ]} />
          <Tip>Your settings and project list live in <span className="mono">{env?.dataDir || '%LOCALAPPDATA%\\ServerBrain'}</span>, separate from the program, so updates never remove them. Server Brain also keeps an automatic copy before every version change, in the <span className="mono">backups</span> folder there.</Tip>
        </>
      ),
    },
    {
      id: 'trouble', icon: LifeBuoy, title: 'Troubleshooting', kw: 'problem error not working fix help does not open crash restart browser tab stuck starting localhost 80 smartscreen',
      body: (
        <Table head={['Problem', 'Fix']} rows={[
          ['Dashboard does not open', 'Check the tray icon. Tray → Open in browser. No tray icon? Start “Server Brain” from the Start menu.'],
          ['name.localhost does not open', 'Use Chrome, Edge or Firefox. If port 80 is used by another program, use the address shown on the card (it contains :8080) or the direct 127.0.0.1:PORT.'],
          ['Card says a program is missing', 'Press its Install button (or Help → Check your PC), then Start again.'],
          ['Stuck on “Starting…”', 'First runs may install packages. Open Logs. If the log shows a different port, press “Use port N” in the yellow box.'],
          ['Project keeps restarting', 'Open Logs for the error. After the maximum number of restarts it shows Failed and stops.'],
          ['A browser tab opens by itself on every start', 'Your own start.bat does that. In the project’s Settings choose a direct command such as npm start.'],
          ['“Windows protected your PC” when installing', 'The installer is not code-signed. Choose More info → Run anyway.'],
          ['The window is empty or white', 'WebView2 may be missing: Help → Check your PC → install it, or just use the dashboard in your browser.'],
          ['Anything else', 'Open Activity for what happened, and %LOCALAPPDATA%\\ServerBrain\\crash.log if the app itself crashed.'],
        ]} />
      ),
    },
    {
      id: 'keys', icon: Keyboard, title: 'Keyboard shortcuts', kw: 'keyboard shortcut hotkey ctrl k search',
      body: <Table rows={[['Ctrl + K', 'Quick actions / command palette'], ['/', 'Focus the search box'], ['Esc', 'Close the open panel, menu or dialog']]} />,
    },
    {
      id: 'about', icon: Info, title: 'About, license and safety', kw: 'about license mit safety security privacy version open source',
      body: (
        <>
          <p style={{ marginTop: 0 }}>Server Brain is free, open-source software under the <b>MIT License</b>. It never modifies the projects it runs and sends nothing anywhere (public sharing only happens when you switch it on).</p>
          <ul style={{ paddingLeft: 20, margin: 0, display: 'grid', gap: 6 }}>
            <li>Everything listens on this PC only unless you share a project publicly.</li>
            <li>The dashboard rejects other websites and anything arriving through a tunnel.</li>
            <li>Tunnel tokens are stored encrypted for your Windows account.</li>
            <li>The “Free port” action never stops system programs.</li>
          </ul>
        </>
      ),
    },
  ];
}

export default function Help({ go, onAdd }) {
  const env = useStore((s) => s.env);
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(() => new Set(['start']));
  const list = useMemo(() => sections({ env, go, onAdd }), [env, go, onAdd]);
  const f = q.trim().toLowerCase();
  const shown = f ? list.filter((s) => (s.title + ' ' + s.kw).toLowerCase().includes(f)) : list;
  const toggle = (id) => setOpen((o) => { const n = new Set(o); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const isOpen = (id) => !!f || open.has(id);

  return (
    <div className="page" style={{ maxWidth: 980 }}>
      <div className="topbar">
        <div><h1>Help &amp; guide</h1><div className="sub">Everything you need to use Server Brain. Search, or open a topic.</div></div>
        <div className="grow" />
        <button className="btn" onClick={() => setOpen(new Set(list.map((s) => s.id)))}>Open all</button>
        <button className="btn" onClick={() => setOpen(new Set())}>Close all</button>
      </div>
      <div className="search" style={{ maxWidth: 'none', marginBottom: 18 }}><Search size={16} /><input className="input" placeholder="Search help… (try: port, new pc, update, public)" value={q} onChange={(e) => setQ(e.target.value)} /></div>

      <div className="row wrap" style={{ marginBottom: 18 }}>
        {list.map((s) => <button key={s.id} className="chip" style={{ cursor: 'pointer' }} onClick={() => { setQ(''); setOpen((o) => new Set(o).add(s.id)); setTimeout(() => document.getElementById('help-' + s.id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60); }}><s.icon size={12} />{s.title.split(':')[0].split('(')[0].trim()}</button>)}
      </div>

      {shown.length === 0 && <div className="empty" style={{ padding: 30 }}><p>No help topic matches “{q}”.</p></div>}
      {shown.map((s, i) => (
        <motion.section key={s.id} id={'help-' + s.id} className="glass" style={{ marginBottom: 12, overflow: 'hidden' }} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ ...spring, delay: i * 0.025 }}>
          <button onClick={() => toggle(s.id)} style={{ display: 'flex', alignItems: 'center', gap: 12, width: '100%', padding: '15px 18px', border: 0, background: 'none', textAlign: 'left' }}>
            <s.icon size={20} color="var(--a1)" /><b style={{ fontSize: 15.5, flex: 1 }}>{s.title}</b>
            <motion.span animate={{ rotate: isOpen(s.id) ? 180 : 0 }} style={{ display: 'flex' }}><ChevronDown size={18} /></motion.span>
          </button>
          <AnimatePresence initial={false}>
            {isOpen(s.id) && (
              <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.22 }} style={{ overflow: 'hidden' }}>
                <div style={{ padding: '0 20px 20px' }}>{s.body}</div>
              </motion.div>
            )}
          </AnimatePresence>
        </motion.section>
      ))}
    </div>
  );
}
