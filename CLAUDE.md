# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Repository

GitHub: **https://github.com/pwbrophy/emf-TG** (branch: `master`)

---

## Working with Claude Code — Required Practices

### Branches, worktrees, and how they interact with the server and OTA

Each Claude Code session works on a dedicated branch (`claude/<random-name>`) inside a git worktree at `.claude/worktrees/<name>/`. This means the session's file edits exist **only in that worktree directory** — the main project tree (`F:\Data\Thundergeddon\EMF_Project\`) stays on `master` and is untouched.

**Critical consequence for the Go server and OTA:** both build from whichever directory you run them in.
- **Go server:** `Server/run.ps1` and `Server/deploy.ps1` build the `Server/` folder they are run from.
- **PlatformIO OTA** compiles from `ESP32/thundergeddon/src/` in whatever directory `pio run` is invoked from. If you run OTA from the worktree path, it compiles worktree sources. If you run from the main project path, it compiles master. **Always run OTA from the main project path after merging**, or explicitly `cd` into the worktree path when testing pre-merge.

**Workflow:**
1. Make all code changes inside the worktree (Claude does this automatically).
2. After Go changes, run `go test ./...` in `Server/`. Run OTA from the worktree path to test firmware pre-merge.
3. Commit changes on the `claude/<name>` branch, then merge to `master` (or raise a PR).
4. After merge, builds from the main tree pick up the changes.
5. Old `claude/` branches and their worktree directories can be deleted once their commits are reachable from master. Use `git log --oneline <branch> ^master` to verify before deleting.

**Always commit and merge to master at the end of every session.** The user cannot see server, web page or firmware changes until they are on master. Never leave work only in the worktree branch — always finish by committing, merging to master, and confirming the merge succeeded. If the main tree has an unstaged copy of a file, run `git restore <file>` in the main tree before merging.

### ESP32 firmware: always upload via OTA
Never ask the user to connect a USB cable. After every firmware change, upload using the `thundergeddon_ota` PlatformIO environment, which pushes over Wi-Fi via ArduinoOTA:

```
cd "F:\Data\Thundergeddon\EMF_Project\ESP32\thundergeddon"
pio run -e thundergeddon_ota --target upload
```

The robot must be powered on and connected to Wi-Fi for OTA to work. The `platformio.ini` `[env:thundergeddon_ota]` section is configured with:
- `upload_protocol = espota`
- `upload_port = thunder-9CF218697090.local` (mDNS hostname — may need replacing with the robot's IP if mDNS doesn't resolve on Windows; see the IP table below or serial output)
- `--auth=${secrets.ota_password}` (from `secrets.ini` — see **Secrets** below; NEVER commit the actual password, this repo is public)
- `--port=3232`

**Secrets (never commit):** three gitignored files hold the shared credentials and must exist on every machine (desktop + laptop) — copy from their `.template` siblings on a fresh clone:
- `ESP32/thundergeddon/src/secrets.h` — Wi-Fi creds, `OTA_PASSWORD`, `WS_HELLO_TOKEN` (baked into firmware)
- `ESP32/thundergeddon/secrets.ini` — `ota_password` used by `pio run --target upload` and `flash_all.ps1`; must equal `OTA_PASSWORD` in secrets.h
- `Server/ws_token.txt` — must equal `WS_HELLO_TOKEN` in secrets.h; the Go server refuses robot `hello` messages without the matching `tok` field (and accepts every tank if it has no token file). `Server/run.ps1` uses it directly and `Server/deploy.ps1` uploads it to the router

To rotate the OTA password: change it in `secrets.h`, flash the fleet with the OLD password via `.\flash_all.ps1 -Auth <old-password>`, then update `secrets.ini` to the new value.

A successful upload ends with `Result: OK` / `Success`. If the hostname doesn't resolve, substitute the robot's current IP directly, e.g. `pio run -e thundergeddon_ota --target upload --upload-port 192.168.x.x`.

**Robot IPs by network:**

| Robot | Name | MAC | BFG (desktop, 192.168.86.x) | TG (laptop, 192.168.8.x) |
|-------|------|-----|------------------------------|--------------------------|
| Laptop | HP | D8:C0:A6:D9:3E:89 | — | 192.168.8.100 |
| 1 | Desert-01 | 90:70:69:18:F2:9C | 192.168.86.101 | 192.168.8.101 |
| 2 | Desert-02 | 90:70:69:18:F2:A8 | 192.168.86.102 | 192.168.8.102 |
| 3 | Desert-03 | 90:70:69:18:F2:78 | 192.168.86.103 | 192.168.8.103 |
| 4 | Desert-04 | 90:70:69:18:F2:A0 | 192.168.86.104 | 192.168.8.104 |
| 5 | Desert-05 | 90:70:69:18:F2:94 | 192.168.86.105 | 192.168.8.105 |
| 6 | Jungle-06 | 90:70:69:18:F2:2C | 192.168.86.106 | 192.168.8.106 |
| 7 | Jungle-07 | 90:70:69:18:F2:A4 | 192.168.86.107 | 192.168.8.107 |
| 8 | Jungle-08 | 90:70:69:09:B6:D0 | 192.168.86.108 | 192.168.8.108 |
| 9 | Jungle-09 | 90:70:69:18:F2:64 | 192.168.86.109 | 192.168.8.109 |
| 10 | Jungle-10 | 90:70:69:09:B6:50 | 192.168.86.110 | 192.168.8.110 |

