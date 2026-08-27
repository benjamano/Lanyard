// Intentionally does nothing beyond existing.
//
// Lanyard is Blazor *Server*: every page needs a live SignalR circuit, so there is no useful
// offline mode to cache for - a cached shell would render and then sit there unable to do
// anything. The only thing this worker buys is that the app stays installable as a PWA
// ("Add to home screen" on the kiosks and on mobile), which historically required a registered
// worker with a fetch handler.
//
// Leave the handler empty. Browsers detect a no-op fetch handler and skip the worker entirely
// for navigation requests, so this costs nothing; adding a respondWith() here would put a
// caching layer in front of a connection that cannot work offline anyway.
self.addEventListener('fetch', () => { });
