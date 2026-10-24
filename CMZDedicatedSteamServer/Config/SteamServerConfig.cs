/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System.Collections.Generic;
using System.Net;
using System.IO;
using System;
using System.Text;
using CMZDedicatedSteamServer.Commands;

namespace CMZDedicatedSteamServer.Config
{
    /// <summary>
    /// Steam-dedicated server configuration for the headless Steam host account path.
    ///
    /// Purpose:
    /// - Preserves the useful settings from the current dedicated host.
    /// - Adds Steam-specific knobs used by the new transport.
    /// - Supplies safe defaults when server.properties is missing or incomplete.
    ///
    /// Notes:
    /// - This configuration intentionally does NOT expose Steam username/password login.
    ///   The normal Steam client API path requires an already-running Steam client session.
    /// </summary>
    internal sealed class SteamServerConfig
    {
        #region Logging

        public bool LogNetworkPackets { get; set; } = false;
        public bool LogHostMessages { get; set; } = false;

        /// <summary>
        /// Unified combat-debug logging: writes one [CombatEvidence] audit
        /// line per once-per-record kill debit (claimer/killer, weapon,
        /// enemy HP, killer bank) AND feeds accepted combat messages into
        /// the diagnostic-only shadow tracker ([ShadowCombat] lines - see
        /// Trust/CombatSimulationNotes.md). Pure diagnostics - it never
        /// affects enforcement. Off by default.
        /// </summary>
        public bool LogCombat { get; set; } = false;

        /// <summary>
        /// Writes command audit lines to Logs\commands-yyyy-MM-dd.log.
        /// Enabled by default so hosts can review who ran which commands.
        /// </summary>
        public bool LogCommandAudit { get; set; } = true;

        #endregion

        #region Core Identity

