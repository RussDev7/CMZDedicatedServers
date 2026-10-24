/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System.Collections.Generic;
using System.IO;
using System;

namespace CMZDedicatedSteamServer.Hosting
{
    /// <summary>
    /// Persistent kick/ban storage for dedicated-server player enforcement.
    ///
    /// Purpose:
    /// - Stores bans on disk so they survive server restarts.
    /// - Supports SteamID, IP, and player-name ban keys.
    /// - Keeps the last known player name beside stronger identifiers so the ban list remains readable.
    /// - Supports exact unban matches and safe unique partial-name unban matches.
    ///
    /// Supported key types:
    /// - steam : SteamID-backed ban, strongest for CMZDedicatedSteamServer.
    /// - ip    : IP-backed ban, useful as a fallback if remote IP data is available.
    /// - name  : Gamertag fallback ban.
    ///
    /// File format:
    /// type|value|lastName|reason|createdUtcTicks|expiresUtcTicks|strikes
    /// expiresUtcTicks = DateTime.MaxValue means permanent; strikes counts
    /// auto-bans so repeat offenders escalate to longer durations.
    /// </summary>
    internal sealed class ServerBanStore
    {
        #region Fields

        /// <summary>
        /// Auto-ban duration ladder in days. Strike N gets EscalationDays[N-1];
        /// past the end of the ladder the ban is permanent.
        /// </summary>
        private static readonly int[] EscalationDays = [1, 3, 7, 14, 30];

        /// <summary>
        /// Synchronizes access to the in-memory ban list and disk save/load operations.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// In-memory copy of all currently loaded ban records.
        /// </summary>
        private readonly List<ServerBanRecord> _records = [];

        /// <summary>
        /// Server logger used for player-enforcement messages.
        /// </summary>
        private readonly Action<string> _log;

        /// <summary>
        /// Full path to the persisted ban file.
        /// </summary>
        private readonly string _path;

        #endregion

        #region Construction

        /// <summary>
        /// Creates the ban store and loads any existing bans from disk.
        /// </summary>
        /// <param name="baseDir">
        /// Base server directory. If null, the current AppDomain base directory is used.
        /// </param>
        /// <param name="log">
        /// Optional logger used for enforcement messages.
        /// </param>
        public ServerBanStore(string baseDir, Action<string> log)
        {
            _log = log ?? (_ => { });

            string dir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
            _path = Path.Combine(dir, "Bans.ini");

            // Bans used to live in a PlayerEnforcement subfolder; keep them when flattening.
            string legacyPath = Path.Combine(dir, "PlayerEnforcement", "Bans.ini");
            if (!File.Exists(_path) && File.Exists(legacyPath))
            {
                try
                {
                    File.Move(legacyPath, _path);
                }
                catch
                {
                    try
                    {
                        File.Copy(legacyPath, _path);
                    }
                    catch
                    {
                    }
                }
            }

            Load();
        }
        #endregion

        #region Ban Checks

        /// <summary>
        /// Checks whether a player matches any saved ban record.
        ///
        /// Match order:
        /// - SteamID bans when <paramref name="steamId"/> is non-zero.
        /// - IP bans when <paramref name="ip"/> is available.
        /// - Name bans when <paramref name="name"/> is available.
        /// </summary>
        /// <param name="steamId">SteamID to check, or 0 when unavailable.</param>
        /// <param name="ip">Remote IP address to check, or null/empty when unavailable.</param>
        /// <param name="name">Player name to check, or null/empty when unavailable.</param>
        /// <param name="record">Matching ban record when a ban is found.</param>
        /// <returns>True if the player matches a saved ban.</returns>
        public bool IsBanned(ulong steamId, string ip, string name, out ServerBanRecord record)
        {
            record = null;

            string steamKey = steamId == 0UL ? string.Empty : steamId.ToString();
            string ipKey = Normalize(ip);
            string nameKey = Normalize(name);

            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;

                foreach (ServerBanRecord ban in _records)
                {
                    // Expired records stay on file (their strike count drives
                    // the next auto-ban duration) but no longer block joins.
                    if (ban == null || ban.ExpiresUtc <= now)
                        continue;

                    if (ban.Type == "steam" && steamKey.Length > 0 && ban.Value == steamKey)
                    {
                        record = ban;
                        return true;
                    }

                    if (ban.Type == "ip" && ipKey.Length > 0 && string.Equals(ban.Value, ipKey, StringComparison.OrdinalIgnoreCase))
                    {
                        record = ban;
                        return true;
                    }

                    if (ban.Type == "name" && nameKey.Length > 0 && string.Equals(ban.Value, nameKey, StringComparison.OrdinalIgnoreCase))
                    {
                        record = ban;
                        return true;
                    }
                }
            }

