# Thundergeddon at EMF Festival

I will be taking Thundergeddon to EMF Festival to be shown as an installation.  

I need to develop the software to be used in the installation.  The following describes how I would like to set up my game, let people connect and the gameplay in the game itself.

## Setup

On arrival I will set up my installation.   This will consist of a Windows laptop (HP ENVY x360) for the server, with an additional monitor facing players, a table, the tank battlefield playspace and the tanks themselves.  I'd like to have the tanks switch on and be able to connect to the WiFi without needing to reprogram the tanks via USB.  It would be great if they can connect to my server via Bluetooth to obtain credentials.  

## System

**Laptop:** central server, Unity admin interface, hosts the player web page

**Phones:** browser-based player clients for control and viewing

**Robots:** connect to the laptop for game control and state, and provide video to players

**System specifications**

- The game shall run with a **laptop as the central server**.
- The laptop shall run the **Unity application** as the **admin/operator interface**.
- Players shall join the game by scanning a **QR code** that opens a **web page** on their phones.
- The web page shall provide the **player interface**, including controls and game information.
- The laptop shall remain **authoritative** for:
  - robot assignment
  - hit detection / hit logic
  - overall game state
  - routing commands to robots
- Robots shall connect to the **laptop server** for commands and state updates.
- Each robot shall provide a **video stream** for its assigned player.
- Only **one player** shall control and view **one robot at a time**.
- The system shall rely on **EMF Camp’s network infrastructure** rather than a privately created local Wi-Fi network.
- The system shall therefore be designed with consideration for:
  - browser compatibility
  - connection stability
  - security on a public event network

## Minimal Viable Product

### Server Functionality

- before game starts, adjust certain parameters such as 'hit damage' or 'match duration'
- robot test functionality: tools to select a robot and make sure each part of the functionality is working as expected
- opens lobby and waits for clients to connect
- functionality to assign players to teams/robots
- functionality to disconnect/kick a player
- button to 'Begin Game' activates robots and starts counting down match timer
- handle firing and hit detection system

### Client functionality 

- client connects, prompted to enter name, enters holding lobby waiting for game to begin
- when game begins, clients then enter gameplay mode
- during gameplay, client shows
  - drive controls - a circular joystick style control
  - turret controls - left/right slider
  - fire button
  - tank hit points
  - camera video stream
- If players tank is destroyed, disable tank and show 'you are dead' on client screen
- At the end of the game, display 'game over' and show the winning team

### Tank functionality

- Tanks will move and turn using skid steer
- Turret moves left and right
- Top LED
  - shows hit points remaining in blue
  - flash white when firing
  - flash red when hit
- Buzzer
  - Play firing sound and hit sound

### Firing and Hitting

Players can shoot tanks on the enemy team (no friendly fire).  When players are hit, their hit points are reduced.  

Depending where the tank is hit, the damage may be higher.  For example, a direct hit on the rear of a tank will cause a 3x damage multiplier.  

Tanks will have a three second recharge timer before they can fire their next shot

### **Gameplay** Mode 1 - Simple Deathmatch

Two teams of tanks battle each other to the death.  

The objective of the game would be to destroy all enemy tanks.  The team with the most enemy tanks left alive at the end of the round would be the winner.  If the number of tanks are equal, then the team that has done the most damage would be the winner.  The game will end if all tanks on a team are destroyed or the game timer reaches zero.  



## Stretch Goal Features

### Capture Points

The tanks have RFID readers.  I will place RFID stickers on the battlefield.  When the tanks drive over the RFIDs, they will scan them.  This will enable 'capture points'.  

3× capture points for bases using RFID tags

### Shields

2 layer damage: Shield / Hull damage and recharging shields.  Once shields are gone, hull will take damage.  

RFID tag for a 'base' where each team can recharge their shields.  Recharge is instantaneous.  This will mean that when someone has been hit a few times they can retreat to their base and recharge their shields.  However they will leave the capture points unguarded.  

### Player Facing Gameplay Information Screen

