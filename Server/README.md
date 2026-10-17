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
| `client.go` | Phone pages' WebSocket (`/ws`): requests in, state snapshots out |
| `tanks.go` | Tank discovery, control WebSocket (`/esp32`), command queue, video pull |
| `video.go`, `stats.go` | Video relay to viewers; frame-rate and CPU stats |
| `settings.go` | Host settings, saved to `settings.json` |
| `web/index.html`, `app.js`, `app.css` | The phone app (players and host) |
| `web/streams.html` | Video test page |
| `cmd/loadtest` | Fake tanks and viewers for load tests |

Host password default: `thundergeddon` (change it in the host's Game
settings). Settings are saved on the router in
`/etc/thundergeddon/settings.json`.

Not built yet: hit detection and damage, capture points, pause, presets,
tank tuning settings, 2-player tanks, the spectator display.

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
"phone" in each browser tab.

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
