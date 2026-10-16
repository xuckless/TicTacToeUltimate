// admin.alidirect.dev: static admin site; /api/admin/* goes to the game server with the origin secret. Cloudflare Access sits in
// front of this hostname, so every request here already carries Cf-Access-Jwt-Assertion (forwarded; the server checks it and
// the email allowlist). Nothing else under /api is reachable from this hostname.
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname.startsWith('/api/')) {
      if (!/^\/api\/admin(\/|$)/.test(url.pathname)) return new Response('Not found', { status: 404 });
      const target = new URL(url.pathname + url.search, env.ORIGIN);
      const headers = new Headers(request.headers);
      headers.set('X-Forwarded-Host', url.host);
      headers.set('X-Forwarded-Proto', 'https');
      headers.set('X-Origin-Auth', env.ORIGIN_SECRET);
      try {
        return await fetch(target, { method: request.method, headers, body: ['GET', 'HEAD'].includes(request.method) ? undefined : request.body, redirect: 'manual' });
      } catch {
        return new Response(JSON.stringify({ error: 'server_unreachable' }), { status: 503, headers: { 'Content-Type': 'application/json', 'Retry-After': '3' } });
      }
    }
    return env.ASSETS.fetch(request);
  },
};
