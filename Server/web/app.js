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
    case 'state': {
      const wasInGame = state && (state.phase === 'countdown' || state.phase === 'playing');
      state = m;
      stateAt = performance.now();
      if (!wasInGame && hostInGame()) {
        // A game has started: the host's page opens on its Battle tab.
        delete tabChoice['host-game'];
        session('tg_tabs', JSON.stringify(tabChoice));
      }
      render();
      break;
    }
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
      // Players without a tank, and tanks the check on Start didn't like
      // (Server/health.go): the host can fix things first or start anyway.
      const parts = [];
      for (const w of m.warnings || []) {
        parts.push(`${w.tank}: ${w.problems.join('; ')}. ${w.advice}`);
      }
      const names = m.names || [];
      if (names.length) {
        parts.push(`${names.join(', ')} ${names.length === 1 ? "hasn't" : "haven't"} chosen a tank: they sit this game out at the front of the queue.`);
      }
      if (confirm(parts.join('\n\n') + '\n\nStart anyway?')) {
        send({ t: 'host_start', force: true });
      } else {
        // Not starting: offer to restart the tanks that looked wrong.
        const warned = (m.warnings || []).filter(w => w.id);
        const who = warned.map(w => w.tank).join(', ');
        if (warned.length && confirm(`Restart ${who} now? ${warned.length === 1 ? 'It is' : 'They are'} back in about 10 seconds; tap Start again after that.`)) {
          for (const w of warned) send({ t: 'host_tank_restart', tank: w.id });
          toast('Restarting ' + who);
        }
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

let playAgainSent = false;

function currentScreen() {
  if (!state) return localView;
  const me = state.me;
  if (me.status !== 'finished') playAgainSent = false;
  const inGame = state.phase === 'countdown' || state.phase === 'playing';
  if (me.isHost && hostWanted) {
    if (hostDriving() && me.status === 'playing' && inGame) return 'play';
    return 'host';
  }
  switch (me.status) {
    case 'queued': return 'wait';
    case 'lobby': return state.phase === 'lobby' ? 'lobby' : 'wait';
    case 'playing': return 'play';
    // Just played: the game page showed the result and stats; arriving here
    // (Play Again there, or a reload) means on to the next game, so join the
    // back of the queue. Sent once per game; the server ignores repeats.
    case 'finished':
      if (!playAgainSent) { playAgainSent = true; send({ t: 'play_again' }); }
      return 'wait';
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
  }
}

function enterScreen(screen) {
  if (screen === 'join') {
    $('join-name').value = store('tg_player_name') || '';
    setTimeout(() => $('join-name').focus(), 50);
  }
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
  for (const body of ['host-body', 'lobby-body']) $(body).scrollTop = 0;
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
  } else if (state.phase === 'ended') {
    title = 'Game over';
    detail = "You're in the queue for the next game: the lobby opens soon.";
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

// allianceCards: players choose a side and are given one of its free tanks
// (all tanks are the same).
function allianceCards() {
  const me = state.me;
  const cards = state.alliances.map((a, i) => {
    const tanks = state.tanks.filter((t) => t.online && t.allianceIndex === i);
    const free = tanks.filter((t) => !t.player).length;
    const players = state.players.filter((p) => p.alliance === a.name).length;
    const mine = !!me.tank && me.role !== 'gunner' && me.allianceIndex === i;
    const sub = mine ? 'Your alliance' : free ? `${free} free ${free === 1 ? 'tank' : 'tanks'}` : tanks.length ? 'All tanks taken' : 'No tanks';
    return `<button type="button" class="alliance-pick ${mine ? 'mine' : ''}" style="--ac:${esc(a.color)}"
        data-action="${mine ? '' : 'pick-alliance'}" data-alliance="${esc(a.id)}" ${!mine && !free ? 'disabled' : ''}>
      <span class="name">${esc(a.name)}</span>
      <span class="sub">${sub}</span>
      <span class="sub">${players} ${players === 1 ? 'player' : 'players'}</span></button>`;
  }).join('');
  return `<div class="alliance-grid">${cards}</div>${gunnerCards()}`;
}

// gunnerCards (2-player tanks): tanks whose driver wants a gunner.
function gunnerCards() {
  const me = state.me;
  if (!state.twoPlayer) return '';
  const tanks = state.tanks.filter((t) => t.online && t.player && t.allowGunner && !t.gunner && t.id !== me.tank);
  if (!tanks.length) return '';
  return `<h3 class="section-h">Be a gunner</h3><div class="tank-grid">${tanks.map((t) =>
    `<button type="button" class="tank-card join" data-action="join-gunner" data-tank="${esc(t.id)}">
      <span class="name">${esc(t.name)}</span>
      <span class="sub" ${allianceStyle(t.allianceIndex)}>${esc(t.alliance || 'No alliance')}</span>
      <span class="sub">${esc(t.player)} drives: tap to be gunner</span></button>`).join('')}</div>`;
}

// myTankCard: your tank, its lobby colour and crew, with the 2-player choices.
function myTankCard() {
  const me = state.me;
  if (!me.tank) {
    return `<div class="big-status"><div class="title">Choose your alliance</div>
      <div class="detail">You&rsquo;ll be given one of its tanks${state.twoPlayer ? ', or join a tank as its gunner' : ''}</div></div>`;
  }
  const role = me.role === 'gunner' ? `Gunner &middot; ${esc(me.partner)} drives`
    : me.role === 'driver' ? `Driver &middot; ${esc(me.partner)} is your gunner` : 'You drive and shoot';
  let crew = '';
  if (state.twoPlayer && me.role !== 'gunner') {
    crew += `<label class="chk"><input type="checkbox" data-action="allow-gunner" ${me.allowGunner ? 'checked' : ''}> Let a second player be my gunner</label>`;
  }
  const btns = [];
  if (me.partner) btns.push('<button class="btn small secondary" type="button" data-action="swap-roles">Swap roles</button>');
  btns.push(me.role === 'gunner'
    ? '<button class="btn small secondary" type="button" data-action="leave-gunner">Leave this tank</button>'
    : '<button class="btn small secondary" type="button" data-action="give-up-tank">Give up tank</button>');
  const glow = me.tankColor
    ? `<div class="glow-line" style="color:${esc(me.tankColorCss)};text-shadow:0 0 12px ${esc(me.tankColorCss)}88">Your tank is glowing ${esc(me.tankColor)}!</div>` : '';
  return `<div class="card"><div class="timer-row"><div>
      <div style="font-size:1.3rem;font-weight:800">${esc(me.tankName)}</div>
      <div class="sub" style="font-weight:700" ${allianceStyle(me.allianceIndex)}>${esc(me.alliance || 'No alliance')}</div></div>
      <div class="end" style="text-align:right;color:var(--muted);font-size:.9rem">${role}</div></div>
      ${glow}
      ${crew ? `<div style="margin-top:8px">${crew}</div>` : ''}
      <div class="btn-row" style="margin-top:8px">${btns.join('')}</div></div>`;
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
    { id: 'tanks', label: 'Alliances' },
    { id: 'players', label: 'Players', count: state.players.length },
  ];
  const tab = currentTab('lobby', tabs);
  renderTabs($('lobby-tabs'), 'lobby', tabs, tab);
  $('lobby-info').textContent = me.name;
  setHTML($('lobby-body'), tab === 'tanks'
    ? `${myTankCard()}${allianceCards()}`
    : playerRows(state.players));
  $('lobby-mid').textContent = me.tank ? 'Waiting for the host to start' : 'Choose an alliance';
}

// ── Game Over / Stats ────────────────────────────────────────────────────────
// The host console's Game Over page. Players see their result and stats on
// play.html, which has Play Again.

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
  ['config-server', 'Server Settings', 'Game password, players before queuing, debug menu, 2-player tanks, buzzer, airtime fairness'],
  ['config-gameplay', 'Gameplay Settings', 'Match length, points, damage, reload, dead time'],
  ['config-advanced', 'Advanced Settings', 'Motor, control and video tuning, shot timing, presets'],
  ['config-alliances', 'Alliances', 'Names, colours, add and remove'],
  ['config-tags', 'Tags and Capture Points', 'RFID tags for bases and capture points'],
  ['config-signal', 'Signal Quality Test', 'Turn every turret round and chart the radio at each angle'],
];
// Config pages left out of the menu during a game (the hub refuses their
// changes then too).
const CONFIG_PAGES_BETWEEN_GAMES = ['config-alliances', 'config-tags', 'config-signal'];
// Settings that only matter when joining or starting a game: greyed out during
// a game (the hub keeps their old values then too, applySettings).
const SETTINGS_BETWEEN_GAMES = ['clientPassword', 'maxPlayers', 'twoPlayerTanks', 'matchSeconds',
  'countdownSeconds', 'pointsToWin'];