To flash all robots simultaneously, run `flash_all.ps1` from `ESP32/thundergeddon/` — it builds once then calls `espota.py` in parallel for every robot. Use `-Network TG` when on the Thundergeddon network (laptop), default is BFG (desktop). Do **not** run multiple `pio run --target upload` in parallel; they share the scons build database and conflict.

```
cd "F:\Data\Thundergeddon\EMF_Project\ESP32\thundergeddon"
.\flash_all.ps1                      # all 10 robots, BFG network (desktop)
.\flash_all.ps1 -Network TG          # all 10 robots, Thundergeddon network (laptop)
.\flash_all.ps1 1,2,7,8              # subset, BFG network
.\flash_all.ps1 1,2,7,8 -Network TG  # subset, Thundergeddon network
```

**Per-robot OTA envs** (single robot) — `_r1.._r10` = BFG network, `_tg_r1.._tg_r10` = Thundergeddon network:
```
pio run -e thundergeddon_ota_r1     --target upload   # BFG  192.168.86.101 Desert-01
pio run -e thundergeddon_ota_tg_r1  --target upload   # TG   192.168.8.101  Desert-01
...
pio run -e thundergeddon_ota_r10    --target upload   # BFG  192.168.86.110 Jungle-10
pio run -e thundergeddon_ota_tg_r10 --target upload   # TG   192.168.8.110  Jungle-10
```

---

## Project Overview

**Thundergeddon** is a hybrid physical/digital RC tank battle game for EMF Festival. Small robotic tanks fight in the real world while software handles game logic, player connections, and robot control.

The system has two components:

| Component | Location | Language | Role |
|-----------|----------|----------|------|
| Go server | `Server/` | Go + JS | Authoritative server on the GL-MT3000 hub: game state, hit logic, video relay, host console, phone web pages |
| ESP32 firmware | `ESP32/` | C++ | Robot controller: motors, camera, IR, LEDs, comms |

**Retired, don't work here:** `Unity/EMF_Server/` (the old server, see its `DEPRECATED.md`) and `Webserver/ThundergeddonWeb/` (the old ASP.NET phone client, which only worked through it).

### Go server (`Server/`)

