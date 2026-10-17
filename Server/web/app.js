'use strict';

// Thundergeddon phone app: one page for players and the host.
//
// The server decides everything; this page shows the screen that matches the
// latest state snapshot (sent over a WebSocket after every change) and sends
// the player's requests and control input back. See Server/game.go.

const $ = (id) => document.getElementById(id);

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function store(key, value) {
  try {
    if (value === undefined) return localStorage.getItem(key);
    localStorage.setItem(key, value);
  } catch { /* private mode: nothing is remembered */ }
  return null;
}

// Each phone keeps a random id, so the server recognises it after a reload
// or a dropped connection. ?device=xyz overrides it, to test several
// "phones" in tabs of one browser.
const DEVICE = (() => {
  const fromUrl = new URLSearchParams(location.search).get('device');
  if (fromUrl) return fromUrl;
  let id = store('tg_device');
  if (!id) {
    id = Array.from(crypto.getRandomValues(new Uint8Array(12)), (b) => b.toString(16).padStart(2, '0')).join('');
    store('tg_device', id);
  }
  return id;
})();

// ── Connection ───────────────────────────────────────────────────────────────

let ws = null;
let state = null;      // latest snapshot from the server
let stateAt = 0;       // performance.now() when it arrived (for the clocks)
let retryMs = 500;
let bannerTimer = 0;

function connect() {
  ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws');
  ws.onopen = () => {
    retryMs = 500;
    clearTimeout(bannerTimer);
    $('conn-banner').hidden = true;
    send({ t: 'hello', device: DEVICE });
  };
  ws.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    onMessage(m);
  };
  ws.onclose = () => {
    // Only show the banner if reconnecting takes a moment.
    clearTimeout(bannerTimer);
    bannerTimer = setTimeout(() => { $('conn-banner').hidden = false; }, 1500);
    setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 4000);
  };
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

setInterval(() => send({ t: 'ping' }), 5000);

function onMessage(m) {
  switch (m.t) {
    case 'state':
      state = m;
      stateAt = performance.now();
      render();
      break;
    case 'error':
      toast(m.msg);
      break;
    case 'kicked':
      toast('The host removed you from the game.');
      localView = 'menu';
      break;
    case 'confirm_start': {
      const names = m.names.join(', ');
      if (confirm(`${names} ${m.names.length === 1 ? "hasn't" : "haven't"} chosen a tank and will sit this game out. Start anyway?`)) {
        send({ t: 'host_start', force: true });
      }
      break;
    }
  }
}

// ── Which screen ─────────────────────────────────────────────────────────────

let localView = 'title';  // before joining or hosting: title | menu | host-login | join
let hostWanted = true;    // the host sees the console (false after tapping Menu)

function currentScreen() {
  if (!state) return localView;
  const me = state.me;
  const inGame = state.phase === 'countdown' || state.phase === 'playing';
  if (me.isHost && hostWanted) {
    if (hostDriving() && me.status === 'playing' && inGame) return 'play';
    return 'host';
  }
  switch (me.status) {
    case 'queued': return 'wait';
    case 'lobby': return state.phase === 'lobby' ? 'lobby' : 'wait';
    case 'playing': return 'play';
    case 'finished': return 'results';
  }
  return localView === 'host-login' && me.isHost ? 'menu' : localView;
}

let shownScreen = '';

function render() {
  const screen = currentScreen();
  if (screen === 'play') {
    goPlay(); // the game runs on its own page
    return;
  }
  if (screen !== shownScreen) {
    document.querySelectorAll('.screen').forEach((s) => s.classList.toggle('active', s.id === 'screen-' + screen));
    shownScreen = screen;
    enterScreen(screen);
  }
  switch (screen) {
    case 'menu': renderMenu(); break;
    case 'join': $('join-password-field').hidden = !(state && state.passwordRequired); break;
    case 'host': renderHost(); break;
    case 'wait': renderWait(); break;
    case 'lobby': renderLobby(); break;
    case 'results': renderResults(); break;
  }
}