function hostInGame() {
  return state.phase === 'countdown' || state.phase === 'playing';
}

function lockedNow(path) {
  return hostInGame() && SETTINGS_BETWEEN_GAMES.includes(path);
}

function lockedNote(paths) {
  return hostInGame() && paths.some((p) => SETTINGS_BETWEEN_GAMES.includes(p))
    ? '<p class="hint wide">A game is on: the greyed-out settings can be changed between games.</p>' : '';
}

function renderHost() {
  const ph = state.phase;
  // Outside a game the host is back on the console; "Drive My Tank" is per game.
  if (ph !== 'countdown' && ph !== 'playing') setHostView('console');
  const config = hostPage !== '';
  $('host-config-btn').hidden = config;
  $('host-banner').hidden = config || !hostInGame();

  if (config) {
    renderHostConfig();
    return;
  }
  const players = state.players.filter((p) => !p.host).length;
  $('host-title').textContent = { idle: 'Server Settings', lobby: 'Server Lobby', countdown: 'Starting',
    playing: state.paused ? 'Paused' : 'Server Gameplay', ended: 'Game Over' }[ph];
  $('host-info').textContent = ph === 'lobby' ? `${players}/${state.capacity}` : '';

  const tabs = hostTabs(ph);
  // The countdown and the game share one tab choice (reset to Battle when a
  // game starts, onMessage).
  const scope = hostInGame() ? 'host-game' : 'host-' + ph;
  const tab = tabs.length ? currentTab(scope, tabs) : '';
  renderTabs($('host-tabs'), scope, tabs, tab);

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

  // Bottom bar: Menu on the left (Close Game in the lobby: there's no reason
  // to leave an open lobby without closing it), the next step on the right.
  const back = ph === 'lobby'
    ? '<button class="nav-btn back danger" type="button" data-action="host-close">Close Game</button>'
    : '<button class="nav-btn back" type="button" data-action="host-menu">Menu</button>';
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
  const close = '<button class="btn small danger" type="button" data-action="host-close">Close Game</button>';
  return `<div class="card"><div class="timer-row">
      <div><h3 style="margin:0">${ph === 'countdown' ? 'Starting in' : 'Time left'}</h3>
      <div class="timer-big" id="host-timer-big">${fmtClock(remainingMs())}</div>
      ${state.paused ? '<div class="paused-tag">PAUSED</div>' : ''}</div><div class="battle-btns">${pause}${close}</div></div></div>
    ${drive}
    <div class="two-col">${scoreCard()}${pointsCard()}</div>`;
}

