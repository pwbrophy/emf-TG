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
| `beacons.go` | Capture-point LED beacons (`/beacon`): owner colours, capture and score ripples |
| `display.go` | The spectator display's feed and the announcer lines (from Unity) |
| `fake.go` | Pretend IR for `-fake-tanks`, and `POST /api/dev/rfid` to pretend a tag scan |
| `client.go` | Phone pages' WebSocket (`/ws`): requests in, state snapshots out |
| `tanks.go` | Tank discovery, control WebSocket (`/esp32`), command queue, video pull |
| `video.go`, `stats.go` | Video relay to viewers; frame-rate and CPU stats |
| `settings.go` | Host settings, saved to `settings.json` |
| `web/index.html`, `app.js`, `app.css` | The phone app: menu, join, queue, lobby, results, host console. Every screen: top bar, tabs, content, bottom bar with Back on the left and Next/Start on the right. Host settings save as soon as they change |
| `web/display.html` | The spectator display for a big screen (the Unity-era page with its connection swapped for the Go server's) |
| `web/play.html` | The in-game screen: the Unity-era player page kept as it was (controls, HUD, effects, keyboard/gamepad), with `TGHub` standing in for its SignalR connection. Players are sent here when their game starts |
| `web/streams.html` | Video test page |
| `web/test-grid.html` | Test grid: six player pages on one screen (each its own pretend phone), with Connect All / Choose Tanks / New Names / Reload All. `?players=N` for 1-12 |
| `cmd/loadtest` | Fake tanks and viewers for load tests |

Players' link: `http://<hub>:8088/join` goes straight to Name Entry (no
title or menu), e.g. from a QR code at a festival.

No host password for now: tapping Host Game makes that phone the host
(taking over from another phone after a check). Players can be asked for a
game password, set in Server Settings. Settings are saved on the router in
`/etc/thundergeddon/settings.json`. The host pages follow
`Docs/ui-flow.drawio`: Server Settings → Server Lobby (Robots, Players,
Client Lobby, Queue) → Server Gameplay Control → Game Over; everything else
is under Server Config (the sliders button).

Combat follows the Unity version: 25 damage a hit (rear ×3, side ×1.5), a
5 s explosion on death, then "return to base" until the tank scans its own
alliance's base tag, 25 points a kill and 300 to win. "Alliance" is the
realtime game's side (the planned turn-based mode can have several teams in
an alliance). The host can add, remove, rename and recolour alliances; a
tank is in the alliance its name starts with (Desert-01 → Desert Squad)
unless the host chooses another.

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
