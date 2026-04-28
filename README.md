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

## Project layout

```text
CMZDedicatedServers/
├─ LICENSE
├─ README.md
├─ build.bat
├─ clean.bat
├─ Directory.Build.props
├─ Build/
│  ├─ CMZDedicatedSteamServer/
│  └─ CMZDedicatedLidgrenServer/
├─ CMZDedicatedServers.sln
├─ CMZDedicatedLidgrenServer/
│  ├─ CMZDedicatedLidgrenServer.csproj
│  ├─ Program.cs
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
   ├─ Common/
   │  └─ ReflectEx.cs
   ├─ Config/
   │  └─ SteamServerConfig.cs
   ├─ Hosting/
   │  ├─ ServerAssemblyLoader.cs
   │  ├─ SteamConnectionApproval.cs
   │  ├─ SteamDedicatedServer.cs
   │  ├─ SteamLobbyHost.cs
   │  └─ SteamPeerRegistry.cs
   ├─ Networking/
   │  └─ CmzMessageCodec.cs
   ├─ Steam/
   │  └─ SteamServerBootstrap.cs
   ├─ Templates/
   │  ├─ server.properties
   │  └─ steam_appid.txt
   ├─ World/
   │  └─ ServerWorldHandler.cs
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
└─ CMZDedicatedLidgrenServer/
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
```

Important project settings:
- Target framework: `net481`
- Platform target: `x86`
- Shared build root: `Build\`
- Steam host output path: `Build\CMZDedicatedSteamServer\`
- Lidgren host output path: `Build\CMZDedicatedLidgrenServer\`

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

## World and save data

The host derives the world folder from `world-guid` and expects it under the selected host's world storage layout:

```text
Worlds\{world-guid}
```

Typical files used by the host include:
- `world.info`
- chunk delta data
- player inventory saves

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
````

Steam bans are SteamID-backed and also save the last known player name for readability.

Lidgren bans are IP/name-backed and are useful for direct-IP hosting, but they are weaker than SteamID bans because names and IP addresses can change.

Player Enforcement uses a hard drop / transport-level removal path instead of relying only on normal client-side kick messages. This helps prevent modified clients from bypassing enforcement by removing the kick-message pipeline.

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
- The server update loop is driven by `tick-rate-hz`.
- Pickup, inventory, chunk, and terrain flow are now handled server-side well enough for basic dedicated play.
- The repository now keeps **Steam** and **Lidgren** as separate projects under one solution.
- The root build process outputs both hosts into the shared `Build` folder.

## Server Plugins

CastleForge Dedicated Servers now include basic **server-side plugin support** for host-authoritative world protections and future server extensions.

Plugins run inside the dedicated server process and can inspect selected host/world packets before the server applies or relays them. This allows the server to enforce rules even when connecting players do **not** have the matching client-side mod installed.

Current built-in plugin support includes:

- **Announcements** private join messages and timed global messages
- **FloodGuard** malicious packet spam protection
- **RememberTime** per-world time persistence between restarts
- **RegionProtect** server enforcement
- **Player Enforcement** console commands for `players`, `kick`, `ban`, `unban`, and `bans`
- block mining / placing protection
- explosion protection
- crate item protection
- crate break protection
- per-world plugin configuration

> Server plugins are currently compiled into the dedicated server build. External plugin DLL loading may be added later.

## Announcements Server Plugin

The dedicated servers include a built-in **Announcements** plugin for simple server messages.

Announcements can:

- send a private welcome message to each joining player
- send a timed global message to all connected players
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
PrivateJoinMessage = Welcome {player}! This is a CastleForge dedicated server. Join us: dsc.gg/cforge

[Global]
TimedGlobalMessageEnabled = true
GlobalMessage = Need help, updates, or mods? Join the CastleForge Discord: dsc.gg/cforge
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

### Behavior

The private join message is sent only to the joining player.

The timed global message is broadcast to all connected players after `InitialGlobalDelaySeconds`, then repeats every `GlobalMessageIntervalMinutes`.

Set `MinimumPlayersForGlobalMessage = 0` to allow global messages even when the server is empty, or set it to `1` or higher to only announce when players are online.

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

```ini
[SpawnProtection]
Enabled        = true
Range          = 64
AllowedPlayers = RussDev7

[Region:SpawnTown]
Min            = -80,0,-80
Max            = 80,120,80
AllowedPlayers = RussDev7,SomeAdmin
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
- unknowghost0
