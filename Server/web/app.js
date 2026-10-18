'use strict';

// Thundergeddon phone app: one page for players and the host.
//
// The server decides everything; this page shows the screen that matches the
// latest state snapshot (sent over a WebSocket after every change) and sends
// the player's requests back. See Server/game.go. The game itself runs on
// play.html.
//
// Every screen has the same frame (index.html): top bar, tabs, content, and a
// bottom bar with Back on the left and Next / Start on the right. Screens are
// named as in Docs/ui-flow.drawio (Name Entry, Server Settings, Server Lobby,
// Server Gameplay Control, ...).

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

function session(key, value) {
  try {
    if (value === undefined) return sessionStorage.getItem(key);
    sessionStorage.setItem(key, value);
  } catch { /* not remembered */ }
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

// The players' link (/join, e.g. on a QR code at a festival) goes straight
// to Name Entry: those players never see the title or the menu. The choice
// lasts for this browser tab (play.html sends players back here).
if (location.hash === '#join') {
  session('tg_join_link', '1');
  history.replaceState(null, '', location.pathname + location.search);
}
const JOIN_LINK = session('tg_join_link') === '1';

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
      localView = homeView();
      break;
    case 'rfid_scanned':
      flashTag(m.uid);
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

// Before joining or hosting: title | menu | join.
let localView = JOIN_LINK ? 'join' : 'title';
let hostWanted = true;    // the host sees the console (false after tapping Menu)

// Where a player goes after leaving: Name Entry for the players' link.
function homeView() {
  return JOIN_LINK ? 'join' : 'menu';
}

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
  return localView;
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
    case 'join':
      $('join-back').style.visibility = JOIN_LINK ? 'hidden' : ''; // keeps Join on the right
      $('join-password-field').hidden = !(state && state.passwordRequired);
      break;
    case 'host': renderHost(); break;
    case 'wait': renderWait(); break;
    case 'lobby': renderLobby(); break;
    case 'results': renderResults(); break;
  }
}

function enterScreen(screen) {
  if (screen === 'join') {
    $('join-name').value = store('tg_player_name') || '';
    setTimeout(() => $('join-name').focus(), 50);
  }
  if (screen === 'results') fireworks();
}

function go(view) {
  localView = view;
  render();
}

// ── Building blocks ──────────────────────────────────────────────────────────

// Tabs: which one is open is kept per screen ("scope") for this tab.
const tabChoice = JSON.parse(session('tg_tabs') || '{}');

function currentTab(scope, tabs) {
  const want = tabChoice[scope];
  return tabs.some((t) => t.id === want) ? want : tabs[0].id;
}

function setTab(scope, id) {
  tabChoice[scope] = id;
  session('tg_tabs', JSON.stringify(tabChoice));
  render();
  for (const body of ['host-body', 'lobby-body', 'results-body']) $(body).scrollTop = 0;
}

function renderTabs(el, scope, tabs, current) {
  setHTML(el, tabs.map((t) =>
    `<button class="tab" type="button" role="tab" aria-selected="${t.id === current}"
       data-action="tab" data-scope="${scope}" data-tab="${t.id}">${esc(t.label)}${
       t.count != null ? `<span class="count">${t.count}</span>` : ''}</button>`).join(''));
}

// Segmented sub-tabs inside a page.
function seg(scope, options, current) {
  return `<div class="seg">${options.map(([id, label]) =>
    `<button type="button" aria-pressed="${id === current}" data-action="tab" data-scope="${scope}" data-tab="${id}">${esc(label)}</button>`).join('')}</div>`;
}

function segChoice(scope, options) {
  return currentTab(scope, options.map(([id]) => ({ id })));
}

// An alliance's colour, as a style attribute.
function allianceStyle(i) {
  const a = state && state.alliances && state.alliances[i];
  return a ? `style="color:${esc(a.color)}"` : '';
}

function allianceByName(name) {
  return (state.alliances || []).findIndex((a) => a.name === name);
}

function emptyNote(text) {
  return `<p class="empty">${text}</p>`;
}

// Re-rendering a section replaces its HTML, which would wipe anything the
// host is typing. So a section is left alone while an input in it has the
// focus, and when its HTML hasn't changed.
function setHTML(el, html) {
  if (el._html === html) return;
  const active = document.activeElement;
  const typing = active && (active.tagName === 'SELECT' ||
    (active.tagName === 'INPUT' && !['checkbox', 'radio', 'button', 'color'].includes(active.type)));
  if (typing && el.contains(active)) return;
  el.innerHTML = html;
  el._html = html;
}

// Expandable rows (<details>) stay open across re-renders.
const openRows = new Set();
document.addEventListener('toggle', (e) => {
  const id = e.target.dataset && e.target.dataset.row;
  if (!id) return;
  if (e.target.open) openRows.add(id); else openRows.delete(id);
}, true);

function nav(el, left, mid, right) {
  setHTML(el, `${left || '<span></span>'}<span class="nav-mid">${mid || ''}</span>${right || '<span></span>'}`);
}

// ── Menu, join ───────────────────────────────────────────────────────────────

function renderMenu() {
  const hosted = state && state.hosted;
  const isHost = state && state.me.isHost;
  $('host-label').textContent = isHost ? 'Host Console' : hosted ? 'Take Over Hosting' : 'Host Game';
  $('host-sub').textContent = isHost ? 'Back to running the game'
    : hosted ? 'A game is being hosted: run it from this phone instead' : 'Run a battle on this hub';
}

// Host Game: no host password for now, so this phone becomes the host
// straight away (after a check if another phone is hosting).
function becomeHost() {
  if (state && state.me.isHost) { hostWanted = true; render(); return; }
  if (state && state.hosted && !confirm('Another phone is hosting. Run the game from this phone instead?')) return;
  hostWanted = true;
  setHostPage('');
  send({ t: 'host_login' });
}

$('form-join').addEventListener('submit', (e) => {
  e.preventDefault();
  const name = $('join-name').value.trim();
  if (!name) return;
  store('tg_player_name', name);
  send({ t: 'join', name, password: $('join-password').value });
});

// ── Wait for game / Queue ────────────────────────────────────────────────────

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
  $('wait-title').textContent = me.status === 'queued' ? 'Queue' : 'Wait for Game';
  const pos = me.status === 'queued'
    ? `<div class="queue-pos">#${me.queuePos}</div><div class="detail">in the queue</div>` : '<div class="spinner"></div>';
  setHTML($('wait-body'), `
    <div class="big-status">
      ${pos}
      <div class="title" style="margin-top:14px">${esc(title)}</div>
      <div class="detail">${esc(detail)}</div>
    </div>`);
  $('wait-mid').innerHTML = `Playing as <b>${esc(me.name)}</b>`;
}

// ── Client Lobby ─────────────────────────────────────────────────────────────