// pointsCard: who holds each capture point (display only).
function pointsCard() {
  const pts = state.capturePoints.map((p) => {
    const a = p.owner >= 0 ? state.alliances[p.owner] : null;
    const style = a ? `style="border-color:${esc(a.color)}"` : '';
    return `<div class="point" ${style}><div class="name">${esc(p.name)}</div>
      <div class="sub" ${a ? `style="color:${esc(a.color)}"` : ''}>${esc(a ? a.name : 'Nobody')}</div></div>`;
  }).join('');
  return `<div class="card"><h3>Capture points</h3>
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
        <div class="compass-row">${irCompass(t, 76)}
          <p class="hint">Last hit: ${t.lastHitDir ? `${{ N: 'front', E: 'right side', S: 'rear', W: 'left side' }[t.lastHitDir]}, sensors ${esc(t.lastHit.replace(/^.*sensors /, '').replace(')', ''))}` : 'none yet'}<br>
          Ring: the sensors that saw it. Middle: the side it counted as.</p></div>`;
    }
    tools += `<h4>Flip</h4>
      <div class="chk-grid">${chk('invThrottle', 'Drive forward/back')}${chk('invSteer', 'Drive left/right')}${chk('invTurret', 'Turret')}
        ${chk('hflip', 'Video sideways')}${chk('vflip', 'Video upside down')}</div>
      <h4>Test</h4>
      <div class="btn-row">
        <button class="btn small secondary" type="button" data-action="tank-identify" data-tank="${esc(t.id)}">Identify</button>
        <button class="btn small secondary" type="button" data-action="tank-ping" data-tank="${esc(t.id)}">Ping${t.pingMs ? ` (${t.pingMs} ms)` : ''}</button>
        ${inGame && t.player ? '' : `<button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="0.5">&#9664; Turret</button>
        <button class="btn small secondary" type="button" data-hold="turret" data-tank="${esc(t.id)}" data-s="-0.5">Turret &#9654;</button>`}
        <button class="btn small secondary" type="button" data-action="tank-resync" data-tank="${esc(t.id)}">Reset</button>
        <button class="btn small secondary" type="button" data-action="tank-restart" data-tank="${esc(t.id)}" data-name="${esc(t.name)}">Restart</button>
        ${state.phase === 'lobby' && t.player ? `<button class="btn small danger" type="button" data-action="tank-free" data-tank="${esc(t.id)}">Remove Player</button>` : ''}
      </div>
      <h4>Name and default alliance</h4>
      <div class="inline-form">
        <input type="text" maxlength="16" value="${esc(t.name)}" id="rename-${esc(t.id)}" aria-label="Robot name">
        <button class="btn small secondary" type="button" data-action="tank-rename" data-tank="${esc(t.id)}">Rename</button>
      </div>
      <div class="inline-form" style="margin-top:6px">${allianceSelect(t)}</div>
      <p class="hint" style="margin-top:6px">${esc(t.ip || '')}${t.ip ? ' &middot; ' : ''}id ${esc(t.id)}</p>
      ${t.boot ? `<p class="hint">${esc(t.boot.charAt(0).toUpperCase() + t.boot.slice(1))}</p>` : ''}`;
  }
  return `<details class="item" data-row="tank-${esc(t.id)}" ${openRows.has('tank-' + t.id) ? 'open' : ''}>
    <summary><span class="dot ${t.online ? '' : 'off'}"></span>
      <div class="main"><div class="name">${esc(t.name)}</div>
      <div class="sub"><span ${allianceStyle(t.allianceIndex)}>${esc(t.alliance || 'No alliance')}</span> &middot; ${crew}${
        t.color ? ` &middot; <span style="color:${esc(t.colorCss)}">&#9679; ${esc(t.color)}</span>` : ''}</div></div>
      <span class="end">${status}</span>${inGame && t.life ? irCompass(t, 30) : ''}</summary>
    <div class="tools">${tools}</div></details>`;
}

// irCompass draws a robot's last hit, like the Unity operator screen: the
// outer ring is its 8 IR sensors (lit = saw the shot), the middle squares
// the side the hit counted as. It flashes for a moment after a new hit.
const compassFlash = {}; // tank id → { hits, until }
function irCompass(t, size) {
  const mask = t.lastHitMask || 0;
  const hits = t.hits || 0;
  const f = compassFlash[t.id] || { hits: 0, until: 0 };
  if (hits > f.hits) compassFlash[t.id] = { hits, until: performance.now() + 1200 };
  const fresh = performance.now() < (compassFlash[t.id] || f).until;
  const dots = [...Array(8)].map((_, i) => {
    const a = i * Math.PI / 4; // bit 0 = N (front), clockwise
    return `<circle cx="${(50 + 38 * Math.sin(a)).toFixed(1)}" cy="${(50 - 38 * Math.cos(a)).toFixed(1)}" r="9" class="${mask & (1 << i) ? 'on' : ''}"/>`;
  }).join('');
  const sides = { N: [50, 30], E: [70, 50], S: [50, 70], W: [30, 50] };
  const marks = Object.entries(sides).map(([d, [x, y]]) =>
    `<rect x="${x - 7}" y="${y - 7}" width="14" height="14" rx="2" class="${d === t.lastHitDir ? 'on' : ''}"/>`).join('');
  return `<svg class="compass ${fresh ? 'fresh' : ''}" viewBox="0 0 100 100" width="${size}" height="${size}" role="img"
    aria-label="Last hit: ${esc(t.lastHitDir || 'none')}">${dots}${marks}</svg>`;
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
    ? (state.phase === 'lobby' ? `${myTankCard()}${allianceCards()}` : `<p class="hint">Playing as <b>${esc(me.name)}</b>.</p>`)
    : `<label class="field">Your player name<input type="text" id="host-play-name" maxlength="16" value="${esc(store('tg_player_name') || '')}"></label>`;
  return `<div class="card">
    <div class="srow" style="padding:0 0 8px;border:0"><span>Server as player</span>
      <button class="toggle" type="button" role="switch" aria-checked="${playing}" data-action="host-play"></button></div>
    ${body}</div>`;
}

// ── Server Config ────────────────────────────────────────────────────────────

function renderHostConfig() {
  if (hostInGame() && CONFIG_PAGES_BETWEEN_GAMES.includes(hostPage)) {
    hostPage = 'config'; // a game started while it was open: back to the menu
    session('tg_host_page', hostPage);
  }
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
    case 'config-signal': html = signalTestPage(); break;
    case 'config-tags': {
      tabs = [{ id: 'tags', label: 'RFID Tags', count: (state.rfidTags || []).length },
        { id: 'points', label: 'Capture Points', count: state.capturePoints.length }];
      const tab = currentTab('config-tags', tabs);
      renderTabs($('host-tabs'), 'config-tags', tabs, tab);
      html = tab === 'tags' ? rfidTagsTab() : capturePointsTab();
      break;
    }
    default:
      html = `<div class="list">${CONFIG_PAGES.filter(([id]) => !hostInGame() || !CONFIG_PAGES_BETWEEN_GAMES.includes(id)).map(([id, title, sub]) =>
        `<button type="button" class="item menu-row" data-action="host-page" data-page="${id}">
          <div class="main"><div class="name">${title}</div><div class="sub">${sub}</div></div><span class="chev">&rsaquo;</span></button>`).join('')}</div>`;
  }
  if (!tabs.length) renderTabs($('host-tabs'), 'none', [], '');
  setHTML($('host-body'), html);
  if (hostPage === 'config-signal') sigCamsMount();
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
  const locked = lockedNow(path);
  return `<label class="field ${wide ? 'wide' : ''} ${locked ? 'locked' : ''}">${label}<input type="${type}" ${type === 'number' ? 'inputmode="decimal"' : ''}
    data-set="${path}" data-kind="${kind}" value="${esc(v)}" ${attrs} ${locked ? 'disabled' : ''}></label>`;
}

function check(label, path) {
  const locked = lockedNow(path);
  return `<label class="chk ${locked ? 'locked' : ''}"><input type="checkbox" data-set="${path}" data-kind="bool" ${getPath(state.settings, path) ? 'checked' : ''} ${locked ? 'disabled' : ''}> ${label}</label>`;
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
  return `<div class="settings-grid">${lockedNote(['clientPassword'])}
    <label class="field wide">Game<select disabled><option>Realtime</option><option>Turn-based (coming later)</option></select></label>
    ${field('Game password for players (blank = none)', 'clientPassword', 'text', 'maxlength="32" autocomplete="off"', true)}
    ${field('Players in lobby before queuing (0 = one per robot)', 'maxPlayers', 'int', 'min="0" max="40"', true)}
    <div class="wide chk-grid">${check('Debug menu on players&rsquo; phones', 'debugConsole')}
      ${check('2-player tanks (driver + gunner)', 'twoPlayerTanks')}${check('Tank buzzers on', 'tuning.buzzer')}</div>
    <div class="wide">${check('Router airtime fairness (both Wi-Fi bands)', 'airtimeFairness')}
      <p class="hint">Leave off: with it on, several tanks switching off or dying within seconds can slow every other tank for about 8 minutes.
      With it off, a phone with a poor signal on the tanks&rsquo; 2.4&nbsp;GHz network can slow them more, so keep phones on 5&nbsp;GHz.
      Set again every minute; takes effect within seconds of changing it here.</p></div>
  </div>`;
}

function gameplaySettingsForm() {
  return `<div class="settings-grid">${lockedNote(['matchSeconds'])}
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
  const opts = [['motors', 'Motor'], ['controls', 'Control'], ['stab', 'Stabiliser'], ['video', 'Video'], ['shots', 'Shots'], ['presets', 'Presets']];
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
    case 'stab':
      html = `<p class="hint">Turret stabilisation: with STAB on, the turret turns against the hull so it keeps pointing the same way,
        and the player's turret slider adds on top. Robots have no compass, so the server works out the hull's turning from the drive commands.
        Tune with a robot spinning on the spot: raise the gain if the turret still turns with the hull, lower it if it overshoots.</p>
        <div class="settings-grid">
        <div class="wide chk-grid">${check('STAB button on players&rsquo; phones', 'tuning.stabAllowed')}${check('Correct the other way', 'tuning.stabInvert')}</div>
        ${field('Gain (turning on the spot)', 'tuning.stabGain', 'num', 'min="0" max="3" step="0.05"')}
        ${field('Gain &times; when driving and turning', 'tuning.stabArcGain', 'num', 'min="0" max="3" step="0.05"')}
        ${field('Curve (1 = in proportion)', 'tuning.stabCurve', 'num', 'min="0.2" max="5" step="0.1"')}
        ${field('Ignore turns below', 'tuning.stabDeadband', 'num', 'min="0" max="0.5" step="0.01"')}
        ${field('Most correction', 'tuning.stabMax', 'num', 'min="0" max="1" step="0.05"')}
        ${field('Hull spin-up (ms)', 'tuning.stabSpinUpMs', 'int', 'min="0" max="2000" step="10"')}
        ${field('Hull spin-down (ms)', 'tuning.stabSpinDownMs', 'int', 'min="0" max="2000" step="10"')}
        ${field('Update every (ms)', 'tuning.stabSendMs', 'int', 'min="20" max="500" step="10"')}</div>
        <p class="hint" style="margin-top:8px">Keep &ldquo;Most correction&rdquo; at or below the turret&rsquo;s lowest steady speed (Motor tab)
        so corrections use the smooth pulsed mode. The hull model also follows the tracks&rsquo; acceleration settings.</p>`;
      break;
    case 'video':
      html = `<p class="hint">Changing these restarts each robot's camera for a moment. Lower quality number = sharper picture, bigger frames.</p>
        <div class="settings-grid">
        ${field('Frames per second', 'tuning.videoFps', 'int', 'min="1" max="30"')}
        ${field('Frame size (0 smallest)', 'tuning.videoFrameSize', 'int', 'min="0" max="13"')}
        ${field('JPEG quality (4-63)', 'tuning.videoQuality', 'int', 'min="4" max="63"')}
        <div class="wide">${check('Keep cameras on between games', 'camerasAlwaysOn')}</div></div>
        <p class="hint" style="margin-top:8px">Normally a robot&rsquo;s camera is only on while it&rsquo;s in a game (less Wi-Fi traffic and heat).
        Switch this on to use the video test page or a load test.</p>`;
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

