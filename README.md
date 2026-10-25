# CMZDedicatedServers

A dedicated server host for **CastleMiner Z** built in **C# / .NET Framework 4.8.1**.

This project now uses a **split host layout** with two dedicated server implementations:
- **CMZDedicatedSteamServer** for Steam-based dedicated hosting
- **CMZDedicatedLidgrenServer** for direct-IP / Lidgren-based dedicated hosting

Both hosts load the original game/runtime assemblies through reflection, start a compatible dedicated server runtime, and host persistent world state outside the normal game client.

## What it does

- Starts a dedicated CastleMiner Z compatible server by IP and port
- Loads the original game assemblies at runtime via reflection
- Supports a configurable `game-path` instead of requiring the game under a hardcoded local `Game` folder
- Loads Harmony from a local `Libs` folder instead of next to the executable
- Handles connection approval, discovery/session metadata, and gameplay packet relay
- Hosts world state server-side through `ServerWorldHandler`
- Loads `world.info`, chunk delta data, and player inventories from disk
- Relays player visibility, movement, text, block edits, chunk requests, pickups, and inventory flow
- Keeps authoritative day/time progression server-side and periodically broadcasts it to clients
- Automatically respawns all players in Endurance mode when every real connected player is dead, preventing dedicated servers from getting stuck on "Waiting for host to restart"
- Can persist and restore world time between restarts through the built-in RememberTime plugin
- Supports a configurable bind IP, port, player count, world GUID, view distance, and tick rate
- Separates the dedicated server implementations into **Steam** and **Lidgren** projects while keeping the shared project flow familiar
- Includes server-side Player Enforcement commands for listing players, hard-kicking, banning, unbanning, and viewing saved bans
- Hardens host authority by validating packet sender identity and blocking client-authored host-only messages such as forced host migration and spoofed kick packets.
- Logs command usage to dated audit files under `Logs\commands-yyyy-MM-dd.log` when `log-command-audit=true`.
- Includes a built-in VanillaSpawners plugin that can prevent new vanilla spawner blocks and random vanilla loot blocks from generating, while optionally blocking existing spawner activation.
- Supports configurable command prefix aliases such as `!` and `/` for in-game server commands.
- Supports escaped multiline Announcements plugin messages using `\n`.
- Supports external server plugin DLLs under each host's `Plugins` folder, with optional per-plugin `Dependencies` folders.
- Includes a shared `CMZDedicatedServer.PluginAPI` project for transport-neutral plugin contracts.
- RegionProtect supports name-based allow-lists and SteamID64 allow-lists on the Steam dedicated server.
- Treats every client packet as an untrusted request through the built-in server policy (`Trust\ServerPolicy.cs`), Minecraft-style: movement speed, stamina, flight, noclip/embed, teleport, reach, inventory/item provenance, held-item claims, block edits, crates, pickups, enemy spawning/control, combat claims, explosives, dragon control, and server-authority messages are all validated against server-owned state before anything is accepted or relayed. Violations strike per kind, kicks persist per SteamID, and repeat offenders escalate into timed auto-bans.
- Sends the real protocol `KickMessage` (plus a private reason text) before transport deny so kicked players actually leave the world instead of lingering in a zombie session.
- Automatically generates a fresh world when `world-guid` is configured but no `world.info` or backup exists.
- Self-heals the hosted Steam lobby: a once-a-minute liveness probe detects when Steam has silently dropped the lobby and recreates it automatically, so a long-running server no longer vanishes from the in-game browser while still running.
- Cleans up orphaned dragons server-side: when the current dragon controller disconnects, the removal is broadcast to every player exactly once instead of leaving a ghost dragon behind.

## Project layout

```text
CMZDedicatedServers/
├─ LICENSE
├─ README.md
├─ GUI-TODO.md
├─ BuildPlugin.ps1          # menu script: builds the server or Shared/Plugins projects
├─ Directory.Build.props
├─ Build/
│  ├─ CMZDedicatedSteamServer/    # deployed exe, server.properties, Plugins/, Logs/, Worlds/
│  └─ CMZDedicatedLidgrenServer/
├─ CMZDedicatedServers.sln
├─ CMZDedicatedServer.PluginAPI/
│  ├─ CMZDedicatedServer.PluginAPI.csproj
│  └─ Plugins/
│     └─ ServerPluginContracts.cs
├─ Shared/                        # created on demand: shared plugin source projects
│  └─ Plugins/
│     └─ Example/
│        ├─ Example.csproj
│        └─ ServerExamplePlugin.cs
├─ CMZDedicatedLidgrenServer/
│  ├─ CMZDedicatedLidgrenServer.csproj
│  ├─ Program.cs
│  ├─ Commands/                   # in-game command registry, ranks, permission store
│  │  ├─ ServerCommandManager.cs
│  │  ├─ ServerCommandContext.cs
│  │  ├─ ServerCommandParser.cs
│  │  ├─ ServerCommandPermissionStore.cs
│  │  └─ ServerCommandRank.cs
│  ├─ Config/
│  │  └─ ServerConfig.cs
│  ├─ Hosting/
│  │  ├─ LidgrenServer.cs
│  │  ├─ ServerAssemblyLoader.cs
│  │  └─ ServerRuntime.cs
│  ├─ Networking/
│  │  └─ CmzMessageCodec.cs
│  ├─ Patching/
│  │  └─ ServerPatches.cs
│  ├─ Plugins/                    # built-in plugins compiled into the host
│  │  ├─ ServerPluginManager.cs
│  │  ├─ Announcements/
│  │  ├─ FloodGuard/
│  │  ├─ RegionProtect/
│  │  ├─ RememberTime/
│  │  └─ VanillaSpawners/
│  ├─ Templates/
│  │  └─ server.properties
│  ├─ World/
│  │  └─ ServerWorldHandler.cs
│  ├─ Libs/
│  │  └─ 0Harmony.dll
│  └─ ServerHost/
│     ├─ RunServer.bat
│     ├─ Game/
│     ├─ Inventory/
│     ├─ Libs/
│     └─ Worlds/
└─ CMZDedicatedSteamServer/
   ├─ CMZDedicatedSteamServer.csproj
   ├─ Program.cs
   ├─ ServerGuiForm.cs
   ├─ Commands/                   # in-game command registry, ranks, permission store
   │  ├─ ServerCommandManager.cs
   │  ├─ ServerCommandContext.cs
   │  ├─ ServerCommandParser.cs
   │  ├─ ServerCommandPermissionStore.cs
   │  └─ ServerCommandRank.cs
   ├─ Common/
   │  └─ ReflectEx.cs
   ├─ Config/
   │  └─ SteamServerConfig.cs
   ├─ Hosting/
   │  ├─ ServerAssemblyLoader.cs
   │  ├─ ServerBanStore.cs
   │  ├─ SteamConnectionApproval.cs
   │  ├─ SteamDedicatedServer.cs
   │  ├─ SteamLobbyHost.cs
   │  └─ SteamPeerRegistry.cs
   ├─ Networking/
   │  └─ CmzMessageCodec.cs
   ├─ Patching/
   │  └─ ServerPatches.cs
   ├─ Plugins/                    # built-in plugins compiled into the host
   │  ├─ ServerPluginManager.cs
   │  ├─ Announcements/
   │  ├─ FloodGuard/
   │  ├─ RegionProtect/
   │  ├─ RememberTime/
   │  ├─ VanillaSpawners/
   │  └─ DiscordPlugin/
   ├─ Steam/
   │  └─ SteamServerBootstrap.cs
   ├─ Templates/
   │  ├─ server.properties
   │  └─ steam_appid.txt
   ├─ Trust/                      # core server-authority enforcement (not a plugin)
   │  ├─ ServerPolicy.cs
   │  ├─ ItemPolicy.cs
   │  ├─ ShadowCombatTracker.cs
   │  ├─ ShadowCombatTracker.Projectiles.cs
   │  ├─ ShadowVoxelMotion.cs
   │  └─ CombatSimulationNotes.md
   ├─ World/
   │  ├─ ServerWorldHandler.cs
   │  └─ TerrainProbe.cs
   ├─ Libs/
   │  └─ 0Harmony.dll
   └─ ServerHost/
      ├─ RunServer.bat
      ├─ Game/
      ├─ Inventory/
      ├─ Libs/
      └─ Worlds/
```

