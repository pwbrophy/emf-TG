'use strict';

// Quickstart tutorial for new players (the #tutorial overlay in index.html,
// styled in tutorial.css). Six pages: the two squads, the bases and capture
// points, how to win, the controls, capturing a point, repairing at your
// base. The arrows at the bottom go forwards and backwards; on the controls page they first step through
// the parts of the game screen, one pointer at a time.
//
// app.js opens it once after a player first joins (remembered per browser as
// tg_tutorial_seen) and from every "How to play" button: openTutorial().
//
// The tanks in the pictures light their 6-LED strips as the real ones do
// (ESP32/thundergeddon/src/LedController.h): see the LED part below.

(() => {
  const root = document.getElementById('tutorial');
  if (!root) return;
  const pages = Array.from(root.querySelectorAll('.tut-page'));
  const dots = document.getElementById('tut-dots');
  const prevBtn = root.querySelector('[data-tut="prev"]');
  const nextBtn = root.querySelector('[data-tut="next"]');
  const mock = document.getElementById('tut-mock');
  const pointer = document.getElementById('tut-pointer');
  const CONTROLS = pages.findIndex((p) => p.classList.contains('tut-controls'));

  // The controls page, one step per control. On the mock game screen (its
  // 216 x 100 units; the phone around it reaches 8 units further out):
  // tip = where the arrow points, from = where it starts, outside the phone,
  // so it's plainly not part of the game; hole = the part left lit while the
  // rest of the screen is darkened (a circle [x, y, r] or a rounded box
  // [x, y, w, h, r]).
  const STEPS = [
    { key: 'drive', tip: [19, 82], from: [-22, 123], hole: [40.3, 60.5, 32], title: 'Drive forward / backward' },
    { key: 'turn', tip: [19, 82], from: [-22, 123], hole: [40.3, 60.5, 32], title: 'Turn left / right' },
    { key: 'fire', tip: [186, 54.6], from: [246, 54.6], hole: [172, 54.6, 14.5], title: 'Fire' },
    { key: 'turret', tip: [148.3, 91.5], from: [148.3, 128], hole: [99.7, 68.8, 97.2, 23.7, 11.8], title: 'Turn turret' },
    { key: 'hp', tip: [108, 2.6], from: [108, -30], hole: [30, 0.7, 156, 13, 6.5], title: 'Hit points' },
  ];
  const shade = document.getElementById('tut-shade');
  const shape = document.getElementById('tut-pointer-shape');

  function holePath(h) {
    if (h.length === 3) {
      const [x, y, r] = h;
      return `M${x - r} ${y}a${r} ${r} 0 1 0 ${2 * r} 0a${r} ${r} 0 1 0 ${-2 * r} 0Z`;
    }
    const [x, y, w, hh, r] = h;
    return `M${x + r} ${y}H${x + w - r}a${r} ${r} 0 0 1 ${r} ${r}V${y + hh - r}a${r} ${r} 0 0 1 ${-r} ${r}` +
      `H${x + r}a${r} ${r} 0 0 1 ${-r} ${-r}V${y + r}a${r} ${r} 0 0 1 ${r} ${-r}Z`;
  }

  let page = 0;
  let step = 0;

  dots.innerHTML = pages.map(() => '<span></span>').join('');

  function showStep() {
    const s = STEPS[step];
    mock.setAttribute('data-step', s.key);
    shade.setAttribute('d', 'M-10 -10H226V110H-10Z' + holePath(s.hole));
    const dx = s.tip[0] - s.from[0];
    const dy = s.tip[1] - s.from[1];
    const len = Math.hypot(dx, dy);
    shape.setAttribute('d', `M0 0L-10 -8V-3.4H${-len}V3.4H-10V8Z`);
    pointer.setAttribute('transform', `translate(${s.tip[0]} ${s.tip[1]}) rotate(${Math.atan2(dy, dx) * 180 / Math.PI})`);
    pointer.classList.remove('enter');
    void pointer.getBoundingClientRect();
    pointer.classList.add('enter');
    document.getElementById('tut-step-n').textContent = step + 1;
    document.getElementById('tut-step-title').textContent = s.title;
  }

  function show(p, s) {
    const changed = p !== page || !root.classList.contains('open');
    page = p;
    step = s;
    pages.forEach((el, i) => {
      el.classList.toggle('active', i === page);
      el.classList.toggle('before', i < page);
      el.setAttribute('aria-hidden', i === page ? 'false' : 'true');
      if (i !== page) el.classList.remove('play');
    });
    if (changed) {
      // Start this page's animations from the beginning.
      const el = pages[page];
      el.classList.remove('play');
      void el.getBoundingClientRect();
      el.classList.add('play');
      el.playStart = performance.now();
    }
    if (page === CONTROLS) showStep();
    Array.from(dots.children).forEach((d, i) => d.classList.toggle('on', i === page));
    prevBtn.disabled = page === 0;
    const last = page === pages.length - 1;
    nextBtn.classList.toggle('last', last);
    nextBtn.setAttribute('aria-label', last ? 'Done' : 'Next');
  }

  function next() {
    if (page === CONTROLS && step < STEPS.length - 1) show(page, step + 1);
    else if (page < pages.length - 1) show(page + 1, 0);
    else close();
  }

  function prev() {
    if (page === CONTROLS && step > 0) show(page, step - 1);
    else if (page > 0) show(page - 1, page - 1 === CONTROLS ? STEPS.length - 1 : 0);
  }

  let closeTimer = 0;
  function open() {
    clearTimeout(closeTimer);
    try { localStorage.setItem('tg_tutorial_seen', '1'); } catch { /* private mode */ }
    root.hidden = false;
    root.classList.remove('open');
    show(0, 0);
    void root.getBoundingClientRect();
    root.classList.add('open');
    nextBtn.focus({ preventScroll: true });
    startLeds();
  }

  function close() {
    root.classList.remove('open');
    pages.forEach((el) => el.classList.remove('play'));
    closeTimer = setTimeout(() => { root.hidden = true; ledsRunning = false; }, 260);
  }

  root.addEventListener('click', (e) => {
    const el = e.target.closest('[data-tut]');
    if (!el) return;
    if (el.dataset.tut === 'next') next();
    else if (el.dataset.tut === 'prev') prev();
    else close();
  });

  document.addEventListener('keydown', (e) => {
    if (root.hidden) return;
    if (e.key === 'ArrowRight') { next(); e.preventDefault(); }
    else if (e.key === 'ArrowLeft') { prev(); e.preventDefault(); }
    else if (e.key === 'Escape') close();
  });

  // Swipe sideways to turn the page too.
  let swipe = null;
  root.addEventListener('pointerdown', (e) => {
    if (e.target.closest('button')) return;
    swipe = { x: e.clientX, y: e.clientY, id: e.pointerId };
  });
  root.addEventListener('pointerup', (e) => {
    if (!swipe || swipe.id !== e.pointerId) return;
    const dx = e.clientX - swipe.x;
    const dy = e.clientY - swipe.y;
    swipe = null;
    if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) (dx < 0 ? next : prev)();
  });
  root.addEventListener('pointercancel', () => { swipe = null; });

  // ── The tanks' LEDs ──────────────────────────────────────────────────────
  // Shown as a bar above each tank, coloured and flashing as the tank's
  // strip of 6 LEDs does (LedController.h). The bar is six parts with LED 5
  // (the front, where the HP bar is lit from) on the left, like the phone's
  // HP bar, whichever way the tank faces. Places are in the tank picture's
  // own pixels (480 wide).
  const BAR = { desert: { x: 84 }, jungle: { x: 118 } }; // centred over the hull
  const BAR_W = 260;
  const BAR_H = 34;
  const BAR_Y = -52;
  const OFF = '#0a0a10';
  const BLUE = '#3d7dff';     // the HP bar (dim blue on a real tank)
  const WHITE = '#ffffff';
  const DIM_WHITE = '#b9bdc9';
  const RED = '#ff2a2a';
  const GREEN = '#2ee05a';
  const GOLD = '#ffa500';
  const SVGNS = 'http://www.w3.org/2000/svg';

  function svgEl(tag, attrs) {
    const el = document.createElementNS(SVGNS, tag);
    Object.keys(attrs).forEach((k) => el.setAttribute(k, attrs[k]));
    return el;
  }

  const strips = Array.from(root.querySelectorAll('.tut-leds')).map((g, n) => {
    const bar = BAR[g.dataset.kind];
    const id = 'tut-bar-' + n;
    const clip = svgEl('clipPath', { id });
    clip.appendChild(svgEl('rect', { x: bar.x, y: BAR_Y, width: BAR_W, height: BAR_H, rx: BAR_H / 2 }));
    g.appendChild(clip);
    const parts = svgEl('g', { 'clip-path': `url(#${id})` });
    g.appendChild(parts);
    const w = BAR_W / 6;
    const leds = [0, 1, 2, 3, 4, 5].map((i) => {
      const slot = 5 - i;
      // a hair wider than its share, so no seams show between the parts
      const core = svgEl('rect', { x: bar.x + slot * w - 0.5, y: BAR_Y, width: w + 1, height: BAR_H, fill: OFF, 'fill-opacity': 0.8 });
      parts.appendChild(core);
      return { core, colour: '' };
    });
    g.appendChild(svgEl('rect', { x: bar.x, y: BAR_Y, width: BAR_W, height: BAR_H, rx: BAR_H / 2,
      fill: 'none', stroke: '#fff', 'stroke-opacity': 0.9, 'stroke-width': 3 }));
    return { page: g.closest('.tut-page'), mode: g.dataset.led, leds };
  });

  function paint(strip, colours) {
    strip.leds.forEach((led, i) => {
      const c = colours[i] || '';
      if (c === led.colour) return;
      led.colour = c;
      led.core.setAttribute('fill', c || OFF);
      led.core.setAttribute('fill-opacity', c ? 1 : 0.8);
    });
  }

  // The firmware's patterns: each returns six colours ('' = off).
  const all = (c) => [c, c, c, c, c, c];
  // HP bar: lit from the front; down to one LED, the front one pulses (0.5 s).
  function hpBar(lit, t) {
    if (lit <= 1) return ['', '', '', '', '', Math.floor(t / 500) % 2 ? '' : BLUE];
    return [0, 1, 2, 3, 4, 5].map((i) => (i >= 6 - lit ? BLUE : ''));
  }
  // Base repair: green fills back to front (750 ms), then white flashes
  // (90 ms each), 1200 ms in all.
  function heal(dt) {
    if (dt < 750) {
      const n = Math.floor(dt * 6 / 750);
      return [0, 1, 2, 3, 4, 5].map((i) => (i < n ? GREEN : ''));
    }
    return (dt - 750) % 90 < 45 ? all(WHITE) : all('');
  }
  // Can't be hurt (5 s after a repair): dim white and the HP bar, 4 times a second.
  function invulnerable(dt, lit) {
    return Math.floor(dt / 125) % 2 ? hpBar(lit, dt) : all(DIM_WHITE);
  }
  // Capture: gold pairs from the ends in, 150 ms each, then all gold to 650 ms.
  function capture(dt) {
    if (dt >= 450) return all(GOLD);
    const pairs = Math.floor(dt / 150) + 1;
    return [0, 1, 2, 3, 4, 5].map((i) => (i < pairs || i >= 6 - pairs ? GOLD : ''));
  }

  // What each tank shows over its picture's loop (t = ms into the loop; the
  // times match the CSS keyframes in tutorial.css).
  const SCRIPTS = {
    // How to win: hit (red, 300 ms) when the explosion shows (58% of 2.6 s).
    hit: { loop: 2600, mover: '.t2-tank', at: (t) => (t < 1508 ? hpBar(4, t) : t < 1808 ? all(RED) : hpBar(3, t)) },
    // Capturing: full HP; captures as the back of the tank (the RFID reader)
    // passes over the point's tag (48.3% of 9 s).
    capture: { loop: 9000, mover: '.t4-tank', at: (t) => (t >= 4350 && t < 5000 ? capture(t - 4350) : hpBar(6, t)) },
    // Repair: one LED left (pulsing) until the reader at the back passes the
    // base's tag (38.2% of 9 s), then the repair and 5 s of flicker.
    // Its smoke stops when the repair starts (see frame()).
    repair: { loop: 9000, mover: '.t5-tank', smoke: '.t5-smoke', repairAt: 3440, at: (t) => {
      const dt = t - 3440;
      if (dt < 0) return hpBar(1, t);
      if (dt < 1200) return heal(dt);
      return dt < 6200 ? invulnerable(dt - 1200, 6) : hpBar(6, t);
    } },
  };

  // Each page's loop time comes from its own CSS animation, so the lights
  // stay in step with the pictures.
  function loopTime(strip, script, now) {
    if (!script.loop) return now;
    const mover = strip.page.querySelector(script.mover);
    const a = mover && mover.getAnimations ? mover.getAnimations()[0] : null;
    const t = a && a.currentTime != null ? a.currentTime : now - (strip.page.playStart || 0);
    return ((t % script.loop) + script.loop) % script.loop;
  }

  let ledsRunning = false;
  function frame(now) {
    if (!ledsRunning) return;
    strips.forEach((strip) => {
      if (!strip.page.classList.contains('active')) return;
      const script = SCRIPTS[strip.mode];
      if (!script) return;
      const t = loopTime(strip, script, now);
      paint(strip, script.at(t));
      if (script.smoke) {
        // Smoke while damaged; gone within 0.3 s of the repair starting.
        const smoke = strip.page.querySelector(script.smoke);
        const o = t < script.repairAt ? 1 : Math.max(0, 1 - (t - script.repairAt) / 300);
        if (smoke && smoke.getAttribute('opacity') !== String(o)) smoke.setAttribute('opacity', o);
      }
    });
    requestAnimationFrame(frame);
  }
  function startLeds() {
    if (ledsRunning || !strips.length) return;
    ledsRunning = true;
    requestAnimationFrame(frame);
  }

  window.openTutorial = open;
  window.tutorialSeen = () => {
    try { return localStorage.getItem('tg_tutorial_seen') === '1'; } catch { return false; }
  };
})();