// tankCards: the tanks to choose from. A free tank: tap to drive it. A
// 2-player tank whose driver wants a gunner: tap to join as gunner.
function tankCards() {
  const me = state.me;
  const tanks = state.tanks.filter((t) => t.online || t.id === me.tank);
  if (!tanks.length) return emptyNote('No tanks are switched on yet.');
  return '<div class="tank-grid">' + tanks.map((t) => {
    const mine = t.id === me.tank;
    const crew = t.gunner ? `${esc(t.player)} + ${esc(t.gunner)}` : esc(t.player);
    let action = 'pick', tank = t.id, cls = '', sub = 'Free', disabled = false;
    if (!t.online) {
      sub = 'Offline';
    } else if (mine && me.role === 'gunner') {
      action = 'leave-gunner'; cls = 'mine'; sub = `Gunner with ${esc(t.player)}: tap to leave`;
    } else if (mine) {
      tank = ''; cls = 'mine'; sub = t.gunner ? `${crew}: tap to give up` : 'Your tank: tap to give up';
    } else if (t.player && state.twoPlayer && t.allowGunner && !t.gunner) {
      action = 'join-gunner'; cls = 'join'; sub = `${crew} drives: tap to be gunner`;
    } else if (t.player) {
      cls = 'taken'; sub = crew; disabled = true;
    }
    return `<button type="button" class="tank-card ${cls}" data-action="${action}" data-tank="${esc(tank)}" ${disabled ? 'disabled' : ''}>
      <span class="name">${esc(t.name)}</span>
      <span class="sub" ${allianceStyle(t.allianceIndex)}>${esc(t.alliance || 'No alliance')}</span>
      <span class="sub">${sub}</span></button>`;
  }).join('') + '</div>';
}

// myTankCard: your tank and crew, with the 2-player choices.
function myTankCard() {
  const me = state.me;
  if (!me.tank) {
    return `<div class="big-status"><div class="title">Choose your tank</div>
      <div class="detail">${state.twoPlayer ? 'Tap a free tank to drive it, or a dashed one to be its gunner' : 'Each tank&rsquo;s alliance is shown on its card'}</div></div>`;
  }
  const role = me.role === 'gunner' ? `Gunner &middot; ${esc(me.partner)} drives`
    : me.role === 'driver' ? `Driver &middot; ${esc(me.partner)} is your gunner` : 'You drive and shoot';
  let crew = '';
  if (state.twoPlayer && me.role !== 'gunner') {
    crew += `<label class="chk"><input type="checkbox" data-action="allow-gunner" ${me.allowGunner ? 'checked' : ''}> Let a second player be my gunner</label>`;
  }
  if (me.partner) {
    crew += `<div class="btn-row" style="margin-top:6px"><button class="btn small secondary" type="button" data-action="swap-roles">Swap roles</button></div>`;
  }
  return `<div class="card"><div class="timer-row"><div>
      <div style="font-size:1.3rem;font-weight:800">${esc(me.tankName)}</div>
      <div class="sub" style="font-weight:700" ${allianceStyle(me.allianceIndex)}>${esc(me.alliance || 'No alliance')}</div></div>
      <div class="end" style="text-align:right;color:var(--muted);font-size:.9rem">${role}</div></div>
      ${crew ? `<div style="margin-top:8px">${crew}</div>` : ''}</div>`;
}

function playerRows(players, extra) {
  if (!players.length) return emptyNote('Nobody yet.');
  return '<div class="list">' + players.map((p) => {
    let sub = p.tank ? esc(p.tank) : p.status === 'lobby' && state.phase !== 'lobby' && state.phase !== 'idle' ? 'Next game' : 'Choosing&hellip;';
    if (p.role) sub += ` &middot; ${p.role}${p.partner ? ' with ' + esc(p.partner) : ''}`;
    if (p.status === 'playing' && p.life) {
      sub += ' &middot; ' + (p.life === 'dead' ? 'Destroyed' : p.life === 'returning' ? 'Returning to base' : `${p.hp} HP`);
    }
    return `<div class="item"><span class="dot ${p.online ? '' : 'off'}" title="${p.online ? 'Connected' : 'Disconnected'}"></span>
      <div class="main"><div class="name">${esc(p.name)}${p.host ? ' (host)' : ''}</div>
      <div class="sub" ${allianceStyle(allianceByName(p.alliance))}>${sub}</div></div>${extra ? extra(p) : ''}</div>`;
  }).join('') + '</div>';
}

function renderLobby() {
  const me = state.me;
  const tabs = [
    { id: 'tanks', label: 'Robots', count: state.tanks.filter((t) => t.online).length },
    { id: 'players', label: 'Players', count: state.players.length },
  ];
  const tab = currentTab('lobby', tabs);
  renderTabs($('lobby-tabs'), 'lobby', tabs, tab);
  $('lobby-info').textContent = me.name;
  setHTML($('lobby-body'), tab === 'tanks'
    ? `${myTankCard()}${tankCards()}`
    : playerRows(state.players));
  $('lobby-mid').textContent = me.tank ? 'Waiting for the host to start' : 'Choose a tank';
}

// ── Game Over / Stats ────────────────────────────────────────────────────────

function resultsTable() {
  const rows = (state.results || []).map((r) =>
    `<tr><td>${esc(r.name)}${r.role ? ` <span class="chip">${r.role === 'gunner' ? 'G' : 'D'}</span>` : ''}</td>
     <td ${allianceStyle(r.allianceIndex)}>${esc(r.tank)}</td>
     <td class="num">${r.kills}</td><td class="num">${r.deaths}</td>
     <td class="num">${r.damage}</td><td class="num">${r.captures}</td><td class="num"><b>${r.victoryPoints}</b></td></tr>`).join('');
  return `<table class="results"><thead><tr><th>Player</th><th>Tank</th>
    <th class="num">K</th><th class="num">D</th><th class="num">Dmg</th><th class="num">Caps</th><th class="num">Pts</th></tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="empty">No results</td></tr>'}</tbody></table>`;
}

function resultsDetails() {
  const rows = (state.results || []).map((r) =>
    `<tr><td>${esc(r.name)}</td><td class="num">${r.shots}</td><td class="num">${r.driveSeconds}s</td>
     <td class="num">${r.vpFromKills}</td><td class="num">${r.vpFromCaptures}</td><td>${esc(r.nemesis || '—')}</td></tr>`).join('');
  return `<table class="results"><thead><tr><th>Player</th><th class="num">Shots</th><th class="num">Driving</th>
    <th class="num">Kill pts</th><th class="num">Capture pts</th><th>Nemesis</th></tr></thead>
    <tbody>${rows || '<tr><td colspan="6" class="empty">No results</td></tr>'}</tbody></table>`;
}