## Main components

### `Program.cs`
Entry point for each dedicated host.

It:
- loads `server.properties`
- resolves the game binaries folder from `game-path` or falls back to the local server layout
- resolves support libraries from the local `Libs` folder
- loads `CastleMinerZ.exe` and related assemblies
- applies Harmony patches
- prints a startup summary
- starts the server and enters the update loop

### `CMZDedicatedLidgrenServer`
The direct-IP / Lidgren dedicated host.

It is responsible for:
- binding the socket
- connection approval
- direct-IP traffic handling
- gameplay packet relay
- player-exists cache/replay for new joiners
- live connection enumeration for outgoing sends
- pickup consume relay support
- authoritative day/time progression and periodic world time broadcasts

Key files include:
- `Hosting/LidgrenServer.cs`
- `Hosting/ServerRuntime.cs`
- `World/ServerWorldHandler.cs`
- `Networking/CmzMessageCodec.cs`
- `Patching/ServerPatches.cs`

### `CMZDedicatedSteamServer`
The Steam-based dedicated host.

It is responsible for:
- Steam dedicated server bootstrap and registration
- Steam connection approval and peer tracking
- lobby and Steam networking host flow
- gameplay packet relay through the reflected game runtime
- authoritative world hosting and persistence

Key files include:
- `Hosting/SteamDedicatedServer.cs`
- `Hosting/SteamLobbyHost.cs`
- `Hosting/SteamConnectionApproval.cs`
- `Hosting/SteamPeerRegistry.cs`
- `Steam/SteamServerBootstrap.cs`
- `World/ServerWorldHandler.cs`
- `Networking/CmzMessageCodec.cs`

### `CMZDedicatedServer.PluginAPI`
The shared plugin API project contains transport-neutral server plugin contracts and context types used by both dedicated hosts.

It is intended for external and shared server plugins to reference without depending on Steam-only or Lidgren-only internals.

### `Shared/Plugins`
The shared plugin source tree can hold reusable plugin projects that target both hosts.

A plugin under `Shared/Plugins/<PluginName>/` can build once and copy its output into both:

```text
Build\CMZDedicatedSteamServer\Plugins\<PluginName>\
Build\CMZDedicatedLidgrenServer\Plugins\<PluginName>\
```

### Shared responsibilities
Across both hosts, the project still centers around the same core flow:
- config loading
- reflected game/runtime loading
- packet/message translation
- server-side world persistence
- inventory/world save handling
- authoritative time/day progression

## Requirements

- Windows
- **.NET Framework 4.8.1**
- Visual Studio / MSBuild capable of building .NET Framework projects
- The original CastleMiner Z game files available somewhere on disk

The game files do **not** have to live under a folder literally named `Game` as long as `game-path` points to the correct location.

## Configuration

Each dedicated host reads configuration from `server.properties` in its server root.

Example:

```properties
server-name=CMZ Server
game-name=CastleMinerZSteam
network-version=4

game-path=C:\Program Files (x86)\Steam\steamapps\common\CastleMiner Z

server-ip=0.0.0.0
server-port=61903
max-players=8

steam-user-id=76561198296842857
world-guid=b8c81243-b6ac-48fe-a782-1e2dc5a44d17

view-distance-chunks=8
tick-rate-hz=60

game-mode=1
pvp-state=0
difficulty=1
```

### Dynamic server-name tokens

The `server-name` value supports simple runtime tokens. These tokens are replaced by the dedicated server before the name is shown to players.

Example:

```properties
server-name=Test Server | Day {day}
```

This may appear in the server browser or join/session info as:

```text
Test Server | Day 12
```

Supported tokens:

| Token          | Description                             | Example |
| -------------- | --------------------------------------- | ------- |
| `{day}`        | Current player-facing world day.        | `12`    |
| `{day00}`      | Current world day padded to two digits. | `07`    |
| `{players}`    | Current connected player count.         | `3`     |
| `{maxplayers}` | Configured maximum player count.        | `32`    |

Example with player count:

```properties
server-name=Test Server | Day {day00} | {players}/{maxplayers}
```

Example output:

```text
Test Server | Day 07 | 3/32
```

Notes:

* Tokens are optional. A normal static name such as `server-name=CMZ Server` still works.
* The day value is controlled by the dedicated server's authoritative time progression.
* Very long names may be shortened before being published to the server/session browser.

For **CMZDedicatedLidgrenServer**, the resolved name is sent through discovery responses and join-time server/session info packets. The raw template remains in `server.properties`, but compatible clients see the resolved display name.

Example:

```properties
server-name=Test Server | Day {day} | dsc.gg/cforge
```

DirectConnect/session display:

```text
Test Server | Day 12 | dsc.gg/cforge
```

### Server message

The `server-message` value controls the player-facing in-game/session message.

Example:

```properties
server-message=Welcome to the CastleForge 24/7 server! Discord: dsc.gg/cforge
```

The message can also use the same runtime tokens as `server-name`:

```properties
server-message=Welcome! Current day: {day}. Players online: {players}/{maxplayers}. Discord: dsc.gg/cforge
```