        public string ServerName { get; private set; } = "CMZ Steam Server";
        public string ServerMessage { get; private set; } = "Welcome to this CastleForge dedicated server.";
        public string GameName { get; private set; } = "CastleMinerZSteam";
        public int NetworkVersion { get; private set; } = 4;
        public string GamePath { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;

        #endregion

        #region Session / World

        public IPAddress BindAddress { get; private set; } = IPAddress.Any;
        public int Port { get; private set; } = 61903;
        public int MaxPlayers { get; private set; } = 8;
        public ulong SaveOwnerSteamId { get; private set; } = 0UL;
        public string WorldGuid { get; private set; } = string.Empty;
        public string WorldFolder { get; private set; } = string.Empty;
        public string WorldPath { get; private set; } = string.Empty;
        public int ViewDistanceChunks { get; private set; } = 8;
        public int TickRateHz { get; private set; } = 60;
        public int GameMode { get; private set; } = 1;
        public int PvpState { get; private set; } = 0;
        public int Difficulty { get; private set; } = 1;
        public bool InfiniteResourceMode { get; private set; } = false;
        public bool AllowClientTimeSync { get; private set; } = false;

        #endregion

        #region Steam Settings

        public uint SteamAppId { get; private set; } = 253430;
        public bool SteamLobbyVisible { get; private set; } = true;
        public bool SteamAllowMinimalUpdates { get; private set; } = false;
        public bool SteamAccountRequired { get; private set; } = true;
        public bool SteamFriendsOnly { get; private set; } = false;
        public bool WriteSteamAppIdFile { get; private set; } = true;
        public bool RequireRunningSteamClient { get; private set; } = true;

        #endregion

        #region Trust / Authority Enforcement

        /// <summary>
        /// Master switch for the built-in server-authoritative trust layer.
        /// When enabled, the server validates client-originated movement, teleport,
        /// and world-mutation messages instead of trusting clients.
        /// </summary>
        public bool TrustEnforcement { get; } = true;

        /// <summary>
        /// Players at or above this rank skip all movement/teleport enforcement.
        /// </summary>
        public ServerCommandRank TrustExemptMinRank { get; } = ServerCommandRank.Moderator;

        /// <summary>
        /// Minimum rank allowed to teleport while the configured game mode or
        /// difficulty does not offer the vanilla Teleport menu
        /// (Endurance, or any mode on HARDCORE difficulty).
        /// </summary>
        public ServerCommandRank TrustTeleportOpRank { get; } = ServerCommandRank.Moderator;

        /// <summary>Max accepted horizontal position speed in m/s before an update is rejected.</summary>
        public float TrustMaxHorizontalSpeed { get; } = 20f;

        /// <summary>Max accepted vertical position speed in m/s before an update is rejected.</summary>
        public float TrustMaxVerticalSpeed { get; } = 15f;

        /// <summary>
        /// Position jump distance in meters that counts as a teleport attempt.
        /// Kept well above the largest legit per-update step.
        /// </summary>
        public float TrustTeleportDistance { get; } = 30f;

        /// <summary>
        /// Minimum seconds between teleports for normal players in modes that allow
        /// the vanilla Teleport menu. 0 disables the cooldown.
        /// </summary>
        public float TrustTeleportCooldownSeconds { get; } = 60f;

        /// <summary>
        /// Seconds of continuous rising with almost no horizontal movement allowed in
        /// non-Creative modes before it counts as flight. Vanilla jump ascent ends
        /// well under 1 second.
        /// </summary>
        public float TrustFlyGraceSeconds { get; } = 1.25f;

        /// <summary>
        /// Meters of free air below the player's feet that still count as supported.
        /// Covers jump apexes and stair height; set well above a max jump (~1.5m).
        /// </summary>
        public float TrustHoverMaxHeight { get; } = 3f;

        /// <summary>
        /// Seconds a non-Creative player may stay unsupported in the air before it
        /// counts as flight. Catches hovering, stair-speed flight, and slow gliding.
        /// </summary>
        public float TrustMaxAirborneSeconds { get; } = 5f;

        /// <summary>
        /// Downward speed in m/s considered a real fall. Faster descents drain the
        /// airborne timer instead of growing it, so legit long falls stay clean.
        /// </summary>
        public float TrustMinSinkSpeed { get; } = 8f;

        /// <summary>
        /// Max distance in meters between a player's position and a world-mutation
        /// target (block dig/place, crate, explosive, pickup spawn, enemy spawn).
        /// Vanilla reach is ~6m; the default leaves generous lag headroom.
        /// </summary>
        public float TrustMaxReach { get; } = 7f;

        /// <summary>Max distance (blocks) between a player and their claimed melee hit origin.</summary>
        public float TrustMeleeReach { get; } = 8f;

        /// <summary>
        /// Max distance (blocks) for blast-side message targets (explosions, crate breaks
        /// from blasts). Thrown explosives legitimately land far from the thrower.
        /// </summary>
        public float TrustBlastReach { get; } = 64f;
        public float TrustSpawnReach { get; } = 900f;

        /// <summary>Rejected updates of the SAME violation kind needed before a
        /// player is kicked. Each kind has its own bucket. Position/probe-derived
        /// kinds (fly, reach, speed, noclip, godmode) kick at 10 instead — see
        /// ServerPolicy.Violation.</summary>
        public int TrustViolationsKick { get; } = 5;

        /// <summary>Rejected updates needed before a player is banned. 0 disables auto-ban.</summary>
        public int TrustViolationsBan { get; } = 0;

        /// <summary>
        /// Kicks within <see cref="TrustKickBanWindowSeconds"/> before the next
        /// kick becomes a persisted ban. Counts by SteamID so it survives
        /// rejoins — a kicked cheater that keeps reconnecting escalates into
        /// Bans.ini instead of bouncing forever. 0 disables escalation.
        /// Default 200 over a 3-day window keeps bans for true repeat
        /// offenders — false-positive kicks decay long before they pile up.
        /// </summary>
        public int TrustKickBan { get; } = 200;

        /// <summary>Window for the cross-session kick counter (3 days).</summary>
        public int TrustKickBanWindowSeconds { get; } = 259200;

        /// <summary>
        /// When true, players assigned the Visitor rank cannot send world-mutation
        /// messages (block edits, crates, explosives, enemy/dragon control, pickups).
        /// </summary>
        public bool TrustVisitorEditBlock { get; } = true;

        /// <summary>
        /// When true, the server validates every client-supplied InventoryItem
        /// payload (inventory stores, crate writes, pickup spawns): known item
        /// ids only, stack limits, banned/teleport-gated items, exact wire shape.
        /// </summary>
        public bool TrustItemValidation { get; } = true;

        /// <summary>
        /// Maximum stack count accepted for any item. Vanilla blocks cap near
        /// 999; anything above is a forged stack.
        /// </summary>
        public int TrustMaxItemStack { get; } = 999;

        /// <summary>
        /// When true, the server tracks what each player legitimately owns:
        /// drops require ownership, world pickups must be real drops, block
        /// placements debit items, and inventory saves must reconcile against
        /// served/persisted state plus authorized gains and crafting recipes.
        /// Skipped entirely in Creative mode (infinite items by design).
        /// </summary>
        public bool TrustProvenance { get; } = true;

        #endregion

        #region Load

        // Templates/server.properties embedded in the assembly - the live
        // file is never copied over, only created or appended to.
        private const string DefaultPropertiesResource = "CMZDedicatedSteamServer.DefaultServerProperties";

        public static SteamServerConfig Load(string serverDir)
        {
            var cfg = new SteamServerConfig();
            string path = Path.Combine(serverDir, "server.properties");
            EnsureProperties(serverDir, path);
            if (!File.Exists(path))
                return cfg;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            if (map.TryGetValue("log-network-packets", out string logNetworkPackets) && bool.TryParse(logNetworkPackets, out bool logNetwork))
                cfg.LogNetworkPackets = logNetwork;

            if (map.TryGetValue("log-host-messages", out string logHostMessages) && bool.TryParse(logHostMessages, out bool logHost))
                cfg.LogHostMessages = logHost;

            if (map.TryGetValue("log-combat", out string logCombatRaw) && bool.TryParse(logCombatRaw, out bool logCombat))
                cfg.LogCombat = logCombat;

            if (map.TryGetValue("log-command-audit", out string logCommandAuditRaw) && bool.TryParse(logCommandAuditRaw, out bool logCommandAudit))
                cfg.LogCommandAudit = logCommandAudit;

            if (map.TryGetValue("server-name", out string serverName) && !string.IsNullOrWhiteSpace(serverName))
                cfg.ServerName = serverName;

            if (map.TryGetValue("server-message", out var serverMessage) && !string.IsNullOrWhiteSpace(serverMessage))
                cfg.ServerMessage = serverMessage;

            if (map.TryGetValue("game-name", out string gameName) && !string.IsNullOrWhiteSpace(gameName))
                cfg.GameName = gameName;

            if (map.TryGetValue("network-version", out string networkVersionRaw) && int.TryParse(networkVersionRaw, out int networkVersion) && networkVersion > 0)
                cfg.NetworkVersion = networkVersion;

            if (map.TryGetValue("game-path", out string gamePath) && !string.IsNullOrWhiteSpace(gamePath))
                cfg.GamePath = gamePath;

            if (map.TryGetValue("password", out string password))
                cfg.Password = password ?? string.Empty;

            if (map.TryGetValue("server-port", out string portRaw) && int.TryParse(portRaw, out int port) && port > 0 && port < 65536)
                cfg.Port = port;

            if (map.TryGetValue("max-players", out string maxPlayersRaw) && int.TryParse(maxPlayersRaw, out int maxPlayers) && maxPlayers > 0)
                cfg.MaxPlayers = maxPlayers;

            if (map.TryGetValue("server-ip", out string ipRaw) && !string.IsNullOrWhiteSpace(ipRaw))
            {
                if (ipRaw.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) || ipRaw.Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.BindAddress = IPAddress.Any;
                }
                else if (IPAddress.TryParse(ipRaw, out IPAddress ip))
                {
                    cfg.BindAddress = ip;
                }
            }

            if (map.TryGetValue("save-owner-steam-id", out string ownerRaw) && ulong.TryParse(ownerRaw, out ulong owner))
                cfg.SaveOwnerSteamId = owner;

            if (map.TryGetValue("world-guid", out string worldGuid) && !string.IsNullOrWhiteSpace(worldGuid))
                cfg.WorldGuid = worldGuid;

            if (map.TryGetValue("view-distance-chunks", out string viewDistanceRaw) && int.TryParse(viewDistanceRaw, out int viewDistance) && viewDistance > 0)
                cfg.ViewDistanceChunks = viewDistance;

            if (map.TryGetValue("tick-rate-hz", out string tickRateRaw) && int.TryParse(tickRateRaw, out int tickRate) && tickRate > 0)
                cfg.TickRateHz = tickRate;

            if (map.TryGetValue("game-mode", out string gameModeRaw) && int.TryParse(gameModeRaw, out int gameMode))
                cfg.GameMode = gameMode;

            if (map.TryGetValue("pvp-state", out string pvpRaw) && int.TryParse(pvpRaw, out int pvp))
                cfg.PvpState = pvp;

            if (map.TryGetValue("difficulty", out string difficultyRaw) && int.TryParse(difficultyRaw, out int difficulty))
                cfg.Difficulty = difficulty;

            if (map.TryGetValue("infinite-resource-mode", out string infRaw) && bool.TryParse(infRaw, out bool inf))
                cfg.InfiniteResourceMode = inf;

            if (map.TryGetValue("allow-client-time-sync", out string allowTimeRaw) && bool.TryParse(allowTimeRaw, out bool allowTime))
                cfg.AllowClientTimeSync = allowTime;

            if (map.TryGetValue("steam-app-id", out string steamAppIdRaw) && uint.TryParse(steamAppIdRaw, out uint appId))
                cfg.SteamAppId = appId;

            if (map.TryGetValue("steam-lobby-visible", out string lobbyVisibleRaw) && bool.TryParse(lobbyVisibleRaw, out bool lobbyVisible))
                cfg.SteamLobbyVisible = lobbyVisible;

            if (map.TryGetValue("steam-allow-minimal-updates", out string minimalUpdatesRaw) && bool.TryParse(minimalUpdatesRaw, out bool minimalUpdates))
                cfg.SteamAllowMinimalUpdates = minimalUpdates;

            if (map.TryGetValue("steam-account-required", out string accountRequiredRaw) && bool.TryParse(accountRequiredRaw, out bool accountRequired))
                cfg.SteamAccountRequired = accountRequired;

            if (map.TryGetValue("steam-friends-only", out string friendsOnlyRaw) && bool.TryParse(friendsOnlyRaw, out bool friendsOnly))
                cfg.SteamFriendsOnly = friendsOnly;

            if (map.TryGetValue("write-steam-appid-file", out string writeAppIdRaw) && bool.TryParse(writeAppIdRaw, out bool writeAppId))
                cfg.WriteSteamAppIdFile = writeAppId;

            if (map.TryGetValue("require-running-steam-client", out string requireSteamRaw) && bool.TryParse(requireSteamRaw, out bool requireSteam))
                cfg.RequireRunningSteamClient = requireSteam;

            // Trust enforcement values are hardcoded above - a server.properties
            // edit must never be able to weaken the anti-forgery layer.

            if (!string.IsNullOrWhiteSpace(cfg.WorldGuid))
            {
                cfg.WorldFolder = Path.Combine("Worlds", cfg.WorldGuid);
                cfg.WorldPath = Path.Combine(serverDir, cfg.WorldFolder);
            }

            return cfg;
        }