Displays QR code containing URL of server (unique for each game to avoid people reconnecting later on when they're not present for the game) on the player-facing screen

Displays which team is currently holding capture points



### Gameplay Mode 2 - Capture Points

Points are gained by damaging enemy tanks and holding capture points.  

When a capture point is held, points are awarded at a rate of 2 per second for each capture point that's held.  

Destroying an enemy tank would award 30 points.  

Tanks can retreat to their base and recharge shields.  

When a tank is destroyed, it's disabled for 10 seconds.  After ten seconds it can drive but not shoot.  It must return to base to recharge shields and repair before it can shoot again.  



# System Specs

## Thundergeddon EMF System Specification (Draft)

### 1. Purpose

Thundergeddon shall operate as a hybrid physical/digital multiplayer installation in which players use their phones to control real tabletop tanks over the EMF network. The system shall support short, spectator-friendly matches and shall allow players to join via a browser rather than a native mobile app.  

### 2. High-level architecture

The system shall consist of three main parts:

- **Laptop**
  - central authoritative server
  - Unity admin/operator interface
  - web server for player clients
- **Mobile phones**
  - browser-based player clients
  - control input and gameplay UI
  - video display for assigned tank
- **Robots**
  - ESP32-based tanks
  - connect to the laptop for game control and state
  - provide live camera video to players

Unity shall remain authoritative for robot assignment, hit logic, overall match state, and routing approved commands to robots.  

### 3. Laptop software structure

The laptop shall run **two cooperating software services**:

#### 3.1 Unity application

Unity shall provide:

- operator/admin interface
- robot discovery and health monitoring
- lobby and match control
- team and robot assignment
- authoritative gameplay logic
- hit processing and scoring
- game timer and end-of-match handling

#### 3.2 Companion web server

A separate companion web server shall run on the same laptop alongside Unity. This server shall:

- host the player web page
- accept browser connections from phones
- maintain live client communication
- pass player input to Unity
- pass Unity state updates back to phones

The companion web server shall be implemented using **ASP.NET Core with Kestrel**. Kestrel is Microsoft’s recommended ASP.NET Core server and is included by default in ASP.NET Core templates. For real-time browser communication, **SignalR** shall be preferred over raw WebSockets, because Microsoft recommends it for most applications and it uses WebSockets whenever possible. 

### 4. Player access and page hosting

Players shall join the game by scanning a QR code shown on the player-facing display. The QR code shall open a page hosted on the laptop over the EMF LAN.

The player URL shall use the laptop’s LAN IP address and port, rather than a public domain name or cloud host. No public domain or cloud hosting shall be required for the MVP, provided that player phones can reach the laptop over the event network.  

Example pattern:

```
http://<laptop-lan-ip>:<port>/game/<session-code>
```

The phone shall load the page from the laptop and then open a persistent realtime connection back to the same laptop server.

### 5. Network model

The system shall rely on **EMF Camp’s network infrastructure** and shall not depend on a privately created Wi-Fi network unless EMF explicitly approves an exception. Current EMF network documentation says inbound connections from other users on the event network are allowed by default on the wireless network, advises against bringing your own access point if possible, and notes that some details on the page still reference the 2024 event and will be updated closer to EMF 2026.  

The design shall therefore assume:

- browser compatibility matters
- connection stability matters
- the event network is a public shared environment
- direct phone-to-laptop and phone-to-robot connectivity must be verified on-site or with EMF NOC in advance

### 6. Unity ↔ web server interaction

The companion web server shall not contain the authoritative game rules. It shall act as a browser-facing transport and session layer only.

#### 6.1 Phone to server

The phone client shall send:

- player name / join request
- drive input
- turret input
- fire request
- keepalive / reconnect state

#### 6.2 Server to Unity

The web server shall forward:

- join requests
- control input
- fire attempts
- disconnect/reconnect events

#### 6.3 Unity to server

Unity shall send:

- lobby status
- player-to-robot assignment
- match start / match end
- current HP / status / cooldown
- death / disable state
- winner / game over state
- assigned video feed information

#### 6.4 Server to phone

The web server shall push:

- current UI state
- robot assignment
- health and cooldown info
- game timer
- death/game-over messaging
- assigned video stream endpoint

### 7. Robot communications

Each robot shall connect to the laptop for control and state updates. The robot-side ESP32 firmware shall:

- connect to the network
- identify itself to the laptop
- receive drive/turret/fire commands
- drive motors and peripherals
- report status and heartbeat
- report hit-related data
- provide live camera output

This matches the existing design in which Unity is the main controller and the ESP32 is the robot-side controller. 

### 8. Video delivery

#### 8.1 Chosen architecture

Video should be delivered **directly from the assigned robot to the assigned player’s phone** where the event network allows it. Control, assignment, hit logic, and match state shall continue to pass through the laptop server.

This gives the preferred hybrid structure:

- **laptop** = authoritative game and session server
- **robot → phone** = video path

#### 8.2 Why direct video is preferred

Relaying all video through the laptop would make the laptop the bottleneck and add unnecessary network load. Direct delivery is therefore preferred for the player feed. The laptop may still provide the phone with the correct stream URL or token.

#### 8.3 Fallback

If direct phone-to-robot streaming is not possible on the EMF network, the system may fall back to relaying video via the laptop server.

#### 8.4 Video constraints

The current ESP32-S3 camera path should be treated as **MJPEG-based**, not hardware H.264/H.265. Espressif states that ESP32-S3 supports MJPEG capture and does not provide hardware-accelerated H.264/H.265 encoding. Because of that, high-resolution/high-frame-rate multi-stream video is bandwidth-heavy. 

For the EMF MVP:

- six simultaneous **640×480 @ 30 fps** streams shall **not** be treated as a design target
- video shall instead be tuned for **usable aiming and driving**, not high visual quality
- frame rate and quality shall be reduced as needed to keep the installation reliable on a crowded shared network

### 9. Wi-Fi provisioning for robots

Robots should not require reflashing over USB on arrival at EMF just to join the event network.

#### 9.1 Preferred approach

Each robot shall store **editable Wi-Fi profiles** in non-volatile storage. At minimum, the robot should support:

- a home/test profile
- an EMF event profile
- optionally one fallback/debug profile

The robot shall attempt saved profiles in order until a connection succeeds.

#### 9.2 Configuration fields

The stored EMF profile shall support:

- SSID
- username
- password
- any enterprise auth parameters required by the event network

This is important because EMF’s currently published secure wireless setup uses enterprise authentication rather than a simple home-style pre-shared password. 

#### 9.3 Update method

The preferred update path shall be a **simple USB credential writer** or equivalent configuration tool, rather than relying on BLE provisioning during setup.

#### 9.4 BLE provisioning

Bluetooth provisioning may be supported as an optional fallback, but it shall not be the primary setup path for the installation.

### 10. Session and match flow

#### 10.1 Pre-match

Before the match starts, the operator shall be able to:

- adjust game parameters such as damage and match duration
- test individual robots
- open the lobby
- assign players to teams/robots
- kick/disconnect players if necessary

#### 10.2 Join flow

The join flow shall be:

1. operator starts the server
2. player-facing display shows QR code
3. player scans QR code
4. phone loads web page from laptop
5. player enters a name
6. player waits in lobby
7. operator assigns player to a robot/team
8. operator starts the match

#### 10.3 In-match client UI

During gameplay, the phone UI shall show:

- drive control
- turret control
- fire button
- tank hit points
- live camera view

If the player’s tank is destroyed, the client shall disable controls and show an eliminated/dead state. At the end of the match, the client shall show game over and the winning team. 

### 11. MVP gameplay rules

The MVP gameplay mode shall be **Simple Deathmatch**.

#### 11.1 Rules

- two teams battle each other
- objective is to destroy all enemy tanks
- no friendly fire
- hits reduce hit points
- rear hits may apply increased damage
- tanks have a recharge/cooldown between shots
- match ends when one team is destroyed or time expires
- if time expires, winner is determined by surviving tanks, then total damage dealt

These rules are already aligned with the current gameplay notes. 

#### 11.2 Stretch goals

Capture points, shield recharge, and similar RFID-driven objectives shall be treated as post-MVP features and shall not block the deathmatch MVP. The hardware and design already anticipate RFID-based gameplay expansion.  

### 12. Robot behaviour

Each robot shall support:

- skid-steer drive
- left/right turret motion
- top LED feedback
- buzzer sound effects
- IR firing
- directional hit detection
- first-person camera feed

Top LED behaviour shall support:

- HP indication
- white flash on firing
- red flash on hit

Buzzer behaviour shall support:

- firing sound
- hit sound 

### 13. Player-facing display

The player-facing display shall show:

- QR code for joining the current session
- session-specific URL/code
- selected gameplay information as appropriate

The QR code/session link should be unique per session or match so that stale players are less likely to reconnect later. 

### 14. Operational assumptions and open risks

The system design assumes:

- the EMF network will permit phone-to-laptop access on the LAN
- ideally it will also permit phone-to-robot direct video access
- EMF’s final 2026 Wi-Fi details may differ from the currently published 2024-labelled examples
- direct peer access and the exact auth details must be confirmed with EMF NOC before the event 

The main technical risks are:

- event-network reachability
- enterprise Wi-Fi provisioning
- multi-stream MJPEG bandwidth
- browser/device variability on player phones

