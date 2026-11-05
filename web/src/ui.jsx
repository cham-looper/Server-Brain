import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { motion, AnimatePresence, animate } from 'framer-motion';
import {
  Hexagon, Code2, Braces, Container, FileText, Box, X, Check, AlertTriangle, Info, CircleCheck, CircleX,
} from 'lucide-react';
import { STATUS, cx } from './util.js';
import { useStore, dismissToast } from './store.js';

export const spring = { type: 'spring', stiffness: 420, damping: 32 };

const TYPE_ICON = { node: Hexagon, python: Code2, php: Braces, docker: Container, static: FileText, custom: Box };
const TYPE_COLORS = {
  node: ['#4ade80', '#16a34a'], python: ['#60a5fa', '#facc15'], php: ['#a78bfa', '#6366f1'],
  docker: ['#38bdf8', '#2563eb'], static: ['#fbbf24', '#f97316'], custom: ['#8b7bff', '#35d6c4'],
};
export function TypeIcon({ type, color, size = 22 }) {
  const Ico = TYPE_ICON[type] || Box;
  const [c1, c2] = color ? [color, color] : TYPE_COLORS[type] || TYPE_COLORS.custom;
  return (
    <div className="picon" style={{ '--pc1': c1, '--pc2': c2 }}><Ico size={size} strokeWidth={2.2} /></div>
  );
}

export function StatusPill({ status }) {
  const s = STATUS[status] || STATUS.stopped;
  return (
    <span className={cx('status', s.tone)}>
      <span className={cx('dot', s.tone)} />{s.label}
    </span>
  );
}

export function Switch({ on, onChange, disabled, label }) {
  return <button type="button" role="switch" aria-checked={on} aria-label={label} disabled={disabled} className={cx('switch', on && 'on')} onClick={() => onChange(!on)} />;
}

export function Segmented({ options, value, onChange, small }) {
  const id = useId();
  return (
    <div className="seg" role="tablist">
      {options.map((o) => (
        <button key={o.id} type="button" title={o.hint} className={value === o.id ? 'on' : ''} onClick={() => onChange(o.id)} style={small ? { padding: '4px 10px', fontSize: 12 } : undefined}>
          {value === o.id && <motion.span layoutId={'seg' + id} className="thumb" transition={spring} />}
          <span style={{ position: 'relative' }}>{o.label}</span>
        </button>
      ))}
    </div>
  );
}

export function Counter({ value, decimals = 0, suffix = '' }) {
  const ref = useRef(null);
  const prev = useRef(0);
  useEffect(() => {
    const c = animate(prev.current, value ?? 0, {
      duration: 0.7, ease: [0.16, 1, 0.3, 1],
      onUpdate: (v) => { if (ref.current) ref.current.textContent = v.toFixed(decimals) + suffix; },
    });
    prev.current = value ?? 0;
    return () => c.stop();
  }, [value, decimals, suffix]);
  return <span ref={ref}>{(value ?? 0).toFixed(decimals) + suffix}</span>;
}