// The host's clocks tick between snapshots, and turn red and flash for the
// last 10 seconds of a game.
setInterval(() => {
  if (shownScreen !== 'host') return;
  const ms = remainingMs();
  const urgent = state && state.phase === 'playing' && !state.paused && ms <= 10000;
  for (const id of ['host-timer', 'host-timer-big']) {
    const el = $(id);
    if (!el) continue;
    el.textContent = fmtClock(ms);
    el.classList.toggle('urgent', urgent);
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
    case 'pick-alliance': send({ t: 'pick_alliance', alliance: el.dataset.alliance }); break;
    case 'give-up-tank': send({ t: 'pick_alliance', alliance: '' }); break;
    case 'join-gunner': send({ t: 'join_gunner', tank }); break;
    case 'leave-gunner': send({ t: 'leave_gunner' }); break;
    case 'allow-gunner': send({ t: 'allow_gunner', on: el.checked }); break;
    case 'swap-roles': send({ t: 'swap_roles' }); break;
    case 'host-drive': setHostView('drive'); render(); break;
    case 'host-open': send({ t: 'host_open' }); break;
    case 'host-start': send({ t: 'host_start' }); break;
    case 'sigtest-start':
      send({ t: 'host_sigtest_start' });
      sigData = sigData ? { ...sigData, running: true, state: 'warm-up' } : sigData;
      render();
      break;
    case 'sigtest-stop': send({ t: 'host_sigtest_stop' }); break;
    case 'host-end': if (confirm('End the game now?')) send({ t: 'host_end' }); break;
    case 'host-lobby': send({ t: 'host_lobby' }); break;
    case 'host-pause': send({ t: 'host_pause', on: el.dataset.on === 'true' }); break;
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
    case 'tank-restart':
      if (confirm(`Restart ${el.dataset.name}? It is back in about 10 seconds.`)) {
        send({ t: 'host_tank_restart', tank });
        toast(`Restarting ${el.dataset.name}`);
      }
      break;
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
    case 'sig-compare': sigCompare = el.value; store('tg_sig_compare', sigCompare); sigRefresh(); break;
    case 'sig-comment': sigCommentChanged(d.run, el.value); break;
    case 'sig-avg': sigAvgChanged(d.tank, d.end, el.value); break;
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
  if (!el || el.disabled) return;
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

// ── Signal Quality Test (Server/signaltest.go) ───────────────────────────────
// Turns every connected turret slowly round and charts the radio by angle.
// The page polls /api/sigtest once a second while it's open, and tells the
// hub every few seconds that it's open, so every camera streams for lining
// the turrets up (the hub switches them off ~15 s after it stops hearing).

let sigData = null;
let sigRuns = null;      // saved runs, newest first (/api/sigtest/runs)
let sigRunsFor = null;   // the last run's file when sigRuns was fetched
let sigCompare = store('tg_sig_compare') || ''; // saved run drawn underneath, '' = none
const sigCompareData = {}; // file → that run, binned
let sigAlignSent = 0;    // when the hub was last told the page is open
let sigHistory = null;   // every saved run, each tank's directions merged (/api/sigtest/history)

setInterval(async () => {
  const open = hostPage === 'config-signal' && shownScreen === 'host' && !document.hidden &&
    state && state.me && state.me.isHost;
  if (!open) {
    if (sigAlignSent) { send({ t: 'host_sigtest_align', on: false }); sigAlignSent = 0; }
    if (sigCams.size) sigCamsMount(); // no tiles on screen: closes the feeds
    return;
  }
  if (Date.now() - sigAlignSent > 5000) {
    send({ t: 'host_sigtest_align', on: true });
    sigAlignSent = Date.now();
  }
  try {
    sigData = await (await fetch('/api/sigtest', { cache: 'no-store' })).json();
    const file = sigData.file || '';
    if (!sigRuns || sigRunsFor !== file) {
      sigRuns = await (await fetch('/api/sigtest/runs', { cache: 'no-store' })).json();
      sigHistory = await (await fetch('/api/sigtest/history', { cache: 'no-store' })).json();
      sigRunsFor = file;
    }
  } catch (_) {
    return;
  }
  sigRefresh();
}, 1000);

// sigRefresh redraws the results, fetching the run to compare with first.
async function sigRefresh() {
  await null; // after the picker that called this has been let go of
  if (sigCompare && !sigCompareData[sigCompare]) {
    try {
      const r = await fetch('/api/sigtest?run=' + encodeURIComponent(sigCompare), { cache: 'no-store' });
      if (r.ok) sigCompareData[sigCompare] = await r.json();
      else sigCompare = '';
    } catch (_) { /* tried again next second */ }
  }
  const el = $('sig-live');
  if (el) setHTML(el, sigLiveHTML());
}

const SIG_CURVE = 2; // radius = R × (how good)², so the edge gets twice the room

const SIG_METRICS = [
  // Further out on the chart is always better.
  // The scale is curved (SIG_CURVE): the good end, near the edge, gets
  // more room, so the rings crowd outwards.
  { id: 'fps', label: 'Video fps', key: 'fps', lo: 0, hi: 25, unit: 'fps', rings: [10, 15, 20, 25] },
  { id: 'gap', label: 'Frame waits', key: 'gapMs', lo: 0, hi: 1000, invert: true, unit: 'ms', rings: [500, 250, 100, 0] },
  { id: 'dbm', label: 'Router signal', key: 'dbm', lo: -80, hi: -10, unit: 'dBm', rings: [-50, -35, -20, -10] },
  { id: 'rx', label: 'Router speed', key: 'rxMbit', lo: 0, hi: 72, unit: 'Mbit/s', rings: [36, 48, 60, 72] },
];

function signalTestPage() {
  const online = new Map(state.tanks.filter((t) => t.online).map((t) => [tankNum(t.name), t]));
  const pairs = [1, 2, 3, 4, 5].map((n) => {
    const a = online.get(n), b = online.get(n + 5);
    if (!a && !b) return '';
    const name = (t, num) => t ? esc(t.name) : `<span class="sig-missing">no tank ${num}</span>`;
    return `<li>${name(a, n)} &harr; ${name(b, n + 5)}</li>`;
  }).join('');
  return `<div class="card"><h3>Set up</h3>
    <ol class="hint sig-steps">
      <li>Stand the tanks in pairs, hull front to hull front, about 1.5&nbsp;m apart. Several pairs can run together: only one barrel is lit at a time, so they can't confuse each other.</li>
      <li>Point every turret exactly forward: use the cameras below.</li>
      <li>Start: every turret turns slowly round, then back, and stops facing forward again. Each time a turret points at its partner, the partner's IR sensors mark 0&deg;.</li>
    </ol>
    ${pairs ? `<ul class="sig-pairs">${pairs}</ul>` : emptyNote('No tanks are connected.')}</div>
    ${sigCamsHTML()}
    <div class="settings-grid">
      ${field('Turret speed (slower = finer detail)', 'signalTest.speed', 'num', 'min="0.05" max="1" step="0.05"')}
      ${field('Turns each way', 'signalTest.turns', 'int', 'min="1" max="10"')}
      ${field('Give up if a turn takes longer than (s)', 'signalTest.maxTurnS', 'num', 'min="10" max="600" step="5"')}
      <div class="wide">${check('Then turn back the other way', 'signalTest.reverse')}</div>
      <div class="wide">${check('Run the tracks while the turrets turn (motors test)', 'signalTest.drive')}
        <p class="hint">Lift each tank so its tracks are off the ground. The tracks then run a game-like pattern: full speed and creeping,
        turning, spinning, reversing and stopping. It looks random but is the same on every tank and in every run, so runs compare fairly.
        Saved as a separate kind of run (&ldquo;driving&rdquo;).</p></div>
    </div>
    <div id="sig-live">${sigLiveHTML()}</div>`;
}

function tankNum(name) {
  const m = /(\d+)\s*$/.exec(name || '');
  return m ? +m[1] : 0;
}

// ── Lining up: a camera per tank, with turn buttons ──

const SIG_FAST = 0.6; // turret speed for the fast buttons

// sigCamsHTML: a tile per connected tank, partners side by side. The
// pictures are canvases kept in sigCams and put back into their tiles after
// each redraw (sigCamsMount), so a redraw doesn't restart the feeds.
function sigCamsHTML() {
  const order = (t) => {
    const n = tankNum(t.name);
    return n >= 1 && n <= 10 ? ((n - 1) % 5) * 2 + (n > 5 ? 1 : 0) : 100 + n;
  };
  const tanks = state.tanks.filter((t) => t.online).sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name, undefined, { numeric: true }));
  if (!tanks.length) return '';
  const busy = !!(sigData && sigData.running);
  const slow = Math.max(0.05, +(state.settings.signalTest && state.settings.signalTest.speed) || 0.2);
  // A positive turret speed turns the turret left (the test's first way round).
  const btn = (t, s, label, title) => `<button class="btn small secondary" type="button" data-hold="turret"
      data-tank="${esc(t.id)}" data-s="${s}" title="${title}" aria-label="${title}" ${busy ? 'disabled' : ''}>${label}</button>`;
  const tiles = tanks.map((t) => `<div class="sig-cam">
      <div class="sig-cam-video" data-cam="${esc(t.id)}"></div>
      <div class="sig-cam-name">${esc(t.name)}</div>
      <div class="sig-cam-btns">
        ${btn(t, SIG_FAST, '&#9664;&#9664;', 'Turn left fast')}${btn(t, slow, '&#9664;', 'Turn left slowly')}
        ${btn(t, -slow, '&#9654;', 'Turn right slowly')}${btn(t, -SIG_FAST, '&#9654;&#9654;', 'Turn right fast')}
      </div></div>`).join('');
  return `<div class="card"><h3>Line up the turrets</h3>
    <p class="hint">Cameras stay on while this page is open. Hold a button to turn (a tap nudges) until the partner tank sits on the centre line.</p>
    <div class="sig-cams">${tiles}</div></div>`;
}

const sigCams = new Map(); // tank id → { canvas, ws, closed }

function sigCamsMount() {
  const seen = new Set();
  document.querySelectorAll('.sig-cam-video[data-cam]').forEach((el) => {
    const id = el.dataset.cam;
    seen.add(id);
    let cam = sigCams.get(id);
    if (!cam) {
      cam = sigCamOpen(id);
      sigCams.set(id, cam);
    }
    if (cam.canvas.parentNode !== el) el.appendChild(cam.canvas);
  });
  for (const [id, cam] of sigCams) {
    if (seen.has(id)) continue;
    cam.closed = true;
    if (cam.ws) cam.ws.close();
    sigCams.delete(id);
  }
}

// sigCamOpen: a tank's feed from the hub, one JPEG per WebSocket message
// (works on iPhones too), drawn on a canvas. Reconnects until closed.
function sigCamOpen(id) {
  const canvas = document.createElement('canvas');
  canvas.width = 320;
  canvas.height = 240;
  const cam = { canvas, ws: null, closed: false };
  let drawing = false;
  const connect = () => {
    if (cam.closed) return;
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(`${proto}://${location.host}/api/video-ws?url=${encodeURIComponent(id)}&who=line-up&maxfps=10`);
    ws.binaryType = 'blob';
    ws.onmessage = async (e) => {
      if (drawing) return; // still drawing the last one: skip
      drawing = true;
      try {
        const bmp = await createImageBitmap(e.data);
        if (canvas.width !== bmp.width || canvas.height !== bmp.height) {
          canvas.width = bmp.width;
          canvas.height = bmp.height;
        }
        canvas.getContext('2d').drawImage(bmp, 0, 0);
        bmp.close();
      } catch (_) { /* a broken frame */ }
      drawing = false;
    };
    ws.onclose = () => { if (!cam.closed) setTimeout(connect, 2000); };
    cam.ws = ws;
  };
  connect();
  return cam;
}

// ── Results ──

// The Start/Stop button and everything below it: refreshed by the poll.
function sigLiveHTML() {
  const d = sigData;
  const inGame = state.phase === 'countdown' || state.phase === 'playing';
  const button = `<div class="btn-row" style="margin:12px 0">${d && d.running
    ? '<button class="btn danger" type="button" data-action="sigtest-stop">Stop Test</button>'
    : `<button class="btn primary" type="button" data-action="sigtest-start" ${inGame ? 'disabled' : ''}>Start Test</button>`}</div>`;
  if (!d) return button + emptyNote('Loading…');
  if (d.state === 'none') return button + emptyNote('No test run yet.');
  const words = { 'warm-up': 'Starting…', turning: 'Turning', stopping: 'Stopping…', done: 'Finished', stopped: 'Stopped early: not saved' };
  let html = button + `<div class="card"><h3>${esc(words[d.state] || d.state)} &middot; ${Math.round(d.elapsedS)} s${d.drive ? ' &middot; with driving' : ''}</h3>
    <p class="hint">Started ${esc(sigWhen(d.startedAt))}. ${(d.tanks || []).length} tanks on air${
      d.irEveryS ? `; each listens for its partner every ${d.irEveryS.toFixed(2)}&nbsp;s` : ''}.${d.routerOk ? '' : ` Router view: ${esc(d.routerNote || 'waiting')}.`}</p>
    ${d.run && d.state !== 'stopped' ? sigCommentBox(d.run, d.comment, 'Comment on this run') : ''}
    ${d.note ? `<p class="hint">${esc(d.note)}</p>` : ''}
    ${(() => {
      const warm = (d.tanks || []).filter((t) => t.warmingUp);
      return warm.length ? `<p class="hint">Powered on less than 5 minutes before this run: ${warm.map((t) => esc(t.name)).join(', ')}.</p>` : '';
    })()}
    ${d.running ? `<ul class="sig-progress">${d.tanks.map((t) => `<li><b>${esc(t.name)}</b> ${esc(t.progress || '')}</li>`).join('')}</ul>` : ''}</div>`;

  // Compare: an earlier run drawn underneath, in grey.
  const others = (sigRuns || []).filter((r) => r.file !== d.file);
  const cmp = sigCompare && sigCompare !== d.file ? sigCompareData[sigCompare] : null;
  if (others.length) {
    const label = (r) => `${sigWhen(r.started)} · ${r.tanks.length <= 4 ? r.tanks.join(', ') : r.tanks.length + ' tanks'}${
      r.drive ? ' · driving' : ''}${r.comment ? ' · ' + r.comment : ''}`;
    html += `<label class="sig-compare">Compare with
      <select data-action="sig-compare">
        <option value="">Nothing</option>
        ${others.map((r) => `<option value="${esc(r.file)}" ${r.file === sigCompare ? 'selected' : ''}>${esc(label(r))}</option>`).join('')}
      </select></label>`;
    if (cmp) html += sigCommentBox(sigCompare, cmp.comment, 'Comment on the grey run');
  }

  const metric = segChoice('sigmetric', SIG_METRICS.map((m) => [m.id, m.label]));
  const m = SIG_METRICS.find((x) => x.id === metric);
  html += seg('sigmetric', SIG_METRICS.map((x) => [x.id, x.label]), metric);
  html += `<p class="hint">Top = turret forward. Further out = better (the scale stretches towards the edge). <span class="sig-key fwd">&#9472;&#9472;</span> first way round
    <span class="sig-key back">&#8211; &#8211;</span> back${cmp ? `. <span class="sig-key before">Grey</span>: ${esc(sigWhen(cmp.startedAt))}${cmp.drive ? ', driving' : ''}${cmp.comment ? ` (${esc(cmp.comment)})` : ''}` : ''}.</p>`;
  const before = (t) => cmp && (cmp.tanks.find((b) => b.id === t.id) || cmp.tanks.find((b) => b.name === t.name));
  html += `<div class="sig-grid">${d.tanks.map((t) => sigChart(t, m, before(t))).join('')}</div>`;
  html += sigTable(d.tanks, before);
  if (!d.running) html += '<a class="btn small secondary" href="/api/sigtest.csv" download style="margin-top:10px">Download CSV</a>';
  html += sigAveragesHTML(m);
  return html;
}

// ── Averages over saved runs ──
// A chart per tank: both directions, averaged over its runs between two
// dates the host picks per tank (remembered in this browser).

function sigAvgRange(id) {
  try {
    return JSON.parse(store('tg_sig_avg_' + id) || '{}');
  } catch (_) {
    return {};
  }
}

// sigAvgTanks: each tank in the saved runs, oldest run first. Runs without
// IR angle marks are left out (their angles are guesses).
// SIG_KINDS: the kinds of run the averages keep apart.
const SIG_KINDS = [['turret', 'Turret only'], ['drive', 'With driving']];
// SIG_AIRS: a busy channel is a different test, so its runs are kept apart too.
const SIG_AIRS = [['pair', '2 tanks on air'], ['busy', '3 or more on air']];

function sigAvgKind() {
  return segChoice('sigavgkind', SIG_KINDS);
}

function sigAvgAir() {
  return segChoice('sigavgair', SIG_AIRS);
}

function sigAvgTanks() {
  const tanks = new Map();
  const driving = sigAvgKind() === 'drive';
  const busy = sigAvgAir() === 'busy';
  for (const r of sigHistory || []) {
    if (!!r.drive !== driving || r.tanks.length > 2 !== busy) continue;
    for (const t of r.tanks) {
      // By name: that's the tank, even if its board has been swapped.
      if (!tanks.has(t.name)) tanks.set(t.name, { id: t.name, name: t.name, runs: [], skipped: 0 });
      const e = tanks.get(t.name);
      if (t.ref === 'ok') e.runs.push({ started: r.started, bins: t.bins, comment: r.comment || '' });
      else e.skipped++;
    }
  }
  // By the number the name ends with (Desert-01 … Jungle-10); unnumbered last.
  const order = (e) => tankNum(e.name) || 1000;
  return [...tanks.values()].filter((e) => e.runs.length)
    .sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name, undefined, { numeric: true }));
}

