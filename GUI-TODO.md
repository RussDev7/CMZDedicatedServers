# CastleForge Server GUI TODO

Tracks the `ServerGuiForm` desktop host window. Anything not listed here is considered
not started — if it ships, it gets checked off here.

## Done

### Core
- [x] Desktop GUI wraps the server as a hidden `--console` child process
- [x] Start / Stop / Restart controls (always visible; state shown via color)
- [x] Blocks window close while the server is running
- [x] Startup-failure and unexpected-exit popups with recent error lines
- [x] Double-start guard
- [x] Restart cooldown countdown on the button
- [x] Terminal switch — real console window (AllocConsole), no restart; `gui` returns to GUI

### Header / status
- [x] Custom tab strip (Console / Players / Settings) — no default gray TabControl
- [x] Status chips: Status (colored dot), Players, Uptime, TPS, Memory
- [x] TPS is real: the server emits `@@TPS` every ~2 s when stdout is redirected (GUI child only); the GUI consumes it silently
- [x] Header can't clip buttons — TableLayout layout, chips right-align and clip first

### Console
- [x] Streamed output with batching, trimming, render-pause safety for floods
- [x] Log category colors (errors red, trust/enforcement amber, GUI/steam gray-blue)
- [x] Search box (Enter = next match, wraps)
- [x] Pause-scroll toggle
- [x] Clear button
- [x] Hidden caret — read-only output doesn't look editable
- [x] Malformed/partial log line resilience

### Command input
- [x] Enter to send
- [x] Up/Down history (last 50)
- [x] Quick-commands dropdown (help, list, say, kick, ban, save-all, !restart)
- [x] Placeholder text
- [x] Disabled while server is stopped/starting

### Players
- [x] Live player cards from log parsing (connect/disconnect/handshake/host-msg)
- [x] Name + SteamID filter with placeholder
- [x] "N online" count, empty states
- [x] Expand arrow → Copy Name / Copy SteamID / Kick / Ban + session time
- [x] Right-click context menu (Copy name / Copy SteamID / Kick / Ban)
- [x] Placeholder rows while handshake resolves
- [x] Large-list collapse for expanded rows

### Settings — server.properties editor
- [x] Renders the file's own sections and order (parsed from `=====` comment blocks)
- [x] Every key gets a row — unknown keys shown under their raw name, nothing dropped
- [x] Enum dropdowns for game-mode / difficulty / pvp-state ("1 - Hard", custom values preserved)
- [x] true/false dropdowns — wheel-safe (scrolling the page can't flip values; wheel forwards to the page)
- [x] File watcher — external edits reload the editor automatically
- [x] Filter box filters rows live
- [x] Save to file — preserves comments/order, `.bak` backup, appends new keys, marks dirty state

### Appearance
- [x] Accent color presets + custom color dialog
- [x] Console text color presets
- [x] Persisted to `GuiSettings.ini`, applied live
- [x] Dark right-click/context menus

## Not done (still wanted)
- [ ] Confirmation dialog before kick/ban
- [ ] Unban action + ban list view
- [ ] Kick/ban success/failure feedback surfaced in the UI
- [ ] Player rank badges (needs rank data exposed to the GUI process)
- [ ] Duplicate-name-safe targeting for admin commands
- [ ] Favorite/saved commands + recent-command resend list
- [ ] Export console log button
- [ ] Plugins tab (enable/disable)
- [ ] World/session info summary panel
- [ ] Tooltips on controls
- [ ] DPI scaling pass

## Deferred
- [ ] First-run setup flow
- [ ] Scheduled restart UI
- [ ] Multiple server profiles
- [ ] Remote admin/web panel
- [ ] Update checker
- [ ] Light theme

## Notes
- Keep the GUI focused on server hosting/admin use first.
- `GuiSettings.ini` is GUI-only state; `server.properties` is the server's — Save writes the real file, never a GUI-only copy.
