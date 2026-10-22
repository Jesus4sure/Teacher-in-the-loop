// Shared helpers for the TITLAPE PWA (no external libraries: everything must work offline on the hub).
const T = (() => {
  const store = {
    get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* storage unavailable */ } },
    clear() { try { sessionStorage.clear(); } catch { } }
  };

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const pct = (v, d = 0) => (v == null || isNaN(v)) ? '—' : `${(v * 100).toFixed(d)}%`;

  async function api(path, { method = 'GET', body, raw = false } = {}) {
    const headers = { 'Content-Type': 'application/json' };
    const token = store.get('titlape.token');
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (res.status === 401) { store.clear(); location.href = '/'; throw new Error('Session expired'); }
    if (raw) return res;
    const text = await res.text();
    const data = text ? JSON.parse(text) : null;
    if (!res.ok) throw new Error(data?.detail || data?.title || res.statusText);
    return data;
  }

  async function login(userId) {
    const r = await api('/api/auth/login', { method: 'POST', body: { userId } });
    store.set('titlape.token', r.token);
    store.set('titlape.role', r.role);
    store.set('titlape.subject', r.subject);
    store.set('titlape.classes', JSON.stringify(r.classes || []));
    return r;
  }

  function requireRole(...roles) {
    const role = store.get('titlape.role');
    if (!role || !roles.includes(role)) { location.href = '/'; return null; }
    return { role, subject: store.get('titlape.subject'), classes: JSON.parse(store.get('titlape.classes') || '[]') };
  }

  function logout() { store.clear(); location.href = '/'; }

  function toast(msg, ms = 4200) {
    let el = document.querySelector('.toast');
    if (!el) { el = document.createElement('div'); el.className = 'toast'; el.setAttribute('role', 'status'); document.body.appendChild(el); }
    el.textContent = msg; el.classList.add('show');
    clearTimeout(el._t); el._t = setTimeout(() => el.classList.remove('show'), ms);
  }

  function netChip(el) {
    const paint = () => {
      el.className = `chip ${navigator.onLine ? 'good' : 'warn'}`;
      el.innerHTML = `<span class="dot"></span>${navigator.onLine ? 'Hub connected' : 'Offline — working locally'}`;
    };
    addEventListener('online', paint); addEventListener('offline', paint); paint();
  }

  /** Reads a POST Server-Sent-Events response, calling onEvent(name, data) for each event. */
  async function readSse(res, onEvent) {
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const block = buf.slice(0, i); buf = buf.slice(i + 2);
        let ev = 'message', data = '';
        for (const line of block.split('\n')) {
          if (line.startsWith('event:')) ev = line.slice(6).trim();
          else if (line.startsWith('data:')) data += line.slice(5).trim();
        }
        onEvent(ev, data ? JSON.parse(data) : null);
      }
    }
  }

  const masteryClass = (p, attempts) => attempts === 0 ? 'none' : p >= .85 ? 'm4' : p >= .65 ? 'm3' : p >= .45 ? 'm2' : p >= .25 ? 'm1' : 'm0';

  if ('serviceWorker' in navigator) {
    addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(() => { }));
  }

  return { api, login, logout, requireRole, esc, pct, toast, netChip, readSse, masteryClass, store };
})();