// sigAvgSpan: the first and last run a tank's chart uses (start times).
// Unset ends are its first and last runs, so new runs join in. Ranges saved
// as dates by older versions still work.
function sigAvgSpan(e) {
  const rg = sigAvgRange(e.id);
  const at = (v) => (v == null || v === '' ? null : /^\d+$/.test(String(v)) ? +v : new Date(v).getTime());
  let from = at(rg.from) ?? -Infinity;
  let to = at(rg.to) ?? Infinity;
  if (rg.to && !/^\d+$/.test(String(rg.to))) to += 59999; // a date: its whole minute
  if (from > to) [from, to] = [to, from];
  return { from, to };
}

// sigAvgData: one tank's runs between its dates, and per 10° across them
// the average, worst and best (worst = nearest the middle of the chart, so
// the lowest fps but the longest frame wait).
function sigAvgData(e) {
  const { from, to } = sigAvgSpan(e);
  const used = e.runs.filter((x) => x.started >= from && x.started <= to);
  const avg = [], worst = [], best = [];
  for (let i = 0; i < 36; i++) {
    avg.push({}); worst.push({}); best.push({});
    for (const x of SIG_METRICS) {
      const v = used.map((r) => r.bins[i] && r.bins[i][x.key]).filter((r) => r != null);
      if (!v.length) continue;
      const lo = Math.min(...v), hi = Math.max(...v);
      avg[i][x.key] = v.reduce((a, c) => a + c, 0) / v.length;
      worst[i][x.key] = x.invert ? hi : lo;
      best[i][x.key] = x.invert ? lo : hi;
    }
  }
  return { used, avg, worst, best };
}

