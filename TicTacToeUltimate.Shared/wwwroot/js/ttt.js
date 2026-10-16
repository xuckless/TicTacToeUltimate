// TTT Ultimate shared JS module (WP6, ARCHITECTURE §8.5). ES module, imported via IJSRuntime.
// Storage, vibration, WebAudio-synthesized SFX + heartbeat/music schedulers, share, lifecycle, theme, motion, focus.

function area(a) { try { return a === 'session' ? window.sessionStorage : window.localStorage; } catch { return null; } }
export function storageGet(a, key) { try { return area(a)?.getItem(key) ?? null; } catch { return null; } }
export function storageSet(a, key, value) { try { area(a)?.setItem(key, value); } catch { /* quota / private mode */ } }
export function storageRemove(a, key) { try { area(a)?.removeItem(key); } catch { } }

export function vibrate(pattern) {
  try { return typeof navigator.vibrate === 'function' ? navigator.vibrate(pattern) : false; } catch { return false; }
}

// ---------- Audio ----------
let ctx = null, sfxGain = null, musicGain = null, master = null;
let sfxVol = 0.8, musicVol = 0.5;
let hbBpm = null, hbNext = 0, hbTimer = null;
let musicOn = false, musicTimer = null, musicNext = 0, musicStep = 0;

export function audioInit() { /* context is created lazily on the first user gesture (autoplay policy) */ return true; }

export function audioUnlock() {
  try {
    if (!ctx) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return false;
      ctx = new AC();
      master = ctx.createGain(); master.gain.value = 1; master.connect(ctx.destination);
      sfxGain = ctx.createGain(); sfxGain.gain.value = sfxVol; sfxGain.connect(master);
      musicGain = ctx.createGain(); musicGain.gain.value = musicVol * 0.25; musicGain.connect(master);
    }
    if (ctx.state === 'suspended') ctx.resume();
    if (hbBpm) startHeartbeat();
    if (musicOn) startMusic();
    return true;
  } catch { return false; }
}

// Unlock audio on the first user gesture even if .NET has not asked yet.
if (typeof document !== 'undefined') {
  const u = () => audioUnlock();
  document.addEventListener('pointerdown', u, { once: true, capture: true });
  document.addEventListener('keydown', u, { once: true, capture: true });
}

function tone(t, { type = 'sine', f0 = 440, f1 = null, dur = 0.12, vol = 0.4, attack = 0.005, dest = null }) {
  const o = ctx.createOscillator(), g = ctx.createGain();
  o.type = type; o.frequency.setValueAtTime(f0, t);
  if (f1) o.frequency.exponentialRampToValueAtTime(Math.max(1, f1), t + dur);
  g.gain.setValueAtTime(0.0001, t);
  g.gain.exponentialRampToValueAtTime(vol, t + attack);
  g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  o.connect(g); g.connect(dest || sfxGain); o.start(t); o.stop(t + dur + 0.02);
}

function noise(t, { dur = 0.2, vol = 0.3, from = 4000, to = 300 }) {
  const len = Math.floor(ctx.sampleRate * dur), buf = ctx.createBuffer(1, len, ctx.sampleRate), d = buf.getChannelData(0);
  for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / len);
  const s = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  s.buffer = buf; f.type = 'lowpass'; f.frequency.setValueAtTime(from, t); f.frequency.exponentialRampToValueAtTime(to, t + dur);
  g.gain.setValueAtTime(vol, t); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  s.connect(f); f.connect(g); g.connect(sfxGain); s.start(t); s.stop(t + dur);
}