function enterScreen(screen) {
  if (screen === 'join') {
    $('join-name').value = store('tg_player_name') || '';
  }
  if (screen === 'host-login') {
    $('host-password').value = '';
    setTimeout(() => $('host-password').focus(), 50);
  }
  if (screen === 'results') fireworks();
}

function go(view) {
  localView = view;
  render();
}

// ── Menu, sign-in, join ──────────────────────────────────────────────────────

function renderMenu() {
  const hosted = state && state.hosted;
  const isHost = state && state.me.isHost;
  $('host-label').textContent = isHost ? 'Host Console' : hosted ? 'Resume Hosting' : 'Host Game';
  $('host-sub').textContent = isHost ? 'Back to running the game'
    : hosted ? 'A game is being hosted: sign in to take over' : 'Run a battle on this hub';
}

$('form-host-login').addEventListener('submit', (e) => {
  e.preventDefault();
  hostWanted = true;
  send({ t: 'host_login', password: $('host-password').value });
});

$('form-join').addEventListener('submit', (e) => {
  e.preventDefault();
  const name = $('join-name').value.trim();
  if (!name) return;
  store('tg_player_name', name);
  send({ t: 'join', name, password: $('join-password').value });
});

// ── Waiting ──────────────────────────────────────────────────────────────────

function renderWait() {
  const me = state.me;
  let title, detail;
  if (me.status === 'lobby') {
    title = 'Game in progress';
    detail = "You're in the lobby for the next game.";
  } else if (!state.hosted) {
    title = 'Waiting for a host';
    detail = 'The game starts once someone opens a lobby.';
  } else if (state.phase === 'idle') {
    title = 'The host is setting up';
    detail = 'Hang on: the lobby opens soon.';
  } else if (state.phase === 'lobby') {
    title = 'The lobby is full';
    detail = "You'll be let in when there's room.";
  } else {
    title = 'Game in progress';
    detail = "You're in the queue for the next game.";
  }
  const pos = me.status === 'queued'
    ? `<div class="queue-pos">#${me.queuePos}</div><div class="detail">in the queue</div>` : '<div class="spinner"></div>';
  $('wait-body').innerHTML = `
    <div class="big-status" style="margin-top:8vh">
      ${pos}
      <div class="title" style="margin-top:18px">${esc(title)}</div>
      <div class="detail">${esc(detail)}</div>
      <div class="detail">Playing as <b>${esc(me.name)}</b></div>
    </div>
    <button class="btn secondary" type="button" data-action="leave">Leave</button>`;
}

// ── Lobby ────────────────────────────────────────────────────────────────────

function teamClass(team) {
  return team.startsWith('Desert') ? 'team-desert' : team.startsWith('Jungle') ? 'team-jungle' : '';
}

function tankCards(myTank) {
  const tanks = state.tanks.filter((t) => t.online || t.id === myTank);
  if (!tanks.length) return '<p class="empty">No tanks are switched on yet.</p>';
  return '<div class="tank-grid">' + tanks.map((t) => {
    const mine = t.id === myTank;
    const taken = t.player && !mine;
    const sub = !t.online ? 'Offline' : mine ? 'Your tank: tap to change' : taken ? esc(t.player) : 'Free';
    return `<button type="button" class="tank-card ${mine ? 'mine' : ''} ${taken ? 'taken' : ''}"
      data-action="pick" data-tank="${esc(mine ? '' : t.id)}" ${taken ? 'disabled' : ''}>
      <span class="name">${esc(t.name)}</span>
      <span class="sub ${teamClass(t.team)}">${esc(t.team || 'No team')}</span>
      <span class="sub">${sub}</span></button>`;
  }).join('') + '</div>';
}