Supported tokens:

| Token          | Description                             | Example |
|----------------|-----------------------------------------|---------|
| `{day}`        | Current player-facing world day.        | `12`    |
| `{day00}`      | Current world day padded to two digits. | `07`    |
| `{players}`    | Current connected player count.         | `3`     |
| `{maxplayers}` | Configured maximum player count.        | `32`    |

For **CMZDedicatedLidgrenServer**, the resolved message is sent through discovery/session info so compatible clients can show it separately from the server name.
For **CMZDedicatedSteamServer**, the resolved message is published through the Steam-hosted session/lobby metadata used by CastleMiner Z's browser/details flow.

> Steam note: CastleMiner Z's vanilla Steam browser may use the same Steam session metadata field for the displayed server message/details text, so the visible browser/details behavior can depend on how the Steam lobby metadata is consumed by the client.

### Config fields

| Key | Purpose |
|------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| `server-name`          | Display name shown to clients                                                                                               |
| `server-message`       | Player-facing in-game/session message. Supports dynamic tokens such as `{day}`, `{day00}`, `{players}`, and `{maxplayers}`. |
| `game-name`            | Expected network game name                                                                                                  |
| `network-version`      | Expected protocol version                                                                                                   |
| `game-path`            | Optional path to the CastleMiner Z binaries folder; falls back to the local server layout when omitted                      |
| `server-ip`            | Bind address (`0.0.0.0` or `any` binds all interfaces)                                                                      |
| `server-port`          | Port clients connect to                                                                                                     |
| `max-players`          | Maximum connected players                                                                                                   |
| `steam-user-id`        | Save-device / world key identity used for storage access                                                                    |
| `world-guid`           | GUID used to locate the world folder                                                                                        |
| `view-distance-chunks` | Chunk radius used by the host                                                                                               |
| `tick-rate-hz`         | Server update loop rate                                                                                                     |
| `game-mode`            | Session game mode value. Within Endurance; when all players are dead, the server automatically sends a restart message.     |
| `pvp-state`            | Session PVP state value                                                                                                     |
| `difficulty`           | Session difficulty value                                                                                                    |

## Building

### Option 1: Use the included batch script

```bat
build.bat
```

This script:
- locates MSBuild using `vswhere`
- restores and builds the dedicated-server solution
- writes output into the shared root `Build` folder
- keeps the Steam and Lidgren outputs separated into their own build folders

Expected build output:

```text
Build/
├─ CMZDedicatedSteamServer/
│  ├─ CastleForge.ServerPluginAPI.dll
│  └─ Plugins/
│     └─ Example/
│        └─ Example.dll
└─ CMZDedicatedLidgrenServer/
   ├─ CastleForge.ServerPluginAPI.dll
   └─ Plugins/
      └─ Example/
         └─ Example.dll
```

### Option 2: Build from Visual Studio / MSBuild

Solution:

```text
src/CMZDedicatedServers.sln
```

Project files:

```text
src/CMZDedicatedSteamServer/CMZDedicatedSteamServer.csproj
src/CMZDedicatedLidgrenServer/CMZDedicatedLidgrenServer.csproj
src/CMZDedicatedServer.PluginAPI/CMZDedicatedServer.PluginAPI.csproj
src/Shared/Plugins/Example/Example.csproj
```

