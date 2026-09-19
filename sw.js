const CACHE='gyosei2026-v11';
const CORE=['./manifest.webmanifest','./icon.svg'];

self.addEventListener('install',event=>{
  event.waitUntil(
    caches.open(CACHE)
      .then(cache=>cache.addAll(CORE))
      .then(()=>self.skipWaiting())
  );
});

self.addEventListener('activate',event=>{
  event.waitUntil(
    caches.keys()
      .then(keys=>Promise.all(keys.filter(k=>k!==CACHE).map(k=>caches.delete(k))))
      .then(()=>self.clients.claim())
  );
});

self.addEventListener('fetch',event=>{
  if(event.request.method!=='GET') return;
  const req=event.request;
  const url=new URL(req.url);

  // Always prefer the network for page navigations and index.html so app updates appear immediately.
  if(req.mode==='navigate' || url.pathname.endsWith('/index.html') || url.pathname.endsWith('/')){
    event.respondWith(
      fetch(req,{cache:'no-store'})
        .then(resp=>{
          const copy=resp.clone();
          caches.open(CACHE).then(cache=>cache.put('./index.html',copy)).catch(()=>{});
          return resp;
        })
        .catch(()=>caches.match('./index.html'))
    );
    return;
  }

  // Static assets: serve cached copy quickly, but refresh it in the background.
  event.respondWith(
    caches.match(req).then(cached=>{
      const network=fetch(req,{cache:'no-store'}).then(resp=>{
        if(resp && resp.ok){
          const copy=resp.clone();
          caches.open(CACHE).then(cache=>cache.put(req,copy)).catch(()=>{});
        }
        return resp;
      });
      return cached || network;
    }).catch(()=>fetch(req))
  );
});