function renderLobby() {
  const me = state.me;
  const status = me.tank
    ? `<div class="title">${esc(me.tankName)}</div><div class="detail ${teamClass(me.team)}">${esc(me.team || '')}</div>
       <div class="detail">Waiting for the host to start</div>`
    : `<div class="title">Choose your tank</div><div class="detail">Each tank&rsquo;s team is shown on its card</div>`;
  const others = state.players.map((p) =>
    `<div class="item"><span class="dot ${p.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${esc(p.name)}${p.host ? ' (host)' : ''}</div>
      <div class="sub">${p.tank ? esc(p.tank) : 'Choosing&hellip;'}</div></div></div>`).join('');
  setHTML($('lobby-body'), `
    <div class="big-status">${status}</div>
    <div class="card"><h3>Tanks</h3>${tankCards(me.tank)}</div>
    <div class="card"><h3>Players</h3><div class="list">${others}</div></div>`);
}

// ── Results ──────────────────────────────────────────────────────────────────

function resultsTable() {
  const rows = (state.results || []).map((r) =>
    `<tr><td>${esc(r.name)}</td><td class="${teamClass(r.team)}">${esc(r.tank)}</td>
     <td class="num">${r.shots}</td><td class="num">${r.driveSeconds.toFixed(0)}s</td></tr>`).join('');
  return `<table class="results"><thead><tr><th>Player</th><th>Tank</th><th class="num">Shots</th><th class="num">Driving</th></tr></thead>
    <tbody>${rows || '<tr><td colspan="4" class="empty">No results</td></tr>'}</tbody></table>`;
}

function renderResults() {
  setHTML($('results-body'), `
    <div class="game-over">GAME OVER</div>
    <div class="big-status"><div class="detail">${esc(state.endReason || '')}</div></div>
    <div class="card">${resultsTable()}</div>
    <div class="form" style="max-width:none">
      <button class="btn primary" type="button" data-action="play-again">Play Again</button>
      <button class="btn secondary" type="button" data-action="leave">Leave</button>
    </div>`);
}

function fireworks() {
  const colours = ['#ff4d1c', '#ff8c00', '#ffd27a', '#ffffff', '#4fbf5a', '#1e90ff'];
  for (let burst = 0; burst < 6; burst++) {
    setTimeout(() => {
      const x = 15 + Math.random() * 70, y = 15 + Math.random() * 40;
      for (let i = 0; i < 18; i++) {
        const s = document.createElement('div');
        s.className = 'spark';
        const a = (i / 18) * Math.PI * 2, d = 60 + Math.random() * 60;
        s.style.left = x + 'vw';
        s.style.top = y + 'vh';
        s.style.background = colours[(burst + i) % colours.length];
        s.style.setProperty('--dx', Math.cos(a) * d + 'px');
        s.style.setProperty('--dy', Math.sin(a) * d + 'px');
        document.body.append(s);
        setTimeout(() => s.remove(), 1200);
      }
    }, burst * 350);
  }
}

// ── Host console ─────────────────────────────────────────────────────────────

function hostSkeleton() {
  if ($('h-main')) return;
  $('host-body').innerHTML = '<div id="h-main"></div><div id="h-play"></div><div id="h-settings"></div><div id="h-close"></div>';
}