const cues = {
  Tap: t => tone(t, { type: 'triangle', f0: 900, dur: 0.05, vol: 0.2 }),
  PlaceX: t => { tone(t, { type: 'triangle', f0: 520, f1: 260, dur: 0.14, vol: 0.45 }); tone(t, { f0: 90, f1: 50, dur: 0.12, vol: 0.5 }); },
  PlaceO: t => { tone(t, { type: 'triangle', f0: 400, f1: 200, dur: 0.14, vol: 0.45 }); tone(t, { f0: 80, f1: 45, dur: 0.12, vol: 0.5 }); },
  Vanish: t => noise(t, { dur: 0.22, vol: 0.25, from: 6000, to: 400 }),
  Tick: t => tone(t, { type: 'square', f0: 1400, dur: 0.03, vol: 0.12 }),
  Warning: t => { tone(t, { type: 'square', f0: 880, dur: 0.08, vol: 0.2 }); tone(t + 0.1, { type: 'square', f0: 880, dur: 0.08, vol: 0.2 }); },
  SuddenDeath: t => { tone(t, { type: 'sawtooth', f0: 220, f1: 110, dur: 0.6, vol: 0.4 }); tone(t, { f0: 60, f1: 30, dur: 0.7, vol: 0.7 }); noise(t, { dur: 0.4, vol: 0.3 }); },
  Win: t => [523, 659, 784, 1047].forEach((f, i) => tone(t + i * 0.09, { type: 'triangle', f0: f, dur: 0.22, vol: 0.35 })),
  Loss: t => [392, 330, 262].forEach((f, i) => tone(t + i * 0.14, { type: 'triangle', f0: f, f1: f * 0.97, dur: 0.3, vol: 0.3 })),
  Promotion: t => [523, 659, 784, 1047, 1319].forEach((f, i) => tone(t + i * 0.07, { type: 'square', f0: f, dur: 0.18, vol: 0.2 })),
  QuestDone: t => { tone(t, { type: 'triangle', f0: 988, dur: 0.1, vol: 0.3 }); tone(t + 0.1, { type: 'triangle', f0: 1319, dur: 0.2, vol: 0.3 }); },
  Emote: t => tone(t, { type: 'sine', f0: 660, f1: 990, dur: 0.12, vol: 0.25 }),
  Error: t => tone(t, { type: 'square', f0: 180, dur: 0.18, vol: 0.25 }),
  Shatter: t => { noise(t, { dur: 0.35, vol: 0.45, from: 9000, to: 300 }); tone(t, { f0: 110, f1: 40, dur: 0.25, vol: 0.7 }); },
  Crit: t => { noise(t, { dur: 0.6, vol: 0.55, from: 12000, to: 200 }); tone(t, { type: 'sawtooth', f0: 880, f1: 110, dur: 0.5, vol: 0.35 }); tone(t + 0.12, { f0: 70, f1: 30, dur: 0.5, vol: 0.8 }); },
  ComboBreak: t => { tone(t, { type: 'square', f0: 600, f1: 150, dur: 0.25, vol: 0.25 }); noise(t + 0.05, { dur: 0.15, vol: 0.2, from: 3000, to: 500 }); },
  RedAlert: t => [0, 0.3, 0.6].forEach(d => tone(t + d, { type: 'sawtooth', f0: 740, f1: 520, dur: 0.24, vol: 0.25 })),
  MatchFound: t => { tone(t, { type: 'triangle', f0: 660, dur: 0.12, vol: 0.3 }); tone(t + 0.12, { type: 'triangle', f0: 990, dur: 0.25, vol: 0.3 }); },
};

export function audioPlay(cue) {
  try { if (!ctx || ctx.state !== 'running') return; const fn = cues[cue]; if (fn) fn(ctx.currentTime + 0.005); } catch { }
}

function beat(t) {
  tone(t, { f0: 70, f1: 45, dur: 0.12, vol: 0.8 });          // lub
  tone(t + 0.16, { f0: 60, f1: 40, dur: 0.1, vol: 0.55 });   // dub
}
function startHeartbeat() {
  if (!ctx || hbTimer) return;
  hbNext = ctx.currentTime + 0.05;
  hbTimer = setInterval(() => {
    if (!hbBpm || !ctx) return;
    while (hbNext < ctx.currentTime + 0.15) { beat(hbNext); hbNext += 60 / hbBpm; }
  }, 25);
}
function stopHeartbeat() { if (hbTimer) { clearInterval(hbTimer); hbTimer = null; } }

export function audioHeartbeat(bpm) {
  hbBpm = bpm ? Math.max(30, Math.min(220, bpm)) : null;
  if (hbBpm) startHeartbeat(); else stopHeartbeat();
}

const melody = [262, 330, 392, 494, 440, 392, 330, 294];
function startMusic() {
  if (!ctx || musicTimer) return;
  musicNext = ctx.currentTime + 0.1;
  musicTimer = setInterval(() => {
    while (musicNext < ctx.currentTime + 0.3) {
      tone(musicNext, { type: 'triangle', f0: melody[musicStep % melody.length], dur: 0.35, vol: 0.5, attack: 0.03, dest: musicGain });
      if (musicStep % 4 === 0) tone(musicNext, { type: 'sine', f0: 131, dur: 0.9, vol: 0.4, attack: 0.05, dest: musicGain });
      musicStep++; musicNext += 0.375;
    }
  }, 50);
}
export function audioMusic(on) { musicOn = !!on; if (musicOn) startMusic(); else if (musicTimer) { clearInterval(musicTimer); musicTimer = null; } }
export function audioVolumes(sfx, music) {
  sfxVol = sfx; musicVol = music;
  if (sfxGain) sfxGain.gain.value = sfx;
  if (musicGain) musicGain.gain.value = music * 0.25;
}

// ---------- Share ----------
export async function share(title, text, url) {
  try { if (navigator.share) { await navigator.share({ title, text, url }); return 'shared'; } } catch (e) { if (e && e.name === 'AbortError') return 'failed'; }
  try { await navigator.clipboard.writeText(url); return 'copied'; } catch { return 'failed'; }
}

