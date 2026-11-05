import { useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Sparkles, MonitorCheck, FolderPlus, PartyPopper, ArrowRight, ArrowLeft } from 'lucide-react';
import { Modal } from './ui.jsx';
import { SystemCheckPanel } from './SystemCheck.jsx';
import { put } from './api.js';
import { useStore } from './store.js';

const STEPS = ['Welcome', 'Your PC', 'Projects', 'Ready'];

/** First-run wizard: what the app is, whether this PC has what it needs, then add projects. */
export default function Welcome({ open, onClose, onAdd, go }) {
  const [step, setStep] = useState(0);
  const settings = useStore((s) => s.settings);

  const finish = async (then) => {
    try { await put('/settings', { ...settings, onboardingDone: true }); } catch { /* the wizard just shows again next time */ }
    onClose();
    if (then === 'add') onAdd();
    if (then === 'help') go('/help');
  };

  return (
    <Modal open={open} onClose={() => finish()} dismissable={false} title="Welcome to Server Brain"
      subtitle={`Step ${step + 1} of ${STEPS.length}: ${STEPS[step]}`}
      footer={
        <>
          <button className="btn ghost" onClick={() => finish()}>Skip setup</button>
          <span className="grow" />
          {step > 0 && <button className="btn" onClick={() => setStep(step - 1)}><ArrowLeft size={15} />Back</button>}
          {step < STEPS.length - 1
            ? <button className="btn primary" onClick={() => setStep(step + 1)}>Next<ArrowRight size={15} /></button>
            : <button className="btn primary" onClick={() => finish('add')}><FolderPlus size={16} />Add my projects</button>}
        </>
      }>
      <div style={{ display: 'flex', gap: 6, marginBottom: 18 }}>
        {STEPS.map((s, i) => <div key={s} style={{ flex: 1, height: 4, borderRadius: 4, background: i <= step ? 'linear-gradient(90deg,var(--a1),var(--a2))' : 'var(--border)', transition: 'background .3s' }} />)}
      </div>
      <AnimatePresence mode="wait" initial={false}>
        <motion.div key={step} initial={{ opacity: 0, x: 24 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -24 }} transition={{ duration: 0.18 }}>
          {step === 0 && (
            <div style={{ textAlign: 'center', padding: '10px 10px 0' }}>
              <img src="/logo.svg" alt="" width="84" height="84" />
              <h3 style={{ fontSize: 22, margin: '14px 0 8px' }}>All your local servers in one place</h3>
              <p className="muted" style={{ maxWidth: 520, margin: '0 auto 14px' }}>Add your Node.js, Python and PHP projects once. Server Brain starts them with Windows, keeps them running, warns you about port clashes, opens them by name in your browser and uses almost no resources while idle.</p>
              <div className="cols3" style={{ textAlign: 'left', marginTop: 18 }}>
                <div className="card-in"><Sparkles size={18} color="var(--a1)" /><b style={{ display: 'block', margin: '6px 0 2px' }}>Manual · Always · On demand</b><span className="muted" style={{ fontSize: 12.5 }}>Choose how each project runs.</span></div>
                <div className="card-in"><Sparkles size={18} color="var(--a2)" /><b style={{ display: 'block', margin: '6px 0 2px' }}>name.localhost</b><span className="muted" style={{ fontSize: 12.5 }}>Easy addresses, no port numbers.</span></div>
                <div className="card-in"><Sparkles size={18} color="var(--ok)" /><b style={{ display: 'block', margin: '6px 0 2px' }}>Safe</b><span className="muted" style={{ fontSize: 12.5 }}>Never edits your project files.</span></div>
              </div>
            </div>
          )}
          {step === 1 && (
            <>
              <div className="row" style={{ marginBottom: 10 }}><MonitorCheck size={20} color="var(--a1)" /><b>Does this PC have what your projects need?</b></div>
              <p className="muted" style={{ marginTop: 0 }}>On a new computer some programs may be missing. Install only what your projects use. You can come back to this any time in <b>Help → Check your PC</b>.</p>
              <SystemCheckPanel compact />
            </>
          )}
          {step === 2 && (
            <div style={{ textAlign: 'center', padding: 10 }}>
              <FolderPlus size={46} color="var(--a2)" />
              <h3 style={{ fontSize: 20, margin: '12px 0 6px' }}>Add your projects</h3>
              <p className="muted" style={{ maxWidth: 520, margin: '0 auto' }}>Pick a folder that contains your projects (for example <span className="mono">D:\Local-AI-Studios</span>) and Server Brain finds the runnable ones, suggests the start command and port, and flags clashes before you import.</p>
              <p className="hint" style={{ marginTop: 12 }}>Nothing inside your folders is changed. You can also add one project at a time.</p>
            </div>
          )}
          {step === 3 && (
            <div style={{ textAlign: 'center', padding: 10 }}>
              <PartyPopper size={46} color="var(--a1)" />
              <h3 style={{ fontSize: 20, margin: '12px 0 6px' }}>You are all set</h3>
              <p className="muted" style={{ maxWidth: 520, margin: '0 auto 14px' }}>Tips: closing this window only hides it, Server Brain keeps running in the tray. Press <span className="kbd">Ctrl K</span> for quick actions. The <b>Help</b> page has a full guide, including addresses, port fixes, public sharing, updates and backup.</p>
              <button className="btn" onClick={() => finish('help')}>Open the guide first</button>
            </div>
          )}
        </motion.div>
      </AnimatePresence>
    </Modal>
  );
}
