'use strict';

// Thundergeddon phone app: one page for players and the host.
//
// The server decides everything; this page shows the screen that matches the
// latest state snapshot (sent over a WebSocket after every change) and sends
// the player's requests back. See Server/game.go. The game itself runs on
// play.html.
//
// Every screen has the same frame (index.html): top bar, tabs, content, and a
// bottom bar with Back on the left and Next / Start on the right.

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
    setTimeout(() => $('join-name').focus(), 50);
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
  const body = { host: 'host-body', lobby: 'lobby-body', results: 'results-body' }[scope];
  if (body && $(body)) $(body).scrollTop = 0;
}

function renderTabs(el, scope, tabs, current) {
  setHTML(el, tabs.map((t) =>
    `<button class="tab" type="button" role="tab" aria-selected="${t.id === current}"
       data-action="tab" data-scope="${scope}" data-tab="${t.id}">${esc(t.label)}${
       t.count != null ? `<span class="count">${t.count}</span>` : ''}</button>`).join(''));
}

// Segmented sub-tabs inside a tab.
function seg(scope, options, current) {
  return `<div class="seg">${options.map(([id, label]) =>
    `<button type="button" aria-pressed="${id === current}" data-action="tab" data-scope="${scope}" data-tab="${id}">${esc(label)}</button>`).join('')}</div>`;
}

function segChoice(scope, options) {
  return currentTab(scope, options.map(([id]) => ({ id })));
}

function teamClass(team) {
  return /^desert/i.test(team || '') ? 'team-desert' : /^jungle/i.test(team || '') ? 'team-jungle' : '';
}

// Team colour by index (team names can be renamed).
function teamIdxClass(i) {
  return i === 0 ? 'team-desert' : i === 1 ? 'team-jungle' : '';
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
    (active.tagName === 'INPUT' && !['checkbox', 'radio', 'button'].includes(active.type)));
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
  $('wait-title').textContent = me.status === 'queued' ? 'Queue' : 'Waiting';
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

// ── Lobby ────────────────────────────────────────────────────────────────────

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
      <span class="sub ${teamIdxClass(t.teamIndex)}">${esc(t.team || 'No team')}</span>
      <span class="sub">${sub}</span></button>`;
  }).join('') + '</div>';
}

// myTankCard: your tank and crew, with the 2-player choices.
function myTankCard() {
  const me = state.me;
  if (!me.tank) {
    return `<div class="big-status"><div class="title">Choose your tank</div>
      <div class="detail">${state.twoPlayer ? 'Tap a free tank to drive it, or a dashed one to be its gunner' : 'Each tank&rsquo;s team is shown on its card'}</div></div>`;
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
      <div class="sub ${teamClass(me.team)}" style="font-weight:700">${esc(me.team || 'No team')}</div></div>
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
      <div class="sub ${teamClass(p.team)}">${sub}</div></div>${extra ? extra(p) : ''}</div>`;
  }).join('') + '</div>';
}