        /// <summary>
        /// Backfills server.properties from the embedded template so live
        /// values are never overwritten: a missing file is created from the
        /// template verbatim; an existing file gets ONLY the keys it lacks
        /// appended, each with the comment/blank block that precedes it in
        /// the template. Existing values - including empty or malformed
        /// ones - count as present; unknown keys, comments, ordering, and
        /// line endings are preserved byte-for-byte. IO/authorization
        /// failures propagate to the caller's normal startup error path.
        /// </summary>
        private static void EnsureProperties(string serverDir, string path)
        {
            string templateText;
            using (Stream res = typeof(SteamServerConfig).Assembly
                       .GetManifestResourceStream(DefaultPropertiesResource))
            {
                if (res == null)
                    return; // template not embedded - nothing to backfill from
                using (var reader = new StreamReader(res))
                    templateText = reader.ReadToEnd();
            }

            if (!File.Exists(path))
            {
                if (!string.IsNullOrEmpty(serverDir))
                    Directory.CreateDirectory(serverDir);
                File.WriteAllText(path, templateText, new UTF8Encoding(false));
                return;
            }

            // Read with BOM detection so the migration rewrite (the only
            // path allowed to rewrite existing bytes) keeps the file's
            // encoding; the append path below is unchanged.
            string existing;
            Encoding existingEnc;
            using (var sr = new StreamReader(path))
            {
                existing = sr.ReadToEnd();
                existingEnc = sr.CurrentEncoding;
            }

            // One-time migration of renamed combat-debug keys, then
            // documentation repair for a bare canonical key - both only
            // when actually needed, folded into a single
            // encoding-preserving rewrite.
            bool changed = NormalizeCombatLogging(existing, out string migrated);
            if (changed)
                existing = migrated;
            if (EnsureCombatLogComment(existing, templateText, out string repaired))
            {
                existing = repaired;
                changed = true;
            }
            if (changed)
            {
                Encoding writeEnc = new UTF8Encoding(false);
                byte[] preamble = existingEnc.GetPreamble();
                if (preamble.Length > 0)
                {
                    byte[] head = new byte[preamble.Length];
                    int got = 0;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        got = fs.Read(head, 0, head.Length);
                    bool match = got == preamble.Length;
                    for (int b = 0; match && b < preamble.Length; b++)
                        match = head[b] == preamble[b];
                    if (match)
                        writeEnc = existingEnc; // BOM'd file keeps its BOM
                }
                File.WriteAllText(path, existing, writeEnc);
            }

            // Active keys already present, parsed with the loader's own
            // semantics (trim, skip blanks/comments, first '=').
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in existing.Split('\n'))
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                present.Add(line.Substring(0, eq).Trim());
            }

