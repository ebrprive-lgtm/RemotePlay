        const CACHE_NAME = 'remoteplay-shell-v1';
        const SHELL_ASSETS = [
          '/',
          '/manifest.webmanifest',
          '/icons/icon-192.png',
          '/icons/icon-512.png',
          '/icons/apple-touch-icon.png'
        ];

        self.addEventListener('install', event => {
          event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(SHELL_ASSETS)));
          self.skipWaiting();
        });

        self.addEventListener('activate', event => {
          event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))));
          self.clients.claim();
        });

        self.addEventListener('fetch', event => {
          const url = new URL(event.request.url);
          if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/icons/') || url.pathname === '/service-worker.js') {
            event.respondWith(fetch(event.request));
            return;
          }

          event.respondWith(fetch(event.request).catch(() => caches.match(event.request).then(r => r || caches.match('/'))));
        });
