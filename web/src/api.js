// Thin client for the Server Brain engine. Same origin, so no CORS and no tokens.
export async function api(method, path, body) {
  const res = await fetch('/api' + path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : { 'Content-Type': 'application/json' },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  let data = null;
  try { data = await res.json(); } catch { /* empty body */ }
  if (!res.ok) {
    const msg = data?.error || (data?.errors && data.errors.join(' ')) || res.statusText || 'Request failed';
    const err = new Error(msg);
    err.data = data;
    throw err;
  }
  return data;
}

export const get = (p) => api('GET', p);
export const post = (p, b) => api('POST', p, b ?? {});
export const put = (p, b) => api('PUT', p, b);
export const del = (p) => api('DELETE', p);

// ---- bridge to the Windows shell (WebView2). Falls back gracefully in a normal browser.
const wv = typeof window !== 'undefined' ? window.chrome?.webview : undefined;
export const isDesktopApp = !!wv;
const pending = new Map();
if (wv) {
  wv.addEventListener('message', (e) => {
    const m = e.data;
    if (m && m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  });
}
export function pickFolder() {
  if (!wv) return Promise.resolve(null);
  return new Promise((resolve) => {
    const id = Math.random().toString(36).slice(2);
    pending.set(id, (m) => resolve(m.path || null));
    wv.postMessage({ type: 'pickFolder', id });
  });
}
export function openUrl(url) {
  if (!url) return;
  window.open(url, '_blank', 'noopener');
}