function renderLobby() {
  const me = state.me;
  const tabs = [
    { id: 'tanks', label: 'Tanks', count: state.tanks.filter((t) => t.online).length },
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

// ── Results ──────────────────────────────────────────────────────────────────

function resultsTable() {
  const rows = (state.results || []).map((r) =>
    `<tr><td>${esc(r.name)}${r.role ? ` <span class="chip">${r.role === 'gunner' ? 'G' : 'D'}</span>` : ''}</td>
     <td class="${teamClass(r.team)}">${esc(r.tank)}</td>
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
  const [d, j] = state.teamPoints;
  const pct = (v) => Math.min(100, Math.round(v / state.maxTeamPoints * 100));
  const n = state.teamNames;
  return `<div class="card"><h3>Score &middot; ${state.maxTeamPoints} to win</h3>
    <div class="srow" style="padding:0 0 4px;border:0"><span class="team-desert">${esc(n[0])}</span><span class="value">${d}</span></div>
    <div class="bar"><div class="bar-desert" style="width:${pct(d)}%"></div></div>
    <div class="srow" style="padding:4px 0;border:0"><span class="team-jungle">${esc(n[1])}</span><span class="value">${j}</span></div>
    <div class="bar"><div class="bar-jungle" style="width:${pct(j)}%"></div></div></div>`;
}

function resultsBody(scope) {
  const tabs = [{ id: 'scores', label: 'Scores' }, { id: 'details', label: 'Details' }, { id: 'teams', label: 'Teams' }];
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

// ── Host console ─────────────────────────────────────────────────────────────

function hostTabs(ph) {
  const online = state.tanks.filter((t) => t.online).length;
  const players = state.players.filter((p) => !p.host).length;
  const q = state.queue.length;
  switch (ph) {
    case 'idle':
      return [{ id: 'setup', label: 'Set up' }, { id: 'tanks', label: 'Tanks', count: online },
        { id: 'settings', label: 'Settings' }, { id: 'advanced', label: 'Advanced' }, { id: 'tags', label: 'Tags' }];
    case 'lobby':
      return [{ id: 'tanks', label: 'Tanks', count: online }, { id: 'players', label: 'Players', count: players },
        { id: 'teams', label: 'Teams' }, { id: 'queue', label: 'Queue', count: q },
        { id: 'settings', label: 'Settings' }, { id: 'advanced', label: 'Advanced' }, { id: 'tags', label: 'Tags' }];
    case 'countdown':
    case 'playing':
      return [{ id: 'battle', label: 'Battle' }, { id: 'tanks', label: 'Tanks', count: online },
        { id: 'players', label: 'Players', count: players }, { id: 'queue', label: 'Queue', count: q }, { id: 'tags', label: 'Tags' }];
    default: // ended
      return [{ id: 'results', label: 'Results' }, { id: 'queue', label: 'Queue', count: q },
        { id: 'tanks', label: 'Tanks', count: online }, { id: 'settings', label: 'Settings' }, { id: 'advanced', label: 'Advanced' }];
  }
}

function renderHost() {
  const ph = state.phase;
  // Outside a game the host is back on the console; "Drive My Tank" is per game.
  if (ph !== 'countdown' && ph !== 'playing') setHostView('console');
  $('host-title').textContent = { idle: 'Set up', lobby: 'Lobby', countdown: 'Starting', playing: state.paused ? 'Paused' : 'Game on', ended: 'Game over' }[ph];
  const players = state.players.filter((p) => !p.host).length;
  $('host-info').textContent = ph === 'lobby' || ph === 'idle' ? `${players} of ${state.capacity} players` : '';

  const tabs = hostTabs(ph);
  const tab = currentTab('host-' + ph, tabs);
  renderTabs($('host-tabs'), 'host-' + ph, tabs, tab);

  let html = '';
  switch (tab) {
    case 'setup': html = hostSetupTab(); break;
    case 'battle': html = hostBattleTab(); break;
    case 'tanks': html = hostTanksTab(); break;
    case 'players': html = hostPlayersTab(); break;
    case 'teams': html = hostTeamsTab(); break;
    case 'queue': html = hostQueueTab(); break;
    case 'settings': html = hostSettingsTab(); break;
    case 'advanced': html = hostAdvancedTab(); break;
    case 'tags': html = hostTagsTab(); break;
    case 'results': html = resultsBody('host-results').html; break;
  }
  if (tab === 'results') {
    const r = resultsBody('host-results');
    html = seg('host-results', r.tabs.map((t) => [t.id, t.label]), r.tab) + r.html;
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
      mid = ready ? `${ready} ready` : 'Nobody has a tank yet';
      right = `<button class="nav-btn next" type="button" data-action="host-start" ${ready ? '' : 'disabled'}>Start</button>`;
      break;
    case 'countdown':
    case 'playing':
      mid = `<span id="host-timer">${fmtClock(remainingMs())}</span>`;
      right = '<button class="nav-btn next danger" type="button" data-action="host-end">End Game</button>';
      break;
    case 'ended':
      right = '<button class="nav-btn next" type="button" data-action="host-lobby">Next Game</button>';
      break;
  }
  nav($('host-nav'), back, mid, right);
}

function hostSetupTab() {
  return `<div class="card"><h3>Ready?</h3>
      <p class="hint">Check the tanks and settings, then <b>Open Lobby</b> (bottom right) so players can choose tanks.
      ${state.queue.length ? `${state.queue.length} ${state.queue.length === 1 ? 'player is' : 'players are'} waiting.` : ''}</p></div>
    ${hostPlayCard()}
    <div class="card"><h3>Spectator display</h3>
      <p class="hint">Open <b>${esc(location.host)}/display.html</b> on a laptop or TV for the big screen: score, tank cameras and an announcer.</p></div>`;
}

function hostBattleTab() {
  const ph = state.phase;
  const drive = state.me.status === 'playing'
    ? '<button class="btn primary" type="button" data-action="host-drive" style="margin-bottom:10px">Drive My Tank</button>' : '';
  const pause = ph === 'playing'
    ? `<button class="btn small ${state.paused ? 'primary' : 'secondary'}" type="button" data-action="host-pause" data-on="${!state.paused}">${state.paused ? 'Resume' : 'Pause'}</button>` : '';
  return `<div class="card"><div class="timer-row">
      <div><h3 style="margin:0">${ph === 'countdown' ? 'Starting in' : 'Time left'}</h3>
      <div class="timer-big" id="host-timer-big">${fmtClock(remainingMs())}</div>
      ${state.paused ? '<div class="paused-tag">PAUSED</div>' : ''}</div>${pause}</div></div>
    ${drive}
    <div class="two-col">${scoreCard()}${pointsCard(true)}</div>`;
}

// pointsCard: the three capture points. In a game the host can tap one to
// change its owner (nobody → first team → second team → nobody).
function pointsCard(canCycle) {
  const pts = state.capturePoints.map((p, i) => {
    const owner = p.owner >= 0 ? state.teamNames[p.owner] : 'Nobody';
    const inner = `<div class="name">${esc(p.name)}</div>
      <div class="sub ${teamIdxClass(p.owner)}">${esc(owner)}</div>
      <div class="sub">${p.beacon ? 'Beacon on' : 'No beacon'}</div>`;
    return canCycle && state.phase === 'playing'
      ? `<button type="button" class="point owner-${p.owner}" data-action="host-capture" data-point="${i}">${inner}</button>`
      : `<div class="point owner-${p.owner}">${inner}</div>`;
  }).join('');
  return `<div class="card"><h3>Capture points${canCycle && state.phase === 'playing' ? ' &middot; tap to change' : ''}</h3>
    <div class="points">${pts}</div></div>`;
}

// One tank: a row that opens into the host's tools for it.
function hostTankRow(t) {
  const inGame = state.phase === 'playing' || state.phase === 'countdown';
  const crew = t.player ? (t.gunner ? `${esc(t.player)} + ${esc(t.gunner)}` : esc(t.player)) : 'free';
  let status = '';
  if (inGame && t.life) {
    const max = state.me.maxHp || 100;
    status = t.life === 'dead' ? 'Destroyed' : t.life === 'returning' ? 'Returning' : `${t.hp} HP`;
    status += t.invulnerable ? ' &middot; shielded' : '';
  } else if (t.pingMs) {
    status = `${t.pingMs} ms`;
  }
  const f = t.flags || {};
  const chk = (key, label) =>
    `<label class="chk"><input type="checkbox" data-action="tank-flag" data-tank="${esc(t.id)}" data-flag="${key}" ${f[key] ? 'checked' : ''}> ${label}</label>`;
  let tools = '';
  if (!t.online) {
    tools = emptyNote('This tank is offline.');
  } else {
    if (inGame && t.life) {
      const max = state.me.maxHp || 100;
      const pct = Math.max(0, Math.min(100, Math.round((t.hp || 0) / max * 100)));
      tools += `<h4>In this game</h4>
        <div class="hpbar ${pct <= 25 ? 'low' : ''}"><div style="width:${pct}%"></div></div>
        <div class="btn-row" style="margin-top:8px">
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="N">Hit front</button>
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="E">Hit side</button>
          <button class="btn small secondary" type="button" data-action="host-damage" data-tank="${esc(t.id)}" data-dir="S">Hit rear</button>
          <button class="btn small primary" type="button" data-action="host-heal" data-tank="${esc(t.id)}">${t.life === 'alive' ? 'Heal' : 'Revive'}</button>
        </div>
        <p class="hint" style="margin-top:6px">Last hit: ${esc(t.lastHit || 'none yet')}</p>`;
    }
    tools += `<h4>Camera and controls</h4>
      <div class="chk-grid">${chk('hflip', 'Flip video sideways')}${chk('vflip', 'Flip video upside down')}
        ${chk('invThrottle', 'Reverse forward/back')}${chk('invSteer', 'Reverse steering')}${chk('invTurret', 'Reverse turret')}</div>
      <h4>Test</h4>
      <div class="btn-row">
        <button class="btn small secondary" type="button" data-action="tank-identify" data-tank="${esc(t.id)}">Flash lights</button>
        <button class="btn small secondary" type="button" data-action="tank-ping" data-tank="${esc(t.id)}">Ping${t.pingMs ? ` (${t.pingMs} ms)` : ''}</button>
        ${inGame && t.player ? '' : `<button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="-0.5">&#9664; Turret</button>
        <button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="0.5">Turret &#9654;</button>`}
        <button class="btn small secondary" type="button" data-action="tank-resync" data-tank="${esc(t.id)}">Resend tuning</button>
      </div>
      <h4>Name and team</h4>
      <div class="inline-form">
        <input type="text" maxlength="16" value="${esc(t.name)}" id="rename-${esc(t.id)}" aria-label="Tank name">
        <button class="btn small secondary" type="button" data-action="tank-rename" data-tank="${esc(t.id)}">Rename</button>
      </div>
      <div class="inline-form" style="margin-top:6px">${teamSelect(t)}</div>
      <p class="hint" style="margin-top:6px">${esc(t.ip || '')}${t.ip ? ' &middot; ' : ''}id ${esc(t.id)}</p>`;
  }
  return `<details class="item" data-row="tank-${esc(t.id)}" ${openRows.has('tank-' + t.id) ? 'open' : ''}>
    <summary><span class="dot ${t.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${esc(t.name)}</div>
      <div class="sub"><span class="${teamIdxClass(t.teamIndex)}">${esc(t.team || 'No team')}</span> &middot; ${crew}</div></div>
      <span class="end">${status}</span></summary>
    <div class="tools">${tools}</div></details>`;
}

function teamSelect(t, short) {
  const n = state.teamNames;
  const auto = t.teamOverride ? -1 : t.teamIndex;
  const disabled = state.phase === 'playing' || state.phase === 'countdown' ? 'disabled' : '';
  const opt = (v, label) => `<option value="${v}" ${(t.teamOverride ? t.teamIndex : -1) === v ? 'selected' : ''}>${esc(label)}</option>`;
  return `<select data-action="tank-team" data-tank="${esc(t.id)}" aria-label="Team" ${disabled}>
    ${opt(-1, short ? 'Auto' : 'Team from its name' + (auto >= 0 && !t.teamOverride ? ` (${n[auto]})` : ''))}${opt(0, n[0])}${opt(1, n[1])}</select>`;
}

function hostTanksTab() {
  if (!state.tanks.length) return emptyNote('No tanks are switched on. Tanks join by themselves when they power up on the Thundergeddon Wi-Fi.');
  return `<p class="hint">Tap a tank for its tools.</p><div class="list">${state.tanks.map(hostTankRow).join('')}</div>`;
}

function hostPlayersTab() {
  const lobby = state.phase === 'lobby';
  const actions = (p) => {
    if (p.host) return '';
    const btns = [];
    if (lobby && p.tank) btns.push(`<button class="btn small secondary" type="button" data-action="host-release" data-target="${esc(p.device)}">Free tank</button>`);
    if (lobby) btns.push(`<button class="btn small secondary" type="button" data-action="host-queue" data-target="${esc(p.device)}">Queue</button>`);
    btns.push(`<button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Remove</button>`);
    return `<div class="row-actions">${btns.join('')}</div>`;
  };
  return `${playerRows(state.players, actions)}${lobby || state.phase === 'idle' ? `<div style="margin-top:10px">${hostPlayCard()}</div>` : ''}`;
}

function hostTeamsTab() {
  const n = state.teamNames;
  const col = (i) => {
    const tanks = state.tanks.filter((t) => t.teamIndex === i);
    const rows = tanks.map((t) => `<div class="item"><div class="main"><div class="name">${esc(t.name)}</div>
        <div class="sub">${t.player ? esc(t.player) + (t.gunner ? ' + ' + esc(t.gunner) : '') : 'free'}</div></div>
        <div style="width:140px">${teamSelect(t, true)}</div></div>`).join('');
    return `<div class="card"><h3 class="${teamIdxClass(i)}">Team ${i + 1}</h3>
      <div class="inline-form" style="margin-bottom:8px"><input type="text" maxlength="24" value="${esc(n[i])}" data-set="teamNames.${i}" data-kind="text" aria-label="Team ${i + 1} name"></div>
      <div class="list">${rows || emptyNote('No tanks on this team.')}</div></div>`;
  };
  const none = state.tanks.filter((t) => t.teamIndex < 0);
  return `<div class="two-col">${col(0)}${col(1)}</div>
    ${none.length ? `<div class="card"><h3>No team</h3><div class="list">${none.map((t) => `<div class="item"><div class="main"><div class="name">${esc(t.name)}</div></div><div style="width:140px">${teamSelect(t, true)}</div></div>`).join('')}</div>
      <p class="hint" style="margin-top:6px">Tanks named Desert-&hellip; or Jungle-&hellip; join that team. These can't fight until they have one.</p></div>` : ''}`;
}

function hostQueueTab() {
  if (!state.queue.length) return emptyNote('Nobody is waiting.');
  return '<div class="list">' + state.queue.map((p, i) =>
    `<div class="item"><span class="dot ${p.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${i + 1}. ${esc(p.name)}</div></div>
      <button class="btn small danger" type="button" data-action="host-kick" data-target="${esc(p.device)}" data-name="${esc(p.name)}">Remove</button></div>`).join('') + '</div>';
}

function hostPlayCard() {
  const me = state.me;
  const playing = !!me.status;
  const body = playing
    ? (state.phase === 'lobby' ? `${myTankCard()}${tankCards()}` : `<p class="hint">Playing as <b>${esc(me.name)}</b>.</p>`)
    : `<label class="field">Your player name<input type="text" id="host-play-name" maxlength="16" value="${esc(store('tg_player_name') || '')}"></label>`;
  return `<div class="card"><h3>Play too</h3>
    <div class="srow" style="padding:0 0 8px;border:0"><span>Drive a tank yourself</span>
      <button class="toggle" type="button" role="switch" aria-checked="${playing}" data-action="host-play"></button></div>
    ${body}</div>`;
}

// ── Settings: every field saves as soon as it changes ───────────────────────

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
// seconds), text, uids (list shown comma-separated).
function field(label, path, kind, attrs = '', wide = false) {
  let v = getPath(state.settings, path);
  if (kind === 'min') v = v / 60;
  if (kind === 'ms') v = v / 1000;
  if (kind === 'uids') v = (v || []).join(', ');
  const type = kind === 'text' || kind === 'uids' ? 'text' : 'number';
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
  if (kind === 'uids') return el.value.split(/[\s,;]+/).filter(Boolean);
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

function hostSettingsTab() {
  const opts = [['game', 'Game'], ['combat', 'Combat']];
  const sub = segChoice('settings', opts);
  let html = '';
  if (sub === 'game') {
    html = `<div class="settings-grid">
      ${field('Game length (minutes)', 'matchSeconds', 'min', 'min="0.5" max="60" step="0.5"')}
      ${field('Countdown (seconds)', 'countdownSeconds', 'int', 'min="0" max="30"')}
      ${field('Players before queuing (0 = one per tank)', 'maxPlayers', 'int', 'min="0" max="40"')}
      ${field('Points to win', 'maxTeamPoints', 'int', 'min="1"')}
      ${field('Game password (blank = none)', 'clientPassword', 'text', 'maxlength="32"')}
      ${field('Host password', 'hostPassword', 'text', 'maxlength="32"')}
      <h4>Options</h4>
      <div class="wide chk-grid">${check('2-player tanks (driver + gunner)', 'twoPlayerTanks')}${check('Debug log button on phones', 'debugConsole')}</div>
    </div>`;
  } else {
    html = `<div class="settings-grid">
      ${field('Hit points', 'maxHp', 'int', 'min="1" max="10000"')}
      ${field('Damage per hit', 'damagePerHit', 'int', 'min="0" max="10000"')}
      ${field('Rear hit &times;', 'rearMultiplier', 'num', 'min="0" max="20" step="0.1"')}
      ${field('Side hit &times;', 'sideMultiplier', 'num', 'min="0" max="20" step="0.1"')}
      ${field('Reload time (seconds)', 'fireCooldownMs', 'ms', 'min="0.25" max="30" step="0.25"')}
      ${field('Points per kill', 'teamPointsPerKill', 'int', 'min="0"')}
      ${field('Explosion (seconds)', 'explosionSeconds', 'num', 'min="0" max="60" step="0.5"')}
      ${field('Shielded after respawn (s)', 'invulnerableSeconds', 'num', 'min="0" max="60" step="0.5"')}
      ${field('Capture point scores every (s)', 'captureSeconds', 'num', 'min="0.5" max="600" step="0.5"')}
      <h4>Shot timing (milliseconds)</h4>
      ${field('IR listen window', 'irWindowMs', 'int', 'min="5" max="1000"')}
      ${field('Wait for barrel', 'irAckTimeoutMs', 'int', 'min="50" max="3000"')}
      ${field('Wait for results', 'irWindowTimeoutMs', 'int', 'min="50" max="3000"')}
    </div>`;
  }
  return `${seg('settings', opts, sub)}<p class="hint">Changes save straight away.</p>${html}
    <button class="btn danger" type="button" data-action="host-close" style="margin-top:16px">Close Game</button>`;
}

function hostAdvancedTab() {
  const opts = [['motors', 'Motors'], ['controls', 'Controls'], ['video', 'Video'], ['presets', 'Presets']];
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
      html = `<p class="hint">Sent to every tank now and whenever one connects. 0 acceleration = instant.</p>
        <div class="settings-grid">${motor('Tracks', 'drive')}${motor('Turret', 'turret')}
        <h4>Sound</h4><div class="wide">${check('Tank buzzer on', 'tuning.buzzer')}</div></div>`;
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
      html = `<p class="hint">Changing these restarts each tank's camera for a moment. Lower quality number = sharper picture, bigger frames.</p>
        <div class="settings-grid">
        ${field('Frames per second', 'tuning.videoFps', 'int', 'min="1" max="30"')}
        ${field('Frame size (0 smallest)', 'tuning.videoFrameSize', 'int', 'min="0" max="13"')}
        ${field('JPEG quality (4-63)', 'tuning.videoQuality', 'int', 'min="4" max="63"')}</div>`;
      break;
    case 'presets': {
      const rows = (state.presets || []).map((n) => `<div class="item"><div class="main"><div class="name">${esc(n)}</div></div>
        <div class="row-actions"><button class="btn small secondary" type="button" data-action="preset-load" data-name="${esc(n)}">Load</button>
        <button class="btn small danger" type="button" data-action="preset-delete" data-name="${esc(n)}">Delete</button></div></div>`).join('');
      html = `<p class="hint">A preset saves all the Advanced settings (motors, controls, video) under a name.</p>
        <div class="inline-form" style="margin-bottom:10px"><input type="text" id="preset-name" maxlength="16" placeholder="Preset name" aria-label="Preset name">
          <button class="btn small primary" type="button" data-action="preset-save">Save</button></div>
        <div class="list">${rows || emptyNote('No presets saved yet.')}</div>
        <button class="btn secondary" type="button" data-action="tuning-reset" style="margin-top:14px">Reset to defaults</button>`;
      break;
    }
  }
  return seg('advanced', opts, sub) + html;
}

function hostTagsTab() {
  const scans = (state.rfidScans || []).slice().reverse();
  const rows = scans.map((s) =>
    `<div class="item"><div class="main"><div class="name">${esc(s.uid)}</div>
      <div class="sub">${esc(s.at)} &middot; ${esc(s.tank)}${s.what ? ' &middot; ' + esc(s.what) : ''}</div></div></div>`).join('');
  const editable = state.phase !== 'playing' && state.phase !== 'countdown';
  const opts = [['points', 'Points'], ['scans', 'Scans'], ['numbers', 'Tag numbers']];
  const sub = segChoice('tags', opts);
  let html = '';
  if (sub === 'points') html = pointsCard(true);
  if (sub === 'scans') html = `<p class="hint">Drive a tank over a tag to see its number here.</p><div class="list">${rows || emptyNote('No tags read yet.')}</div>`;
  if (sub === 'numbers') {
    html = editable ? `<p class="hint">Each place can have several tags: separate them with commas.</p><div class="settings-grid">
      ${field(esc(state.teamNames[0]) + ' base', 'desertBaseUids', 'uids', '', true)}
      ${field(esc(state.teamNames[1]) + ' base', 'jungleBaseUids', 'uids', '', true)}
      ${field('North point', 'northPointUids', 'uids', '', true)}
      ${field('Centre point', 'centrePointUids', 'uids', '', true)}
      ${field('South point', 'southPointUids', 'uids', '', true)}</div>`
      : emptyNote('Tag numbers can be changed between games.');
  }
  return seg('tags', opts, sub) + html;
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

// The host console's clocks tick between snapshots.
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
  if (!el || el.tagName === 'SELECT') return;
  const a = el.dataset.action;
  const tank = el.dataset.tank;
  switch (a) {
    case 'tab': setTab(el.dataset.scope, el.dataset.tab); break;
    case 'to-title': go('title'); break;
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
        localView = 'menu';
      }
      break;
    case 'host-kick':
      if (confirm(`Remove ${el.dataset.name}?`)) send({ t: 'host_kick', target: el.dataset.target });
      break;
    case 'host-queue': send({ t: 'host_to_queue', target: el.dataset.target }); break;
    case 'host-release': send({ t: 'host_release', target: el.dataset.target }); break;
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
    case 'tank-resync': send({ t: 'host_tank_resync', tank }); toast('Tuning sent'); break;
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
      if (confirm('Put all the Advanced settings back to their defaults?')) send({ t: 'host_tuning_reset' });
      break;
    case 'open-settings': openSheet('sheet-settings'); loadInfo(); break;
    case 'open-howto': openSheet('sheet-howto'); break;
    case 'close-sheet': closeSheets(); break;
  }
});

// Team choice for a tank (a <select>).
document.addEventListener('change', (e) => {
  const el = e.target;
  if (el.dataset && el.dataset.action === 'tank-team') {
    send({ t: 'host_tank_team', tank: el.dataset.tank, team: +el.value });
    el.blur();
  }
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

// The title screen shows before anything else.
document.querySelectorAll('.screen').forEach((s) => s.classList.toggle('active', s.id === 'screen-title'));
shownScreen = 'title';

loadInfo();
connect();
