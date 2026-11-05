import { post, put, del, openUrl } from './api.js';
import { toast } from './store.js';

export const urlFor = (p) => p.runtime.localUrl || p.runtime.directUrl;

export async function startProject(p, opts = {}) {
  try {
    const r = await post(`/projects/${p.config.id}/start?open=${opts.open ?? true}&force=${!!opts.force}`);
    if (!r.ok && !r.blocked) toast('error', `Could not start ${p.config.name}`, r.error);
    return r;
  } catch (e) { toast('error', 'Could not start', e.message); }
}

export async function stopProject(p) {
  try { await post(`/projects/${p.config.id}/stop`); } catch (e) { toast('error', 'Could not stop', e.message); }
}

export async function restartProject(p) {
  try { await post(`/projects/${p.config.id}/restart`); } catch (e) { toast('error', 'Could not restart', e.message); }
}

/** Open in the browser; if the project is not running yet, start it and open once it is ready. */
export async function openProject(p) {
  const up = p.runtime.status === 'running';
  if (up) { openUrl(urlFor(p)); return; }
  if (p.runtime.status === 'starting') { await post(`/projects/${p.config.id}/start?open=true`); toast('info', `Opening ${p.config.name} when ready…`); return; }
  await startProject(p, { open: true });
}

export async function patchProject(p, patch) {
  try {
    const next = { ...p.config, ...patch };
    await put(`/projects/${p.config.id}`, next);
    return true;
  } catch (e) { toast('error', 'Could not save', e.message); return false; }
}

export async function removeProject(p) {
  try { await del(`/projects/${p.config.id}`); toast('ok', 'Removed', `${p.config.name} was removed. Its folder was not touched.`); }
  catch (e) { toast('error', 'Could not remove', e.message); }
}

export async function killProcess(port, pid, name) {
  try {
    const r = await post('/ports/kill', { port, pid });
    toast('ok', 'Port freed', r.message || `${name} stopped.`);
    return true;
  } catch (e) { toast('error', 'Could not free the port', e.message); return false; }
}

/** Move the project to a free port and hand that port to it through its port environment variable. */
export async function moveToFreePort(p) {
  try {
    const from = (p.config.port || 3000) + 1;
    const { port } = await (await fetch(`/api/ports/suggest?project=${p.config.id}&from=${from}`)).json();
    const ok = await patchProject(p, { port, injectPort: true });
    if (ok) toast('ok', 'Port changed', `${p.config.name} will be started on port ${port} (passed as ${p.config.portEnvName || 'PORT'}). Projects that hard-code their port ignore this.`);
    return ok;
  } catch (e) { toast('error', 'Could not pick a port', e.message); return false; }
}