function renderHost() {
  hostSkeleton();
  const ph = state.phase;
  // Outside a game the host is back on the console; "Drive My Tank" is per game.
  if (ph !== 'countdown' && ph !== 'playing') setHostView('console');
  $('host-title').textContent = { idle: 'Set up', lobby: 'Lobby', countdown: 'Starting', playing: 'Game on', ended: 'Game over' }[ph];

  let main = '';
  if (ph === 'idle') {
    main = `<div class="card"><h3>Ready?</h3>
      <p class="empty">Check the settings below, then open the lobby so players can choose tanks.
      ${state.queue.length ? `${state.queue.length} waiting.` : ''}</p>
      <button class="btn primary" type="button" data-action="host-open">Open Lobby</button></div>`;
  } else if (ph === 'lobby') {
    const ready = state.players.filter((p) => p.tank).length;
    main = `<div class="card"><h3>Start</h3>
      <button class="btn primary" type="button" data-action="host-start" ${ready ? '' : 'disabled'}>
        Start Game${ready ? ` (${ready} ${ready === 1 ? 'tank' : 'tanks'})` : ''}</button>
      ${ready ? '' : '<p class="empty" style="margin:10px 0 0">Waiting for players to choose tanks.</p>'}</div>
      ${hostPlayersCard()} ${hostTanksCard()} ${hostQueueCard()}`;
  } else if (ph === 'countdown' || ph === 'playing') {
    const drive = state.me.status === 'playing'
      ? '<button class="btn primary" type="button" data-action="host-drive" style="margin-bottom:10px">Drive My Tank</button>' : '';
    main = `<div class="card"><h3>${ph === 'countdown' ? 'Starting in' : 'Time left'}</h3>
      <div class="timer-big" id="host-timer">${fmtClock(remainingMs())}</div></div>
      ${drive}
      <button class="btn danger" type="button" data-action="host-end" style="margin-bottom:14px">End Game</button>
      ${hostPlayersCard()} ${hostQueueCard()}`;
  } else if (ph === 'ended') {
    main = `<div class="game-over">GAME OVER</div>
      <div class="big-status"><div class="detail">${esc(state.endReason || '')}</div></div>
      <div class="card">${resultsTable()}</div>
      <button class="btn primary" type="button" data-action="host-lobby" style="margin-bottom:14px">Back to Lobby</button>
      ${hostQueueCard()}`;
  }
  setHTML($('h-main'), main);
  setHTML($('h-play'), ph === 'lobby' || ph === 'idle' ? hostPlayCard() : '');
  setHTML($('h-settings'), ph === 'idle' || ph === 'lobby' || ph === 'ended' ? hostSettingsCard(ph !== 'idle') : '');
  setHTML($('h-close'), `<button class="btn danger" type="button" data-action="host-close">Close Game</button>`);
}

function hostPlayersCard() {
  const rows = state.players.map((p) => {
    const actions = state.phase === 'lobby' && !p.host
      ? `<div class="row-actions">
           <button class="btn small secondary" type="button" data-action="host-queue" data-target="${esc(p.device)}">Queue</button>
           <button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Remove</button>
         </div>` : '';
    return `<div class="item"><span class="dot ${p.online ? '' : 'off'}" title="${p.online ? 'Connected' : 'Disconnected'}"></span>
      <div class="main"><div class="name">${esc(p.name)}${p.host ? ' (you)' : ''}</div>
      <div class="sub">${p.tank ? esc(p.tank) : p.status === 'lobby' && state.phase !== 'lobby' ? 'Next game' : 'Choosing&hellip;'}</div></div>
      ${actions}</div>`;
  }).join('');
  return `<div class="card"><h3>Players (${state.players.filter((p) => !p.host).length} of ${state.capacity})</h3>
    <div class="list">${rows || '<p class="empty">Nobody yet. Players join from their phones.</p>'}</div></div>`;
}

function hostTanksCard() {
  const rows = state.tanks.map((t) =>
    `<div class="item"><span class="dot ${t.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${esc(t.name)}</div>
      <div class="sub"><span class="${teamClass(t.team)}">${esc(t.team || 'No team')}</span> &middot; ${t.player ? esc(t.player) : 'free'}</div></div></div>`).join('');
  return `<div class="card"><h3>Tanks (${state.tanks.filter((t) => t.online).length} on)</h3>
    <div class="list">${rows || '<p class="empty">No tanks are switched on.</p>'}</div></div>`;
}

function hostQueueCard() {
  if (!state.queue.length) return '';
  const rows = state.queue.map((p, i) =>
    `<div class="item"><span class="dot ${p.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${i + 1}. ${esc(p.name)}</div></div>
      <button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Remove</button></div>`).join('');
  return `<div class="card"><h3>Queue</h3><div class="list">${rows}</div></div>`;
}