// ---------- Lifecycle ----------
export function lifecycleSubscribe(ref) {
  const vis = () => ref.invokeMethodAsync('OnVisibility', document.visibilityState === 'visible');
  const on = () => ref.invokeMethodAsync('OnOnline', true);
  const off = () => ref.invokeMethodAsync('OnOnline', false);
  document.addEventListener('visibilitychange', vis);
  window.addEventListener('online', on); window.addEventListener('offline', off);
  return { visible: document.visibilityState === 'visible', online: navigator.onLine !== false };
}

// ---------- Theme / motion ----------
const themeColors = { dark: '#120a22', light: '#f5f1ff' };
export function setTheme(theme) {
  const t = (theme || 'dark').toLowerCase();
  document.documentElement.setAttribute('data-theme', t);
  storageSet('local', 'ttt.theme', t);
  let meta = document.querySelector('meta[name="theme-color"]');
  if (!meta) { meta = document.createElement('meta'); meta.name = 'theme-color'; document.head.appendChild(meta); }
  const light = t === 'light' || (t === 'system' && window.matchMedia && matchMedia('(prefers-color-scheme: light)').matches);
  meta.content = light ? themeColors.light : themeColors.dark;
}

let motionMq = null, motionMode = 'system';
function applyMotion() {
  const reduce = motionMode === 'reduced' || (motionMode === 'system' && motionMq && motionMq.matches);
  document.documentElement.setAttribute('data-motion', reduce ? 'reduce' : 'full');
}
export function setMotion(mode) {
  motionMode = (mode || 'system').toLowerCase();
  if (!motionMq && window.matchMedia) { motionMq = matchMedia('(prefers-reduced-motion: reduce)'); motionMq.addEventListener?.('change', applyMotion); }
  applyMotion();
  return document.documentElement.getAttribute('data-motion') === 'reduce';
}

// ---------- Focus ----------
export function focusCell(boardEl, index) {
  try { const cells = boardEl.querySelectorAll('[data-cell]'); cells[index]?.focus(); } catch { }
}

// ---------- Diagnostics (crash + bug reports, 2026-10-16) ----------
let diagInstalled = false;
// Taps go to the recent-actions trail by label (data-testid, aria-label or short text; never input values or chat text).
// Uncaught JS errors and promise rejections are reported as crashes.
export function diagInstall(dotnetRef) {
  if (diagInstalled) return;
  diagInstalled = true;
  document.addEventListener('click', e => {
    try {
      const el = e.target && e.target.closest ? e.target.closest('button,a,[role=button],[data-testid],input[type=checkbox]') : null;
      if (!el) return;
      const label = el.getAttribute('data-testid') || el.getAttribute('aria-label')
        || (el.closest('.chat, .msg, textarea, input[type=text]') ? '(chat)' : (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40));
      dotnetRef.invokeMethodAsync('OnTap', (el.tagName || '').toLowerCase() + ':' + label);
    } catch { }
  }, { capture: true, passive: true });
  window.addEventListener('error', e => {
    try {
      const err = e.error;
      dotnetRef.invokeMethodAsync('OnJsError', err && err.name ? err.name : 'Error', String(e.message || err || 'script error'),
        String((err && err.stack) || (e.filename ? e.filename + ':' + e.lineno + ':' + e.colno : '')));
    } catch { }
  });
  window.addEventListener('unhandledrejection', e => {
    try {
      const r = e.reason;
      dotnetRef.invokeMethodAsync('OnJsError', 'UnhandledRejection', String((r && r.message) || r || 'rejected promise'), String((r && r.stack) || ''));
    } catch { }
  });
}

export function diagDescribe() {
  const ua = navigator.userAgent || '';
  const os = /Windows/.test(ua) ? 'Windows' : /iPhone|iPad|iPod/.test(ua) ? 'iOS' : /Android/.test(ua) ? 'Android' : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : 'Unknown';
  return { os, device: null, browser: ua.slice(0, 300), screen: `${window.innerWidth}x${window.innerHeight}@${window.devicePixelRatio || 1}`,
           locale: (navigator.languages && navigator.languages[0]) || navigator.language || null };
}

let h2c = null;
function loadHtml2Canvas() {
  if (window.html2canvas) return Promise.resolve(window.html2canvas);
  if (!h2c) h2c = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = new URL('./vendor/html2canvas.min.js', import.meta.url).href;
    s.onload = () => resolve(window.html2canvas);
    s.onerror = reject;
    document.head.appendChild(s);
  });
  return h2c;
}

// PNG (base64, no prefix) of what the player sees, max 720 px wide; null on failure.
export async function diagScreenshot() {
  try {
    const html2canvas = await loadHtml2Canvas();
    const scale = Math.min(1, 720 / Math.max(1, window.innerWidth));
    const canvas = await html2canvas(document.body, { scale, useCORS: true, logging: false, backgroundColor: getComputedStyle(document.body).backgroundColor,
      width: window.innerWidth, height: window.innerHeight, x: window.scrollX, y: window.scrollY });
    return canvas.toDataURL('image/png').split(',')[1] || null;
  } catch { return null; }
}
