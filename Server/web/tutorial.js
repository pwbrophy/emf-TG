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

  // The controls page: what each pointer shows. x, y = where the arrow's tip
  // sits on the mock game screen (its 216 × 100 units, see index.html); a =
  // the way it points, in degrees (0 = right, 90 = down).
  const STEPS = [
    { key: 'drive', x: 63, y: 38, a: 135, title: 'Drive forward / backward' },
    { key: 'turn', x: 63, y: 38, a: 135, title: 'Turn left / right' },
    { key: 'fire', x: 158, y: 54.6, a: 0, title: 'Fire' },
    { key: 'turret', x: 124, y: 69.5, a: 60, title: 'Turn turret' },
    { key: 'hp', x: 95, y: 12.5, a: -90, title: 'Hit points' },
  ];

  let page = 0;
  let step = 0;

  dots.innerHTML = pages.map(() => '<span></span>').join('');

  function showStep(jump) {
    const s = STEPS[step];
    mock.setAttribute('data-step', s.key);
    pointer.classList.toggle('jump', !!jump);
    pointer.style.transform = `translate(${s.x}px, ${s.y}px) rotate(${s.a}deg)`;
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
    if (page === CONTROLS) showStep(changed);
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
