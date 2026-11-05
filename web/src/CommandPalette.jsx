import { useEffect, useMemo, useRef, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Search, Play, Square, ExternalLink, LayoutDashboard, Network, Globe, Activity, Settings, Plus, Terminal } from 'lucide-react';
import { useStore, isUp } from './store.js';
import { startProject, stopProject, openProject } from './actions.js';
import { cx } from './util.js';

export default function CommandPalette({ open, onClose, go, onAdd, onOpenDrawer }) {
  const projects = useStore((s) => s.projects);
  const [q, setQ] = useState('');
  const [sel, setSel] = useState(0);
  const input = useRef(null);

  useEffect(() => { if (open) { setQ(''); setSel(0); setTimeout(() => input.current?.focus(), 30); } }, [open]);

  const items = useMemo(() => {
    const nav = [
      { id: 'n-dash', label: 'Go to Dashboard', icon: LayoutDashboard, run: () => go('/') },
      { id: 'n-ports', label: 'Go to Ports', icon: Network, run: () => go('/ports') },
      { id: 'n-pub', label: 'Go to Public access', icon: Globe, run: () => go('/public') },
      { id: 'n-act', label: 'Go to Activity', icon: Activity, run: () => go('/activity') },
      { id: 'n-set', label: 'Go to Settings', icon: Settings, run: () => go('/settings') },
      { id: 'n-help', label: 'Help & guide', icon: Settings, run: () => go('/help') },
      { id: 'n-pc', label: 'Check your PC (install missing programs)', icon: Settings, run: () => go('/help') },
      { id: 'n-add', label: 'Add project…', icon: Plus, run: onAdd },
    ];
    const proj = projects.flatMap((p) => [
      isUp(p)
        ? { id: 's-' + p.config.id, label: `Stop ${p.config.name}`, hint: 'project', icon: Square, run: () => stopProject(p) }
        : { id: 'a-' + p.config.id, label: `Start ${p.config.name}`, hint: 'project', icon: Play, run: () => startProject(p) },
      { id: 'o-' + p.config.id, label: `Open ${p.config.name}`, hint: 'browser', icon: ExternalLink, run: () => openProject(p) },
      { id: 'l-' + p.config.id, label: `Logs: ${p.config.name}`, hint: 'console', icon: Terminal, run: () => onOpenDrawer(p, 'logs') },
    ]);
    const all = [...proj, ...nav];
    const f = q.trim().toLowerCase();
    return (f ? all.filter((i) => i.label.toLowerCase().includes(f)) : all).slice(0, 40);
  }, [projects, q, go, onAdd, onOpenDrawer]);

  useEffect(() => setSel(0), [q]);

  const run = (it) => { onClose(); setTimeout(it.run, 60); };
  const onKey = (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setSel((s) => Math.min(items.length - 1, s + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setSel((s) => Math.max(0, s - 1)); }
    else if (e.key === 'Enter' && items[sel]) run(items[sel]);
    else if (e.key === 'Escape') onClose();
  };

  return (
    <AnimatePresence>
      {open && (
        <>
          <motion.div className="scrim" style={{ zIndex: 94 }} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={onClose} />
          <motion.div className="palette" initial={{ opacity: 0, y: -16, scale: 0.97, x: '-50%' }} animate={{ opacity: 1, y: 0, scale: 1, x: '-50%' }} exit={{ opacity: 0, y: -10, scale: 0.97, x: '-50%' }} transition={{ type: 'spring', stiffness: 460, damping: 34 }}>
            <input ref={input} value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={onKey} placeholder="Type a command or project name…" />
            <div className="res">
              {items.length === 0 && <div className="hint" style={{ padding: 16 }}>No results.</div>}
              {items.map((it, i) => (
                <button key={it.id} className={cx('it', i === sel && 'sel')} onMouseEnter={() => setSel(i)} onClick={() => run(it)}>
                  <it.icon size={17} color="var(--a1)" />{it.label}{it.hint && <small>{it.hint}</small>}
                </button>
              ))}
            </div>
          </motion.div>
        </>
      )}
    </AnimatePresence>
  );
}
