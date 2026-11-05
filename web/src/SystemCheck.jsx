import { useCallback, useEffect, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { CheckCircle2, XCircle, MinusCircle, Download, ExternalLink, Loader2, RefreshCw, ShieldCheck, AlertTriangle, Power } from 'lucide-react';
import { Switch, spring } from './ui.jsx';
import { get, post } from './api.js';
import { useStore, toast } from './store.js';
import { cx } from './util.js';

/** Starts a winget install for a missing tool (used by the System check and by project cards). */
export async function installTool(id) {
  try { await post(`/system/install/${id}`); toast('info', 'Installing…', 'Windows may ask for permission. This can take a few minutes.', 6000); return true; }
  catch (e) { toast('error', 'Could not start the installer', e.message, 8000); return false; }
}

export function useSystemCheck() {
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const installs = useStore((s) => s.installs);
  const load = useCallback(async () => {
    setLoading(true);
    try { setData(await get('/system/check')); } catch (e) { toast('error', 'System check failed', e.message); } finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);
  // re-check whenever an install finishes
  const doneKey = Object.values(installs).filter((i) => i.status === 'done').map((i) => i.id).join(',');
  useEffect(() => { if (doneKey) load(); }, [doneKey, load]);
  return { data, loading, reload: load, installs };
}

export function SystemCheckPanel({ compact = false, showStartup = true }) {
  const { data, loading, reload, installs } = useSystemCheck();
  const [armed, setArmed] = useState(null);
  const startupOn = data?.startup;

  const missingRequired = useMemo(() => (data?.tools || []).filter((t) => !t.found && !t.optional), [data]);
  const missingOptional = useMemo(() => (data?.tools || []).filter((t) => !t.found && t.optional), [data]);

  if (!data) return <div className="skeleton" style={{ height: 160 }} />;

  return (
    <div>
      <div className={cx('alert', missingRequired.length ? 'warn' : 'info')} style={{ marginBottom: 14 }}>
        {missingRequired.length ? <AlertTriangle size={18} color="var(--warn)" /> : <ShieldCheck size={18} color="var(--ok)" />}
        <div>
          {missingRequired.length
            ? <><b>{missingRequired.map((t) => t.name).join(' and ')} {missingRequired.length > 1 ? 'are' : 'is'} not installed.</b> Server Brain runs your projects with the programs on this PC, so install what your projects use (you can skip the ones you don't need).</>
            : <><b>Your PC is ready.</b> The main programs were found{missingOptional.length ? `. ${missingOptional.length} optional one${missingOptional.length > 1 ? 's are' : ' is'} not installed, which is fine unless a project needs ${missingOptional.length > 1 ? 'them' : 'it'}.` : '.'}</>}
          {!data.winget && missingRequired.concat(missingOptional).length > 0 && <div className="hint" style={{ marginTop: 4 }}>One-click install needs the Windows Package Manager (winget), which is not available here. Use the Download buttons instead.</div>}
        </div>
      </div>

      {data.tools.map((t, i) => {
        const inst = installs[t.id];
        const running = inst?.status === 'running';
        return (
          <motion.div key={t.id} className="setting" initial={{ opacity: 0, x: -10 }} animate={{ opacity: 1, x: 0 }} transition={{ ...spring, delay: i * 0.03 }} style={{ alignItems: 'flex-start' }}>
            <div style={{ marginTop: 2 }}>
              {t.found ? <CheckCircle2 size={20} color="var(--ok)" /> : t.optional ? <MinusCircle size={20} color="var(--faint)" /> : <XCircle size={20} color="var(--bad)" />}
            </div>
            <div className="t">
              <b>{t.name} {t.optional && <span className="chip" style={{ marginLeft: 6 }}>optional</span>} {t.version && <span className="faint mono" style={{ fontSize: 11.5, marginLeft: 6 }}>{t.version}</span>}</b>
              <span>{t.why}</span>
              {t.note && <span style={{ color: 'var(--warn)' }}>{t.note}</span>}
              {t.found && t.path && !compact && <span className="mono faint" style={{ fontSize: 11 }}>{t.path}</span>}
              {inst && inst.status !== 'idle' && !t.found && <span style={{ color: inst.status === 'error' ? 'var(--bad)' : 'var(--a2)' }}>{running && <Loader2 size={12} className="spin" style={{ verticalAlign: -2, marginRight: 6 }} />}{inst.message}</span>}
            </div>
            {!t.found && (
              <div className="row" style={{ flex: 'none' }}>
                {t.canInstall && !running && (armed === t.id
                  ? <><button className="btn primary sm" onClick={() => { setArmed(null); installTool(t.id); }}><Download size={14} />Yes, install</button><button className="btn sm" onClick={() => setArmed(null)}>Cancel</button></>
                  : <button className="btn sm primary" onClick={() => setArmed(t.id)}><Download size={14} />Install</button>)}
                {running && <button className="btn sm" disabled><Loader2 size={14} className="spin" />Installing</button>}
                <a className="btn sm" href={t.url} target="_blank" rel="noreferrer"><ExternalLink size={14} />Download page</a>
              </div>
            )}
          </motion.div>
        );
      })}

      {showStartup && (
        <div className="setting">
          <div style={{ marginTop: 2 }}><Power size={20} color={startupOn ? 'var(--ok)' : 'var(--faint)'} /></div>
          <div className="t"><b>Start with Windows</b><span>Server Brain starts hidden in the tray when you sign in, then starts your Always-on projects.</span></div>
          <Switch on={!!startupOn} label="startup" onChange={async (v) => { try { await post('/system/startup', { enabled: v }); reload(); } catch (e) { toast('error', 'Could not change startup', e.message); } }} />
        </div>
      )}

      {data.ports.dashboard !== data.ports.dashboardWanted && (
        <div className="alert warn" style={{ marginTop: 12 }}><AlertTriangle size={16} color="var(--warn)" /><div>Port {data.ports.dashboardWanted} was taken by another program, so the dashboard uses port <b>{data.ports.dashboard}</b>.</div></div>
      )}
      {data.ports.namesEnabled && data.ports.names !== data.ports.namesWanted && (
        <div className="alert warn" style={{ marginTop: 12 }}><AlertTriangle size={16} color="var(--warn)" /><div>Port {data.ports.namesWanted} is used by another program (often IIS, Skype or another web server). Project names therefore include a port: <span className="mono">http://name.localhost:{data.ports.names}</span>.</div></div>
      )}

      <div className="row" style={{ marginTop: 12 }}>
        <button className="btn sm" onClick={reload} disabled={loading}><RefreshCw size={14} className={loading ? 'spin' : ''} />Check again</button>
        <span className="hint">Windows: {data.os}</span>
      </div>
    </div>
  );
}