export function Sparkline({ data, color = 'var(--a1)', max, height = 40, fill = true }) {
  const id = useId();
  const pts = data && data.length > 1 ? data : [0, 0];
  const top = max ?? Math.max(1, ...pts) * 1.15;
  const w = 200;
  const step = w / (pts.length - 1);
  const ys = pts.map((v) => height - 3 - (Math.min(v, top) / top) * (height - 8));
  let d = `M0 ${ys[0]}`;
  for (let i = 1; i < pts.length; i++) {
    const x0 = (i - 1) * step, x1 = i * step, mx = (x0 + x1) / 2;
    d += ` C${mx} ${ys[i - 1]}, ${mx} ${ys[i]}, ${x1} ${ys[i]}`;
  }
  return (
    <svg viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" width="100%" height="100%" style={{ display: 'block' }}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={color} stopOpacity=".35" /><stop offset="1" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      {fill && <path d={`${d} L${w} ${height} L0 ${height} Z`} fill={`url(#${id})`} />}
      <path d={d} fill="none" stroke={color} strokeWidth="2" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

export function Ring({ value, size = 64, color = 'var(--a1)', children }) {
  const r = (size - 10) / 2, c = 2 * Math.PI * r;
  return (
    <div style={{ position: 'relative', width: size, height: size }}>
      <svg className="ring" width={size} height={size}>
        <circle cx={size / 2} cy={size / 2} r={r} stroke="var(--border)" />
        <motion.circle cx={size / 2} cy={size / 2} r={r} stroke={color} strokeDasharray={c}
          initial={false} animate={{ strokeDashoffset: c * (1 - Math.min(1, (value || 0) / 100)) }} transition={{ duration: 0.8, ease: 'easeOut' }} />
      </svg>
      <div style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', fontWeight: 700 }}>{children}</div>
    </div>
  );
}

export function Modal({ open, onClose, title, subtitle, children, footer, small, dismissable = true }) {
  useEffect(() => {
    if (!open) return;
    const h = (e) => { if (e.key === 'Escape' && dismissable) onClose?.(); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [open, onClose, dismissable]);
  return (
    <AnimatePresence>
      {open && (
        <>
          <motion.div className="scrim" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={dismissable ? onClose : undefined} />
          <motion.div className={cx('modal', small && 'sm')} role="dialog" aria-modal="true"
            initial={{ opacity: 0, scale: 0.94, x: '-50%', y: '-46%' }} animate={{ opacity: 1, scale: 1, x: '-50%', y: '-50%' }} exit={{ opacity: 0, scale: 0.96, x: '-50%', y: '-48%' }} transition={spring}>
            <div className="modal-h">
              <div className="grow"><h2>{title}</h2>{subtitle && <div className="muted" style={{ fontSize: 13 }}>{subtitle}</div>}</div>
              {dismissable && <button className="btn ghost icon" onClick={onClose} aria-label="Close"><X size={18} /></button>}
            </div>
            <div className="modal-b">{children}</div>
            {footer && <div className="modal-f">{footer}</div>}
          </motion.div>
        </>
      )}
    </AnimatePresence>
  );
}

export function Drawer({ open, onClose, children }) {
  useEffect(() => {
    if (!open) return;
    const h = (e) => { if (e.key === 'Escape') onClose?.(); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [open, onClose]);
  return (
    <AnimatePresence>
      {open && (
        <>
          <motion.div className="scrim" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={onClose} />
          <motion.aside className="drawer" initial={{ x: '100%' }} animate={{ x: 0 }} exit={{ x: '100%' }} transition={{ type: 'spring', stiffness: 330, damping: 36 }}>
            {children}
          </motion.aside>
        </>
      )}
    </AnimatePresence>
  );
}

export function Menu({ trigger, children, align = 'right' }) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const menuRef = useRef(null);

  // The menu is rendered in a portal with fixed positioning, so no parent (cards use overflow:hidden) can clip it.
  const place = () => {
    const r = ref.current?.getBoundingClientRect();
    if (!r) return;
    const w = 220, h = menuRef.current?.offsetHeight || 260;
    const below = window.innerHeight - r.bottom;
    const up = below < h + 12 && r.top > below;
    const left = Math.max(8, Math.min(window.innerWidth - w - 8, align === 'left' ? r.left : r.right - w));
    setPos({ left, top: up ? Math.max(8, r.top - h - 6) : r.bottom + 6, up });
  };

  useEffect(() => {
    if (!open) return undefined;
    place();
    const raf = requestAnimationFrame(place);   // re-measure once the real height is known
    const close = (e) => { if (!ref.current?.contains(e.target) && !menuRef.current?.contains(e.target)) setOpen(false); };
    const hide = () => setOpen(false);
    const esc = (e) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('mousedown', close);
    window.addEventListener('resize', hide);
    window.addEventListener('scroll', hide, true);
    window.addEventListener('keydown', esc);
    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('mousedown', close);
      window.removeEventListener('resize', hide);
      window.removeEventListener('scroll', hide, true);
      window.removeEventListener('keydown', esc);
    };
  }, [open]);

  return (
    <div ref={ref} style={{ position: 'relative' }}>
      {trigger({ open, toggle: () => setOpen((o) => !o) })}
      {createPortal(
        <AnimatePresence>
          {open && pos && (
            <motion.div ref={menuRef} className="menu" style={{ position: 'fixed', left: pos.left, top: pos.top, right: 'auto', width: 220, zIndex: 120 }}
              initial={{ opacity: 0, y: pos.up ? 6 : -6, scale: 0.97 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, scale: 0.97 }} transition={{ duration: 0.14 }}
              onClick={() => setOpen(false)}>
              {children}
            </motion.div>
          )}
        </AnimatePresence>,
        document.body,
      )}
    </div>
  );
}

export function Field({ label, hint, children, error }) {
  return (
    <div className="field">
      {label && <label>{label}</label>}
      {children}
      {error ? <div className="hint" style={{ color: 'var(--bad)' }}>{error}</div> : hint && <div className="hint">{hint}</div>}
    </div>
  );
}

export function Toasts() {
  const toasts = useStore((s) => s.toasts);
  const icons = { error: <CircleX size={20} color="var(--bad)" />, warn: <AlertTriangle size={20} color="var(--warn)" />, ok: <CircleCheck size={20} color="var(--ok)" />, info: <Info size={20} color="var(--info)" /> };
  return (
    <div className="toasts">
      <AnimatePresence initial={false}>
        {toasts.map((t) => (
          <motion.div key={t.id} layout className={cx('toast', t.level)} initial={{ opacity: 0, x: 60, scale: 0.95 }} animate={{ opacity: 1, x: 0, scale: 1 }} exit={{ opacity: 0, x: 60, scale: 0.9 }} transition={spring}>
            {icons[t.level] || icons.info}
            <div style={{ flex: 1, minWidth: 0 }}><b>{t.title}</b>{t.message && <span>{t.message}</span>}</div>
            <button className="btn ghost sm icon" onClick={() => dismissToast(t.id)} aria-label="Dismiss"><X size={14} /></button>
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  );
}

export function Aurora() {
  return <div className="aurora" aria-hidden><div className="blob b1" /><div className="blob b2" /><div className="blob b3" /></div>;
}

export function Check_({ on, onChange }) {
  return <button type="button" className={cx('check', on && 'on')} onClick={() => onChange(!on)} aria-pressed={on}>{on && <Check size={14} strokeWidth={3} />}</button>;
}

export function Confirm({ open, title, message, confirmLabel = 'Confirm', danger, onConfirm, onClose }) {
  return (
    <Modal open={open} onClose={onClose} title={title} small
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className={cx('btn', danger ? 'danger' : 'primary')} onClick={() => { onConfirm(); onClose(); }}>{confirmLabel}</button></>}>
      <p style={{ margin: 0 }} className="muted">{message}</p>
    </Modal>
  );
}