Important project settings:
- Target framework: `net481`
- Platform target: `x86`
- Shared build root: `Build\`
- Steam host output path: `Build\CMZDedicatedSteamServer\`
- Lidgren host output path: `Build\CMZDedicatedLidgrenServer\`
- Shared plugin API project: `CMZDedicatedServer.PluginAPI`
- Shared plugin source projects: `Shared\Plugins\<PluginName>\`

## Running

After building, run the host you want to use.

### Steam host

```bat
Build\CMZDedicatedSteamServer\CMZDedicatedSteamServer.exe
```

### Lidgren host

```bat
Build\CMZDedicatedLidgrenServer\CMZDedicatedLidgrenServer.exe
```

You can also use the included per-host helper scripts under each project's `ServerHost` folder if you prefer to maintain a host-specific runtime layout.

On startup the server prints a summary similar to:

```text
CMZ Dedicated Server
--------------------
GameName         : CastleMinerZSteam
NetworkVersion   : 4
Bind             : 0.0.0.0:61903
ServerName       : CMZ Server
MaxPlayers       : 8
SaveOwnerSteamId : 76561198296842857
WorldGuid        : ...
WorldFolder      : Worlds\...
WorldPath        : ...
WorldInfo file   : ...\world.info
World loaded     : True
```

Then connect using the server IP and the configured `server-port`.

## Desktop GUI

Launching `CMZDedicatedSteamServer.exe` with no arguments opens a desktop window that wraps the server:

- **Header**: Start / Stop / Restart controls plus live status chips — status dot, connected players, uptime, **TPS** (real ticks/sec measured by the server loop), and memory usage. TPS colors: green ≥50, amber 30–50, red <30.
- **Console tab**: streamed server output with category colors, live search (highlights every match as you type; Enter jumps to the next), pause-scroll toggle, a command bar with Up/Down history and a quick-commands dropdown.
- **Players tab**: connected player cards with name, SteamID, session time, and Kick/Ban/copy actions (expand arrow or right-click).
- **Settings tab**: a `server.properties` editor that mirrors the file's own sections and order — every key in the file gets a row, including ones the editor doesn't recognize (shown under their raw name). Boolean and enumerated fields get wheel-safe dropdowns (scrolling the page can't flip values); **Save to file** writes back preserving comments and ordering (with a `.bak` backup); a `FileSystemWatcher` auto-reloads the editor when the file changes on disk, and a filter box narrows rows live.
- **Appearance**: accent color presets + custom picker and console text color, stored in `GuiSettings.ini` next to the exe.
- **Terminal button**: detaches the running server into a real console window (the child process calls `AllocConsole`) without restarting — output tees to both sides so GUI history stays complete. The console's X is disabled (closing a console force-kills the owning process); type `gui` + Enter in the terminal to return to the GUI. Ctrl+C still stops the server.

The GUI runs the server as a hidden `--console` child process; closing the window requires stopping the server first.

## Game binaries and support libraries

### Game folder
The server needs access to the real CastleMiner Z binaries and content.

Each host can be pointed at the real game install using `game-path`, for example:

```properties
game-path=C:\Program Files (x86)\Steam\steamapps\common\CastleMiner Z
```

If you prefer a local runtime layout instead, the host-specific `ServerHost\Game\` folders can be used as the local game-content location.

### Harmony
`0Harmony.dll` is expected under the host's local `Libs` folder/runtime layout.

Examples:

```text
src\CMZDedicatedSteamServer\Libs\0Harmony.dll
src\CMZDedicatedLidgrenServer\Libs\0Harmony.dll
```

And for local host layouts:

```text
src\CMZDedicatedSteamServer\ServerHost\Libs\0Harmony.dll
src\CMZDedicatedLidgrenServer\ServerHost\Libs\0Harmony.dll
```

It does not need to live next to the executable.

### Server plugin API
External server plugins reference the shared plugin API assembly.

For public/release builds, the intended friendly DLL name is:

```text
CastleForge.ServerPluginAPI.dll
```

This file should live beside each dedicated server executable:

```text
Build\CMZDedicatedSteamServer\CastleForge.ServerPluginAPI.dll
Build\CMZDedicatedLidgrenServer\CastleForge.ServerPluginAPI.dll
```

Do not copy a separate copy of the API DLL into each plugin's `Dependencies` folder. Keeping one shared API assembly loaded by the server avoids plugin type identity problems.

## World and save data

The host derives the world folder from `world-guid` and expects it under the selected host's world storage layout:

```text
Worlds\{world-guid}
```

Typical files used by the host include:
- `world.info`
- chunk delta data
- player inventory saves

If `world-guid` is configured but neither `world.info` nor a backup exists, the host **generates a fresh world automatically** on first start — no manual world file editing or restart is needed. The generated world is then persisted like any other save.

Both dedicated hosts keep the same general world/save flow even though the repository is now split into two server implementations.

## Networking notes

The current implementation includes:
- compatible gameplay packet handling through the reflected game runtime
- direct send and broadcast wrapper handling
- player visibility bootstrap via cached/replayed `PlayerExistsMessage`
- relay of text/chat-style packets and gameplay updates
- server-side pickup resolution for create/request/consume flow
- live connection enumeration to avoid stale connection lists on outbound sends

The exact transport/bootstrap behavior differs by host:
- **CMZDedicatedSteamServer** focuses on Steam hosting/bootstrap flow
- **CMZDedicatedLidgrenServer** focuses on direct-IP / Lidgren hosting flow

## Packet security / host authority

Both dedicated hosts validate packet sender identity before relaying gameplay packets. Client-declared sender ids must match the actual connected peer assigned by the server.

The hosts also block client-authored host-only packets, including forced host migration and spoofed kick-style packets. This keeps host authority on the dedicated server instead of allowing a modified peer to appoint itself as host or remove other players.

This protection is built in and has no required configuration.

## Steam lobby watchdog

The Steam host's in-game browser listing lives in a Steam lobby owned by the host account. Steam destroys a user's owned lobbies whenever the client session drops — and it does so **silently**: metadata updates to the dead lobby keep "succeeding" locally, so a long-running dedicated server could disappear from the browser while logging healthy "Updated lobby metadata" lines.

To prevent this, the Steam host probes its own lobby once a minute by asking Steam to re-read the lobby data. If the lobby is actually gone, the probe flips the native session state to *not in session*, which the watchdog uses as the signal to mark the lobby lost and create a fresh one a few seconds later — retrying until Steam cooperates, so the listing recovers on its own with no restart needed.

The watchdog only recreates on positive proof the lobby is gone. A still-alive lobby answers the probe cleanly and is never touched, and when Steam itself is unreachable the probe simply yields no answer instead of risking a duplicate "ghost" lobby in the browser.

## Server trust policy

The Steam host includes a core server-authority layer in `Trust\ServerPolicy.cs` — not a plugin. Every client packet is treated as an untrusted request and validated against server-owned state before it is accepted or relayed. The design goal is the same model a Minecraft dedicated server uses: a client cannot simply *claim* something happened; the server decides whether it is allowed. A client can't change its own game mode, can't grant itself items, can't fly, and can't teleport unless the server's rules allow it.

### What is checked

| Area | Enforcement |
|------|-------------|
| Movement speed | Position updates are compared against the accepted baseline; sustained speed above ~20 m/s counts violations, above ~40 m/s kicks immediately. Falls are excluded — only upward vertical counts. |
| Stamina | A server-side shadow pool tracks sprint drain and regen in real time. Sprinting on an empty pool strikes; in game modes where sprint is disabled (Endurance, DragonEndurance) any sprint-speed update drains it instantly. |
| Flight / hover | Sustained airborne time without support counts fly violations; fast flight usually trips the speed cap first. |
| Noclip / embed | Checked against the terrain probe plus the saved block-delta overlay. Only a fully buried player counts — feet solid, head solid, solid cells above the head, and solid lateral neighbors — so bumping into a wall never false-flags. |
| Teleport | Distance beyond the snap threshold counts unless the game mode permits it; re-teleports inside the cooldown are rate violations. The baseline resyncs to the accepted position so legitimate lag teleports cannot cascade into false kicks. |
| Reach | Block edits and pickup requests beyond ~7 m are rejected. Reach grows with position staleness so lag and frozen baselines don't produce false positives. Claimed digs of air are treated as desync, not cheats. |
| Block edits | Placement/digging is checked against the authoritative overlay **before** the world state changes: forged/non-vanilla block types, undiggable blocks, and region-protected edits are rejected. |
| Inventory / items | Server-owned baselines track provenance: what a player provably owns, credits earned (picked up, crafted), and ammo spent. Unearned items, forged stacks, and unexplained ammo gains are denied; craft chains are solved automatically so honest crafting passes. |
| Phantom items | Once a save reconcile *proves* an item unearned, that id is flagged — dropping it, placing it, equipping it, firing it, or swinging it is an instant kick. |
| Held-item claims | Gunshots, rockets, grenades, and melee claims must name the weapon the client last reported holding. Shooting rockets while holding a pistol is denied. |
| Pickups / crates | Create/request/consume flow is resolved server-side with reach checks; forged drops are rejected while legitimate drops stay reclaimable. |
| Enemy spawning / kills | Spawn and kill claims are checked against a live enemy registry plus a shadow-combat simulation. Fabricated claims — instant kills, impossible time-to-kill, repeated kill-aura sweeps, kills with a gun that never fired, and melee kill floods — are denied and strike. |
| Canary enemies | The server plants its own unreachable decoy enemies and retires them itself. A kill claim naming a canary is provably fabricated — instant strike, no grace, and the trap stays armed. |
| Explosives | Blast loot drops are correlated to real detonations near real loot blocks; forged drops are rejected. Lethal blast exposure arms the godmode check — surviving a provably lethal blast strikes. |
| Dragons | The server runs dragon scheduling itself instead of trusting client spawn requests. Update/attack/migrate messages are only legal from the current controller; when the controller disconnects the server removes the orphaned dragon for everyone. |
| Peer relay | Only genuinely peer-directed packets get relayed. Client-declared sender ids must match the real connection; relayed world mutations and spoofed senders are dropped. |
| Server authority | Client-authored host-only messages — forced host migration, spoofed kicks, server-only types — are rejected outright. |
| Ghost / invisibility | Clients must announce themselves before acting and keep streaming position and inventory normally. Suppressed announcements, actions while dead, spoofed positions, and silent clients all strike. |
| Malformed packets | Declared payload lengths are bounded before allocation, so crafted oversized counts can't alloc-bomb the host. Parse failures and unregistered message ids are dropped, never relayed, and count as protocol violations. |
| Chat / relay forgery | Messages forged in another player's name or carrying a fake `[Server]` prefix are denied and strike. |
| Flood | FloodGuard blackholes senders above ~120 packets/sec for 3 seconds. |

### Ranks — who is trusted and who is not

**Nobody is trusted except Moderator and Admin.** Rank order: `Visitor < Member < Moderator < Admin`.

- **Every player who joins is assigned Member** and persisted to `Commands\CommandRanks.ini` — every player always holds a member rank.
- **Member** = normal player. Fully subject to every trust check above.
- **Visitor** = restricted rank below Member, assigned by `!setrank` for guests/demoted players. Fully subject to trust checks, plus the optional `TrustVisitorEditBlock` restriction on world edits.
- **Moderator and Admin** = operators. They bypass the core trust checks — the server treats them as staff, not as clients to police.

There is no longer a `Default` rank. Legacy `Default`/`Defualt` values in old config files are migrated automatically: player-rank entries read as `Member` (ordinary player) and command-permission entries read as `Visitor` (usable by everyone).

### Violations, kicks, and bans

- Violations are counted **per kind in separate buckets** — a fly strike never adds to an item strike. Provable wire-fact kinds (reach, stamina, combat, teleport, canary, ghost, godmode, protocol, malformed) kick at **5 strikes**; timing- and desync-sensitive kinds (fly, speed, noclip, item) get more slack and kick at **10**.
- Each bucket **decays independently after ~120 seconds idle** — a messy minute does not follow a player for the whole session.
- Some offenses skip counting entirely: proven phantom-item use, identity forgery, and sustained extreme speed are **instant kicks**.
- Once a kick is issued, `KickIssued` latches: later packets from that player are consumed silently with no extra violations or duplicate kick attempts.
- The kick sequence sends a private `[Server] Kicked...` text, then the real protocol `KickMessage` so the client drops to the actual kicked/banned dialog, then transport `Deny` cleans up. Admin `!kick`/`!ban` use the same protocol message so every enforced drop is a real disconnect.
- **Kick counts are SteamID-keyed and persisted to `Kicks.ini`** — they survive restarts and never auto-reset. Per-session violation state resets on rejoin; the kick count does not. Rejoining cannot launder it.
- Kicks inside the escalation window convert to **auto-bans** persisted in `Bans.ini` on a growing ladder — **1, 3, 7, 14, then 30 days, permanent at the 6th ban**. Cheat longer, stay gone longer.
- `SteamDedicatedServer.Update()` drains at most 512 packets per tick, so a packet flood cannot starve the pending-drop queue and delay a kick.

### Honest limits

Client-side rendering cheats — X-ray, ESP, wallhacks, fullbright, minimap reveal, aim-assist visuals — cannot be proven from server-side evidence alone and are out of scope for this layer.

## Player Enforcement

Both dedicated hosts include basic console-based player enforcement.

Supported commands:

| Command                  | Description                    |
|--------------------------|--------------------------------|
| `players`                | Lists connected players.       |
| `bans`                   | Lists saved bans.              |
| `kick <target> [reason]` | Hard-kicks a connected player. |
| `ban <target> [reason]`  | Bans and hard-drops a player.  |
| `unban <target>`         | Removes a saved ban.           |

Examples:

```text
players
bans
kick 2 Being annoying
ban 2 Griefing protected areas
ban "Jacob Smith" Griefing protected areas
unban jacob
```

Steam bans are SteamID-backed and also save the last known player name for readability.

Lidgren bans are IP/name-backed and are useful for direct-IP hosting, but they are weaker than SteamID bans because names and IP addresses can change.

Player Enforcement sends the real protocol `KickMessage` so the client shows the kicked/banned dialog and leaves the session, followed by a transport-level deny for cleanup. Modified clients that ignore the kick message are still severed by the deny, and normal clients exit immediately instead of lingering in a zombie session.

### Command ranks and in-game commands

The hosts also provide in-game commands using configurable prefixes (`!` and `/` by default): `help`, `status`, `players`, `kick`, `ban`, `unban`, `reload`, `restart`, `op`, `deop`, `setrank`.

Every connected player holds a rank stored in `Commands\CommandRanks.ini` (auto-assigned `Member` on first join); each command's required rank lives in `Commands\CommandPermissions.ini`:

```text
Visitor < Member < Moderator < Admin
```

`Visitor` is a restricted rank for demoted/guest players. `Moderator` and `Admin` are operator ranks — they also bypass the server policy described above. `op`/`deop`/`setrank` manage the rank file at runtime, e.g. `!setrank <player> Moderator`.

## Time / day progression

The dedicated host advances world time using **real elapsed time** rather than fixed loop iterations.

This is important because the server loop may run faster or slower than a normal 60 FPS client host. The current implementation keeps authoritative day progression on the server and periodically broadcasts the current world day/time to clients.

## Endurance auto-respawn behavior

When `game-mode=0` is used, CastleMiner Z's normal Endurance flow expects the original host player to restart the level after everyone dies.

Dedicated servers do not have a real playable host sitting on the death screen, so players could otherwise remain stuck on:

```text
Waiting for host to restart
```

To prevent this, the dedicated host tracks the dead/alive state of real connected players. When every real player is dead, the server automatically sends the vanilla restart/respawn message to connected clients.

This does **not** restart the dedicated server process and does **not** intentionally reset the world back to Day 1. After the respawn message is sent, the server continues using its authoritative day/time value and broadcasts that time back to clients.

Notes:

* This behavior only applies to `game-mode=0` / Endurance.
* The synthetic dedicated-server host/player is ignored and does not count as an alive player.
* The world save, chunks, inventories, and server process remain active.
* If the client briefly appears to reset its local day display, the server's authoritative time broadcast corrects it.

## Notes and current implementation details

- The server is built around **reflection** rather than direct game project references.
- The hosts use the original game message types and message registry where possible.
- The server update loop is driven by `tick-rate-hz`. The host raises the process timer resolution (`timeBeginPeriod(1)`) at startup — without it Windows caps sleeps at ~15.6 ms slices and the loop tops out near 32 ticks/s no matter what `tick-rate-hz` is set to.
- Pickup, inventory, chunk, and terrain flow are now handled server-side well enough for basic dedicated play.
- The repository now keeps **Steam** and **Lidgren** as separate projects under one solution.
- The root build process outputs both hosts into the shared `Build` folder.
- External plugin DLLs are loaded after the built-in plugins are registered, then all registered plugins are initialized together.

## Server Plugins

CastleForge Dedicated Servers include **server-side plugin support** for host-authoritative world protections, server automation, and future server extensions.

Plugins run inside the dedicated server process and can inspect selected host/world packets before the server applies or relays them. This allows the server to enforce rules even when connecting players do **not** have the matching client-side mod installed.

Both hosts support two plugin styles:

- **Built-in plugins** compiled directly into the dedicated server build and registered by the host at startup.
- **External plugin DLLs** loaded from each server's runtime `Plugins` folder.

Current built-in plugin support includes:

- **Announcements** private join messages and timed global messages
- **FloodGuard** malicious packet spam protection
- **RememberTime** per-world time persistence between restarts
- **RegionProtect** server-side region enforcement: block mining/placing protection, explosion protection, crate item protection, and crate break protection inside configured regions
- **Player Enforcement** commands for listing players, hard-kicking, banning, unbanning, viewing saved bans, managing command ranks, and protecting operators from kick/ban commands
- **VanillaSpawners** vanilla spawner and random loot block generation controls

Plugins keep global settings in `Plugins\<PluginName>\<PluginName>.Config.ini` and store per-world data under `Plugins\<PluginName>\Worlds\{WorldGuid}\` (for example `RememberTime\Worlds\{guid}\Time.State.ini`), so switching `world-guid` gives each world its own plugin state.

### Plugin API

External plugins should reference the shared server plugin API project:

```text
CMZDedicatedServer.PluginAPI
```

The public runtime assembly is intended to be copied beside each dedicated server executable:

```text
Build\CMZDedicatedSteamServer\CastleForge.ServerPluginAPI.dll
Build\CMZDedicatedLidgrenServer\CastleForge.ServerPluginAPI.dll
```

If your local project file still outputs `CMZDedicatedServer.PluginAPI.dll`, either rename the project `AssemblyName` to `CastleForge.ServerPluginAPI` or adjust the runtime/docs to use the technical DLL name consistently.

The plugin API contains the transport-neutral contracts and context types used by both hosts, such as:

- `IServerPlugin`
- `IServerWorldPlugin`
- `IServerInboundPacketPlugin`
- `IServerPlayerEventPlugin`
- `IServerTickPlugin`
- `IServerShutdownPlugin`
- `IServerCommandPlugin`
- `PluginCommand`
- `PluginCommandContext`
- `ServerPluginContext`
- `HostMessageContext`
- `ServerInboundPacketContext`
- `ServerPlayerEventContext`

Shared plugins should depend on these server callbacks, not on Steam-only or Lidgren-only transport internals.

#### Plugin commands

Plugins implementing `IServerCommandPlugin` return `PluginCommand` entries that are registered as in-game/server-console commands on the Steam host:

- `Name` / `Description` — command name without the prefix and the help text.
- `Handler` — legacy `Action<string[]>` callback with just the argument tokens.
- `ContextHandler` — optional `Action<PluginCommandContext, string[]>` that additionally receives the sender's id/name/keys plus `SendReply` (private response), `Broadcast`, and `Log` callbacks. When set it takes precedence over `Handler`.
- `RequiredRank` — optional default rank name (`Visitor`, `Member`, `Moderator`, `Admin`). When omitted the command is usable by every rank (Visitor and up, matching the old "Default = anyone" behavior); an unrecognized value fails safe to `Admin`. Server owners can still override the required rank per command through the command-permissions file.

### External plugin DLL layout

External plugins are loaded from the host's runtime `Plugins` folder.

Example:

```text
Build/
├─ CMZDedicatedSteamServer/
│  ├─ CMZDedicatedSteamServer.exe
│  ├─ CastleForge.ServerPluginAPI.dll
│  └─ Plugins/
│     └─ Example/
│        ├─ Example.dll
│        └─ Dependencies/
│           └─ SomeDependency.dll
└─ CMZDedicatedLidgrenServer/
   ├─ CMZDedicatedLidgrenServer.exe
   ├─ CastleForge.ServerPluginAPI.dll
   └─ Plugins/
      └─ Example/
         ├─ Example.dll
         └─ Dependencies/
            └─ SomeDependency.dll
