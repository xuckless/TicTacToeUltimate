// alidirect.dev apex: AdSense needs ads.txt on the root domain; everything else goes to the game.
const ADS = 'google.com, pub-7292645320711380, DIRECT, f08c47fec0942fa0\n';
export default {
  fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === '/ads.txt') return new Response(ADS, { headers: { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'public, max-age=3600' } });
    return Response.redirect('https://tictactoeultimate.alidirect.dev' + url.pathname + url.search, 302);
  },
};
