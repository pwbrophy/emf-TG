# Thundergeddon server (Go)

The new game server, written in Go so it can run on the GL-MT3000 router as
well as on a PC. It replaces the Unity server and the ASP.NET web server:
tanks connect to it (same protocol, same firmware), it relays their video,
and phones play the game through one web page. The UI flow it follows is
`Docs/ui-flow.drawio`.

| | Unity / ASP.NET (old) | This server |
|---|---|---|
| Folder | `Unity/`, `Webserver/` | `Server/` |
| Ports | 5000, 8080, 8081, 8082, UDP 30560 | 8088, UDP 30560 |

Both answer the tanks' discovery broadcast on UDP 30560, so **don't run Unity
on the Thundergeddon network while this server is on the router**, or the two
fight over the tanks. Stop it with
`ssh root@192.168.8.1 "/etc/init.d/thundergeddon stop"`.

The web pages live in `web/` and are built into the program, so after
editing them you rebuild (`run.ps1` / `deploy.ps1` do that).

### What's in it

| File | Role |
|---|---|
| `game.go` | The game: host, queue, lobby, tank choice, countdown, play, game over |
| `ir.go` | The IR shot handshake (left barrel, ack, listen, right barrel, listen) |
| `combat.go` | HP, directional damage, death, return to base, respawn, invulnerability, capture points, team points, winner, stats; crews (driver + gunner) |
| `hosttools.go` | Host tools: pause, test hits, heal/revive, capture points by hand, each tank's flips/reversed controls/name/team, ping, turret test; Advanced tuning pushed to tanks; presets (`presets.json`) |
| `stabilise.go` | Turret stabilisation (the STAB button): works out the hull's turn rate from the drive commands (firmware ramp + watchdog, spin-up/down inertia) and turns the turret against it; the player's turret speed adds on top. Tuned in Advanced → Stabiliser (`Tuning.Stab*`) |
| `beacons.go` | Capture-point LED beacons (`/beacon`): owner colours, capture and score ripples |
| `display.go` | The spectator display's feed and the announcer lines (from Unity) |
| `fake.go` | Pretend IR for `-fake-tanks`, and `POST /api/dev/rfid` to pretend a tag scan |
| `client.go` | Phone pages' WebSocket (`/ws`): requests in, state snapshots out |
| `tanks.go` | Tank discovery, control WebSocket (`/esp32`), command queue, video pull |
| `video.go`, `stats.go` | Video relay to viewers; frame-rate and CPU stats |
| `settings.go` | Host settings, saved to `settings.json` |
| `web/index.html`, `app.js`, `app.css` | The phone app: menu, join, queue, lobby, host console. Every screen: top bar, tabs, content, bottom bar with Back on the left and Next/Start on the right. Host settings save as soon as they change |
| `web/display.html` | The spectator display for a big screen (the Unity-era page with its connection swapped for the Go server's) |
| `web/play.html` | The in-game screen: the Unity-era player page kept as it was (controls, HUD, effects, keyboard/gamepad), with `TGHub` standing in for its SignalR connection. Players are sent here when their game starts; after it, it shows the game over sequence (victory/defeat, then Match Stats with Play Again and Visit Thundergeddon.com). Play Again goes back to the main page, which queues them for the next game |
| `web/streams.html` | Video test page |
| `web/test-grid.html` | Test grid: six player pages on one screen (each its own pretend phone), with Connect All / Choose Tanks / New Names / Reload All. `?players=N` for 1-12 |
| `cmd/loadtest` | Fake tanks and viewers for load tests |

Players' link: `http://<hub>:8088/join` goes straight to Name Entry (no
title or menu), e.g. from a QR code at a festival.

In the Client Lobby players choose an alliance and are given one of its free
tanks (all tanks are the same). Each tank with a player glows a colour of
its own (red, green, blue…) until the game starts, and the player's phone
says which, as in the Unity version (`lobby.go`).

Tank cameras only stream while their tank is in a game (countdown to game
over, not while paused), as in the Unity version: less Wi-Fi traffic and
heat. Server Config → Advanced → Video has "Keep cameras on between games"
for the video test page and load tests.

