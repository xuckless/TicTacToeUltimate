// alidirect.dev apex: AdSense needs ads.txt on the root domain; /go/<slug> are tracked campaign links; everything else goes
// to the game.
const ADS = 'google.com, pub-7292645320711380, DIRECT, f08c47fec0942fa0\n';
const GAME = 'https://tictactoeultimate.alidirect.dev';

export default {
  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === '/ads.txt') return new Response(ADS, { headers: { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'public, max-age=3600' } });

    // Campaign short link (admin site → Campaigns): the server counts the click and picks the App Store, Play or web
    // destination for this visitor's device. Unknown or archived links still land on the game.
    const go = url.pathname.match(/^\/go\/([a-z0-9-]{2,40})\/?$/);
    if (go) {
      try {
        const q = new URLSearchParams({ ua: request.headers.get('User-Agent') || '', country: request.cf?.country || '' });
        const res = await fetch(`${GAME}/api/c/${go[1]}?${q}`, { headers: { Accept: 'application/json' } });
        if (res.ok) {
          const { url: target } = await res.json();
          if (typeof target === 'string' && /^https:\/\//.test(target)) return Response.redirect(target, 302);
        }
      } catch { /* fall through to the game */ }
      return Response.redirect(GAME + '/', 302);
    }

    return Response.redirect(GAME + url.pathname + url.search, 302);
  },
};