            return false;
        }
        #endregion

        #region Add / Update Bans

        /// <summary>
        /// Adds or updates a SteamID-backed ban.
        ///
        /// Notes:
        /// - SteamID bans are the strongest ban type for the Steam dedicated server.
        /// - The last known name is stored only for readability.
        /// - Changing the Steam display name does not bypass this ban.
        /// </summary>
        /// <param name="steamId">SteamID to ban.</param>
        /// <param name="lastName">Last known player name for display in the ban list.</param>
        /// <param name="reason">Optional ban reason.</param>
        /// <param name="escalate">
        /// True for policy auto-bans: increments the strike count and applies the
        /// escalating duration ladder. False for manual bans: permanent.
        /// </param>
        public void BanSteam(ulong steamId, string lastName, string reason, bool escalate = false)
        {
            if (steamId == 0UL)
                return;

            AddOrUpdate("steam", steamId.ToString(), lastName, reason, escalate);
        }

        /// <summary>
        /// Adds or updates an IP-backed ban.
        ///
        /// Notes:
        /// - This is mainly a fallback path for transports that expose IP data.
        /// - SteamID bans should be preferred for the Steam dedicated server.
        /// </summary>
        /// <param name="ip">IP address to ban.</param>
        /// <param name="lastName">Last known player name for display in the ban list.</param>
        /// <param name="reason">Optional ban reason.</param>
        public void BanIp(string ip, string lastName, string reason)
        {
            string value = Normalize(ip);
            if (value.Length == 0)
                return;

            AddOrUpdate("ip", value, lastName, reason, escalate: false);
        }

        /// <summary>
        /// Adds or updates a name-backed ban.
        ///
        /// Notes:
        /// - Useful as a fallback when no stronger identifier is available.
        /// - Weakest ban type because players may be able to change names.
        /// - SteamID bans should be preferred for the Steam dedicated server.
        /// </summary>
        /// <param name="name">Player name to ban.</param>
        /// <param name="reason">Optional ban reason.</param>
        public void BanName(string name, string reason)
        {
            string value = Normalize(name);
            if (value.Length == 0)
                return;

            AddOrUpdate("name", value, name, reason, escalate: false);
        }
        #endregion

        #region Remove / Unban

