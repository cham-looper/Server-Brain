import { useEffect, useState } from 'react';

/** Re-renders every `ms` milliseconds (0 = never). */
export function useNow(ms) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!ms) return undefined;
    const t = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}

export function useHotkey(key, handler, { ctrl = false } = {}) {
  useEffect(() => {
    const h = (e) => {
      const tag = document.activeElement?.tagName;
      const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
      if (ctrl ? (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === key : !typing && e.key === key && !e.ctrlKey && !e.metaKey) {
        e.preventDefault();
        handler(e);
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [key, handler, ctrl]);
}