The game server, written in Go so it runs on the **GL-MT3000 router** (linux/arm64) as well as on a PC. It speaks the tank protocol (UDP discovery, `/esp32` WebSocket, pulls `:81/stream` into a video relay) and runs the real-time player flow from `Docs/ui-flow.drawio`: join → queue → lobby (choose tank) → countdown → drive/turret/fire → game over → back of the queue, plus a host console (no host password for now: tapping Host Game makes that phone the host, taking over from any other; players can be asked for a game password set in Server Settings). Shooting: `Server/ir.go` (the ACK-driven IR handshake) and `Server/combat.go` (damage multipliers, death → return to base → RFID base respawn, invulnerability, kill points, winner, stats), with tests in `combat_test.go`. Sides are called **alliances** (not teams) in code and UI (in the turn-based game an alliance holds several armies); the host can add/remove/rename/recolour them (`Settings.Alliances`), and a tank joins the one its name starts with (Desert-01 → "desert") unless the host chooses. Capture points and RFID tags are host-editable lists too (`Settings.CapturePoints`, `Settings.RfidTags` with `use` = `base:<id>` / `point:<id>`); unknown tags a tank reads are added to the list. Host page names follow the flowchart (Server Settings, Server Lobby, Server Gameplay Control, Server Config). During a game (countdown or playing) the settings that only matter when joining or starting a game are greyed out on the host's pages and kept by the hub (`applySettings`): game password, players before queuing, 2-player tanks, match duration, start countdown, victory points to win; the Alliances, Tags and Capture Points and Signal Quality Test pages leave the Server Config menu and the hub refuses their changes (`listToolLocked` in `hosttools.go`). Everything else (damage, reload, tuning...) still changes at once. Players' link `/join` skips the title and menu. Players choose an alliance (not a tank) and get a free tank; tanks glow a lobby colour (`lobby.go`); cameras stream in the lobby (every tank) and during games (tanks playing), via `setStreaming`. Check on Start (`Server/health.go`): when the host taps Start, each chosen tank's last 20 s of video figures (no frames, low fps, freezes, slow sends compared with the other tanks, weak or much weaker Wi-Fi) can raise a confirm popup naming the tank, the problem and "restart it or check its antenna is tight"; the host can start anyway, and on Cancel it offers to restart the warned tanks. Signal Quality Test (`Server/signaltest.go`, Server Config → Signal Quality Test): finds turret angles where a tank's Wi-Fi breaks down (turret/hull board coupling; NS1020CA sheets under the turret board). Tanks stand in pairs hull front to hull front ~1.5 m apart (1↔6, 2↔7, 3↔8, 4↔9, 5↔10, by the number their name ends with), turrets forward; every connected turret turns `signalTest.turns` times at `signalTest.speed`, then back if `signalTest.reverse`, and stops facing forward. With `signalTest.drive` (motors test) the tanks stand lifted, tracks off the ground, and the tracks run a game-like pattern while the turrets turn (`Server/signaltest_drive.go`: full speed, creeping, arcs, spins, reversing, stops with eased changes, made from a fixed seed by a built-in splitmix64 so it is identical on every tank and every run; 180 s, then repeats; the run records `drivePattern` = `game-1`, change that name if the pattern changes); such runs are marked `drive` in the saved run, run list, history and CSV, labelled "driving" on the page, and the averages panel shows Turret only / With driving separately. Angle: the tanks take turns, one barrel lit in the whole room (`ir_emit_left`, wait for `ir_emit_ack`) while only its partner listens (`ir_listen_window` 80 ms), then `ir_emit_stop` and a `ping`/`pong` to prove it's off, so several pairs can run together even though they all see each other; the page and the saved run give how often each tank listened (`irEveryS`), and the averages keep runs with 3 or more tanks on air apart (busy channel); a hit on a listener's front sensors (N/NE/NW, mask 0x83) marks its partner's turret at 0°; beam middles are the marks, angles interpolate between them and extrapolate at the tank's own pace; a missed beam (gap ≈ 2 turns) is filled in. Measured per 10°: the router's view of the tank (`iwinfo ra0 assoclist` every 250 ms: signal, receive rate; `-wifi-iface`, only on the router) and video (frames per second reaching the hub, longest wait between frames, from the relay's per-frame recording). Cameras stream during the test; it can't run during a game and a game can't start during it. Each run records when every tank powered on (`startedAt`, from its hello's `up`; `tankInfo.StartedAt`); a tank on for less than `sigWarmUp` (5 min) is marked `warmingUp` (page: "on N min" and a note; a tank's first minutes are often worse, 27 Oct 2026). The averages include those runs (the user's choice, 29 Oct). Only finished runs are kept: a run stopped early isn't saved, and stopped runs older versions saved are deleted when the runs are listed. Results: `/api/sigtest` (JSON, polled by the page's radial charts; their radius is curved, `SIG_CURVE` = 2, so the good end near the edge gets more room), `/api/sigtest.csv`, and a raw JSON per run (`thundergeddon-sigtest-<time>.json`) kept on flash in a `sigtest` folder next to the settings file (`/etc/thundergeddon/sigtest` on the router; `-sigtest-dir`; the newest 200 kept (`sigKeepRuns`; also capped at 100 MB in all, `sigKeepBytes`), then the oldest go; older ones in the temp folder are copied there at start). After a restart the newest saved run is shown. `/api/sigtest/runs` lists saved runs and `?run=<file>` on `/api/sigtest` / `.csv` loads one (re-binned from the raw data); the page's **Compare with** picker draws an earlier run in grey under the current one, with "was" figures in the table. The host can comment on each run ("bigger antennas"...): `host_sigtest_comment` {id: run file, text}, kept in `comments.json` in the same folder and shown in the picker and the grey run's legend. Below the results, **Average of saved runs** draws a chart per tank (by name), outlined in white, with orange average, red worst and green best lines (both directions together) over its runs from a From run to a To run picked beside each chart (lists of that tank's runs with their comments; remembered per browser, `tg_sig_avg_<name>` as start times; its first/last run = open end, so new runs join in); runs without IR marks are left out; beside each chart, a quality bar (`sigAvgQuality`: each 10° of each run scores 0-1 by the worse of its fps (12→0, 24→1) and longest frame wait (400 ms→0, 100 ms→1); quality = half the all-round average, half the weakest 40° (`SIG_SECTOR`), both averaged over runs; green ≥90%, amber ≥60%, red below; also shows both parts and where the weak spot is) and figures for the chosen runs (`sigAvgStats`: runs used, dates, typical and worst fps, angles under 20 fps, run-to-run spread, frame waits, router signal). Data: `/api/sigtest/history` (every saved run, each tank's passes merged by `mergePasses`). While the page is open every camera streams (`host_sigtest_align` every 5 s, lease 15 s, ignored during games) and **Line up the turrets** shows each tank's feed (pair order, centre line) with hold-to-turn buttons: fast 0.6 and slow (the test's speed); a positive turret speed turns left. Hold-to-turn is ignored while the test runs. Fake tanks (`-fake-tanks 10`) simulate turning turrets and partner IR for trying it without hardware. Tank start-up reports (`Server/boot.go`): each hello says why and when the tank started (`rst`: power-on, software = OTA or Restart, brownout, crash...), how Wi-Fi connected (time, tries, channel, signal, access point) and its firmware build; `cam_status` says how its camera start went. Both are logged (system log and video log rows of kind `boot`/`camera`) and shown on the host's tank page, which also has a **Restart** button (`host_tank_restart` → firmware `reboot`). Also: capture points and LED beacons (`combat.go`, `beacons.go`), pause and host tank tools (`hosttools.go`), Advanced tuning + presets, 2-player crews, and the spectator display (`display.go` + `web/display.html`, the old page with a swapped connection). The phone app (`app.js`) uses one frame on every screen: tabs, Back bottom-left, Next/Start bottom-right; keep that consistent. Port **8088**; it answers tank discovery on UDP 30560, so **only run one copy on a network** (e.g. not `run.ps1 -Lan` while the router's copy is running) or they fight over the tanks. The in-game screen is `Server/web/play.html`, a copy of the old `Webserver/.../wwwroot/index.html` kept identical on purpose (the user wants the old controls and look exactly); only its connection layer changed (`TGHub` turns `/ws` state snapshots into the old SignalR events). Change the gameplay UI there, not in `app.js`. Go-server additions on it: a cog (top right, under the host's HOST button in one column `#tg-topright`) opens a SCREEN SIZE menu that zooms the HUD and controls via `--ui-scale` (40-100%, starting at 70% where the main pointer is a mouse/trackpad and 100% on phones, saved per browser as `tg_ui_scale`, RESET forgets it; the joystick/turret thumbs divide their offsets by `uiScale`), for players on a computer where the phone-sized HUD is huge. The same menu has SHOT EFFECTS (FULL / SIMPLE, `_simpleFx`, saved as `tg_fx`): simple is a plain fading white flash and a hard-edged white beam band (`updateSimpleBeam`, clip-path only: no glow, mask, blend or camera shake), chosen automatically on Android 6 or older or 1 GB of memory (an Android 5 Fire 7 stuttered on every shot). Controls inside the zoomed HUD are measured with `screenRect()`, because Chrome before 128 reports a zoomed element's box divided by its zoom. Video diagnostics: `Server/videolog.go` writes a CSV every 2 s per streaming tank (Wi-Fi signal, tank send times, fps into the hub, what each viewer got) plus each phone's own 30 s report, and `drive_pct`/`turret_pct` (share of the sample the tracks/turret were running, from the drive and turret commands the hub sent; tracks stop 500 ms after the last command, like the tank's watchdog), and the router's view of each tank (`Server/routerlink.go`, read every 500 ms while any tank streams: `router_dbm`, `router_rx_mbit_avg`, `router_rx_mbit_min`, `router_rx_11b_pct`, `router_tx_mbit_min`), which shows whether a tank drops to slow or 802.11b rates during a game; it lives in the temp folder (RAM on the router) and downloads from `/api/video-log`. Use it to tell a weak antenna (low `rssi_dbm`) from a busy channel (good signal but high `tank_send_ms_avg`) from a slow phone link (`viewer_dropped_per_s`) from a slow phone (`phone_shown_fps`), and see whether a feed only breaks up while the tank is moving (motor noise). Airtime fairness (`Server/airtime.go`, `Settings.AirtimeFairness`, tick box in Server Settings, off by default): with the router's airtime fairness on, tanks vanishing within seconds of each other leave about 20 undeliverable packets each in the Wi-Fi driver, and every live tank slows (about 20 fps, 40 ms sends) until the driver drops them about 8½ minutes later (3 Nov 2026). It is one switch for both bands, set only at runtime (`iwpriv ra0 set vow_airtime_fairness_en=0|1`), and the router's files turn it back on when its Wi-Fi restarts, so the hub sets it at start, on a change and every minute. With it off, a phone with a poor signal on 2.4 GHz can slow the tanks more; keep phones on `Thundergeddon-5GHz`. Viewer sockets keep at most ~16 KB unsent (`TCP_NOTSENT_LOWAT`, `sendqueue_linux.go`) so a slow phone gets fewer frames rather than old ones. Turret stabilisation (`Server/stabilise.go`, STAB button on `play.html`) is open loop on the hub: the tanks have no gyro, so it estimates hull yaw from the drive commands it relays and counter-turns the turret; tuning is `Tuning.Stab*` (Advanced → Stabiliser). Game rules are server-side in `Server/game.go` with tests in `game_test.go` (`go test ./...`). Turn-based game (`Server/turns.go`, tests in `turns_test.go`; `Settings.GameMode` = `realtime`/`turns`, chosen in Server Settings, locked during a game; changing it clears the lobby's choices): alliance → army (one player) → tanks. A player joins an alliance with `pick_alliance` (gets its first free tank) and claims more with `claim_tank`/`unclaim_tank` (the last can't be given back; `Turns.MaxTanksPerArmy`); the host moves tanks with `host_tank_army` {tank, target device}. `player.army` holds the tanks, `player.tank` is the one controlled now (`select_tank`), so `seat`/`drive`/`turret`/`fire` still work on one tank. Alliances take turns (first at random, then list order, skipping wiped-out ones); all armies of the active alliance act at once; a turn ends when every army is done (`end_turn` {on}, no tank able to act, or no page open) or `Turns.TurnSeconds` runs out, then `HandoverSeconds`; the host has `host_end_turn`. Action points (`combatant.turn`): `ApPerTurn` at the start of the alliance's turn, unused points lost; driving costs `DriveApPerSec` × (|l|+|r|)/2 per second, metered from the relayed drive commands with the 500 ms watchdog (`meterAp`); turret `TurretApPerSec`; a shot `FireAp` and listens for `ShotWindowMs` (longer than realtime's `IrWindowMs`, carried on `shotRequest.windowMs`). Overwatch (`overwatch` {on}; `Turns.Overwatch` toggle): the tank keeps its points; during enemy turns the IR scheduler runs silent probes back to back between real shots (`nextProbe`, `OverwatchWindowMs`, only moving enemies listen: drive command within 700 ms); a hit is the tank's one overwatch shot (then 0 AP). Destroyed tanks (`lifeState` `destroyed`) are out for the game (camera off, `tank_destroyed` to the player; `you_are_dead` when the whole army is gone); base tag during your turn repairs and uses the tank's points; capture points score `CapturePointsPerTurn` each at the start of their alliance's turn; wins by elimination, points or `MaxRounds` (end codes `elimination`, `rounds`). The turn-based game has its own copies of the game rules (`Settings.Turns`: max HP, damage, multipliers, cooldown, kill points, points to win, countdown) read through `g.rules()` everywhere, so the two games never share a rule value; the host's Gameplay Settings page shows only the current game's rules, and realtime snapshots have no turn fields (`mode`, `turn`, `turnRules`, AP). `hit_taken` and overwatch's `fired` carry `tank`, so a player watching another of their tanks gets a toast instead. On `play.html` the turn HUD (AP bar, turn banner, tank chips with keys 1-9, END TURN, OVERWATCH, fire cost) is shown only with `body.tb-mode`. Web pages in `Server/web/` are embedded in the binary — rebuild after editing them. `Server/run.ps1` runs it locally; `Server/deploy.ps1` builds, uploads over SSH (`scp -O`, no USB) and restarts it on the router (`/opt/thundergeddon`, procd service `/etc/init.d/thundergeddon`, logs via `logread -e thundergeddon`). See `Server/README.md`. Go is installed at `C:\Program Files\Go\bin` (add to PATH in shells opened before the install).

## Architecture

The Go server on the **GL-MT3000 hub** is the **authoritative server** for all game state.

```
Phones (browsers) ──/ws + video──► Go server on the hub (port 8088) ──/esp32 WebSocket──► Robots (ESP32)
                                     ▲                                (commands ↓, status/hits ↑)
                                     └── pulls MJPEG from http://<robot-ip>:81/stream
```

- Players join through `http://<hub>:8088/join`, e.g. from a QR code.
- The server routes commands to robots and resolves hit detection.
- The server relays each robot's FPV video to the phones watching it.
- Network: the Thundergeddon network (192.168.8.x) is the hub's own Wi-Fi.

---

## WebSocket Protocol (Robot ↔ Server)

All messages are flat JSON with a `"cmd"` key. Binary WebSocket frames = raw JPEG camera data.

**Server → Robot:**

| Command | Fields | Effect |
|---------|--------|--------|
| `drive` | `l`, `r` (float −1..1) | Set left/right track speed |
| `turret` | `speed` (float −1..1) | Set turret rotation |
| `stream_on` / `stream_off` | — | Start/stop camera streaming |
| `motors_on` / `motors_off` | — | Enable/disable DRV8833 via SLEEP line |
| `flash` | `pin`, `ms` | Flash LED for duration |
| `flash_fire` | — | White flash: robot is firing |
| `flash_hit` | — | Red flash: robot was hit |
| `set_hp` | `hp`, `max` | Update HP bar LEDs on robot |
| `ir_emit_left` | — | Activate left IR barrel (GPIO39). Robot replies `ir_emit_ack` |
| `ir_emit_right` | — | Activate right IR barrel (GPIO40). No ACK wait — sent simultaneously with `ir_listen_window` in phase 2 |
| `ir_emit_stop` | — | IR LEDs off, carrier off |
| `ir_listen_window` | `ms` | Listen for IR for `ms` ms; robot replies `ir_window_result` with 8-bit receiver bitmask |
| `set_name` | `name` | Save human-readable name to robot NVS (`tg_id`). Robot sends it back in future `hello` messages. |
| `ping` | — | Connectivity test. Robot replies `pong` immediately. |
| `reboot` | — | Restart the tank (software restart, ~300 ms later; camera and stream stopped first). |

**Robot → Server:**

| Message | Fields | Meaning |
|---------|--------|---------|
| `hello` | `id`, `tok`, `name`, `ip`, `hflip`, `vflip`, `inv_throttle`, `inv_steer`, `inv_turret`, `fps`, `fsize`, `fquality`, `rfid`, `rfid_recoveries` | Robot registers itself. `tok` is `WS_HELLO_TOKEN` from `secrets.h` — the server refuses the hello if it doesn't match `ws_token.txt` (auth gate for the tank WebSocket; disabled if it has no token file). `name` is the human-readable name saved in NVS (empty string if never set). `rfid` is 1/0 (RFID reader answering); `rfid_recoveries` counts reader re-inits since boot. Start-up report (firmware from 20 Oct 2026): `up` (ms since start), `rst` (reset reason), `wifi_ms`, `wifi_tries`, `ch`, `rssi0`, `bssid`, `cal` (`full`: the radio is fully calibrated every start-up), `crash` (summary of the last crash from the core dump in the coredump partition: task, pc, cause, backtrace; reported once, then erased; decode with `xtensa-esp32s3-elf-addr2line -pfiaC -e firmware.elf <addrs>` using the build that crashed), `fw` (build date). |
| `cam_status` | `ok`, `tries`, `err` | Sent when the camera starts (on the first `stream_on`): `CameraController::start` tries up to 3 times. |
| `hb` | `t`, `heap`, `maxblk`, `rssi`, `vf`, `vb`, `vsend`, `vmax`, `vmiss`, `temp` | Heartbeat every 2 s: millis, free heap and largest free block (memory health), Wi-Fi signal in dBm, video since the last heartbeat (frames sent, bytes, total and longest ms spent sending a frame, camera misses) and the chip's temperature in °C (`temperatureRead()`, firmware from 27 Oct 2026; tanks send worse for a few minutes after a cold start). The Go server logs these per tank in its video log (`chip_temp_c`). |
| `pong` | — | Reply to `ping`; used by the host's tank tools (`hosttools.go`) |
| `ir_emit_ack` | — | Shooter acknowledges `ir_emit_left` or `ir_emit_right` |
| `ir_window_result` | `mask` (uint8) | 8-bit bitmask of triggered IR receivers: bit 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW |
| `rfid` | `uid` | Tag scanned: 8-char CL1 hex UID (or a text label if the tag carries one). Sent once per tag until it has been out of range ~1 s. Matched against `Settings.RfidTags`. |
| `rfid_status` | `ok` (1/0), `recoveries` | RFID reader lost, re-initialised after a chip reset, or first found after boot. `RfidController` checks the reader every 2 s and re-applies its setup if a power dip / RST glitch reset it — before this, only a reboot recovered it. |

**Directional damage:** `resolveCardinal` (`Server/combat.go`) turns the `mask` bits into one of N/W/S/E. `"S"` = rear hit → `RearMultiplier` (default 3×). E/W = side hit → `SideMultiplier` (default 1.5×). N → 1×. A hit requires both b1 (left LED) and b2 (right LED) masks to be non-zero for the same enemy.

## UDP Discovery Protocol

- Robot broadcasts `{"robotId":"<MAC12>","callsign":""}` to UDP port 30560 every 2s
- The server (`runDiscovery` in `Server/tanks.go`) replies unicast: `{"ws":"ws://<ip>:8088/esp32"}` (capture-point beacons, which send `"kind":"beacon"`, get `/beacon` instead)
- Discovery answers in every game phase and `hello` has no phase gate. Keep it that way: the firmware never persists the WebSocket URL, so a robot that power-cycles (or drops its socket) mid-match can only get back in via a discovery reply.

---

## Game Logic

**Real-time game** (`Server/game.go`, `Server/combat.go`; defaults in `Server/settings.go`):
- IR hit detection: 8 directional receivers. A hit does `DamagePerHit` (25), ×3 from behind, ×1.5 from the side
- 3-second fire cooldown (`FireCooldownMs`); games last 5 minutes (`MatchSeconds`)
- At 0 HP a tank explodes (motors off for `ExplosionSeconds`), then may drive but not fire until it scans its own alliance's base tag, which respawns it at full HP. Scanning your own base while alive refills your HP. After either, the tank can't be hurt for `InvulnerableSeconds`
- Scanning a capture point's tag takes it for your alliance, which then earns points while holding it
- Each kill earns the killer's alliance `PointsPerKill` (25); reaching `PointsToWin` (300) wins at once
- When time runs out, the alliance with more points wins; on a tie, more tanks still in the fight, then more damage dealt

**Tank LED behaviour:**
- Blue WS2812Bs = current HP (proportional)
- `flash_fire` triggers `fireSequence()`: 300 ms ease-in ramp to full white → 100 ms gap (hold white) → 150 ms extinguish LEDs 5→0 (front-first). Total 550 ms then HP bar restores.
- `flash_hit` triggers `flashHit()`: all red for 300 ms then HP bar restores.

**Buzzer fire effect** (`startBuzzerFireEffect` in `main.cpp`):
- Phase 1 (0–300 ms): ease-in sweep 300 → 3000 Hz, volume 0 → 128
- Phase 2 (300–400 ms): silent gap
- Phase 3 (400–900 ms): static that fades out; centre frequency descends 8000 → 150 Hz (blast leaving the tank); block length shrinks 20 ms → 1 ms; linear volume fade 128 → 0

---

## ESP32 Firmware (`ESP32/`)

Target: **ESP32-S3-WROOM-1U-N16R8** (16 MB flash, 8 MB PSRAM). Arduino framework.

**PlatformIO board:** Use `board = 4d_systems_esp32s3_gen4_r8n16`. **Do NOT use `esp32-s3-devkitc-1`** — that targets the N8 variant (8 MB flash, no PSRAM). Using the wrong board compiles a bootloader without OPI PSRAM initialisation; firmware that touches PSRAM crashes immediately on every boot, which manifests as a continuous buzzer tone (GPIO1 glitches on each reset cycle).

### Key GPIO assignments

| GPIO | Function |
|------|----------|
| 1 | Piezo buzzer (via MMBT3904 transistor) |
| 4 | Camera SIOD |
| 5 | Camera SIOC |
| 6 | Camera VSYNC |
| 7 | Camera HREF |
| 8 | Camera PCLK |
| 9–13 | Camera Y6, Y2, Y5, Y3, Y4 |
| 14 | PCA9555 INT |
| 15 | Camera Y9 |
| 16 | Camera XCLK |
| 17 | Camera Y8 |
| 18 | Camera Y7 |
| 19/20 | USB D-/D+ |
| 21 | I2C SDA |
| 38 | WS2812B RGB LEDs (6 LEDs, 330Ω series) |
| 39 | IR LED #1 (left barrel) |
| 40 | IR LED #2 (right barrel) |
| 47 | I2C SCL |
| 48 | Green status LED (active low) |

### I2C devices (SDA=21, SCL=47)

- **PCA9555PW** at `0x20` (A0/A1/A2 = GND): IO expander for IR receivers + motor control. INT on GPIO14.
- **RC522** RFID reader on hull expansion port (I2C)

### PCA9555 port mapping

**Port 0 — IR receivers (inputs, active LOW, 10kΩ pull-ups):**

| Bit | IO | Direction |
|-----|----|-----------|
| 0 | IO0_0 | N |
| 1 | IO0_1 | NE |
| 2 | IO0_2 | E |
| 3 | IO0_3 | SE |
| 4 | IO0_4 | S |
| 5 | IO0_5 | SW |
| 6 | IO0_6 | W |
| 7 | IO0_7 | NW |

**Port 1 — Motor control + LED (outputs):**

| Bit | IO | Function |
|-----|----|----------|
| 0 | IO1_0 | Left motor IN1 |
| 1 | IO1_1 | Left motor IN2 |
| 2 | IO1_2 | Turret motor IN1 |
| 3 | IO1_3 | Turret motor IN2 |
| 4 | IO1_4 | Motors SLEEP (HIGH = awake) |
| 5 | IO1_5 | Right motor IN2 |
| 6 | IO1_6 | Right motor IN1 |
| 7 | IO1_7 | Hull LED |

### Motor control — important constraint

The **PCA9555 is digital-only** (HIGH/LOW, no PWM). The DRV8833 motor driver supports PWM on INx for variable speed, but since INx signals pass through PCA9555, **variable speed is not achievable** with this hardware design. Motors run at full speed only (forward / reverse / stop / brake). Design game controls and gameplay mechanics around this.

DRV8833 INx truth table:
- IN1=1, IN2=0 → forward
- IN1=0, IN2=1 → reverse
- IN1=0, IN2=0 → coast (free spin)
- IN1=1, IN2=1 → brake

### Implemented modules

| Module | Notes |
|--------|-------|
| `MotorController_PCA9555.h` | PCA9555 digital I/O; software PWM via `tick()` called every 1ms (PWM_STEPS=8, ~125 Hz). Full speed forward/reverse/stop/brake. |
| `IrController.h` | LEDC carrier on GPIO39/40 (ch4+5/timer1) for TX; PCA9555 port 0 + GPIO14 INT for 8-dir RX. |
| `LedController.h` | 6× WS2812B on GPIO38; HP bar in blue, fire flash white, hit flash red. |
| `CameraController.h` | PCLK=8, XCLK=16 (corrected from old code). The driver is always initialised at VGA (the largest size offered) because esp32-camera sizes its JPEG buffers at init and never resizes them; the chosen size is then set on the sensor. |
| `MjpegServer.h` | esp_http_server MJPEG stream on port 81 (`http://<robot-ip>:81/stream`). The server pulls it and relays it to phones. |
| `OtaSupport.h` | ArduinoOTA. Hostname `thunder-<MAC12>`, password `OTA_PASSWORD` from `secrets.h`, port 3232. Stops camera + disables motors before update. |
| `main.cpp` | Wires all modules. Handles `drive`, `turret`, `motors_on/off`, `stream_on/off`, `flash_fire`, `flash_hit`, `set_hp`, `ir_emit_left`, `ir_emit_right`, `ir_emit_stop`, `ir_listen_window`, `ping`. Sends `hello`, `hb`, `pong`, `ir_emit_ack`, `ir_window_result`. |

### OTA
- Hostname: `thunder-<MAC12>` (this robot: `thunder-9CF218697090`)
- Password: `OTA_PASSWORD` in `src/secrets.h` / `ota_password` in `secrets.ini` (gitignored — never commit)
- Port: 3232
- Safe: stops camera and disables motors before update
- PlatformIO env: `thundergeddon_ota` — see **Working with Claude Code** section at the top of this file for the exact upload command and troubleshooting notes.

### ArduinoWebsockets library patch — auto-applied at build time

The ArduinoWebsockets library (gilmaimon, v0.5.4) sends `Host: <ip>` in the WebSocket upgrade request, omitting the port. The old server's WebSocket library (WebSocketSharp 1.0.3) rejected this with 400 Bad Request for any non-standard port, which is why this patch exists. The build still applies it.

**This patch is now applied automatically** by [`scripts/patch_websockets.py`](ESP32/thundergeddon/scripts/patch_websockets.py), wired into both env sections via `extra_scripts = pre:scripts/patch_websockets.py` in `platformio.ini`. The script runs at script-load time and again as a pre-link action, is idempotent, and rewrites the file inside `.pio/libdeps/<env>/ArduinoWebsockets/src/websockets_client.cpp`. The library itself is pinned in `lib_deps` to commit `26eecea` (tag `0.5.4`) so the anchor line is stable.

**No manual step is required.** A fresh clone, `pio pkg update`, or deletion of `.pio/libdeps/` will all re-trigger the patch on the next build.

**If a future library bump invalidates the anchor**, the script exits with status 1 and a message naming the file — fix by updating `UNPATCHED_LINE` in `patch_websockets.py` to match the new upstream text. Better to fail loud at build time than silently produce non-connecting firmware.

For reference, the substitution it applies (around line 314 of `websockets_client.cpp`):
```cpp
// before:
auto handshake = generateHandshake(internals::fromInterfaceString(host), internals::fromInterfaceString(path), _customHeaders);

// after:
WSString hostHeader = internals::fromInterfaceString(host) + ":" + std::to_string(port);
auto handshake = generateHandshake(hostHeader, internals::fromInterfaceString(path), _customHeaders);
```

### secrets.h
Wi-Fi credentials live in `secrets.h` which must not be committed. Use a template file. The reference code contains real credentials — do not copy them.

---

## Documentation

Full hardware schematics, to-do lists, and gameplay spec are in `Docs/`:
- `Docs/thundergeddon.md` — hardware design (hull board, turret board, GPIO table)
- `Docs/emf_application.md` — system overview and build status
- `Docs/EMF Festival Gameplay.md` — full MVP spec, UI requirements, stretch goals