        /// <summary>
        /// Removes a ban by exact SteamID/IP/name, or by a unique partial player name match.
        ///
        /// Exact matching:
        /// - Compares against the saved ban value, such as SteamID or IP.
        /// - Compares against the saved last known player name.
        ///
        /// Partial matching:
        /// - Only applies to saved player names.
        /// - Only succeeds when exactly one banned player matches the partial text.
        /// - Refuses ambiguous partial matches to avoid unbanning the wrong player.
        /// </summary>
        /// <param name="valueOrName">SteamID, IP, full player name, or unique partial player name.</param>
        /// <returns>True if a ban was removed.</returns>
        public bool Remove(string valueOrName)
        {
            string key = Normalize(valueOrName);
            if (key.Length == 0)
                return false;

            bool removed = false;

            lock (_sync)
            {
                // First pass: exact match against SteamID/IP/value or full saved player name.
                for (int i = _records.Count - 1; i >= 0; i--)
                {
                    ServerBanRecord ban = _records[i];

                    if (string.Equals(ban.Value, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ban.LastName, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _records.RemoveAt(i);
                        removed = true;
                    }
                }

                if (removed)
                {
                    Save();
                    return true;
                }

                // Second pass: unique partial match against the saved player name.
                int matchIndex = -1;

                for (int i = 0; i < _records.Count; i++)
                {
                    ServerBanRecord ban = _records[i];

                    if (!string.IsNullOrWhiteSpace(ban.LastName) &&
                        ban.LastName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (matchIndex >= 0)
                        {
                            _log($"[PlayerEnforcement] Unban failed. '{key}' matched multiple banned players. Use the full name or SteamID/IP.");
                            return false;
                        }

                        matchIndex = i;
                    }
                }

                if (matchIndex >= 0)
                {
                    ServerBanRecord removedBan = _records[matchIndex];
                    _records.RemoveAt(matchIndex);

                    _log($"[PlayerEnforcement] Removed ban for {removedBan.LastName} ({removedBan.Value}).");
                    removed = true;
                }
            }

            if (removed)
                Save();

            return removed;
        }
        #endregion

        #region Snapshots

        /// <summary>
        /// Returns a copy of the current ban records.
        ///
        /// Notes:
        /// - The returned list is a snapshot.
        /// - Callers can enumerate it safely without holding the store lock.
        /// - Editing the returned list does not modify the live ban store.
        /// </summary>
        /// <returns>A copy of the currently loaded ban records.</returns>
        public List<ServerBanRecord> GetSnapshot()
        {
            lock (_sync)
                return [.. _records];
        }
        #endregion

        #region Internal Add / Update Logic

        /// <summary>
        /// Adds a new ban record or updates an existing record with the same type/value key.
        ///
        /// Notes:
        /// - Type and value are normalized before matching.
        /// - New records receive the current UTC timestamp.
        /// - Existing records keep their original timestamp and update only name/reason.
        /// - Saves the ban file after the in-memory list is changed.
        /// </summary>
        /// <param name="type">Ban type, such as steam, ip, or name.</param>
        /// <param name="value">Ban key value, such as SteamID, IP, or player name.</param>
        /// <param name="lastName">Last known player name for display.</param>
        /// <param name="reason">Optional ban reason.</param>
        /// <param name="escalate">
        /// Auto-ban path: bumps the strike count and sets the expiry from the
        /// duration ladder (1/3/7/14/30 days, then permanent). Strike history
        /// survives expiry because expired records are never deleted.
        /// </param>
        private void AddOrUpdate(string type, string value, string lastName, string reason, bool escalate)
        {
            type = Normalize(type).ToLowerInvariant();
            value = Normalize(value);

            if (type.Length == 0 || value.Length == 0)
                return;

            lock (_sync)
            {
                ServerBanRecord existing = null;

                foreach (ServerBanRecord ban in _records)
                {
                    if (ban.Type == type && string.Equals(ban.Value, value, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = ban;
                        break;
                    }
                }

                int strikes = existing?.Strikes ?? 0;
                DateTime expiresUtc = DateTime.MaxValue;

                if (escalate)
                {
                    strikes++;
                    int days = strikes <= EscalationDays.Length ? EscalationDays[strikes - 1] : 0;
                    if (days > 0)
                        expiresUtc = DateTime.UtcNow.AddDays(days);

                    reason = $"{reason} (auto-ban #{strikes}, {(days > 0 ? days + "d" : "permanent")})";
                    _log($"[PlayerEnforcement] Auto-ban #{strikes} for {lastName} ({value}): {(days > 0 ? days + " days" : "permanent")}.");
                }

                if (existing == null)
                {
                    _records.Add(new ServerBanRecord
                    {
                        Type = type,
                        Value = value,
                        LastName = Clean(lastName),
                        Reason = Clean(reason),
                        CreatedUtc = DateTime.UtcNow,
                        ExpiresUtc = expiresUtc,
                        Strikes = strikes
                    });
                }
                else
                {
                    existing.LastName = Clean(lastName);
                    existing.Reason = Clean(reason);
                    existing.ExpiresUtc = expiresUtc;
                    existing.Strikes = strikes;
                }
            }

            Save();
        }
        #endregion

        #region Persistence

        /// <summary>
        /// Loads persisted ban records from disk into memory.
        ///
        /// Notes:
        /// - Missing files are allowed and simply result in an empty ban list.
        /// - Empty lines and comment lines beginning with # are ignored.
        /// - Malformed lines with fewer than two fields are skipped.
        /// - Older or incomplete entries fall back to empty strings or current UTC time.
        /// </summary>
        private void Load()
        {
            lock (_sync)
            {
                _records.Clear();

                if (!File.Exists(_path))
                    return;

                foreach (string raw in File.ReadAllLines(_path))
                {
                    string line = (raw ?? string.Empty).Trim();

                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 2)
                        continue;

                    DateTime createdUtc =
                        parts.Length >= 5 && long.TryParse(parts[4], out long ticks)
                            ? new DateTime(ticks, DateTimeKind.Utc)
                            : DateTime.UtcNow;

                    // Older 5-field lines load as permanent bans with no strikes.
                    DateTime expiresUtc =
                        parts.Length >= 6 && long.TryParse(parts[5], out long expTicks)
                            ? new DateTime(expTicks, DateTimeKind.Utc)
                            : DateTime.MaxValue;

                    int strikes =
                        parts.Length >= 7 && int.TryParse(parts[6], out int s) && s > 0
                            ? s
                            : 0;

                    _records.Add(new ServerBanRecord
                    {
                        Type = Normalize(parts[0]).ToLowerInvariant(),
                        Value = Normalize(parts[1]),
                        LastName = parts.Length >= 3 ? Clean(parts[2]) : string.Empty,
                        Reason = parts.Length >= 4 ? Clean(parts[3]) : string.Empty,
                        CreatedUtc = createdUtc,
                        ExpiresUtc = expiresUtc,
                        Strikes = strikes
                    });
                }
            }

            _log($"[PlayerEnforcement] Loaded bans from {_path}.");
        }

        /// <summary>
        /// Saves the current ban list to disk.
        ///
        /// Notes:
        /// - Writes the entire file each time.
        /// - Uses a snapshot so the file write does not enumerate the live list directly.
        /// - Values are cleaned before writing so the pipe-delimited format stays valid.
        /// </summary>
        private void Save()
        {
            List<ServerBanRecord> snapshot = GetSnapshot();

            using StreamWriter sw = new(_path, false);
            sw.WriteLine("# CastleForge Dedicated Server bans");
            sw.WriteLine("# type|value|lastName|reason|createdUtcTicks|expiresUtcTicks|strikes");

            foreach (ServerBanRecord ban in snapshot)
            {
                sw.WriteLine(
                    Clean(ban.Type) + "|" +
                    Clean(ban.Value) + "|" +
                    Clean(ban.LastName) + "|" +
                    Clean(ban.Reason) + "|" +
                    ban.CreatedUtc.Ticks + "|" +
                    ban.ExpiresUtc.Ticks + "|" +
                    ban.Strikes);
            }
        }
        #endregion

        #region String Helpers

        /// <summary>
        /// Normalizes nullable text into a safe trimmed string.
        /// </summary>
        /// <param name="value">Input text.</param>
        /// <returns>A non-null trimmed string.</returns>
        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        /// <summary>
        /// Cleans text before saving it to the pipe-delimited ban file.
        ///
        /// Notes:
        /// - Removes line breaks.
        /// - Replaces pipe characters so values cannot break the file format.
        /// - Applies the same null-safe trimming as Normalize.
        /// </summary>
        /// <param name="value">Input text.</param>
        /// <returns>Cleaned text safe for one ban-file field.</returns>
        private static string Clean(string value)
        {
            return Normalize(value)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("|", "/");
        }
        #endregion
    }