function scoreCard() {
  const pct = (v) => Math.min(100, Math.round(v / state.pointsToWin * 100));
  const rows = state.alliances.map((a, i) => {
    const pts = state.alliancePoints[i] || 0;
    return `<div class="srow" style="padding:4px 0;border:0"><span style="color:${esc(a.color)}">${esc(a.name)}</span><span class="value">${pts}</span></div>
      <div class="bar"><div style="width:${pct(pts)}%;background:${esc(a.color)}"></div></div>`;
  }).join('');
  return `<div class="card"><h3>Score &middot; ${state.pointsToWin} to win</h3>${rows}</div>`;
}

function resultsBody(scope) {
  const tabs = [{ id: 'scores', label: 'Stats' }, { id: 'details', label: 'Details' }, { id: 'alliances', label: 'Alliances' }];
  const tab = currentTab(scope, tabs);
  const head = `<div class="game-over">GAME OVER</div>
    <div class="big-status" style="margin-bottom:8px"><div class="detail">${esc(state.endReason || '')}</div></div>`;
  const body = tab === 'scores' ? `<div class="card">${resultsTable()}</div>`
    : tab === 'details' ? `<div class="card">${resultsDetails()}</div>` : scoreCard();
  return { tabs, tab, html: head + body };
}

function renderResults() {
  const r = resultsBody('results');
  renderTabs($('results-tabs'), 'results', r.tabs, r.tab);
  setHTML($('results-body'), r.html);
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

// ── Host pages ───────────────────────────────────────────────────────────────
// The phase decides the main page: Server Settings (before the lobby opens),
// Server Lobby, Server Gameplay Control, Game Over. Server Config (the cog)
// holds everything not needed often, one page per kind of setting.

let hostPage = session('tg_host_page') || '';  // '' = the phase's page, else a config page
function setHostPage(p) {
  hostPage = p;
  session('tg_host_page', p);
  render();
  $('host-body').scrollTop = 0;
}

const CONFIG_PAGES = [
  ['config-server', 'Server Settings', 'Game password, players before queuing, debug menu, 2-player tanks, buzzer'],
  ['config-gameplay', 'Gameplay Settings', 'Match length, points, damage, reload, dead time'],
  ['config-advanced', 'Advanced Settings', 'Motor, control and video tuning, shot timing, presets'],
  ['config-alliances', 'Alliances', 'Names, colours, add and remove'],
  ['config-tags', 'Tags and Capture Points', 'RFID tags for bases and capture points'],
];

function renderHost() {
  const ph = state.phase;
  // Outside a game the host is back on the console; "Drive My Tank" is per game.
  if (ph !== 'countdown' && ph !== 'playing') setHostView('console');
  const config = hostPage !== '';
  $('host-config-btn').hidden = config;

  if (config) {
    renderHostConfig();
    return;
  }
  const players = state.players.filter((p) => !p.host).length;
  $('host-title').textContent = { idle: 'Server Settings', lobby: 'Server Lobby', countdown: 'Starting',
    playing: state.paused ? 'Paused' : 'Server Gameplay', ended: 'Game Over' }[ph];
  $('host-info').textContent = ph === 'lobby' ? `${players}/${state.capacity}` : '';

  const tabs = hostTabs(ph);
  const tab = tabs.length ? currentTab('host-' + ph, tabs) : '';
  renderTabs($('host-tabs'), 'host-' + ph, tabs, tab);

  let html = '';
  switch (tab || ph) {
    case 'idle': html = serverSettingsForm() + playersLinkCard(); break;
    case 'battle': html = hostBattleTab(); break;
    case 'tanks': html = hostTanksTab(); break;
    case 'players': html = hostPlayersTab(); break;
    case 'client': html = hostPlayCard(); break;
    case 'queue': html = hostQueueTab(); break;
    case 'results': {
      const r = resultsBody('host-results');
      html = seg('host-results', r.tabs.map((t) => [t.id, t.label]), r.tab) + r.html;
      break;
    }
  }
  setHTML($('host-body'), html);

  // Bottom bar: Menu on the left, the next step on the right.
  const back = '<button class="nav-btn back" type="button" data-action="host-menu">Menu</button>';
  const ready = state.players.filter((p) => p.tank).length;
  let mid = '', right = '';
  switch (ph) {
    case 'idle':
      mid = state.queue.length ? `${state.queue.length} waiting` : '';
      right = '<button class="nav-btn next" type="button" data-action="host-open">Open Lobby</button>';
      break;
    case 'lobby':
      mid = ready ? `${ready} ready` : 'No tanks chosen';
      right = `<button class="nav-btn next" type="button" data-action="host-start" ${ready ? '' : 'disabled'}>Start Game</button>`;
      break;
    case 'countdown':
    case 'playing':
      mid = `<span id="host-timer">${fmtClock(remainingMs())}</span>`;
      right = '<button class="nav-btn next danger" type="button" data-action="host-end">End Game</button>';
      break;
    case 'ended':
      right = '<button class="nav-btn next" type="button" data-action="host-lobby">Server Lobby</button>';
      break;
  }
  nav($('host-nav'), back, mid, right);
}

function hostTabs(ph) {
  const online = state.tanks.filter((t) => t.online).length;
  const players = state.players.filter((p) => !p.host).length;
  const q = state.queue.length;
  switch (ph) {
    case 'idle':
      return [];
    case 'lobby':
      return [{ id: 'tanks', label: 'Robots', count: online }, { id: 'players', label: 'Players', count: players },
        { id: 'client', label: 'Client Lobby' }, { id: 'queue', label: 'Queue', count: q }];
    case 'countdown':
    case 'playing':
      return [{ id: 'battle', label: 'Battle' }, { id: 'tanks', label: 'Robots', count: online },
        { id: 'players', label: 'Players', count: players }, { id: 'queue', label: 'Queue', count: q }];
    default: // ended
      return [{ id: 'results', label: 'Stats' }, { id: 'queue', label: 'Queue', count: q }];
  }
}

function playersLinkCard() {
  const link = `${location.host}/join`;
  return `<div class="card"><h3>Players' link</h3>
    <p class="hint">Give players <b>http://${esc(link)}</b> (for example on a QR code): it goes straight to Name Entry.
    The spectator display is at <b>http://${esc(location.host)}/display.html</b>.</p></div>`;
}

function hostBattleTab() {
  const ph = state.phase;
  const drive = state.me.status === 'playing'
    ? '<button class="btn primary" type="button" data-action="host-drive" style="margin-bottom:10px">Drive My Tank</button>' : '';
  const pause = ph === 'playing'
    ? `<button class="btn small ${state.paused ? 'primary' : 'secondary'}" type="button" data-action="host-pause" data-on="${!state.paused}">${state.paused ? 'Resume' : 'Pause Game'}</button>` : '';
  return `<div class="card"><div class="timer-row">
      <div><h3 style="margin:0">${ph === 'countdown' ? 'Starting in' : 'Time left'}</h3>
      <div class="timer-big" id="host-timer-big">${fmtClock(remainingMs())}</div>
      ${state.paused ? '<div class="paused-tag">PAUSED</div>' : ''}</div>${pause}</div></div>
    ${drive}
    <div class="two-col">${scoreCard()}${pointsCard(true)}</div>`;
}

// pointsCard: the capture points. In a game the host can tap one to change
// its owner (nobody → each alliance in turn → nobody).
function pointsCard(canCycle) {
  const cycle = canCycle && state.phase === 'playing';
  const pts = state.capturePoints.map((p, i) => {
    const a = p.owner >= 0 ? state.alliances[p.owner] : null;
    const style = a ? `style="border-color:${esc(a.color)}"` : '';
    const inner = `<div class="name">${esc(p.name)}</div>
      <div class="sub" ${a ? `style="color:${esc(a.color)}"` : ''}>${esc(a ? a.name : 'Nobody')}</div>`;
    return cycle
      ? `<button type="button" class="point" ${style} data-action="host-capture" data-point="${i}">${inner}</button>`
      : `<div class="point" ${style}>${inner}</div>`;
  }).join('');
  return `<div class="card"><h3>Capture points${cycle ? ' &middot; tap to change' : ''}</h3>
    ${pts ? `<div class="points">${pts}</div>` : emptyNote('No capture points (add them in Server Config).')}</div>`;
}

// One robot: a row that opens into the host's tools for it.
function hostTankRow(t) {
  const inGame = state.phase === 'playing' || state.phase === 'countdown';
  const crew = t.player ? (t.gunner ? `${esc(t.player)} + ${esc(t.gunner)}` : esc(t.player)) : 'free';
  let status = '';
  if (inGame && t.life) {
    status = t.life === 'dead' ? 'Destroyed' : t.life === 'returning' ? 'Returning' : `${t.hp || 0} HP`;
    status += t.invulnerable ? ' &middot; shielded' : '';
  } else if (t.pingMs) {
    status = `${t.pingMs} ms`;
  }
  const f = t.flags || {};
  const chk = (key, label) =>
    `<label class="chk"><input type="checkbox" data-action="tank-flag" data-tank="${esc(t.id)}" data-flag="${key}" ${f[key] ? 'checked' : ''}> ${label}</label>`;
  let tools = '';
  if (!t.online) {
    tools = emptyNote('This robot is offline.');
  } else {
    if (inGame && t.life) {
      const max = state.me.maxHp || 100;
      const pct = Math.max(0, Math.min(100, Math.round((t.hp || 0) / max * 100)));
      tools += `<h4>In this game</h4>
        <div class="hpbar ${pct <= 25 ? 'low' : ''}"><div style="width:${pct}%"></div></div>
        <div class="btn-row" style="margin-top:8px">
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="N">Damage F</button>
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="E">Damage S</button>
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="S">Damage R</button>
          <button class="btn small primary" type="button" data-action="host-heal" data-tank="${esc(t.id)}">${t.life === 'alive' ? 'Heal' : 'Resurrect'}</button>
        </div>
        <p class="hint" style="margin-top:6px">IR sensor hit: ${esc(t.lastHit || 'none yet')}</p>`;
    }
    tools += `<h4>Flip</h4>
      <div class="chk-grid">${chk('invThrottle', 'Drive forward/back')}${chk('invSteer', 'Drive left/right')}${chk('invTurret', 'Turret')}
        ${chk('hflip', 'Video sideways')}${chk('vflip', 'Video upside down')}</div>
      <h4>Test</h4>
      <div class="btn-row">
        <button class="btn small secondary" type="button" data-action="tank-identify" data-tank="${esc(t.id)}">Identify</button>
        <button class="btn small secondary" type="button" data-action="tank-ping" data-tank="${esc(t.id)}">Ping${t.pingMs ? ` (${t.pingMs} ms)` : ''}</button>
        ${inGame && t.player ? '' : `<button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="-0.5">&#9664; Turret</button>
        <button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="0.5">Turret &#9654;</button>`}
        <button class="btn small secondary" type="button" data-action="tank-resync" data-tank="${esc(t.id)}">Reset</button>
        ${state.phase === 'lobby' && t.player ? `<button class="btn small danger" type="button" data-action="tank-free" data-tank="${esc(t.id)}">Remove Player</button>` : ''}
      </div>
      <h4>Name and default alliance</h4>
      <div class="inline-form">
        <input type="text" maxlength="16" value="${esc(t.name)}" id="rename-${esc(t.id)}" aria-label="Robot name">
        <button class="btn small secondary" type="button" data-action="tank-rename" data-tank="${esc(t.id)}">Rename</button>
      </div>
      <div class="inline-form" style="margin-top:6px">${allianceSelect(t)}</div>
      <p class="hint" style="margin-top:6px">${esc(t.ip || '')}${t.ip ? ' &middot; ' : ''}id ${esc(t.id)}</p>`;
  }
  return `<details class="item" data-row="tank-${esc(t.id)}" ${openRows.has('tank-' + t.id) ? 'open' : ''}>
    <summary><span class="dot ${t.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${esc(t.name)}</div>
      <div class="sub"><span ${allianceStyle(t.allianceIndex)}>${esc(t.alliance || 'No alliance')}</span> &middot; ${crew}</div></div>
      <span class="end">${status}</span></summary>
    <div class="tools">${tools}</div></details>`;
}

function allianceSelect(t) {
  const fromName = !t.allianceOverride && t.allianceIndex >= 0 ? ` (${state.alliances[t.allianceIndex].name})` : '';
  const disabled = state.phase === 'playing' || state.phase === 'countdown' ? 'disabled' : '';
  const chosen = t.allianceOverride ? state.alliances[t.allianceIndex].id : '';
  const opt = (v, label) => `<option value="${esc(v)}" ${chosen === v ? 'selected' : ''}>${esc(label)}</option>`;
  return `<select data-action="tank-alliance" data-tank="${esc(t.id)}" aria-label="Alliance" ${disabled}>
    ${opt('', 'From its name' + fromName)}${state.alliances.map((a) => opt(a.id, a.name)).join('')}</select>`;
}

function hostTanksTab() {
  if (!state.tanks.length) return emptyNote('No robots are switched on. They join by themselves when they power up on the Thundergeddon Wi-Fi.');
  return `<div class="list">${state.tanks.map(hostTankRow).join('')}</div>`;
}

function hostPlayersTab() {
  const lobby = state.phase === 'lobby';
  const actions = (p) => {
    if (p.host) return '';
    const btns = [`<button class="btn small secondary" type="button" data-action="host-rename-player" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Rename</button>`];
    if (lobby) btns.push(`<button class="btn small secondary" type="button" data-action="host-queue" data-target="${esc(p.device)}">Queue</button>`);
    btns.push(`<button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Kick</button>`);
    return `<div class="row-actions">${btns.join('')}</div>`;
  };
  return playerRows(state.players, actions);
}

function hostQueueTab() {
  if (!state.queue.length) return emptyNote('Nobody is waiting.');
  return '<div class="list">' + state.queue.map((p, i) =>
    `<div class="item"><span class="dot ${p.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${i + 1}. ${esc(p.name)}</div></div>
      <button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Kick</button></div>`).join('') + '</div>';
}

// Server Client Lobby: the host as a player.
function hostPlayCard() {
  const me = state.me;
  const playing = !!me.status;
  const body = playing
    ? (state.phase === 'lobby' ? `${myTankCard()}${tankCards()}` : `<p class="hint">Playing as <b>${esc(me.name)}</b>.</p>`)
    : `<label class="field">Your player name<input type="text" id="host-play-name" maxlength="16" value="${esc(store('tg_player_name') || '')}"></label>`;
  return `<div class="card">
    <div class="srow" style="padding:0 0 8px;border:0"><span>Server as player</span>
      <button class="toggle" type="button" role="switch" aria-checked="${playing}" data-action="host-play"></button></div>
    ${body}</div>`;
}

// ── Server Config ────────────────────────────────────────────────────────────

function renderHostConfig() {
  const page = CONFIG_PAGES.find(([id]) => id === hostPage);
  $('host-title').textContent = page ? page[1] : 'Server Config';
  $('host-info').textContent = '';
  let html = '';
  let tabs = [];
  switch (hostPage) {
    case 'config-server': html = serverSettingsForm(); break;
    case 'config-gameplay': html = gameplaySettingsForm(); break;
    case 'config-advanced': html = advancedSettings(); break;
    case 'config-alliances': html = alliancesPage(); break;
    case 'config-tags': {
      tabs = [{ id: 'tags', label: 'RFID Tags', count: (state.rfidTags || []).length },
        { id: 'points', label: 'Capture Points', count: state.capturePoints.length }];
      const tab = currentTab('config-tags', tabs);
      renderTabs($('host-tabs'), 'config-tags', tabs, tab);
      html = tab === 'tags' ? rfidTagsTab() : capturePointsTab();
      break;
    }
    default:
      html = `<div class="list">${CONFIG_PAGES.map(([id, title, sub]) =>
        `<button type="button" class="item menu-row" data-action="host-page" data-page="${id}">
          <div class="main"><div class="name">${title}</div><div class="sub">${sub}</div></div><span class="chev">&rsaquo;</span></button>`).join('')}</div>
        <button class="btn danger" type="button" data-action="host-close" style="margin-top:16px">Close Game</button>`;
  }
  if (!tabs.length) renderTabs($('host-tabs'), 'none', [], '');
  setHTML($('host-body'), html);
  const back = hostPage === 'config'
    ? '<button class="nav-btn back" type="button" data-action="host-page" data-page="">Back</button>'
    : '<button class="nav-btn back" type="button" data-action="host-page" data-page="config">Back</button>';
  nav($('host-nav'), back, hostPage.startsWith('config-') && hostPage !== 'config-tags' && hostPage !== 'config-alliances' ? 'Changes save straight away' : '', '');
}

// Settings fields: each saves as soon as it changes.

function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? o : o[k]), obj);
}

function setPath(obj, path, value) {
  const keys = path.split('.');
  let o = obj;
  for (const k of keys.slice(0, -1)) o = o[k];
  o[keys[keys.length - 1]] = value;
}

// field: a labelled input bound to one setting. kind says how the value is
// shown: int, num, min (seconds shown as minutes), ms (ms shown as
// seconds), text.
function field(label, path, kind, attrs = '', wide = false) {
  let v = getPath(state.settings, path);
  if (kind === 'min') v = v / 60;
  if (kind === 'ms') v = v / 1000;
  const type = kind === 'text' ? 'text' : 'number';
  return `<label class="field ${wide ? 'wide' : ''}">${label}<input type="${type}" ${type === 'number' ? 'inputmode="decimal"' : ''}
    data-set="${path}" data-kind="${kind}" value="${esc(v)}" ${attrs}></label>`;
}

function check(label, path) {
  return `<label class="chk"><input type="checkbox" data-set="${path}" data-kind="bool" ${getPath(state.settings, path) ? 'checked' : ''}> ${label}</label>`;
}

function readSetting(el) {
  const kind = el.dataset.kind;
  if (kind === 'bool') return el.checked;
  if (kind === 'text') return el.value.trim();
  const n = parseFloat(el.value) || 0;
  switch (kind) {
    case 'int': return Math.round(n);
    case 'min': return Math.round(n * 60);
    case 'ms': return Math.round(n * 1000);
  }
  return n;
}

document.addEventListener('change', (e) => {
  const el = e.target.closest('[data-set]');
  if (!el || !state || !state.settings) return;
  const s = JSON.parse(JSON.stringify(state.settings));
  setPath(s, el.dataset.set, readSetting(el));
  send({ t: 'host_settings', settings: s });
  toast('Saved');
});

function serverSettingsForm() {
  return `<div class="settings-grid">
    <label class="field wide">Game<select disabled><option>Realtime</option><option>Turn-based (coming later)</option></select></label>
    ${field('Game password for players (blank = none)', 'clientPassword', 'text', 'maxlength="32" autocomplete="off"', true)}
    ${field('Players in lobby before queuing (0 = one per robot)', 'maxPlayers', 'int', 'min="0" max="40"', true)}
    <div class="wide chk-grid">${check('Debug menu on players&rsquo; phones', 'debugConsole')}
      ${check('2-player tanks (driver + gunner)', 'twoPlayerTanks')}${check('Tank buzzers on', 'tuning.buzzer')}</div>
  </div>`;
}

function gameplaySettingsForm() {
  return `<div class="settings-grid">
    ${field('Match duration (minutes)', 'matchSeconds', 'min', 'min="0.5" max="60" step="0.5"')}
    ${field('Start countdown (seconds)', 'countdownSeconds', 'int', 'min="0" max="30"')}
    ${field('Victory points to win', 'pointsToWin', 'int', 'min="1"')}
    ${field('Points per kill', 'pointsPerKill', 'int', 'min="0"')}
    ${field('Capture point scores every (s)', 'captureSeconds', 'num', 'min="0.5" max="600" step="0.5"')}
    ${field('Hit points', 'maxHp', 'int', 'min="1" max="10000"')}
    ${field('Damage per hit', 'damagePerHit', 'int', 'min="0" max="10000"')}
    ${field('Side multiplier', 'sideMultiplier', 'num', 'min="0" max="20" step="0.1"')}
    ${field('Rear multiplier', 'rearMultiplier', 'num', 'min="0" max="20" step="0.1"')}
    ${field('Firing cooldown (seconds)', 'fireCooldownMs', 'ms', 'min="0.25" max="30" step="0.25"')}
    ${field('Dead time (seconds)', 'explosionSeconds', 'num', 'min="0" max="60" step="0.5"')}
    ${field('Shielded after respawn (s)', 'invulnerableSeconds', 'num', 'min="0" max="60" step="0.5"')}
  </div>`;
}

function advancedSettings() {
  const opts = [['motors', 'Motor'], ['controls', 'Control'], ['video', 'Video'], ['shots', 'Shots'], ['presets', 'Presets']];
  const sub = segChoice('advanced', opts);
  let html = '';
  const motor = (who, w) => `<h4>${who}</h4>
    ${field('Acceleration', `tuning.${w}Accel`, 'num', 'min="0" max="100" step="0.1"')}
    ${field('Deceleration', `tuning.${w}Decel`, 'num', 'min="0" max="100" step="0.1"')}
    ${field('Lowest steady speed', `tuning.${w}Min`, 'num', 'min="0" max="1" step="0.05"')}
    ${field('Pulse (ms)', `tuning.${w}PulseMs`, 'int', 'min="10" max="1000"')}
    ${field('Kick (ms)', `tuning.${w}KickMs`, 'int', 'min="0" max="200"')}
    ${field('Backlash (ms)', `tuning.${w}BacklashMs`, 'int', 'min="0" max="300"')}
    <div class="wide">${check('Brake when stopping', `tuning.${w}Brake`)}</div>`;
  switch (sub) {
    case 'motors':
      html = `<p class="hint">Sent to every robot now and whenever one connects. 0 acceleration = instant.</p>
        <div class="settings-grid">${motor('Tracks', 'drive')}${motor('Turret', 'turret')}</div>`;
      break;
    case 'controls':
      html = `<p class="hint">How the joystick and turret slider feel on the players' phones.</p>
        <div class="settings-grid">
        ${field('Dead zone', 'tuning.joyDeadzone', 'num', 'min="0" max="0.5" step="0.01"')}
        ${field('Starting speed', 'tuning.joyStartSpeed', 'num', 'min="0" max="1" step="0.05"')}
        ${field('Full speed at', 'tuning.joyFullAt', 'num', 'min="0.1" max="1" step="0.05"')}
        ${field('Curve', 'tuning.joyCurve', 'num', 'min="0.2" max="5" step="0.1"')}
        ${field('Snap to straight', 'tuning.joyAxisSnap', 'num', 'min="0" max="0.5" step="0.01"')}
        ${field('Turning speed', 'tuning.joyTurnSpeed', 'num', 'min="0.05" max="1" step="0.05"')}
        ${field('Turret slow speed', 'tuning.slowTurretSpeed', 'num', 'min="0" max="1" step="0.05"')}
        <div class="wide">${check('Turret always slow', 'tuning.slowTurretEnabled')}</div></div>`;
      break;
    case 'video':
      html = `<p class="hint">Changing these restarts each robot's camera for a moment. Lower quality number = sharper picture, bigger frames.</p>
        <div class="settings-grid">
        ${field('Frames per second', 'tuning.videoFps', 'int', 'min="1" max="30"')}
        ${field('Frame size (0 smallest)', 'tuning.videoFrameSize', 'int', 'min="0" max="13"')}
        ${field('JPEG quality (4-63)', 'tuning.videoQuality', 'int', 'min="4" max="63"')}</div>`;
      break;
    case 'shots':
      html = `<p class="hint">The IR shot handshake, in milliseconds. Only change these if hits go missing.</p>
        <div class="settings-grid">
        ${field('IR listen window', 'irWindowMs', 'int', 'min="5" max="1000"')}
        ${field('Wait for barrel', 'irAckTimeoutMs', 'int', 'min="50" max="3000"')}
        ${field('Wait for results', 'irWindowTimeoutMs', 'int', 'min="50" max="3000"')}</div>`;
      break;
    case 'presets': {
      const rows = (state.presets || []).map((n) => `<div class="item"><div class="main"><div class="name">${esc(n)}</div></div>
        <div class="row-actions"><button class="btn small secondary" type="button" data-action="preset-load" data-name="${esc(n)}">Load</button>
        <button class="btn small danger" type="button" data-action="preset-delete" data-name="${esc(n)}">Delete</button></div></div>`).join('');
      html = `<p class="hint">A preset saves the Motor, Control and Video settings under a name.</p>
        <div class="inline-form" style="margin-bottom:10px"><input type="text" id="preset-name" maxlength="16" placeholder="Preset name" aria-label="Preset name">
          <button class="btn small primary" type="button" data-action="preset-save">Save</button></div>
        <div class="list">${rows || emptyNote('No presets saved yet.')}</div>
        <button class="btn secondary" type="button" data-action="tuning-reset" style="margin-top:14px">Reset to defaults</button>`;
      break;
    }
  }
  return seg('advanced', opts, sub) + html;
}

// tagUseLabel: what a tag is for, in words.
function tagUseLabel(use) {
  const [kind, id] = (use || '').split(':');
  if (kind === 'base') {
    const a = state.alliances.find((x) => x.id === id);
    return a ? `${a.name} base` : '';
  }
  if (kind === 'point') {
    const p = state.capturePoints.find((x) => x.id === id);
    return p ? `${p.name} capture point` : '';
  }
  return '';
}

function tagUseSelect(t) {
  const opt = (v, label) => `<option value="${esc(v)}" ${t.use === v ? 'selected' : ''}>${esc(label)}</option>`;
  return `<select data-action="tag-use" data-uid="${esc(t.uid)}" aria-label="What tag ${esc(t.uid)} is for">
    ${opt('', 'Not used')}
    <optgroup label="Alliance base">${state.alliances.map((a) => opt('base:' + a.id, a.name + ' base')).join('')}</optgroup>
    <optgroup label="Capture point">${state.capturePoints.map((p) => opt('point:' + p.id, p.name)).join('')}</optgroup>
  </select>`;
}

// flashTag: a robot just read this tag. Its row in the RFID tag list (if
// that list is on screen) flashes and scrolls into view.
function flashTag(uid) {
  const row = document.querySelector(`.tag-row[data-uid="${CSS.escape(uid)}"]`);
  if (!row) return;
  row.classList.remove('flash');
  void row.offsetWidth; // restart the animation for a repeat scan
  // flash pulses; just-read is a steady highlight, for phones set to
  // reduce motion (they get no animations).
  row.classList.add('flash', 'just-read');
  clearTimeout(row._readTimer);
  row._readTimer = setTimeout(() => row.classList.remove('just-read'), 2000);
  row.scrollIntoView({ block: 'nearest' });
}

function rfidTagsTab() {
  const tags = state.rfidTags || [];
  const rows = tags.map((t) => `<div class="item tag-row ${t.use ? '' : 'unused'}" data-uid="${esc(t.uid)}">
      <div class="main"><div class="name mono">${esc(t.uid)}</div>
        <div class="sub">${t.lastSeen ? `Read ${esc(t.lastSeen)} by ${esc(t.tank)}` : 'Not read yet'}</div></div>
      <div class="tag-use">${tagUseSelect(t)}</div>
      <button class="icon-x" type="button" data-action="tag-forget" data-uid="${esc(t.uid)}" aria-label="Forget ${esc(t.uid)}" title="Forget">&times;</button>
    </div>`).join('');
  return `<p class="hint">Drive a robot over a tag: it appears at the top of this list. Then choose what it is for.</p>
    <div class="inline-form" style="margin-bottom:10px"><input type="text" id="tag-add" maxlength="32" placeholder="Add a tag by its number" aria-label="Tag number">
      <button class="btn small secondary" type="button" data-action="tag-add">Add</button></div>
    <div class="list">${rows || emptyNote('No tags yet.')}</div>`;
}

function capturePointsTab() {
  const locked = state.phase === 'playing' || state.phase === 'countdown';
  const rows = state.capturePoints.map((p) => {
    const beacon = (n, label) => `<option value="${n}" ${p.beacon === n ? 'selected' : ''}>${label}</option>`;
    return `<div class="item point-row">
      <div class="main">
        <div class="inline-form"><input type="text" maxlength="24" value="${esc(p.name)}" data-action="point-name" data-id="${esc(p.id)}" aria-label="Capture point name"></div>
        <div class="sub" style="margin-top:4px">${p.tags} ${p.tags === 1 ? 'tag' : 'tags'}${p.beacon ? ` &middot; beacon ${p.beaconOnline ? 'online' : 'offline'}` : ''}</div>
      </div>
      <select data-action="point-beacon" data-id="${esc(p.id)}" aria-label="LED beacon" style="width:auto">
        ${beacon(0, 'No beacon')}${beacon(1, 'North beacon')}${beacon(2, 'Centre beacon')}${beacon(3, 'South beacon')}</select>
      ${locked ? '' : `<button class="icon-x" type="button" data-action="point-remove" data-id="${esc(p.id)}" data-name="${esc(p.name)}" aria-label="Remove ${esc(p.name)}" title="Remove">&times;</button>`}
    </div>`;
  }).join('');
  return `<p class="hint">Assign each point's tags on the RFID Tags tab. A point's LED beacon shows who holds it.</p>
    <div class="list">${rows || emptyNote('No capture points.')}</div>
    ${locked ? '' : '<button class="btn secondary" type="button" data-action="point-add" style="margin-top:10px">Add Capture Point</button>'}`;
}

function alliancesPage() {
  const locked = state.phase === 'playing' || state.phase === 'countdown';
  const cards = state.alliances.map((a, i) => {
    const tanks = state.tanks.filter((t) => t.allianceIndex === i).map((t) => esc(t.name));
    const bases = (state.rfidTags || []).filter((t) => t.use === 'base:' + a.id).length;
    return `<div class="card alliance-card" style="border-left:6px solid ${esc(a.color)}">
      <div class="inline-form">
        <input type="color" value="${esc(a.color)}" data-action="alliance-color" data-id="${esc(a.id)}" aria-label="${esc(a.name)} colour">
        <input type="text" maxlength="24" value="${esc(a.name)}" data-action="alliance-name" data-id="${esc(a.id)}" aria-label="Alliance name">
        ${locked || state.alliances.length <= 1 ? '' : `<button class="icon-x" type="button" data-action="alliance-remove" data-id="${esc(a.id)}" data-name="${esc(a.name)}" aria-label="Remove ${esc(a.name)}" title="Remove">&times;</button>`}
      </div>
      <p class="hint" style="margin:6px 0 0">Robots: ${tanks.join(', ') || 'none'} &middot; Base tags: ${bases}</p>
    </div>`;
  }).join('');
  return `<p class="hint">Robots join the alliance their name starts with (Desert-01 → Desert Squad) unless you choose another on the Robots tab.
      Base tags are set on the RFID Tags page.</p>
    ${cards}
    ${locked ? '' : '<button class="btn secondary" type="button" data-action="alliance-add">Add Alliance</button>'}`;
}

// ── Gameplay ─────────────────────────────────────────────────────────────────
// The game itself runs on /play.html: the Unity-era player page, kept as it
// was so the controls and look are identical. This page sends players there
// when their game starts, and play.html sends them back afterwards.

function remainingMs() {
  if (!state) return 0;
  if (state.paused) return state.remainingMs;
  return Math.max(0, state.remainingMs - (performance.now() - stateAt));
}

function fmtClock(ms) {
  const s = Math.ceil(ms / 1000);
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
}

// The host's clocks tick between snapshots.
setInterval(() => {
  if (shownScreen !== 'host') return;
  for (const id of ['host-timer', 'host-timer-big']) {
    const el = $(id);
    if (el) el.textContent = fmtClock(remainingMs());
  }
}, 250);

// A host who also plays chooses between the console and driving; the choice
// is kept for this tab because play.html is a separate page.
function hostDriving() {
  return session('tg_host_view') === 'drive';
}
function setHostView(v) {
  session('tg_host_view', v);
}

function goPlay() {
  location.replace('/play.html' + location.search);
}

// ── Buttons ──────────────────────────────────────────────────────────────────

document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-action]');
  if (!el || el.tagName === 'SELECT' || (el.tagName === 'INPUT' && el.type !== 'checkbox')) return;
  const a = el.dataset.action;
  const tank = el.dataset.tank;
  switch (a) {
    case 'tab': setTab(el.dataset.scope, el.dataset.tab); break;
    case 'to-title': go('title'); break;
    case 'to-menu': go('menu'); break;
    case 'to-join': go('join'); break;
    case 'to-host-login': becomeHost(); break;
    case 'host-menu': hostWanted = false; setHostView('console'); go('menu'); break;
    case 'host-config': setHostPage('config'); break;
    case 'host-page': setHostPage(el.dataset.page); break;
    case 'leave':
      if (confirm('Leave the game?')) { send({ t: 'leave' }); localView = homeView(); }
      break;
    case 'pick': send({ t: 'pick_tank', tank }); break;
    case 'join-gunner': send({ t: 'join_gunner', tank }); break;
    case 'leave-gunner': send({ t: 'leave_gunner' }); break;
    case 'allow-gunner': send({ t: 'allow_gunner', on: el.checked }); break;
    case 'swap-roles': send({ t: 'swap_roles' }); break;
    case 'play-again': send({ t: 'play_again' }); break;
    case 'host-drive': setHostView('drive'); render(); break;
    case 'host-open': send({ t: 'host_open' }); break;
    case 'host-start': send({ t: 'host_start' }); break;
    case 'host-end': if (confirm('End the game now?')) send({ t: 'host_end' }); break;
    case 'host-lobby': send({ t: 'host_lobby' }); break;
    case 'host-pause': send({ t: 'host_pause', on: el.dataset.on === 'true' }); break;
    case 'host-capture': send({ t: 'host_capture', point: +el.dataset.point }); break;
    case 'host-damage': send({ t: 'host_damage', tank, dir: el.dataset.dir }); break;
    case 'host-heal': send({ t: 'host_heal', tank }); break;
    case 'host-close':
      if (confirm('Close the game? Players go back to the queue.')) {
        send({ t: 'host_close' });
        hostWanted = false;
        setHostPage('');
        localView = 'menu';
      }
      break;
    case 'host-kick':
      if (confirm(`Kick ${el.dataset.name}?`)) send({ t: 'host_kick', target: el.dataset.target });
      break;
    case 'host-rename-player': {
      const name = prompt('New name for ' + el.dataset.name, el.dataset.name);
      if (name && name.trim()) send({ t: 'host_player_rename', target: el.dataset.target, name: name.trim() });
      break;
    }
    case 'host-queue': send({ t: 'host_to_queue', target: el.dataset.target }); break;
    case 'host-play': {
      const on = el.getAttribute('aria-checked') !== 'true';
      const nameInput = $('host-play-name');
      const name = (nameInput && nameInput.value.trim()) || store('tg_player_name') || 'Host';
      if (on) store('tg_player_name', name);
      send({ t: 'host_play', on, name });
      break;
    }
    case 'tank-flag': {
      const t = state.tanks.find((x) => x.id === tank);
      const flags = Object.assign({}, t && t.flags, { [el.dataset.flag]: el.checked });
      send({ t: 'host_tank_flags', tank, flags });
      break;
    }
    case 'tank-identify': send({ t: 'host_tank_identify', tank }); break;
    case 'tank-ping': send({ t: 'host_tank_ping', tank }); break;
    case 'tank-resync': send({ t: 'host_tank_resync', tank }); toast('Settings sent to the robot'); break;
    case 'tank-free': send({ t: 'host_tank_free', tank }); break;
    case 'tank-rename': {
      const input = $('rename-' + tank);
      if (input && input.value.trim()) send({ t: 'host_tank_rename', tank, name: input.value.trim() });
      break;
    }
    case 'preset-save': {
      const input = $('preset-name');
      const name = input && input.value.trim();
      if (!name) { toast('Give the preset a name'); break; }
      send({ t: 'host_preset_save', name });
      input.value = '';
      toast('Preset saved');
      break;
    }
    case 'preset-load':
      if (confirm(`Load "${el.dataset.name}"? It replaces the current Advanced settings.`)) {
        send({ t: 'host_preset_load', name: el.dataset.name });
        toast('Preset loaded');
      }
      break;
    case 'preset-delete':
      if (confirm(`Delete "${el.dataset.name}"?`)) send({ t: 'host_preset_delete', name: el.dataset.name });
      break;
    case 'tuning-reset':
      if (confirm('Put the Motor, Control and Video settings back to their defaults?')) send({ t: 'host_tuning_reset' });
      break;
    case 'alliance-add': send({ t: 'host_alliance_add' }); break;
    case 'alliance-remove':
      if (confirm(`Remove ${el.dataset.name}? Its robots go back to the alliance their name says, and its base tags become unused.`)) {
        send({ t: 'host_alliance_remove', id: el.dataset.id });
      }
      break;
    case 'point-add': send({ t: 'host_point_add' }); break;
    case 'point-remove':
      if (confirm(`Remove the ${el.dataset.name} capture point? Its tags become unused.`)) {
        send({ t: 'host_point_remove', id: el.dataset.id });
      }
      break;
    case 'tag-add': {
      const input = $('tag-add');
      const uid = input && input.value.trim();
      if (uid) { send({ t: 'host_tag_add', uid }); input.value = ''; }
      break;
    }
    case 'tag-forget':
      if (confirm(`Forget tag ${el.dataset.uid}? If a robot reads it again it comes back as a new tag.`)) {
        send({ t: 'host_tag_forget', uid: el.dataset.uid });
      }
      break;
    case 'open-settings': openSheet('sheet-settings'); loadInfo(); break;
    case 'open-howto': openSheet('sheet-howto'); break;
    case 'close-sheet': closeSheets(); break;
  }
});

// Fields and pickers that save when they change.
document.addEventListener('change', (e) => {
  const el = e.target;
  const d = el.dataset || {};
  switch (d.action) {
    case 'tank-alliance': send({ t: 'host_tank_alliance', tank: d.tank, alliance: el.value }); break;
    case 'tag-use': send({ t: 'host_tag_use', uid: d.uid, use: el.value }); toast(el.value ? 'Saved: ' + tagUseLabel(el.value) : 'Saved: not used'); break;
    case 'point-beacon': send({ t: 'host_point_edit', id: d.id, beacon: +el.value }); break;
    case 'point-name': if (el.value.trim()) send({ t: 'host_point_edit', id: d.id, name: el.value.trim(), beacon: -1 }); break;
    case 'alliance-name': if (el.value.trim()) send({ t: 'host_alliance_edit', id: d.id, name: el.value.trim() }); break;
    case 'alliance-color': send({ t: 'host_alliance_edit', id: d.id, color: el.value }); break;
    default: return;
  }
  if (el.tagName === 'SELECT' || el.type === 'text') el.blur();
});

// Hold-to-turn buttons (turret test): turn while pressed, stop on release.
let holding = null;
function stopHold() {
  if (!holding) return;
  send({ t: 'host_tank_turret', tank: holding, s: 0 });
  holding = null;
}
document.addEventListener('pointerdown', (e) => {
  const el = e.target.closest('[data-hold="turret"]');
  if (!el) return;
  e.preventDefault();
  holding = el.dataset.tank;
  send({ t: 'host_tank_turret', tank: holding, s: +el.dataset.s });
});
['pointerup', 'pointercancel', 'blur'].forEach((ev) => window.addEventListener(ev, stopHold));
document.addEventListener('contextmenu', (e) => { if (e.target.closest('[data-hold]')) e.preventDefault(); });

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

// The first screen shows before anything else.
document.querySelectorAll('.screen').forEach((s) => s.classList.toggle('active', s.id === 'screen-' + localView));
shownScreen = localView;
enterScreen(localView);

loadInfo();
connect();
