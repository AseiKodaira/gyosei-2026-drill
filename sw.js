const CACHE='gyosei2026-v28';
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
      .then(keys=>Promise.all(keys.filter(k=>k.startsWith('gyosei2026-')&&k!==CACHE).map(k=>caches.delete(k))))
      .then(()=>self.clients.claim())
  );
});

self.addEventListener('fetch',event=>{
  if(event.request.method!=='GET') return;
  const req=event.request;
  const url=new URL(req.url);

  if(url.origin!==self.location.origin) return;

  // Always prefer the network for page navigations and index.html so app updates appear immediately.
  if(req.mode==='navigate' || url.pathname.endsWith('/index.html') || url.pathname.endsWith('/voice-test.html') || url.pathname.endsWith('/sbv2-legal-terms/manifest.json') || url.pathname.endsWith('/')){
    const pageKey=url.pathname.endsWith('/')?new URL('./index.html',req.url).href:url.origin+url.pathname;
    event.respondWith(
      fetch(req,{cache:'no-store'})
        .then(async resp=>{
          if(resp.ok){
            const copy=resp.clone();
            await caches.open(CACHE).then(cache=>cache.put(pageKey,copy)).catch(()=>{});
          }
          return resp;
        })
        .catch(async()=>await caches.match(pageKey)||new Response('オフラインです。通信を確認して再読み込みしてください。',{status:503,headers:{'Content-Type':'text/plain; charset=utf-8'}}))
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
      if(cached){event.waitUntil(network.catch(()=>{}));return cached;}
      return network;
    }).catch(()=>fetch(req))
  );
});
