// Offline-first service worker: the app shell is cached so pages open even when the hub's
// Wi-Fi drops; API calls always go to the network (the hub) and are never cached.
const CACHE = 'titlape-shell-v2';
const SHELL = ['/', '/index.html', '/student.html', '/teacher.html', '/ops.html', '/css/app.css',
  '/js/common.js', '/js/index.js', '/js/student.js', '/js/teacher.js', '/js/ops.js', '/icon.svg', '/manifest.webmanifest'];

self.addEventListener('install', (e) => e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener('activate', (e) => e.waitUntil(
  caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())));
self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.pathname.startsWith('/api/')) return;
  e.respondWith(fetch(e.request).then(res => {
    const copy = res.clone();
    caches.open(CACHE).then(c => c.put(e.request, copy));
    return res;
  }).catch(() => caches.match(e.request).then(r => r || caches.match('/index.html'))));
});
