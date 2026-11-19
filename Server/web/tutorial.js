'use strict';

// Quickstart tutorial for new players (the #tutorial overlay in index.html,
// styled in tutorial.css). Five pages: the two squads, how to win, the
// controls, capture points, repairing at your base. The arrows at the bottom
// go forwards and backwards; on the controls page they first step through
// the parts of the game screen, one pointer at a time.
//
// app.js opens it once after a player first joins (remembered per browser as
// tg_tutorial_seen) and from every "How to play" button: openTutorial().

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
  }

  function close() {
    root.classList.remove('open');
    pages.forEach((el) => el.classList.remove('play'));
    closeTimer = setTimeout(() => { root.hidden = true; }, 260);
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

  window.openTutorial = open;
  window.tutorialSeen = () => {
    try { return localStorage.getItem('tg_tutorial_seen') === '1'; } catch { return false; }
  };
})();