            // Match the file's own line ending; an empty file gets the
            // platform default.
            string nl = existing.Length == 0 ? Environment.NewLine
                : existing.Contains("\r\n") ? "\r\n" : "\n";

            // Every template key line carries the comment/blank block that
            // precedes it; only keys the file lacks are appended.
            var pending = new List<string>();
            var append = new StringBuilder();
            foreach (string rawLine in templateText.Split('\n'))
            {
                string line = (rawLine ?? string.Empty).TrimEnd('\r');
                string trimmed = line.Trim();
                int eq = trimmed.IndexOf('=');
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || eq <= 0)
                {
                    pending.Add(line);
                    continue;
                }
                string key = trimmed.Substring(0, eq).Trim();
                if (!present.Contains(key))
                {
                    foreach (string p in pending)
                        append.Append(p).Append(nl);
                    append.Append(line).Append(nl);
                }
                pending.Clear();
            }

            if (append.Length == 0)
                return; // nothing missing - file untouched

            // Glue: a file that doesn't end on a line boundary needs the
            // first appended line on its own line - folded into the single
            // append so the write is atomic.
            string appendText = (!existing.EndsWith("\n", StringComparison.Ordinal) &&
                                 !existing.EndsWith("\r", StringComparison.Ordinal)
                                 ? nl : string.Empty) + append;
            File.AppendAllText(path, appendText, new UTF8Encoding(false));
        }

        // Renamed combat-debug key migration: the two legacy keys folded
        // into the single canonical log-combat.
        private const string CombatLogKey = "log-combat";
        private static readonly string[] LegacyCombatLogKeys = { "log-combat-evidence", "log-shadow-combat" };
        private static readonly string[] LegacyEvidenceComment =
        {
            "# Logs one [CombatEvidence] line per kill-claim damage debit (weapon, enemy HP,",
            "# killer bank) for auditing the damage-budget heuristic. Diagnostic only;",
            "# enforcement is unaffected either way.",
        };
        private static readonly string[] LegacyShadowComment =
        {
            "# Feeds accepted combat traffic into the diagnostic shadow tracker and logs",
            "# [ShadowCombat] lines (approximate enemy position/HP). Pure diagnostics -",
            "# it can never affect kicks, denials, or the damage budget.",
        };

        // Earlier template comment blocks for the canonical log-combat key.
        // When one sits directly above the key it is an outdated doc block,
        // not a user comment: the repair replaces it with the current
        // template text instead of stacking a duplicate above it.
        private static readonly string[][] LegacyUnifiedCombatComments =
        {
            // Pre-humanization labels ([CombatEvidence]/[ShadowCombat]).
            new[]
            {
                "# Unified combat diagnostics: logs one [CombatEvidence] line per kill-claim",
                "# damage debit (weapon, enemy HP, killer bank) for auditing the damage-budget",
                "# heuristic, and feeds accepted combat traffic into the diagnostic shadow",
                "# tracker ([ShadowCombat] lines, approximate enemy position/HP). Pure",
                "# diagnostics - it can never affect kicks, denials, or the damage budget.",
            },
            // Humanized labels, superseded by the shorter current block.
            new[]
            {
                "# Unified combat diagnostics: logs one [Combat Evidence] line per kill-claim",
                "# damage debit (weapon, enemy HP, killer bank) for auditing the damage-budget",
                "# heuristic, and feeds accepted combat traffic into the diagnostic shadow",
                "# tracker ([Shadow Combat] lines, approximate enemy position/HP). Pure",
                "# diagnostics - it can never affect kicks, denials, or the damage budget.",
            },
        };

        /// <summary>
        /// Folds active legacy combat-debug key lines into the canonical
        /// log-combat key. When log-combat already exists (any value - even
        /// malformed counts as present) it always wins and only the legacy
        /// lines are removed; otherwise the first legacy line becomes
        /// log-combat=&lt;OR of valid "true" legacy values&gt;. A contiguous
        /// comment block directly above a removed legacy line is dropped
        /// only when it exactly matches the known old template comments -
        /// user comments are preserved. Every kept line retains its own
        /// ending, so unrelated bytes are untouched. Returns true only when
        /// a rewrite is actually needed.
        /// </summary>
        private static bool NormalizeCombatLogging(string text, out string migrated)
        {
            migrated = text;

            // Split preserving each line's own ending.
            var lines = new List<string>();
            var ends = new List<string>();
            int pos = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;
                int contentEnd = (i > pos && text[i - 1] == '\r') ? i - 1 : i;
                lines.Add(text.Substring(pos, contentEnd - pos));
                ends.Add(text.Substring(contentEnd, i - contentEnd + 1));
                pos = i + 1;
            }
            lines.Add(text.Substring(pos));
            ends.Add(string.Empty);

            var legacy = new List<int>();
            bool canonicalPresent = false;
            bool derived = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#"))
                    continue;
                int eq = t.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = t.Substring(0, eq).Trim();
                if (key.Equals(CombatLogKey, StringComparison.OrdinalIgnoreCase))
                    canonicalPresent = true;
                else
                {
                    foreach (string lk in LegacyCombatLogKeys)
                        if (key.Equals(lk, StringComparison.OrdinalIgnoreCase))
                        {
                            legacy.Add(i);
                            if (bool.TryParse(t.Substring(eq + 1).Trim(), out bool v) && v)
                                derived = true;
                            break;
                        }
                }
            }
            if (legacy.Count == 0)
                return false;

            // A contiguous comment block directly above a dropped legacy
            // line goes too - but only on an exact match to the known old
            // template comments.
            var drop = new HashSet<int>(legacy);
            foreach (int i in legacy)
            {
                string t = lines[i].Trim();
                string key = t.Substring(0, t.IndexOf('=')).Trim();
                string[] comment = key.Equals(LegacyCombatLogKeys[0], StringComparison.OrdinalIgnoreCase)
                    ? LegacyEvidenceComment : LegacyShadowComment;
                int count = 0;
                while (i - count - 1 >= 0 && lines[i - count - 1].TrimStart().StartsWith("#"))
                    count++;
                if (count != comment.Length)
                    continue;
                bool exact = true;
                for (int c = 0; c < comment.Length && exact; c++)
                    exact = lines[i - comment.Length + c].Trim() == comment[c];
                if (!exact)
                    continue;
                for (int c = 0; c < comment.Length; c++)
                    drop.Add(i - comment.Length + c);
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < lines.Count; i++)
            {
                if (drop.Contains(i))
                {
                    if (i == legacy[0] && !canonicalPresent)
                        sb.Append(CombatLogKey).Append('=').Append(derived ? "true" : "false").Append(ends[i]);
                    continue;
                }
                sb.Append(lines[i]).Append(ends[i]);
            }
            migrated = sb.ToString();
            return true;
        }

        /// <summary>
        /// Documentation repair for a bare canonical log-combat line (e.g.
        /// one written by hand, or emitted by the legacy-key migration):
        /// inserts the template's own comment block directly above the
        /// first active key line, unless that exact block already sits
        /// there. A comment block matching a known outdated template
        /// variant is REPLACED by the current one rather than duplicated;
        /// all other user comments above are kept and the key line, its
        /// value, and every other line/ending are untouched.
        /// </summary>
        private static bool EnsureCombatLogComment(string text, string templateText, out string repaired)
        {
            repaired = text;
            string[] block = TemplateCommentFor(templateText, CombatLogKey);
            if (block.Length == 0)
                return false;

            // Split preserving each line's own ending.
            var lines = new List<string>();
            var ends = new List<string>();
            int pos = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;
                int contentEnd = (i > pos && text[i - 1] == '\r') ? i - 1 : i;
                lines.Add(text.Substring(pos, contentEnd - pos));
                ends.Add(text.Substring(contentEnd, i - contentEnd + 1));
                pos = i + 1;
            }
            lines.Add(text.Substring(pos));
            ends.Add(string.Empty);

            int keyIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#"))
                    continue;
                int eq = t.IndexOf('=');
                if (eq <= 0)
                    continue;
                if (t.Substring(0, eq).Trim().Equals(CombatLogKey, StringComparison.OrdinalIgnoreCase))
                {
                    keyIdx = i;
                    break;
                }
            }
            if (keyIdx < 0)
                return false; // no canonical key - backfill handles missing keys

            int above = 0;
            while (keyIdx - above - 1 >= 0 && lines[keyIdx - above - 1].TrimStart().StartsWith("#"))
                above++;
            if (above >= block.Length)
            {
                // Compare only the LAST block.Length comment lines directly
                // above the key - user comments may sit contiguous on top.
                bool exact = true;
                for (int c = 0; c < block.Length && exact; c++)
                    exact = lines[keyIdx - block.Length + c].Trim() == block[c];
                if (exact)
                    return false; // already documented
            }

            // Outdated template comment for this key: drop it so the current
            // block replaces it instead of stacking a duplicate. Loop since
            // multiple superseded variants can sit stacked above the key;
            // only exact matches to known template text are removed - any
            // other comment belongs to the user and is preserved.
            bool dropped;
            do
            {
                dropped = false;
                foreach (string[] legacyComment in LegacyUnifiedCombatComments)
                {
                    if (keyIdx - legacyComment.Length < 0)
                        continue;
                    bool match = true;
                    for (int c = 0; c < legacyComment.Length && match; c++)
                        match = lines[keyIdx - legacyComment.Length + c].Trim() == legacyComment[c];
                    if (!match)
                        continue;
                    for (int c = 0; c < legacyComment.Length; c++)
                    {
                        lines.RemoveAt(keyIdx - legacyComment.Length);
                        ends.RemoveAt(keyIdx - legacyComment.Length);
                    }
                    keyIdx -= legacyComment.Length;
                    dropped = true;
                    break;
                }
            } while (dropped);

            string nl = text.Contains("\r\n") ? "\r\n"
                : text.Contains("\n") ? "\n" : Environment.NewLine;
            var sb = new StringBuilder(text.Length + block.Length * 80);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i == keyIdx)
                    foreach (string c in block)
                        sb.Append(c).Append(nl);
                sb.Append(lines[i]).Append(ends[i]);
            }
            repaired = sb.ToString();
            return true;
        }

        /// <summary>
        /// The contiguous '#' comment lines directly above a key in the
        /// embedded template - the authoritative documentation block,
        /// extracted from the same source the backfill appends.
        /// </summary>
        private static string[] TemplateCommentFor(string templateText, string targetKey)
        {
            var pending = new List<string>();
            foreach (string rawLine in templateText.Split('\n'))
            {
                string line = (rawLine ?? string.Empty).TrimEnd('\r');
                string trimmed = line.Trim();
                int eq = trimmed.IndexOf('=');
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || eq <= 0)
                {
                    pending.Add(line);
                    continue;
                }
                if (trimmed.Substring(0, eq).Trim().Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    int start = pending.Count;
                    while (start > 0 && pending[start - 1].TrimStart().StartsWith("#"))
                        start--;
                    var block = new List<string>();
                    for (int k = start; k < pending.Count; k++)
                        block.Add(pending[k].Trim());
                    return block.ToArray();
                }
                pending.Clear();
            }
            return new string[0];
        }

        #endregion
    }
}