function sigAvgChart(e, m, d) {
  const passes = d.used.length ? [{ cls: 'worst', bins: d.worst }, { cls: 'best', bins: d.best }, { cls: 'avg', bins: d.avg }] : [];
  return sigChart({ name: e.name, ref: 'ok', passes }, m, null, { edge: true });
}

// sigAngleRanges: the angles where flags (one per 10°) are set, as
// "90–190°, 340–20°" (a range can run past forward).
function sigAngleRanges(flags) {
  const n = flags.length;
  if (flags.every(Boolean)) return 'all round';
  const startAt = flags.findIndex((f) => !f); // begin outside a range, so none is split at 0°
  const out = [];
  let open = -1;
  for (let k = 1; k <= n; k++) {
    const i = (startAt + k) % n;
    if (flags[i] && open < 0) open = i;
    if (!flags[i] && open >= 0) {
      out.push(`${open * 10}&ndash;${i * 10 || 360}&deg;`);
      open = -1;
    }
  }
  return out.join(', ') || 'none';
}

// sigAvgQuality: how playable a tank's video is, 0-100, from its chosen runs.
// Each 10° of each run scores 0-1 by the worse of its video rate (12 fps
// or less = 0, 24 or more = 1) and its longest wait between frames (400 ms
// or more = 0, 100 ms or less = 1): either one spoils play. Half the score
// is the average all the way round; half is the tank's weakest 40° (where a
// turret can sit), so a bad spot counts even when it's a small part of the
// circle. Both are averaged over the runs. null without video figures.
const SIG_SECTOR = 4; // bins: 40°