No host password for now: tapping Host Game makes that phone the host
(taking over from another phone after a check). Players can be asked for a
game password, set in Server Settings. Settings are saved on the router in
`/etc/thundergeddon/settings.json`. The host pages follow
`Docs/ui-flow.drawio`: Server Settings → Server Lobby (Robots, Players,
Client Lobby, Queue) → Server Gameplay Control → Game Over; everything else
is under Server Config (the sliders button).

Combat follows the Unity version: 25 damage a hit (rear ×3, side ×1.5), a
5 s explosion on death, then "return to base" until the tank scans its own
alliance's base tag, 25 points a kill and 300 to win. "Alliance" is a
side; the host can add, remove, rename and recolour alliances; a tank is in
the alliance its name starts with (Desert-01 → Desert Squad) unless the host
chooses another.

Turn-based game (Server Settings → Game: Turn-based; `turns.go`), in the
style of X-COM. Each player is an army in an alliance and claims tanks of it
in the lobby (one at first, more by tapping free ones; the host can move
tanks between armies). Alliances take turns, the first at random; all of an
alliance's armies play their turn at once, each player switching between
their own tanks. Every tank gets action points at the start of its turn:
driving spends them per second (metered on the hub from the drive commands,
with the tank's 500 ms watchdog), the turret too if the host wants, a shot a
fixed amount. A turn ends when every army taps END TURN (or can't act) or
its time runs out, then a short handover. Overwatch (host toggle): a tank
keeps its points and, during enemy turns, sends silent IR probes while
enemies move; the first moving enemy it hits takes one shot. Destroyed tanks
are out for the game. Once per turn a tank can spend points to recharge part
of its shield (default 20 % for 5 AP). During its turn a tank's LEDs show its
action points in green instead of its HP in blue. An alliance wins by being the last with tanks, by
points (kills, and capture points held at the start of each of its turns),
or on points at the round limit. The turn-based game has its own copies of
the game rules (`Settings.Turns`: HP, damage, cooldown, points...), so the
two games never share a value; only one game's settings are shown at a time.

RFID tags: every tag a tank reads is listed in Server Config → Tags and
Capture Points, where the host makes it an alliance's base or a capture
point. The defaults are the tags in the Unity scene.

Capture points (host can add, remove and rename them): driving over a
point's tag takes it; an alliance scores one point every 3 s per point held
divided by the points it holds (Unity's rule). There are three LED beacons
(built as North, Centre, South); each capture point can be shown on one.
They find the server by themselves and show the owner's colour. The
spectator display's points bar shows the first two alliances.

2-player tanks (host setting): a driver can let a second player join as
gunner. The driver drives, the gunner turns the turret and fires; kills go
to the gunner, deaths to both.

Spectator display: open `/display.html` on a laptop or TV.

## Run on this PC

```powershell
cd Server
.\run.ps1          # then open http://localhost:8088
.\run.ps1 -Lan     # also reachable from phones and tanks on the same network
```

Without tanks: `go run . -addr 127.0.0.1:8090 -discovery-port 0 -fake-tanks 3`
adds three pretend tanks, and `go run ./cmd/loadtest -server
http://127.0.0.1:8090 -tanks 3 -phones 0 -display-fps 0 -hold 30m` gives
them video. Add `?device=someone` to the page address to be a different
"phone" in each browser tab. `-web-dir web` serves the pages straight from
the folder, so page edits show on reload without rebuilding.

## Put it on the router

No USB cable: everything goes over the network.

### One-time setup

1. Connect this PC to the router's network, either on its Wi-Fi or with an
   Ethernet cable in its **LAN** port.
2. Make sure you can log in. It asks for the router's admin password, and the
   first time asks you to confirm the router's fingerprint (type `yes`):
   ```powershell
   ssh root@192.168.8.1 "echo hello from the router"
   ```
3. Install this PC's SSH key so scripts can log in without the password:
   ```powershell
   Get-Content $env:USERPROFILE\.ssh\id_rsa.pub | ssh root@192.168.8.1 "tr -d '\r' >> /etc/dropbear/authorized_keys && chmod 600 /etc/dropbear/authorized_keys"
   ```

### Every time

```powershell
cd Server
.\deploy.ps1
```

It builds for the router, uploads, installs to `/opt/thundergeddon`,
and restarts. The server starts automatically when the router boots and
restarts itself if it crashes. Open `http://192.168.8.1:8088` from any device
on the router's network. Settings → Server shows `linux/arm64` when the page
came from the router.

### Logs and control

```powershell
ssh root@192.168.8.1 "logread -e thundergeddon | tail -n 50"   # recent log
ssh root@192.168.8.1 "logread -f -e thundergeddon"             # live log (Ctrl+C to stop)
ssh root@192.168.8.1 "/etc/init.d/thundergeddon stop"          # also: start, restart
```

## Logs: looking back on a session of games

The router's own log holds only 64 KB (a few minutes of a game), so the hub
keeps three logs of its own in the temp folder (`/tmp` on the router: RAM, so
gone after the router reboots, but kept over a hub restart or deploy). Each is
capped and keeps one older file (`.1`), so all three together stay under
30 MB of the router's 240 MB.

| File | What | Cap | Alone |
|---|---|---|---|
| `thundergeddon-events.jsonl` | One JSON line per thing that happened (below) | 4 MB × 2 | `/api/events` |
| `thundergeddon-server.log` | Everything the hub printed | 3 MB × 2 | `/api/server-log` |
| `thundergeddon-video.csv` | Every tank's video and Wi-Fi figures every 2 s (`videolog.go`); the time column has the date from 11 Nov 2026 (older rows have the time of day only, and the log runs over several days) | 8 MB × 2 | `/api/video-log` |

(`?old=1` on those addresses gives the older file.)

**Fetch everything in a break:**

```powershell
cd Server
.\fetch-logs.ps1                       # → Logs\<date_time>\ at the top of the project
.\fetch-logs.ps1 -Label "after game 6"
python logsummary.py                   # summary of the newest fetch: hub, each game, each tank, what's worth a look
```

That is one download, `http://192.168.8.1:8088/api/logs.zip`: the three logs,
the hub's state at that moment (`info.json`, `stats.json`, `tanks.json`,
`settings.json` without the game password), the router's own log
(`router-system-log.txt`: Wi-Fi driver and kernel lines) and, if the hub ever
crashed, its last words (`crash-previous.txt`). Fetch as often as you like;
nothing is cleared.

**Events** (`{"t": hub time, "ev": kind, ...}`):

| `ev` | When | Useful fields |
|---|---|---|
| `hub_start` | The hub started (more than one in a session = a deploy or a crash) | `version` |
| `hub` | Every 30 s | `rss_mb`, `mem_avail_mb`, `tmp_free_mb`, `cpu_pct`, `goroutines`, `go_heap_mb`, `phase`, `pages`, `players`, `tanks` |
| `tank` | Every 30 s, per connected tank | lowest free memory `heap_low_kb` and largest free block `blk_low_kb` in those 30 s, `rssi_low`, `temp_c`, `up_s`, `hb_gap_max_ms` (heartbeats are 2 s apart), `ping_ms` / `ping_max_ms` / `pings_lost` (the hub pings each tank every 5 s: the command path there and back), `cmds_dropped`; newer firmware: `loop_max_ms`, `heap_ever_low_kb`, `hub_connects`, `wifi_drops`, `ir_barrel_left_on` |
| `tank_connect` / `tank_disconnect` | A tank joined or dropped | `reset` (why it last started), `up_s`, `fw`, `crash`; `why`, `connected_s`, `silent_ms` |
| `camera_start`, `video_end` | A camera started; a video stream broke and was reopened | `ok`, `tries`; `frames`, `why` |
| `game_start` / `play_begin` / `game_end` | A game | who is on which tank, the rules; `code`, `winner`, `points`, `results`, each tank's end state, and `shots` (the game's tally) |
| `shot` | Every shot | `result` (below), `queued_ms`, `ack_ms`, `left_ms`, `right_ms`, `total_ms`, `saw_left` / `saw_right` (tank → sensor mask), `silent` (tanks that didn't answer in time), `offline`, `hits` |
| `kill`, `respawn`, `capture`, `rfid`, `host_hit` | Combat | |
| `pause`, `resume`, `start_check`, `host_tank_restart` | The host | |
| `phone_connect` / `phone_disconnect` | A player's page | `too_slow` = dropped by the hub for not keeping up |
| `phone` | Every 30 s from each phone in a game | `shown_fps`, `recv_fps`, `decode_ms`, `page_fps`, `rtt_ms` / `rtt_max_ms` (the phone's link to the hub, there and back), `device` |
| `player_join` / `player_leave` | | |
| `video_freeze` | A playing camera sent no frame for a second or more (written when it recovers) | `longest_wait_ms`, `hb_quiet_ms` (how long the tank's heartbeats went quiet too: about 2 s = only the video stalled, more = the whole Wi-Fi link stalled), `rssi`, `send_ms_max`, `cam_misses`, `drive_pct`, `turret_pct` |
| `panic` | A step of the hub's clock, shot or stabiliser loop failed (it carries on; the stack is in the hub log) | `where`, `error` |

A shot's `result`: `hit`; `miss` (no enemy saw the left barrel); `one_barrel`
(seen, but nobody saw both); `no_effect` (both seen, but the tank was dead or
couldn't be hurt); `no_ack` (the shooter's tank never said its barrel was
lit); `shooter_offline`; `stale` / `queue_full` (waited too long behind other
shots: "Miss (lag)"); `too_soon` (arrived before the reload time was up).

`-log-dir <folder>` keeps the event and hub logs somewhere else; `-log-dir off`
switches them off.

## Soak test (many games in a row)

`cmd/soak` plays game after game with bot players and a bot host against a hub
running pretend tanks, to catch what only goes wrong after many games: leaks,
players stuck between games, state left over from the game before. **Never
point it at a hub with real tanks connected: the bots drive.**

```powershell
cd Server
# terminal 1: a hub with pretend tanks and its own settings and logs
go run . -addr 127.0.0.1:8099 -discovery-port 0 -fake-tanks 6 -wifi-iface "" -settings soak/settings.json -log-dir soak -video-log soak/video.csv
# terminal 2
go run ./cmd/soak -server 127.0.0.1:8099 -players 6 -games 30
```

Then look at the `hub` lines in `soak/thundergeddon-events.jsonl`:
`goroutines` and `go_heap_mb` should be the same after the last game as after
the first.

## Video load test (fake tanks)

`cmd/loadtest` pretends to be a fleet of tanks plus the phones and display
watching them. Fake tanks push camera-sized MJPEG streams to the server
(`POST /api/ingest/{id}`), fake viewers pull them back (`GET /api/stream/{id}`),
and it prints frame rates, delay through the server, and the router's CPU and
memory (from `GET /api/stats`).

```powershell
cd Server
go run ./cmd/loadtest                                   # 10 tanks at 20 fps, 11 KB frames
go run ./cmd/loadtest -tanks 20 -fps 25 -kb 15          # heavier than real
go run ./cmd/loadtest -server http://localhost:8088     # against the PC copy
```

While it runs, open `http://192.168.8.1:8088/streams.html` on a phone on the
router's Wi-Fi (or Settings → Video load test) to watch the relayed video and
see the frame rate that phone gets.

First results (2026-10-17, PC on the router's LAN cable, so this measures the
router's processing, not Wi-Fi):

| Load | Video in / out | Frame rate | Delay p95 | Router CPU |
|---|---|---|---|---|
| 10 tanks, 20 fps, 11 KB (real) | 18 / 32 Mbit/s | all 20 fps | 6 ms | 15% |
| 20 tanks, 25 fps, 15 KB (3x) | 63 / 100 Mbit/s | all 25 fps | 14 ms | 38% |

### Remove it from the router

```powershell
ssh root@192.168.8.1 "/etc/init.d/thundergeddon stop; /etc/init.d/thundergeddon disable; rm -rf /opt/thundergeddon /etc/init.d/thundergeddon"
```