```

Dependency DLLs are optional. If a plugin has no extra DLL dependencies, the `Dependencies` folder is not required.

### Shared plugin source layout

Reusable plugins that target both hosts can live under:

```text
Shared/
└─ Plugins/
   └─ Example/
      ├─ Example.csproj
      └─ ServerExamplePlugin.cs
```

Each shared plugin project can build once and copy its output into both dedicated server runtime folders:

```text
Build\CMZDedicatedSteamServer\Plugins\Example\Example.dll
Build\CMZDedicatedLidgrenServer\Plugins\Example\Example.dll
```

### Reload behavior

The server `reload` command can reload runtime-safe config and plugin state, but it should not be treated as a DLL hot-reload system.

On .NET Framework, plugin assemblies loaded into the default AppDomain cannot be unloaded cleanly. Adding, removing, or replacing plugin DLLs requires a dedicated server restart.

### Trust model

External server plugins run in the same process as the dedicated server. Only install plugins from trusted sources, because a plugin can read files, write files, send network messages, or crash the server process.

## Announcements Server Plugin

The dedicated servers include a built-in **Announcements** plugin for simple server messages.

Announcements can:

- send a private welcome message to each joining player
- send a timed global message to all connected players
- split configured messages into multiple chat lines using escaped `\n`
- wait a configurable amount of time before the first global message
- require a minimum number of online players before global messages are sent
- reload its config from disk using the server console `reload` command, if enabled by the host

### Config location

The Announcements config is stored beside each dedicated server executable:

```text
CMZDedicatedSteamServer/
└─ Plugins/
   └─ Announcements/
      └─ Announcements.Config.ini