function sigCellScore(b) {
  if (!b || b.fps == null) return null;
  const c = (x) => Math.max(0, Math.min(1, x));
  const fps = c((b.fps - 12) / 12);
  const wait = b.gapMs == null ? 1 : c((400 - b.gapMs) / 300);
  return Math.min(fps, wait);
}

// sigWeakest: the lowest average over SIG_SECTOR neighbouring bins (round
// past forward too), and where it starts.
function sigWeakest(cells) {
  let worst = null, at = 0;
  for (let i = 0; i < cells.length; i++) {
    const v = [];
    for (let k = 0; k < SIG_SECTOR; k++) {
      const c = cells[(i + k) % cells.length];
      if (c != null) v.push(c);
    }
    if (!v.length) continue;
    const m = v.reduce((a, x) => a + x, 0) / v.length;
    if (worst == null || m < worst) { worst = m; at = i; }
  }
  return { score: worst, at };
}

function sigAvgQuality(d) {
  const all = [], weak = [];
  for (const r of d.used) {
    const cells = Array.from({ length: 36 }, (_, i) => sigCellScore(r.bins[i]));
    const have = cells.filter((x) => x != null);
    if (!have.length) continue;
    all.push(have.reduce((a, x) => a + x, 0) / have.length);
    weak.push(sigWeakest(cells).score);
  }
  if (!all.length) return null;
  const mean = (v) => v.reduce((a, x) => a + x, 0) / v.length;
  // Where the weak spot is, from the runs together.
  const together = Array.from({ length: 36 }, (_, i) => {
    const v = d.used.map((r) => sigCellScore(r.bins[i])).filter((x) => x != null);
    return v.length ? mean(v) : null;
  });
  const at = sigWeakest(together).at * 10;
  return {
    pct: Math.round((mean(all) + mean(weak)) / 2 * 100),
    allRound: Math.round(mean(all) * 100),
    weakest: Math.round(mean(weak) * 100),
    weakAt: `${at}&ndash;${(at + SIG_SECTOR * 10) % 360 || 360}&deg;`,
  };
}

function sigQualityBar(q) {
  if (!q) return '';
  const cls = q.pct >= 90 ? 'good' : q.pct >= 60 ? 'fair' : 'poor';
  return `<div class="sig-quality ${cls}" title="Half all round, half the weakest 40°">
    <div class="sig-quality-label"><span>Quality</span><b>${q.pct}%</b></div>
    <div class="sig-quality-track"><div class="sig-quality-fill" style="width:${q.pct}%"></div></div>
    <div class="sig-quality-parts">All round ${q.allRound}% &middot; weakest 40&deg; ${q.weakest}%${q.weakest < 90 ? ` (${q.weakAt})` : ''}</div></div>`;
}

// sigAvgStats: figures for one tank's chosen runs, beside its chart.
function sigAvgStats(e, d) {
  const n = (v, dp = 0) => (+v).toFixed(dp);
  const median = (v) => {
    const s = [...v].sort((a, b) => a - b);
    return s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2;
  };
  const left = [e.skipped ? `${e.skipped} without angles` : ''].filter(Boolean);
  const rows = [['Runs', `${d.used.length} of ${e.runs.length}${left.length ? ` (left out: ${left.join(', ')})` : ''}`]];
  if (d.used.length) {
    const first = d.used[0].started, last = d.used[d.used.length - 1].started;
    rows.push([d.used.length > 1 ? 'Dates' : 'Date', esc(d.used.length > 1 ? `${sigWhen(first)} – ${sigWhen(last)}` : sigWhen(first))]);
    const fps = d.avg.map((b) => b.fps).filter((v) => v != null);
    if (fps.length) {
      let wi = -1;
      d.worst.forEach((b, i) => { if (b.fps != null && (wi < 0 || b.fps < d.worst[wi].fps)) wi = i; });
      rows.push(['Typical video', `${n(median(fps), 1)} fps`]);
      rows.push(['Worst', `${n(d.worst[wi].fps, 1)} fps at ${wi * 10}&ndash;${wi * 10 + 10}&deg;`]);
      rows.push(['Under 20 fps', sigAngleRanges(d.avg.map((b) => b.fps != null && b.fps < 20))]);
      if (d.used.length > 1) {
        const spread = d.avg.map((b, i) => (b.fps == null ? null : (d.best[i].fps - d.worst[i].fps) / 2)).filter((v) => v != null);
        rows.push(['Run to run', `&plusmn;${n(spread.reduce((a, c) => a + c, 0) / spread.length, 1)} fps`]);
      }
    }
    const gaps = d.avg.map((b) => b.gapMs).filter((v) => v != null);
    const longest = Math.max(...d.worst.map((b) => b.gapMs).filter((v) => v != null));
    if (gaps.length) rows.push(['Frame waits', `${n(median(gaps))} ms typical, ${n(longest)} ms longest`]);
    const dbm = d.avg.map((b) => b.dbm).filter((v) => v != null);
    if (dbm.length) rows.push(['Router signal', `${n(Math.min(...dbm))} to ${n(Math.max(...dbm))} dBm`]);
  }
  return `${sigQualityBar(d.used.length ? sigAvgQuality(d) : null)}
    <dl class="sig-stats">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl>`;
}

function sigAveragesHTML(m) {
  if (!(sigHistory || []).length) return '';
  const tanks = sigAvgTanks();
  const tiles = tanks.map((e) => {
    // From and To: lists of the tank's runs, each with its note.
    const { from, to } = sigAvgSpan(e);
    const firstIn = e.runs.find((x) => x.started >= from) || e.runs[0];
    const lastIn = [...e.runs].reverse().find((x) => x.started <= to) || e.runs[e.runs.length - 1];
    const date = (end, label, chosen) => `<label>${label}<select data-action="sig-avg" data-tank="${esc(e.id)}" data-end="${end}">
        ${e.runs.map((x) => `<option value="${x.started}" ${x === chosen ? 'selected' : ''}>${
          esc(sigWhen(x.started) + (x.comment ? ' · ' + x.comment : ''))}</option>`).join('')}
      </select></label>`;
    const d = sigAvgData(e);
    return `<div class="sig-avg" data-tank="${esc(e.id)}">
      <div class="sig-avg-chart">${sigAvgChart(e, m, d)}</div>
      <div class="sig-avg-side">
        <div class="sig-avg-stats">${sigAvgStats(e, d)}</div>
        <div class="sig-avg-dates">${date('from', 'From', firstIn)}${date('to', 'To', lastIn)}</div>
      </div>
    </div>`;
  }).join('');
  const kind = sigAvgKind();
  return `<div class="card sig-avg-card"><h3>Average of saved runs</h3>
    ${seg('sigavgkind', SIG_KINDS, kind)}
    ${seg('sigavgair', SIG_AIRS, sigAvgAir())}
    <p class="hint">Each tank's ${kind === 'drive' ? 'driving' : 'turret-only'} runs between the two dates beside its chart, both directions together:
    <span class="sig-key avg">average</span>, <span class="sig-key worst">worst</span> and <span class="sig-key best">best</span> at each angle.
    Runs without IR angle marks are left out. Shows the measure chosen above.
    <b>Quality</b>: how playable the video is. Each 10&deg; of each run scores by the worse of its frame rate (24&nbsp;fps and up = full marks, 12 or less = none)
    and its longest wait between frames (100&nbsp;ms or less = full, 400&nbsp;ms or more = none). Half the score is all round the circle, half is the
    weakest 40&deg;, where a turret can sit for a whole game. Green from 90%, amber from 60%.</p>
    ${tiles ? `<div class="sig-avg-list">${tiles}</div>` : emptyNote('No runs of this kind yet.')}</div>`;
}

