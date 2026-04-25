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
- Supports a configurable bind IP, port, player count, world GUID, view distance, and tick rate
- Separates the dedicated server implementations into **Steam** and **Lidgren** projects while keeping the shared project flow familiar

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

### Config fields

| Key | Purpose |
|------------------------|--------------------------------------------------------------------------------------------------------|
| `server-name`          | Display name shown to clients                                                                          |
| `game-name`            | Expected network game name                                                                             |
| `network-version`      | Expected protocol version                                                                              |
| `game-path`            | Optional path to the CastleMiner Z binaries folder; falls back to the local server layout when omitted |
| `server-ip`            | Bind address (`0.0.0.0` or `any` binds all interfaces)                                                 |
| `server-port`          | Port clients connect to                                                                                |
| `max-players`          | Maximum connected players                                                                              |
| `steam-user-id`        | Save-device / world key identity used for storage access                                               |
| `world-guid`           | GUID used to locate the world folder                                                                   |
| `view-distance-chunks` | Chunk radius used by the host                                                                          |
| `tick-rate-hz`         | Server update loop rate                                                                                |
| `game-mode`            | Session game mode value                                                                                |
| `pvp-state`            | Session PVP state value                                                                                |
| `difficulty`           | Session difficulty value                                                                               |

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

## Time / day progression

The dedicated host advances world time using **real elapsed time** rather than fixed loop iterations.

This is important because the server loop may run faster or slower than a normal 60 FPS client host. The current implementation keeps authoritative day progression on the server and periodically broadcasts the current world day/time to clients.

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

- **RegionProtect** server enforcement
- block mining / placing protection
- explosion protection
- crate item protection
- crate break protection
- per-world plugin configuration

> Server plugins are currently compiled into the dedicated server build. External plugin DLL loading may be added later.
```

Then add a dedicated RegionProtect subsection:

````markdown
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
````

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