function hostPlayCard() {
  const me = state.me;
  const playing = !!me.status;
  const body = playing
    ? `<p class="empty" style="margin:0 0 12px">Playing as <b>${esc(me.name)}</b>.</p>
       ${state.phase === 'lobby' ? tankCards(me.tank) : ''}`
    : `<label class="field">Your player name<input type="text" id="host-play-name" maxlength="16" value="${esc(store('tg_player_name') || '')}"></label>`;
  return `<div class="card"><h3>Play too</h3>
    <div class="srow" style="padding-top:0"><span>Drive a tank yourself</span>
      <button class="toggle" type="button" role="switch" aria-checked="${playing}" data-action="host-play"></button></div>
    ${body}</div>`;
}

function hostSettingsCard(collapsed) {
  const s = state.settings;
  const form = `<form id="form-settings" class="settings-grid" autocomplete="off">
    <label class="field">Game length (minutes)<input type="number" name="matchMinutes" min="0.5" max="60" step="0.5" value="${s.matchSeconds / 60}"></label>
    <label class="field">Countdown (seconds)<input type="number" name="countdownSeconds" min="0" max="30" value="${s.countdownSeconds}"></label>
    <label class="field">Players before queuing (0 = one per tank)<input type="number" name="maxPlayers" min="0" max="40" value="${s.maxPlayers}"></label>
    <label class="field">Reload time (seconds)<input type="number" name="fireCooldown" min="0.25" max="30" step="0.25" value="${s.fireCooldownMs / 1000}"></label>
    <label class="field">Game password (blank = none)<input type="text" name="clientPassword" maxlength="32" value="${esc(s.clientPassword)}"></label>
    <label class="field">Host password<input type="text" name="hostPassword" maxlength="32" value="${esc(s.hostPassword)}"></label>
    <button class="btn secondary" type="submit" style="grid-column:1/-1">Save Settings</button>
  </form>`;
  return collapsed
    ? `<details class="card"><summary><h3 style="display:inline">Game settings</h3></summary><div style="margin-top:12px">${form}</div></details>`
    : `<div class="card"><h3>Game settings</h3>${form}</div>`;
}

function saveSettings(form) {
  const f = new FormData(form);
  const num = (k) => parseFloat(f.get(k)) || 0;
  send({
    t: 'host_settings',
    settings: {
      matchSeconds: Math.round(num('matchMinutes') * 60),
      countdownSeconds: Math.round(num('countdownSeconds')),
      maxPlayers: Math.round(num('maxPlayers')),
      fireCooldownMs: Math.round(num('fireCooldown') * 1000),
      clientPassword: String(f.get('clientPassword') || '').trim(),
      hostPassword: String(f.get('hostPassword') || '').trim(),
    },
  });
  delete $('h-settings').dataset.dirty;
  toast('Settings saved');
}

// Re-rendering a section replaces its HTML, which would wipe anything the
// host is typing. So a section is left alone while it has a focused input or
// unsaved edits (data-dirty), and when its HTML hasn't changed.
function setHTML(el, html) {
  if (el._html === html) return;
  const active = document.activeElement;
  if (el.dataset.dirty || (active && el.contains(active) && active.tagName === 'INPUT')) return;
  el.innerHTML = html;
  el._html = html;
}

document.addEventListener('input', (e) => {
  const section = e.target.closest('#h-settings');
  if (section) section.dataset.dirty = '1';
});

document.addEventListener('submit', (e) => {
  if (e.target.id === 'form-settings') {
    e.preventDefault();
    saveSettings(e.target);
  }
});

// ── Gameplay ─────────────────────────────────────────────────────────────────
// The game itself runs on /play.html: the Unity-era player page, kept as it
// was so the controls and look are identical. This page sends players there
// when their game starts, and play.html sends them back afterwards.

function remainingMs() {
  if (!state) return 0;
  return Math.max(0, state.remainingMs - (performance.now() - stateAt));
}

function fmtClock(ms) {
  const s = Math.ceil(ms / 1000);
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
}

// The host console's timer ticks between snapshots.
setInterval(() => {
  const ht = $('host-timer');
  if (shownScreen === 'host' && ht) ht.textContent = fmtClock(remainingMs());
}, 250);