```

For the Lidgren dedicated server:

```text
CMZDedicatedLidgrenServer/
└─ Plugins/
   └─ Announcements/
      └─ Announcements.Config.ini
```

### Example config

```ini
[General]
Enabled = true

[Join]
PrivateJoinMessageEnabled = true
PrivateJoinMessage = Welcome {player}! This is a CastleForge dedicated server. Join us: dsc.gg/cforge\nUse '/help' or '!help' for available commands.

[Global]
TimedGlobalMessageEnabled = true
GlobalMessage = Need help, updates, or mods? Join the CastleForge Discord: dsc.gg/cforge\nCurrent players: {players}/{maxplayers}
InitialGlobalDelaySeconds = 120
GlobalMessageIntervalMinutes = 15
MinimumPlayersForGlobalMessage = 1
```

### Message tokens

Announcement messages support simple runtime tokens:

| Token          | Description                      | Example      |
|----------------|----------------------------------|--------------|
| `{player}`     | Joining player's display name.   | `RussDev7`   |
| `{players}`    | Current connected player count.  | `3`          |
| `{maxplayers}` | Configured maximum player count. | `32`         |
| `{time}`       | Current local server time.       | `8:30 PM`    |
| `{date}`       | Current local server date.       | `2026-04-26` |

### Multiline messages

Announcement messages can use escaped `\n` to split one config value into multiple chat lines.

Example:

```ini
PrivateJoinMessage = Welcome {player}!\nUse '/help' or '!help' for available commands.
GlobalMessage = Join the CastleForge Discord: dsc.gg/cforge\nPlayers online: {players}/{maxplayers}
```

The server sends each line as a separate chat message.

Example private join output:

```text
Welcome RussDev7!
Use '/help' or '!help' for available commands.
```

Example global output:

```text
Join the CastleForge Discord: dsc.gg/cforge
Players online: 3/32
```

Physical multiline INI values are not required. Keep the message on one config line and use `\n` where a line break should appear.

### Behavior

The private join message is sent only to the joining player.

The timed global message is broadcast to all connected players after `InitialGlobalDelaySeconds`, then repeats every `GlobalMessageIntervalMinutes`.

Set `MinimumPlayersForGlobalMessage = 0` to allow global messages even when the server is empty, or set it to `1` or higher to only announce when players are online.

Notes:

* `\n` is decoded by the Announcements plugin and sent as separate chat lines.
* Empty multiline entries are skipped.
* Multiline support applies to both `PrivateJoinMessage` and `GlobalMessage`.

## RegionProtect Server Plugin

The dedicated servers include a built-in **RegionProtect** plugin that protects configured world areas directly from the server.

Unlike the normal client/host RegionProtect mod, the dedicated server version does not require players to install anything client-side. The server checks protected actions before saving or relaying world changes.

### Protected actions

RegionProtect currently protects:

| Action                  | Packet handled                                     | Description                                              |
|-------------------------|----------------------------------------------------|----------------------------------------------------------|
| Mining / block breaking | `AlterBlockMessage`                                | Blocks protected terrain removal                         |
| Block placing           | `AlterBlockMessage`                                | Blocks protected block placement                         |
| Explosions              | `DetonateExplosiveMessage` / `RemoveBlocksMessage` | Blocks protected explosion damage                        |
| Crate item edits        | `ItemCrateMessage`                                 | Blocks adding/removing crate contents in protected areas |
| Crate breaking          | `DestroyCrateMessage`                              | Blocks crate destruction in protected areas              |

### Config location

RegionProtect stores its configuration beside each dedicated server executable:

```text
CMZDedicatedLidgrenServer/
└─ Plugins/
   └─ RegionProtect/
      ├─ RegionProtect.Config.ini
      └─ Worlds/
         └─ <world-guid>/
            └─ RegionProtect.Regions.ini
