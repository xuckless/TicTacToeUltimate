// AdSense for the web build. Interstitials and rewarded ads use the Ad Placement API (adBreak, AdSense "H5 games ads");
// the menu banner is a responsive display unit. Nothing loads until init() is called with a publisher id.
window.tttAds = (() => {
  let ready = false, client = null, scriptState = 'none'; // none | loading | loaded | failed
  function init(publisherId, testMode) {
    if (ready || !publisherId) return ready;
    client = publisherId;
    const s = document.createElement('script');
    s.async = true; s.crossOrigin = 'anonymous';
    s.src = 'https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=' + encodeURIComponent(publisherId);
    if (testMode) s.setAttribute('data-adbreak-test', 'on');
    scriptState = 'loading';
    s.onload = () => { scriptState = 'loaded'; };
    s.onerror = () => { scriptState = 'failed'; };
    document.head.appendChild(s);
    window.adsbygoogle = window.adsbygoogle || [];
    window.adBreak = window.adConfig = function (o) { window.adsbygoogle.push(o); };
    window.adConfig({ preloadAdBreaks: 'on', sound: 'on' });
    ready = true;
    return true;
  }
  // Never block the game: if no ad starts within 5 s (no fill, ad blocker, script blocked) resolve as "no ad";
  // once an ad is showing, give it up to 2 minutes.
  function guarded(start, onTimeout) {
    return new Promise(resolve => {
      let done = false, started = false;
      const finish = v => { if (!done) { done = true; resolve(v); } };
      setTimeout(() => { if (!started) finish(onTimeout); }, 5000);
      setTimeout(() => finish(onTimeout), 120000);
      try { start(() => { started = true; }, finish); } catch (e) { finish(onTimeout); }
    });
  }
  // Resolves true when an ad was shown.
  function interstitial() {
    if (!ready) return Promise.resolve(false);
    return guarded((began, finish) => {
      let shown = false;
      window.adBreak({ type: 'next', name: 'between-games',
        beforeAd: () => { shown = true; began(); }, adBreakDone: () => finish(shown) });
    }, false);
  }
  // Resolves 'viewed' | 'dismissed' | 'unavailable'.
  function rewarded(name) {
    if (!ready) return Promise.resolve('unavailable');
    return guarded((began, finish) => {
      let result = 'unavailable';
      window.adBreak({ type: 'reward', name: name || 'reward',
        beforeReward: showAdFn => { began(); showAdFn(); },
        adDismissed: () => { result = 'dismissed'; },
        adViewed: () => { result = 'viewed'; },
        adBreakDone: () => finish(result) });
    }, 'unavailable');
  }
  function banner(el, slot) {
    if (!ready || !el || !slot || el.dataset.filled) return false;
    el.innerHTML = '';
    const ins = document.createElement('ins');
    ins.className = 'adsbygoogle';
    ins.style.display = 'block';
    ins.setAttribute('data-ad-client', client);
    ins.setAttribute('data-ad-slot', slot);
    ins.setAttribute('data-ad-format', 'horizontal');
    ins.setAttribute('data-full-width-responsive', 'true');
    el.appendChild(ins);
    try { (window.adsbygoogle = window.adsbygoogle || []).push({}); el.dataset.filled = '1'; return true; } catch (e) { return false; }
  }
  // Ad blocker check: a no-cors probe of the AdSense script (blockers make it fail at the network level, while a
  // rejected publisher id still "succeeds" as an opaque response), plus a bait element that cosmetic filters hide.
  function blocked() {
    if (!ready) return Promise.resolve(false);
    const probe = fetch('https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js', { mode: 'no-cors', cache: 'no-store' })
      .then(() => false, () => true);
    const bait = new Promise(resolve => {
      const el = document.createElement('div');
      el.className = 'adsbox ad-banner textads banner-ads pub_300x250';
      el.style.cssText = 'position:absolute;left:-9999px;top:-9999px;width:2px;height:2px;';
      el.innerHTML = '&nbsp;';
      document.body.appendChild(el);
      setTimeout(() => { const hidden = el.offsetHeight === 0 || getComputedStyle(el).display === 'none'; el.remove(); resolve(hidden); }, 400);
    });
    const timeout = new Promise(resolve => setTimeout(() => resolve(false), 5000));
    return Promise.race([Promise.all([probe, bait]).then(([a, b]) => a || b), timeout]);
  }
  return { init, interstitial, rewarded, banner, blocked };
})();