// A From or To run was picked: redraw that tank's chart and figures now.
// Picking its first (From) or last (To) run leaves that end open, so later
// runs join in.
function sigAvgChanged(id, end, value) {
  const rg = sigAvgRange(id);
  const e = sigAvgTanks().find((x) => x.id === id);
  const edge = e && (end === 'from' ? e.runs[0] : e.runs[e.runs.length - 1]);
  if (value && !(edge && String(edge.started) === value)) rg[end] = value;
  else delete rg[end];
  store('tg_sig_avg_' + id, JSON.stringify(rg));
  const tile = document.querySelector(`.sig-avg[data-tank="${CSS.escape(id)}"]`);
  if (e && tile) {
    const metric = segChoice('sigmetric', SIG_METRICS.map((x) => [x.id, x.label]));
    const d = sigAvgData(e);
    tile.querySelector('.sig-avg-chart').innerHTML = sigAvgChart(e, SIG_METRICS.find((x) => x.id === metric), d);
    tile.querySelector('.sig-avg-stats').innerHTML = sigAvgStats(e, d);
  }
}

// sigCommentBox: the host's comment on a run, saved when it changes.
function sigCommentBox(run, comment, label) {
  return `<label class="sig-comment-label">${esc(label)}<input class="sig-comment" type="text" maxlength="300"
    data-action="sig-comment" data-run="${esc(run)}" placeholder="What changed? e.g. bigger antennas" value="${esc(comment || '')}"></label>`;
}

// A comment was changed: saved on the hub; shown straight away here.
function sigCommentChanged(run, text) {
  text = text.trim();
  send({ t: 'host_sigtest_comment', id: run, text });
  if (sigData && sigData.run === run) sigData.comment = text;
  if (sigCompareData[run]) sigCompareData[run].comment = text;
  const r = (sigRuns || []).find((x) => x.file === run);
  if (r) r.comment = text;
  toast(text ? 'Comment saved' : 'Comment removed');
  sigRefresh();
}

function sigWhen(ms) {
  if (!ms) return '';
  const t = new Date(ms);
  const day = t.toDateString() === new Date().toDateString() ? '' : t.toLocaleDateString([], { day: 'numeric', month: 'short' }) + ' ';
  return day + t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

// sigChart: one tank's metric round the turret, 10° bins; before = the same
// tank in the run being compared with, drawn underneath. A pass with cls is
// drawn in that style (the averages' worst, best, avg); opts.edge outlines
// the chart.
function sigChart(t, m, before, opts = {}) {
  const S = 180, c = S / 2, R = 72;
  const radius = (v) => {
    let f = (v - m.lo) / (m.hi - m.lo);
    f = Math.max(0, Math.min(1, f));
    return R * Math.pow(m.invert ? 1 - f : f, SIG_CURVE);
  };
  const pt = (deg, r) => {
    const a = deg * Math.PI / 180;
    return [(c + r * Math.sin(a)).toFixed(1), (c - r * Math.cos(a)).toFixed(1)];
  };
  let grid = '';
  m.rings.forEach((label) => {
    const r = radius(label);
    grid += `<circle cx="${c}" cy="${c}" r="${r}" class="sig-ring"/>`;
    grid += `<text x="${c + 3}" y="${(c - r + 9).toFixed(1)}" class="sig-ring-label">${label}</text>`;
  });
  for (let deg = 0; deg < 360; deg += 90) {
    const [x, y] = pt(deg, R);
    grid += `<line x1="${c}" y1="${c}" x2="${x}" y2="${y}" class="sig-spoke"/>`;
  }
  const lines = (passes, extra) => (passes || []).map((p) => {
    const bins = p.bins || [];
    let d = '', open = false, all = true;
    bins.forEach((b, i) => {
      const v = b[m.key];
      if (v == null) { open = false; all = false; return; }
      const [x, y] = pt(i * 10 + 5, radius(v));
      d += `${open ? 'L' : 'M'}${x},${y} `;
      open = true;
    });
    if (all && d) d += 'Z';
    return d ? `<path d="${d}" class="sig-line ${extra}${p.cls || (p.dir < 0 ? 'back' : 'fwd')}"/>` : '';
  }).join('');
  const ref = t.ref === 'ok' ? '' : ` <span class="sig-warn">${t.ref === 'estimated' ? 'angles estimated' : 'no angles'}</span>`;
  return `<figure class="sig-chart"><svg viewBox="0 0 ${S} ${S}" role="img" aria-label="${esc(t.name)}: ${esc(m.label)} by turret angle">
      ${grid}${opts.edge ? `<circle cx="${c}" cy="${c}" r="${R}" class="sig-edge"/>` : ''}<text x="${c}" y="10" class="sig-fwd">FWD</text>${before ? lines(before.passes, 'before ') : ''}${lines(t.passes, '')}</svg>
    <figcaption><b>${esc(t.name)}</b>${t.partner ? ` &harr; ${esc(t.partner)}` : ''}${ref}${
      t.warmingUp ? ` <span class="sig-warn">on ${Math.max(1, Math.round(t.upS / 60))} min</span>` : ''}</figcaption></figure>`;
}

function sigTable(tanks, before) {
  const n = (v, dp = 0) => (v == null ? '&ndash;' : (+v).toFixed(dp));
  const worst = (t) => t.worstFps == null ? '&ndash;' : `${n(t.worstFps, 1)} at ${t.worstAt}&ndash;${t.worstAt + 10}&deg;`;
  const was = (b, html) => b ? ` <span class="sig-was">was ${html}</span>` : '';
  const rows = tanks.map((t) => {
    const b = before(t);
    return `<tr><td>${esc(t.name)}</td>
    <td>${n(t.medFps)}${was(b, b && n(b.medFps))}</td>
    <td>${worst(t)}${was(b, b && worst(b))}</td>
    <td>${t.dbmMin == null ? '&ndash;' : `${n(t.dbmMin)} to ${n(t.dbmMax)}`}</td>
    <td>${(t.passes || []).filter((p) => p.turnS > 0).map((p) => n(p.turnS, 1)).join(' / ') || '&ndash;'}</td>
    <td>${esc({ ok: 'IR marks', estimated: 'estimated', none: 'none' }[t.ref] || t.ref)}</td></tr>`;
  }).join('');
  return `<div class="sig-table-wrap"><table class="sig-table">
    <thead><tr><th>Tank</th><th>Typical fps</th><th>Worst fps</th><th>Router dBm</th><th>s per turn (each way)</th><th>Angles</th></tr></thead>
    <tbody>${rows}</tbody></table></div>`;
}