    #region Ban Record Model

    /// <summary>
    /// Represents one persisted player-enforcement ban entry.
    /// </summary>
    internal sealed class ServerBanRecord
    {
        /// <summary>
        /// Ban type, such as steam, ip, or name.
        /// </summary>
        public string Type;

        /// <summary>
        /// Ban key value.
        ///
        /// Examples:
        /// - SteamID for steam bans.
        /// - IP address for ip bans.
        /// - Player name for name bans.
        /// </summary>
        public string Value;

        /// <summary>
        /// Last known player name saved for readability.
        /// </summary>
        public string LastName;

        /// <summary>
        /// Optional reason supplied when the ban was created or updated.
        /// </summary>
        public string Reason;

        /// <summary>
        /// UTC time when the ban record was originally created.
        /// </summary>
        public DateTime CreatedUtc;

        /// <summary>
        /// UTC time the ban expires. DateTime.MaxValue = permanent.
        /// Expired records are kept (they hold the auto-ban strike count)
        /// but IsBanned ignores them.
        /// </summary>
        public DateTime ExpiresUtc = DateTime.MaxValue;

        /// <summary>
        /// Number of auto-bans issued for this identity — drives the
        /// escalating duration ladder. Manual bans leave it untouched.
        /// </summary>
        public int Strikes;
    }
    #endregion
}