```

For the Steam dedicated server:

```text
CMZDedicatedSteamServer/
└─ Plugins/
   └─ RegionProtect/
      ├─ RegionProtect.Config.ini
      └─ Worlds/
         └─ <world-guid>/
            └─ RegionProtect.Regions.ini
```

### General config

`RegionProtect.Config.ini` controls which protection systems are enabled:

```ini
[General]
Enabled                = true
ProtectMining          = true
ProtectPlacing         = true
ProtectExplosions      = true
ProtectCrateItems      = true
ProtectCrateMining     = true
WarnPlayers            = true
WarningCooldownSeconds = 2
LogDenied              = true
```

### Region config

Each world has its own `RegionProtect.Regions.ini` file:

`AllowedPlayers` accepts player display names. On the Steam dedicated server, `AllowedSteamIds` also accepts SteamID64 values for stable identity-based allow-lists.

```ini
[SpawnProtection]
Enabled         = true
Range           = 64
AllowedPlayers  = RussDev7
AllowedSteamIds = 76561198XXXXXXXXX

[Region:SpawnTown]
Min             = -80,0,-80
Max             = 80,120,80
AllowedPlayers  = RussDev7,SomeAdmin
AllowedSteamIds = 76561198XXXXXXXXX,76561198XXXXXXXXX
```

### Player warning behavior

When a player tries to edit a protected area, the server blocks the action and sends a warning such as:

```text
[RegionProtect] Protected by region 'SpawnTown'. Breaking blocks here was blocked. Client-only desync; not saved to server.
```

In some cases, the client may briefly show a block as broken or changed. The server does **not** save that blocked change, and the area will correct itself after resyncing or rejoining.

### Notes and limitations

* RegionProtect is server-authoritative.
* Players do not need the RegionProtect mod installed to be blocked by protected regions.
* Commands such as `/regionpos` and `/regioncreate` are not currently part of the dedicated server plugin.
* Regions are currently edited manually through the `.ini` files.
* Explosion restoration can visually desync on the attacking client, but protected explosion damage is not saved to the server.
* On the Steam dedicated server, prefer `AllowedSteamIds` for trusted players/admins because SteamID64 is stable across display-name changes.
* On the Lidgren dedicated server, RegionProtect allow-lists are effectively name-based.
* Existing `RegionProtect.Regions.ini` files are not automatically rewritten with new keys. Add `AllowedSteamIds =` manually or regenerate the file if you want the new example format.
* `AllowedSteamIds` expects numeric SteamID64 values, not Steam profile names or vanity URLs.

## FloodGuard plugin

FloodGuard is a lightweight packet-rate guard for the dedicated server. It watches inbound gameplay packets before normal host/world handling or peer relay. When a sender exceeds the configured rate, the server temporarily blackholes that sender's packets instead of relaying or applying them.

Config is created on first run at:

```text
Plugins\FloodGuard\FloodGuard.Config.ini
```

Default config:

```ini
[General]
Enabled = true
PerSenderMaxPacketsPerSec = 120
BlackholeMs = 3000

