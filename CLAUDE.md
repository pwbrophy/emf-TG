# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Repository

GitHub: **https://github.com/pwbrophy/emf-TG** (branch: `master`)

---

## Working with Claude Code — Required Practices

### Unity: always use Coplay MCP
The project has the `com.coplaydev.coplay` MCP plugin installed. **After every Unity C# change, call `mcp__coplay-mcp__check_compile_errors` to verify the project compiles clean before reporting success.** Use `mcp__coplay-mcp__get_unity_logs` to read the Unity console when diagnosing runtime errors. Never rely solely on reading `.cs` files to confirm correctness — always verify through Coplay.

### ESP32 firmware: always upload via OTA
Never ask the user to connect a USB cable. After every firmware change, upload using the `thundergeddon_ota` PlatformIO environment, which pushes over Wi-Fi via ArduinoOTA:

```
cd "F:\Data\Thundergeddon\EMF_Project\ESP32\thundergeddon"
pio run -e thundergeddon_ota --target upload
```

The robot must be powered on and connected to Wi-Fi for OTA to work. The `platformio.ini` `[env:thundergeddon_ota]` section is configured with:
- `upload_protocol = espota`
- `upload_port = thunder-9CF218697090.local` (mDNS hostname — may need replacing with the robot's IP if mDNS doesn't resolve on Windows; check Unity's robot list or serial output for the IP)
- `--auth=thunder123`
- `--port=3232`

A successful upload ends with `Result: OK` / `Success`. If the hostname doesn't resolve, substitute the robot's current IP directly, e.g. `pio run -e thundergeddon_ota --target upload --upload-port 192.168.x.x`.

---

## Project Overview

**Thundergeddon** is a hybrid physical/digital RC tank battle game for EMF Festival. Small robotic tanks fight in the real world while software handles game logic, player connections, and robot control.

The system has three components:

| Component | Location | Language | Role |
|-----------|----------|----------|------|
| Unity admin app | `Unity/EMF_Server/` | C# | Authoritative server, operator UI, game state, hit logic |
| ESP32 firmware | `ESP32/` | C++ | Robot controller: motors, camera, IR, LEDs, comms |
| Web player client | `Webserver/ThundergeddonWeb/` | ASP.NET Core + JS | Browser UI for players on phones; joystick, video, HUD |

## Architecture

The **laptop** runs both the Unity app (operator/admin) and the Webserver (player web page). It is the **authoritative server** for all game state.

```
Phones (browsers) ──SignalR──► ASP.NET (port 5000) ──WebSocket──► Unity (port 8081)
                                                                        │
Robots (ESP32) ──────────────────────WebSocket──────────────────────────┘
                                     (commands ↓, status/hits ↑, port 8080)
```

- Players join by scanning a QR code that opens the web player page on their phone.
- The laptop routes commands to robots and resolves hit detection.
- Each robot streams FPV video to its assigned player (MJPEG at `http://<robot-ip>:81/stream`).
- Network: EMF Camp's shared Wi-Fi (public event network — design for instability and security).

---

## Unity App (`Unity/EMF_Server/`)

**Open:** Use Unity Hub or open `Unity/EMF_Server/` as a Unity project. The project uses **URP 2D** (Universal Render Pipeline, 2D renderer).

**Build:** File → Build Settings → build for Windows (standalone). No custom build scripts yet.

**Key packages:**
- `WebSocketSharp` v1.0.3-rc11 — server-side WebSocket. **Not a UPM package; DLL is at `Assets/Plugins/websocket-sharp.dll`** (downloaded from NuGet). Uses `Func<T>` factory API: `AddWebSocketService<T>(path, () => new T { Parent = this })`.
- `com.coplaydev.coplay` (beta) — Coplay MCP plugin for Unity editor automation
- `com.unity.inputsystem` — new Input System (EventSystem uses `InputSystemUIInputModule`)
- `com.unity.render-pipelines.universal` — URP 2D
- `com.unity.ugui` — includes TextMeshPro. **TMP Essential Resources have been imported** (`Assets/TextMesh Pro/`). Always assign `LiberationSans SDF` font when creating TMP components in editor scripts.

### Unity Architecture

All services are created once in `AppBootstrap` (`[DefaultExecutionOrder(-1000)]`, `DontDestroyOnLoad`) and accessed globally via the static `ServiceLocator`.

**Important:** `AppBootstrap` uses a static `_made` guard to prevent duplicate initialisation across scene loads. This guard is reset via `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` so that entering play mode a second time in the same editor session works correctly (relevant when domain reload on play is disabled).

**Service layer (pure C#):**

| Class | Role |
|-------|------|
| `ServiceLocator` | Static registry — the only coupling point between systems |
| `AppBootstrap` | Creates and wires all services at startup |
| `GameFlow` | State machine: `MainMenu → Lobby → Playing → Ended`, fires `OnPhaseChanged` |
| `GameState` | Snapshot created at `StartGame()`. Holds `RobotHp`, `TotalDamageDealt`, `DeadRobots`, `WinnerAllianceIndex`, `EndReason` |
| `GameService` | `StartGame()` seeds HP. `ApplyDamage(shooterId, targetId, dir, players, dir)` applies directional multipliers, fires `OnHpChanged`/`OnRobotDied`/`OnGameWon`. `TimeExpired()` determines winner by survivors then damage. |
| `GameSettings` | MonoBehaviour on Bootstrap. `MaxHp=100`, `DamagePerHit=25`, `RearMultiplier=3`, `MatchDurationSeconds=180` |
| `MatchTimer` | MonoBehaviour on Bootstrap. `StartTimer(float)` / `StopTimer()`. Fires `OnTick(float remaining)` and `OnExpired`. |
| `RobotDirectory` | Live registry of connected robots. Persists callsigns + preferred players to `robots.json`. Raises `OnRobotAdded/Updated/Removed`. `SetAssignedPlayer(robotId, playerName)` validates via `PickAssignedPlayer`. `ClearAssignedPlayer(robotId)` sets to null unconditionally. **`PickAssignedPlayer` only assigns to players who have no robot yet** — it builds a taken-set from current assignments so a second robot never steals a player who is already assigned. |
| `PlayersService` | Player list (name + alliance). `LoadOrEnsureDefaults` and `EnsureDefaults` are **no-ops** — no Player1/Player2 defaults are seeded. Players are added exclusively when phones join via `PlayerWebSocketServer.HandleJoin`. |

**Network layer:**

| Class | Role |
|-------|------|
| `RobotWebSocketServer` | `WebSocketSharp` server at `ws://<ip>:8080/esp32`. Thread-safe `PostMain` queue for Unity safety. Heartbeat timeout sweep every 2s. Auto-starts via `Start()`. Events: `OnPong`, `OnIrEmitReady`, `OnIrResult`. **Important:** `hello` is accepted at any game phase — do not add a phase gate, or robots that reconnect during Playing will never register and all commands will fail. `OnClosed` does NOT remove the robot from `RobotDirectory`; only the heartbeat sweep removes stale entries. |
| `PlayerWebSocketServer` | `WebSocketSharp` server at `ws://127.0.0.1:8081/players`. Receives join/leave/drive/turret/fire JSON from `UnityBridgeService`. Routes drive+turret to `RobotWebSocketServer`, fire to `ShootingController.RequestFire`. Subscribes to `GameFlow.OnPhaseChanged`, `GameService.OnHpChanged/OnRobotDied/OnGameWon`. On `Playing`: sends `game_started` per connection. Sends `state_update` (HP + timer) per connection at 1 Hz and immediately on HP change. Fires `OnPlayerInput` event for `PlayerInputMonitor`. **`HandleJoin` calls `TryAssignFreeRobotToPlayer` immediately after adding the player**, so any already-connected unassigned robot is auto-assigned to the new player. |
| `UdpDiscoveryListener` | Background thread on UDP port 30560. Auto-starts via `Start()`. Replies to robot announces with WebSocket URL. Tracks `_repliedTo` set to suppress per-robot log spam on the 2s broadcast cadence. |
| `ESP32VideoReceiver` | Receives JPEG byte arrays, decodes into `Texture2D` on a `RawImage`. |

**UI layer:**

| Class | Role |
|-------|------|
| `GameFlowPresenter` | Shows/hides panels by `GamePhase`. Wires nav buttons to `GameFlow`. **Note:** wired to `_flow` captured at `OnEnable` — null-safe (`_flow?.Method()`) |
| `ServerPanelPresenter` | Updates robot count label from `RobotDirectory` events |
| `RobotSelectionPanel` | Cycle robots. On select: `stream_on + motors_on`. On deselect: `stream_off + motors_off` |
| `ShootingController` | Realtime fire with 3s per-robot cooldown. IR sequence: `ir_emit_prepare → ir_emit_ready → ir_listen_and_report → ir_result`. Calls `GameService.ApplyDamage`, sends `flash_fire`/`flash_hit`/`set_hp`. Registers as `ServiceLocator.Shooting` on Awake. Public `RequestFire(robotId)` called by `PlayerWebSocketServer` for phone fire inputs. **Important:** `SendFlashFire` is called before the enemy-count check so the shooter always gets visual/audio feedback even when no valid targets exist. |
| `RobotPingButton` | On PlayingPanel's `PingRow`. Sends `ping` to the selected robot and displays RTT. Subscribes to `RobotWebSocketServer.OnPong`. Wired by `WirePingButton` editor script. |
| `RobotsPanelPresenter` | Scrollable robot list. Instantiates `Assets/Prefabs/RobotRow.prefab` into `RobotsScrollView/Viewport/Content` (which has `VerticalLayoutGroup` + `ContentSizeFitter`). Each row: Name / IP / Player / Edit button |
| `PlayersEditorPanel` | Per-player row list. Each row: editable name, alliance dropdown (hardcoded 2), robot dropdown (exclusive — one player per robot), remove button. Requires `PlayerRow.prefab` + `rowContainer` (scroll view Content) wired by `WirePlayersPanel` editor script. |
| `PlayerRowUI` | Component on `PlayerRow.prefab`. Holds refs to `nameField`, `allianceDropdown`, `robotDropdown`, `removeButton` so `PlayersEditorPanel` doesn't need `Find()` calls. |
| `DevRobotsToolbar` | Add/remove fake robots for testing. Initialises in `Start()` (not `OnEnable`) — only runs once LobbyPanel becomes active |
| `GameSettingsPanel` | Three `TMP_InputField`s for MaxHp / DamagePerHit / Duration. Writes directly to `ServiceLocator.GameSettings` |
| `MatchTimerDisplay` | Subscribes to `MatchTimer.OnTick`. Displays `M:SS`, flashes red in last 10s |
| `RobotHpPanel` | Subscribes to `GameService.OnHpChanged` / `OnRobotDied`. Dynamically builds HP bar rows in `RowContainer` at runtime |
| `PlayerInputMonitor` | Subscribes to `PlayerWebSocketServer.OnPlayerInput`. Shows a live row per player in `RowContainer` (VLG + ContentSizeFitter) with name + L/R/turret values, updated every frame. Placed on PlayingPanel. |
| `EndedPanelPresenter` | Subscribes to `GameFlow.OnPhaseChanged`. On `Ended`: reads `GameState.WinnerAllianceIndex` + `EndReason`, shows result text |

### Scene (`Assets/SampleScene.unity`)

The scene is fully wired:

```
Bootstrap                    — AppBootstrap, GameSettings, MatchTimer
Servers                      — RobotWebSocketServer, PlayerWebSocketServer, UdpDiscoveryListener, ESP32VideoReceiver
Canvas
  ├── GameFlowPresenter       (wired to all 4 panels + 4 nav buttons)
  ├── MainMenuPanel           — TitleLabel, SubLabel, ToLobbyButton
  ├── LobbyPanel              — RobotsPanelPresenter, DevRobotsToolbar, PlayersEditorPanel,
  │                             ServerPanelPresenter, GameSettingsPanel
  │     ├── RobotsScrollView/Viewport/Content  (VLG + ContentSizeFitter)
  │     ├── NumRobotsText, AddFakeRobotButton, RemoveLastRobotButton
  │     ├── PlayersScrollView/Viewport/Content  (VLG + ContentSizeFitter — rows created at runtime)
  │     ├── AddPlayerButton, StartGameButton
  │     └── GameSettingsPanel (MaxHpField, DamageField, DurationField)
  ├── PlayingPanel            — RobotSelectionPanel, ShootingController, GamePanelPresenter,
  │                             MatchTimerDisplay, RobotHpPanel, PlayerInputMonitor
  │     ├── TimerLabel, EndGameButton
  │     ├── RobotNameLabel, RobotIpLabel, RobotPlayerLabel, RobotAllianceLabel, RobotClientLabel
  │     ├── PrevRobotButton, NextRobotButton, ClearRobotButton
  │     ├── RobotHpPanel/RowContainer
  │     ├── PingRow                            (RobotPingButton, PingButton, PingResult)
  │     └── InputsScrollView/Viewport/Content  (VLG + ContentSizeFitter — phone inputs)
  └── EndedPanel              — EndedPanelPresenter, ResultLabel, BackToMenuButton
EventSystem                  — InputSystemUIInputModule (new Input System)
```

**Note:** `JoystickBase`, `TurretSlider`, `ShootButton`, `ShootResultLabel`, `CooldownLabel` have been removed from PlayingPanel — tank controls are now driven from phone clients. The operator view shows HP bars and live player inputs instead.

### Editor scripts (`Assets/Editor/`)

These are utility scripts for scene setup — not part of the game build:

| Script | Purpose |
|--------|---------|
| `LayoutPanels.cs` | Re-run to reposition all UI elements. Activates panels temporarily so TMP initialises on inactive panels. Also calls `FixAllFonts` to assign `LiberationSans SDF` to all TMP components. |
| `WirePhase2UI.cs` | Added GameSettingsPanel, MatchTimerDisplay, RobotHpPanel, EndedPanelPresenter to scene |
| `WirePlayersPanel.cs` | **Run after any PlayerRow prefab or PlayersEditorPanel change.** Creates `Assets/Prefabs/PlayerRow.prefab` (TMP_InputField + 2× TMP_Dropdown + Button + `PlayerRowUI`), creates `PlayersScrollView` under LobbyPanel, wires `PlayersEditorPanel.rowContainer/rowPrefab/addButton` via reflection. Menu: **Thundergeddon → 4 Wire Players Panel**. |
| `WirePlayerServer.cs` | Adds `PlayerWebSocketServer` to the `Servers` GameObject. Menu: **Thundergeddon → 5 Wire Player Server**. |
| `WireInputMonitor.cs` | Removes old operator tank controls (JoystickBase, TurretSlider, ShootButton, etc.) from PlayingPanel. Adds `InputsScrollView` and `PlayerInputMonitor` component. Menu: **Thundergeddon → 6 Wire Input Monitor**. |
| `WirePingButton.cs` | Adds `PingRow` (HorizontalLayoutGroup) to PlayingPanel containing a Ping button + result label. Adds `RobotPingButton` component and wires refs. Menu: **Thundergeddon → 7 Wire Ping Button**. |
| `RebuildRobotRowPrefab.cs` | Recreates `Assets/Prefabs/RobotRow.prefab` with Name/Ip/Player/EditButton children |
| `ImportTMPResources.cs` | One-time: imports TMP Essential Resources from the ugui package |
| `CleanBadTMPLabels.cs` | Removes TMP labels that have no font assigned (created with broken multi-type constructor pattern) |

**TMP in editor scripts:** Always create text GameObjects by adding components sequentially, not with the multi-type `new GameObject(name, typeof(A), typeof(B))` constructor — TMP's internal `m_canvasRenderer` won't wire correctly that way. Use:
```csharp
var go = new GameObject(name);
go.transform.SetParent(parent, false);
go.AddComponent<RectTransform>();
go.AddComponent<TextMeshProUGUI>(); // [RequireComponent] auto-adds CanvasRenderer
```
Always assign the font explicitly: `tmp.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset")`.

**RectTransform before SetParent:** When building prefab hierarchies in editor scripts, always add `RectTransform` **before** calling `SetParent` on any child that will have one. Adding it after causes Unity's native-side null check to throw "There is no RectTransform attached" when you first set `anchorMin`/`anchorMax`. Pattern:
```csharp
var child = new GameObject("Child");
var childRT = child.AddComponent<RectTransform>(); // add RT FIRST
child.transform.SetParent(parent, false);          // THEN set parent
childRT.anchorMin = Vector2.zero;                  // safe to configure now
```

**Never use `??` with Unity component lookups:** `GetComponent<T>() ?? AddComponent<T>()` uses C# null-coalescing which ignores Unity's fake-null wrapper. Use an explicit `if` check:
```csharp
var rt = go.GetComponent<RectTransform>();
if (rt == null) rt = go.AddComponent<RectTransform>();
```

---

## WebSocket Protocol (Robot ↔ Unity)

All messages are flat JSON with a `"cmd"` key. Binary WebSocket frames = raw JPEG camera data.

**Unity → Robot:**

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
| `ir_emit_prepare` | — | Motors off, IR LEDs on, carrier on. Robot replies `ir_emit_ready` |
| `ir_emit_stop` | — | IR LEDs off, carrier off, motors on |
| `ir_listen_and_report` | `ms` | Listen for IR for window, auto-reply `ir_result` |
| `ping` | — | Connectivity test. Robot replies `pong` immediately. |

**Robot → Unity:**

| Message | Fields | Meaning |
|---------|--------|---------|
| `hello` | `id` | Robot registers itself (MAC-based ID, 12-char hex) |
| `hb` | `t` | Heartbeat (millis timestamp) |
| `pong` | — | Reply to `ping`; used by `RobotPingButton` to display RTT |
| `ir_emit_ready` | — | Robot is ready to emit IR |
| `ir_result` | `hit` (0/1), `dir` (string e.g. `"SE"`) | Result of IR listen window. `dir` is compass direction of hit receiver |

**Directional damage:** `dir` values `"S"`, `"SE"`, `"SW"` are rear hits → `RearMultiplier` (default 3×). All other directions → 1×.

## UDP Discovery Protocol

- Robot broadcasts `{"robotId":"<MAC12>","callsign":""}` to UDP port 30560 every 2s
- Server (Unity `UdpDiscoveryListener`) replies unicast: `{"ws":"ws://<ip>:8080/esp32"}`
- Discovery only active while `GameFlow.Phase == Lobby`

---

## Game Logic

**Deathmatch (MVP):**
- Two teams; last team standing, or most tanks alive when timer expires, wins
- Tiebreak: most total damage dealt (tracked in `GameState.TotalDamageDealt`)
- IR hit detection: 8 directional receivers; rear hit = 3× damage multiplier
- 3-second fire cooldown per tank (enforced in `ShootingController`)
- `GameService.ApplyDamage` → updates `RobotHp`, fires `OnHpChanged`, checks win condition
- `GameService.TimeExpired` → counts alive robots per alliance, uses damage as tiebreaker
- On robot death: motors off, robot greyed in HP panel, `OnRobotDied` event fires
- `GameFlow.StartGame()` starts `MatchTimer`; `GameFlow.EndGame()` stops it

**Tank LED behaviour:**
- Blue WS2812Bs = current HP (proportional)
- `flash_fire` triggers `fireSequence()`: 300 ms ease-in ramp to full white → 100 ms gap (hold white) → 150 ms extinguish LEDs 5→0 (front-first). Total 550 ms then HP bar restores.
- `flash_hit` triggers `flashHit()`: all red for 300 ms then HP bar restores.

**Buzzer fire effect** (`startBuzzerFireEffect` in `main.cpp`):
- Phase 1 (0–300 ms): ease-in sweep 300 → 3000 Hz, volume 0 → 128
- Phase 2 (300–400 ms): silent gap
- Phase 3 (400–900 ms): static that fades out; centre frequency descends 8000 → 150 Hz (blast leaving the tank); block length shrinks 20 ms → 1 ms; linear volume fade 128 → 0

---

## Not Yet Implemented

- **RobotTestPanel** — subsystem test buttons (drive, turret sweep, flash, IR) in Lobby
- **RenamePopup** — `RobotsPanelPresenter.renamePopup` is unwired; Edit button logs a warning
- **Phase 4: Player-facing display** — second monitor panel with QR code, scoreboard not started
- **`PetersUtils.GetLocalIPAddress()`** — referenced in `RobotWebSocketServer` and `UdpDiscoveryListener`; must pick LAN IPv4 (avoids loopback/VPN)

## Known Issues / Gotchas

- **Windows Firewall blocking port 8080** — If the robot can't connect via WebSocket (sends UDP announces, gets replies, but no `hello` appears in Unity console), add a firewall rule: `netsh advfirewall firewall add rule name="UnityRobotWS" dir=in action=allow protocol=TCP localport=8080`
- **Robot re-registration after disconnect** — `RobotWebSocketServer.OnClosed` must NOT call `_dir.Remove()`. If it does, the robot re-enters the directory via UDP `Upsert` with no WebSocket session, and all commands silently fail (`FAILED motors_on`, `FAILED ping`). Only the heartbeat timeout sweep removes stale directory entries.
- **`hello` must be accepted at any game phase** — The `hello` handler must not gate on `GamePhase.Lobby`. If the robot connects just as the game starts, it gets kicked and can never register, so all commands fail for the entire match.
- **WebSocketSharp must bind to 0.0.0.0** — `new WebSocketServer(Port)` (port-only constructor) is used instead of `new WebSocketServer("ws://ip:port")`. Binding to a specific IP in WebSocketSharp can silently reject connections.
- **ArduinoWebsockets Host header missing port** — WebSocketSharp 400s on `Host: 192.168.86.197` (no port). The library patch above fixes this. Root cause: ArduinoWebsockets `generateHandshake()` takes only host (no port), so the call site must pre-concatenate `host:port`.

---

## Web Player Client (`Webserver/ThundergeddonWeb/`)

ASP.NET Core 8 minimal API + SignalR. Run with `dotnet run` from `Webserver/ThundergeddonWeb/`.
Serves on `http://0.0.0.0:5000` (all interfaces). Players navigate to `http://<laptop-ip>:5000` on their phone.

### Files

| File | Role |
|------|------|
| `Program.cs` | Registers SignalR, `UnityBridgeService` (singleton + hosted), static files, hub route |
| `Hubs/GameHub.cs` | SignalR hub. `JoinLobby(name)`, `SendDrive(l,r)`, `SendTurret(speed)`, `Fire()`. `OnDisconnectedAsync` sends leave. |
| `Services/UnityBridgeService.cs` | `BackgroundService`. `ClientWebSocket` connects to `ws://127.0.0.1:8081/players`. Reconnects every 3s on failure. Routes inbound Unity messages (`player_list`, `game_started`, `state_update`, `you_are_dead`, `game_over`) to correct SignalR clients. `SendToUnity(object)` serialises to JSON. |
| `wwwroot/index.html` | Mobile-first dark-theme SPA with 5 screens (see below). Uses `@microsoft/signalr` from unpkg CDN. |
| `appsettings.json` | `"Urls": "http://0.0.0.0:5000"` |

### Phone SPA screens

| Screen | Trigger | Content |
|--------|---------|---------|
| `screen-join` | Initial / on disconnect | Name input + Join button. **Player name persisted in `localStorage['tg_player_name']`** and auto-filled on load. Portrait allowed. |
| `screen-lobby` | SignalR connected + joined | Spinning indicator + live player list (`LobbyUpdate`). Portrait allowed. |
| `screen-playing` | `GameStarted` event | Fullscreen MJPEG stream + HP bar (top) + joystick (bottom-left) + turret pill (bottom-right) + FIRE button (bottom-centre) + timer (top-centre). **Landscape enforced** (`body.game-active` class triggers `#rotate-notice` in portrait). Auto-enters fullscreen on game start. |
| `screen-dead` | `YouAreDead` event | Overlay on playing screen: "YOU ARE DEAD" |
| `screen-gameover` | `GameOver` event | Winner team + reason + Play Again button |

**On SignalR `onclose`** (all reconnect attempts exhausted): `returnToJoin('Server connection lost.')` is called — clears all game state, cancels rAF loop, blanks video stream, returns to `screen-join`.

**Controls:**
- **Joystick** — pointer events, circular base, thumb snaps to centre on release. Tank-drive: `l = clamp(y+x, -1,1)`, `r = clamp(y-x, -1,1)`. Sends `SendDrive` via SignalR at 20 Hz.
- **Turret pill** — horizontal draggable thumb, springs to centre on release. Sends `SendTurret` on change.
- **FIRE button** — sends `Fire()` via SignalR. 3 s client-side cooldown visual (countdown overlay).

### WebSocket protocol (ASP.NET ↔ Unity port 8081)

**ASP.NET → Unity:**
```json
{"cmd":"join",   "name":"Alice", "connectionId":"abc"}
{"cmd":"leave",  "connectionId":"abc"}
{"cmd":"drive",  "connectionId":"abc", "l":0.5,  "r":-0.3}
{"cmd":"turret", "connectionId":"abc", "speed":0.7}
{"cmd":"fire",   "connectionId":"abc"}
```

**Unity → ASP.NET:**
```json
{"cmd":"player_list",  "players":["Alice","Bob"]}
{"cmd":"game_started", "connectionId":"abc", "callsign":"Thunder1", "videoUrl":"http://…:81/stream", "hp":100, "maxHp":100}
{"cmd":"state_update", "connectionId":"abc", "hp":75, "maxHp":100, "timer":142.0, "cooldown":0.0}
{"cmd":"you_are_dead", "connectionId":"abc"}
{"cmd":"game_over",    "winnerTeam":"Alliance 1", "reason":"elimination"}
```

`player_list`, `you_are_dead`, `game_started`, `state_update` are forwarded to specific SignalR clients by `connectionId`. `game_over` is broadcast to all.

### To run
```
cd Webserver/ThundergeddonWeb
dotnet run
```
Then open `http://<your-ip>:5000` on the phone.

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
| `CameraController.h` | PCLK=8, XCLK=16 (corrected from old code); MJPEG frames sent over WebSocket. |
| `MjpegServer.h` | esp_http_server MJPEG stream on port 81 (`http://<robot-ip>:81/stream`). Phones connect directly. |
| `OtaSupport.h` | ArduinoOTA. Hostname `thunder-<MAC12>`, password `thunder123`, port 3232. Stops camera + disables motors before update. |
| `main.cpp` | Wires all modules. Handles `drive`, `turret`, `motors_on/off`, `stream_on/off`, `flash_fire`, `flash_hit`, `set_hp`, `ir_emit_prepare`, `ir_listen_and_report`, `ping`. Sends `hello`, `hb`, `pong`, `ir_emit_ready`, `ir_result`. |

### OTA
- Hostname: `thunder-<MAC12>` (this robot: `thunder-9CF218697090`)
- Password: `thunder123`
- Port: 3232
- Safe: stops camera and disables motors before update
- PlatformIO env: `thundergeddon_ota` — see **Working with Claude Code** section at the top of this file for the exact upload command and troubleshooting notes.

### ArduinoWebsockets library patch — MUST REAPPLY after clean/reinstall

The ArduinoWebsockets library (gilmaimon, v0.5.4) sends `Host: <ip>` in the WebSocket upgrade request, omitting the port. **WebSocketSharp 1.0.3 rejects this with 400 Bad Request** for any non-standard port (i.e. not 80/443).

**After any `pio run` that reinstalls the library**, re-apply this patch to:
`.pio/libdeps/thundergeddon_ota/ArduinoWebsockets/src/websockets_client.cpp`

Find (around line 314):
```cpp
auto handshake = generateHandshake(internals::fromInterfaceString(host), internals::fromInterfaceString(path), _customHeaders);
```
Replace with:
```cpp
// Include port in Host header — WebSocketSharp requires "host:port" for
// non-standard ports (not 80/443), otherwise it rejects with 400.
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