// A host who also plays chooses between the console and driving; the choice
// is kept for this tab because play.html is a separate page.
function hostDriving() {
  try { return sessionStorage.getItem('tg_host_view') === 'drive'; } catch { return false; }
}
function setHostView(v) {
  try { sessionStorage.setItem('tg_host_view', v); } catch { /* not remembered */ }
}

function goPlay() {
  location.replace('/play.html' + location.search);
}

// ── Buttons ──────────────────────────────────────────────────────────────────

document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-action]');
  if (!el) return;
  const a = el.dataset.action;
  switch (a) {
    case 'to-menu': go('menu'); break;
    case 'to-join': go('join'); break;
    case 'to-host-login':
      if (state && state.me.isHost) { hostWanted = true; render(); break; }
      $('host-login-title').textContent = state && state.hosted ? 'Resume Hosting' : 'Host Game';
      $('host-login-detail').textContent = state && state.hosted
        ? 'Someone is hosting. Signing in moves hosting to this phone.' : 'Enter the host password.';
      go('host-login');
      break;
    case 'host-menu': hostWanted = false; setHostView('console'); go('menu'); break;
    case 'leave':
      if (confirm('Leave the game?')) { send({ t: 'leave' }); localView = 'menu'; }
      break;
    case 'pick': send({ t: 'pick_tank', tank: el.dataset.tank }); break;
    case 'play-again': send({ t: 'play_again' }); break;
    case 'host-drive': setHostView('drive'); render(); break;
    case 'host-open': send({ t: 'host_open' }); break;
    case 'host-start': send({ t: 'host_start' }); break;
    case 'host-end': if (confirm('End the game now?')) send({ t: 'host_end' }); break;
    case 'host-lobby': send({ t: 'host_lobby' }); break;
    case 'host-close':
      if (confirm('Close the game? Players go back to the queue.')) {
        send({ t: 'host_close' });
        hostWanted = false;
        localView = 'menu';
      }
      break;
    case 'host-kick':
      if (confirm(`Remove ${el.dataset.name}?`)) send({ t: 'host_kick', target: el.dataset.target });
      break;
    case 'host-queue': send({ t: 'host_to_queue', target: el.dataset.target }); break;
    case 'host-play': {
      const on = el.getAttribute('aria-checked') !== 'true';
      const nameInput = $('host-play-name');
      const name = (nameInput && nameInput.value.trim()) || store('tg_player_name') || 'Host';
      if (on) store('tg_player_name', name);
      send({ t: 'host_play', on, name });
      break;
    }
    case 'open-settings': openSheet('sheet-settings'); loadInfo(); break;
    case 'open-howto': openSheet('sheet-howto'); break;
    case 'close-sheet': closeSheets(); break;
  }
});

// ── Sheets, toast, info ──────────────────────────────────────────────────────

function openSheet(id) {
  document.body.classList.add('sheet-open');
  $(id).classList.add('open');
}

function closeSheets() {
  document.body.classList.remove('sheet-open');
  document.querySelectorAll('.sheet.open').forEach((s) => s.classList.remove('open'));
}

document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeSheets(); });

document.querySelectorAll('.toggle[data-setting]').forEach((t) => {
  const v = store('tg_setting_' + t.dataset.setting);
  if (v !== null) t.setAttribute('aria-checked', v === '1' ? 'true' : 'false');
  t.addEventListener('click', () => {
    const on = t.getAttribute('aria-checked') !== 'true';
    t.setAttribute('aria-checked', on ? 'true' : 'false');
    store('tg_setting_' + t.dataset.setting, on ? '1' : '0');
  });
});

let toastTimer = 0;
function toast(text) {
  const t = $('toast');
  t.textContent = text;
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 2800);
}

async function loadInfo() {
  try {
    const info = await (await fetch('/api/info', { cache: 'no-store' })).json();
    $('info-version').textContent = info.version;
    $('info-platform').textContent = `${info.os}/${info.arch} (${info.hostname})`;
    $('version-label').textContent = info.version === 'dev' ? 'dev build' : 'v' + info.version;
  } catch { /* offline: leave the placeholders */ }
}

loadInfo();
connect();