[AllowedPlayers]
# Comma-separated allow list. Entries may be player names, Player1-style fallback names,
# numeric GIDs, or SteamIDs on the Steam server.
AllowedPlayers =
```

Notes:
- `PerSenderMaxPacketsPerSec` is counted per sender/GID over a one-second window.
- `BlackholeMs` is how long to silently drop packets after the sender exceeds the limit.
- `AllowedPlayers` bypasses FloodGuard for trusted players or test accounts.

## VanillaSpawners Server Plugin

The dedicated servers include a built-in **VanillaSpawners** plugin for controlling vanilla-generated spawner blocks and random vanilla loot blocks.

This is useful for long-running dedicated servers where players stay near one area for a long time and newly generated terrain can accumulate a large number of monster, alien, hell, boss spawner blocks, or random loot blocks.

VanillaSpawners can:

- prevent new vanilla cave / alien / hell / boss spawner blocks from generating
- prevent new random vanilla `LootBlock` / `LuckyLootBlock` blocks from generating
- optionally block activation of already-existing vanilla spawner blocks
- keep old saves intact without deleting existing spawner or loot blocks
- enforce the behavior server-side even when players do not have a matching client-side mod installed

### Config location

For the Steam dedicated server:

```text
CMZDedicatedSteamServer/
└─ Plugins/
   └─ VanillaSpawners/
      └─ VanillaSpawners.Config.ini
```

For the Lidgren dedicated server:

```text
CMZDedicatedLidgrenServer/
└─ Plugins/
   └─ VanillaSpawners/
      └─ VanillaSpawners.Config.ini
```

### Example config

```ini
[General]
Enabled = true

# Allows new vanilla cave / alien / hell / boss spawner blocks to generate.
# false prevents NEW spawner blocks from being placed in newly generated terrain.
# Existing chunks and saves are not deleted or modified.
GenerateSpawnerBlocks = true

# Allows existing vanilla spawner blocks to be activated.
# false consumes spawner-origin enemy spawns and spawner block-state changes server-side.
AllowSpawnerActivation = true

# Allows new vanilla LootBlock / LuckyLootBlock blocks to generate.
# false prevents NEW random loot blocks from being placed in newly generated terrain.
# Existing chunks and saves are not deleted or modified.
GenerateLootBlocks = true

# Logs each blocked spawner activation packet.
# Useful for debugging, but noisy if players keep trying old spawners.
LogBlockedActivation = false
```

### Recommended setting to stop new spawners and random loot blocks

```ini
[General]
Enabled = true
GenerateSpawnerBlocks = false
AllowSpawnerActivation = true
GenerateLootBlocks = false
LogBlockedActivation = false
```

This prevents newly generated terrain from placing new vanilla spawner blocks or random vanilla loot blocks, while still allowing existing spawner blocks to work.

### Stricter setting

```ini
[General]
Enabled = true
GenerateSpawnerBlocks = false
AllowSpawnerActivation = false
GenerateLootBlocks = false
LogBlockedActivation = false
```

This prevents new vanilla spawner blocks and random vanilla loot blocks from generating, and also blocks existing vanilla spawner activation server-side.

### Notes and limitations

* `GenerateSpawnerBlocks=false` only affects newly generated terrain.
* `GenerateLootBlocks=false` only affects newly generated terrain.
* Existing chunks and saved worlds are not modified.
* Existing spawner blocks and loot blocks may still be visible in old worlds.
* `GenerateLootBlocks=false` targets vanilla `LootBlock` and `LuckyLootBlock` world-generation blocks.
* It does not block normal crate/container blocks unless a separate crate-control feature is added later.
* `AllowSpawnerActivation=false` blocks spawner-origin enemy spawns and spawner block-state changes server-side.
* Vanilla clients may briefly appear to interact with an old spawner locally, but the server rejects the resulting spawner behavior.
* For the cleanest client-side experience, pair the server plugin with the ModLoaderExtensions client-side vanilla world-generation controls.

## RememberTime Server Plugin

The dedicated servers include a built-in **RememberTime** plugin that saves the server's authoritative day/time value and restores it when the server starts again.

RememberTime can:

- save the current authoritative server time on a configurable interval
- restore the saved time when the server process starts
- save one final time during clean server shutdown
- store saved time per world so different worlds keep separate day/time progress
- reduce disk writes by using `SaveIntervalSeconds` instead of writing every tick

### Config location

RememberTime creates a shared plugin config and per-world state file beside each dedicated server executable.

For the Steam dedicated server:

```text
CMZDedicatedSteamServer/
└─ Plugins/
   └─ RememberTime/
      ├─ RememberTime.Config.ini
      └─ Worlds/
         └─ <world-guid>/
            └─ Time.State.ini
```

For the Lidgren dedicated server:

```text
CMZDedicatedLidgrenServer/
└─ Plugins/
   └─ RememberTime/
      ├─ RememberTime.Config.ini
      └─ Worlds/
         └─ <world-guid>/
            └─ Time.State.ini
```

### Example config

```ini
[General]
Enabled = true

# Saves the server's current day/time every X seconds.
# Higher values reduce disk writes.
# Lower values reduce lost time if the server crashes.
SaveIntervalSeconds = 60

# Restores the saved time when the server process starts.
RestoreOnStartup = true

# Logs every successful interval save.
# Very spammy. Useful only when debugging RememberTime.
LogIntervalSaves = false

# Writes one final time when the server stops cleanly.
SaveOnShutdown = true
```

### Example saved state

```ini
[State]
TimeOfDay = 12.4135227
DisplayDay = 13
SavedUtc = 2026-04-27T18:20:31.0000000Z
Reason = interval
```

### Notes

- `TimeOfDay` is the full server day/time float, not only the `0.0` to `1.0` visual time fraction.
- Example: `12.41` means the server is on display **Day 13** at roughly the same visual time as `0.41`.
- `SaveIntervalSeconds = 60` is a good default for normal hosting.
- If the process crashes, the server may lose up to the configured interval. Clean shutdowns still write one final save when `SaveOnShutdown = true`.
- The saved time affects dynamic `{day}` and `{day00}` tokens after restore because those tokens use the authoritative server time.
- `LogIntervalSaves = false` keeps normal interval saves quiet in the console. Startup, restore, shutdown, and error logs can still appear.
- Set `LogIntervalSaves = true` only when debugging RememberTime save timing.

## Troubleshooting

### The server says the game folder is missing
Make sure `CastleMinerZ.exe` exists either in the location specified by `game-path` or in the local host runtime layout you are using.

### The server says `0Harmony.dll` is missing
Make sure Harmony exists in the expected host `Libs` folder.

### Clients cannot join
Check:
- `server-port`
- firewall rules
- that both clients are using the same IP and port
- `game-name` and `network-version`
- that you are launching the correct host for the connection flow you want to test

### The wrong world loads
Check the `world-guid` value in `server.properties` and verify the folder exists under the selected host's `Worlds\` storage location.

### Save access fails
Check that `steam-user-id` is populated correctly, because the current save-device setup still uses it as the storage identity/key seed.

## License

This project is licensed under **GPL-3.0-or-later**.
See [LICENSE](LICENSE) for details.

## Credits

Developed and maintained by:
- RussDev7
- unknowghost
