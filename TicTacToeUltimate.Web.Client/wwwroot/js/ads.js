// AdSense for the web build. Interstitials and rewarded ads use the Ad Placement API (adBreak, AdSense "H5 games ads");
// the menu banner is a responsive display unit. Nothing loads until init() is called with a publisher id.
window.tttAds = (() => {
  let ready = false, client = null;
  function init(publisherId, testMode) {
    if (ready || !publisherId) return ready;
    client = publisherId;
    const s = document.createElement('script');
    s.async = true; s.crossOrigin = 'anonymous';
    s.src = 'https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=' + encodeURIComponent(publisherId);
    if (testMode) s.setAttribute('data-adbreak-test', 'on');
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
  return { init, interstitial, rewarded, banner };
})();
