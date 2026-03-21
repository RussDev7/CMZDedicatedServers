# CMZDedicatedServer

A dedicated server host for **CastleMiner Z** built in **C# / .NET Framework 4.8.1**.

This project loads the original game/runtime assemblies from a local `Game` folder, starts a Lidgren-backed server, and hosts a persistent world with chunk, inventory, and message handling outside the normal game client.

## What it does

- Starts a dedicated CastleMiner Z compatible server by IP and port
- Loads the original game assemblies via reflection
- Handles connection approval, discovery/session metadata, and gameplay packet relay
- Hosts world state server-side through `ServerWorldHandler`
- Loads `world.info`, chunk deltas, and player inventories from disk
- Relays player visibility, movement, text, block edits, chunk requests, and inventory flow
- Supports a configurable bind IP, port, player count, world GUID, view distance, and tick rate

## Project layout

```text
CMZDedicatedServer-main/
├─ LICENSE
├─ README.md
├─ build.bat
├─ clean.bat
└─ scr/
   └─ CMZServerHost/
      ├─ App.config
      ├─ CMZServerHost.csproj
      ├─ CmzMessageCodec.cs
      ├─ LidgrenServer.cs
      ├─ Program.cs
      ├─ ServerAssemblyLoader.cs
      ├─ ServerConfig.cs
      ├─ ServerPatches.cs
      ├─ ServerRuntime.cs
      ├─ ServerWorldHandler.cs
      └─ build/
         └─ ServerHost/
            ├─ CMZServerHost.exe
            ├─ server.properties
            └─ Game/
               ├─ CastleMinerZ.exe
               ├─ DNA.Common.dll
               └─ game content files...
```

## Main components

### `Program.cs`
Entry point for the dedicated host.

It:
- resolves the local `Game` folder
- loads `CastleMinerZ.exe` and related assemblies
- applies Harmony patches
- loads `server.properties`
- prints a startup summary
- starts the server and enters the update loop

### `LidgrenServer.cs`
The dedicated networking host.

It is responsible for:
- binding the socket
- connection approval
- session startup
- direct-IP traffic handling
- channel 0 / channel 1 packet handling
- relay of gameplay and bootstrap messages
- periodic world time broadcasts

### `ServerWorldHandler.cs`
Host-side world and persistence bridge.

It handles:
- message ID/type lookup
- `world.info` loading
- save device initialization
- chunk list / chunk request handling
- terrain mutation handling
- inventory persistence
- host-consumed world messages

### `CmzMessageCodec.cs`
Maps CastleMiner Z message IDs to reflected message types and back.

### `ServerConfig.cs`
Loads typed config values from `server.properties`.

## Requirements

- Windows
- **.NET Framework 4.8.1**
- Visual Studio / MSBuild capable of building .NET Framework projects
- The original CastleMiner Z game files available under the server's `Game` directory

## Configuration

The server reads configuration from `server.properties` in the server root.

Example:

```properties
server-name=CMZ Server
game-name=CastleMinerZSteam
network-version=4

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
|---|---|
| `server-name` | Display name shown to clients |
| `game-name` | Expected network game name |
| `network-version` | Expected protocol version |
| `server-ip` | Bind address (`0.0.0.0` or `any` binds all interfaces) |
| `server-port` | Port clients connect to |
| `max-players` | Maximum connected players |
| `steam-user-id` | Save-device / world key identity |
| `world-guid` | GUID used to locate the world folder |
| `view-distance-chunks` | Chunk radius used by the host |
| `tick-rate-hz` | Server update loop rate |
| `game-mode` | Session game mode value |
| `pvp-state` | Session PVP state value |
| `difficulty` | Session difficulty value |

## Building

### Option 1: Use the included batch script

```bat
build.bat
```

This script:
- locates MSBuild using `vswhere`
- restores and builds the project
- collects release files
- creates a zip package

### Option 2: Build from Visual Studio / MSBuild

Project file:

```text
scr/CMZServerHost/CMZServerHost.csproj
```

Important project settings:
- Target framework: `net481`
- Platform target: `x86`
- Output path: `build\ServerHost\`

## Running

After building, run:

```bat
scr\CMZServerHost\build\ServerHost\CMZServerHost.exe
```

On startup the server prints a summary similar to:

```text
CMZ Server Host
---------------
GameName       : CastleMinerZSteam
NetworkVersion : 4
Bind           : 0.0.0.0:61903
ServerName     : CMZ Server
MaxPlayers     : 8
SteamUserId    : 76561198296842857
WorldGuid      : ...
WorldFolder    : Worlds\...
WorldPath      : ...
WorldInfo file : ...\world.info
World loaded   : True
```

Then connect using the server IP and the configured `server-port`.

## World and save data

The host derives the world folder from `world-guid` and expects it under:

```text
Worlds\{world-guid}
```

Typical files used by the host include:
- `world.info`
- chunk delta data
- player inventory saves

## Notes and current implementation details

- The server is built around **reflection** rather than direct game project references.
- The host uses the original game message types and message registry where possible.
- The server updates in a fixed loop based on `tick-rate-hz`.
- Time-of-day/day progression is driven by elapsed real time and periodically broadcast to clients.
- The current repository includes a ready-to-run `build/ServerHost/` layout as well as source.

## Troubleshooting

### The server starts but says the `Game` folder is missing
Make sure `CastleMinerZ.exe` and its companion assemblies are present under:

```text
build\ServerHost\Game\
```

### Clients cannot join
Check:
- `server-port`
- firewall rules
- that both clients are using the same IP and port
- `game-name` and `network-version`

### The wrong world loads
Check the `world-guid` value in `server.properties` and verify the folder exists under `Worlds\`.

### Build script path note
In this archive, the source folder is named `scr/`, while `build.bat` references `src/` paths. If you keep the current folder naming, update the script or rename the folder so the build script matches your layout.

## License

This project is licensed under **GPL-3.0-or-later**.
See [LICENSE](LICENSE) for details.

## Credits

Developed and maintained by authors:
- RussDev7
- unknowghost0
