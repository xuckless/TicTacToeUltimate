// Serves the static web build and proxies the game API to the game server, so the site and the API share one origin
// (no CORS, SignalR WebSockets included, origin IP stays behind Cloudflare). ORIGIN is a wrangler var; ORIGIN_SECRET a wrangler secret the origin's Caddy requires.
const API = [/^\/api\//, /^\/hubs\//, /^\/bot\//, /^\/health$/];

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (API.some(r => r.test(url.pathname))) {
      const target = new URL(url.pathname + url.search, env.ORIGIN);
      const headers = new Headers(request.headers);
      headers.set('X-Forwarded-Host', url.host);
      headers.set('X-Forwarded-Proto', 'https');
      headers.set('X-Origin-Auth', env.ORIGIN_SECRET);
      const init = { method: request.method, headers, redirect: 'manual' };
      if (!['GET', 'HEAD'].includes(request.method)) init.body = request.body;
      try {
        return await fetch(target, init);
      } catch {
        return new Response(JSON.stringify({ error: 'server_unreachable' }), { status: 503, headers: { 'Content-Type': 'application/json', 'Retry-After': '3' } });
      }
    }
    return env.ASSETS.fetch(request);
  },
};
