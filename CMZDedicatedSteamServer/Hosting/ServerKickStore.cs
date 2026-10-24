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
    /// Persistent kick-count storage for dedicated-server player enforcement.
    ///
    /// Purpose:
    /// - Stores per-SteamID kick counts on disk so they survive server restarts.
    /// - Counts never auto-reset: only a manual wipe (Remove/Clear, wired to an
    ///   admin command) clears them. The TrustKickBan escalation window keeps
    ///   its own timestamp so the count file is all the policy needs.
    ///
    /// File format:
    /// steamid|firstUtcTicks|count
    /// firstUtcTicks = the anchor of the current escalation window (UTC ticks).
    /// </summary>
    internal sealed class ServerKickStore
    {
        private readonly object _sync = new();
        private readonly Action<string> _log;
        private readonly string _path;

        public ServerKickStore(string baseDir, Action<string> log)
        {
            _log = log ?? (_ => { });
            string dir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
            _path = Path.Combine(dir, "Kicks.ini");
        }

        /// <summary>
        /// Loads persisted kick counts from disk.
        /// Missing files yield an empty map; malformed lines are skipped.
        /// </summary>
        public Dictionary<ulong, (DateTime FirstUtc, int Count)> Load()
        {
            var map = new Dictionary<ulong, (DateTime FirstUtc, int Count)>();
            lock (_sync)
            {
                if (!File.Exists(_path))
                    return map;

                foreach (string raw in File.ReadAllLines(_path))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 3 ||
                        !ulong.TryParse(parts[0], out ulong steamId) || steamId == 0UL ||
                        !long.TryParse(parts[1], out long firstTicks) ||
                        !int.TryParse(parts[2], out int count) || count <= 0)
                        continue;

                    map[steamId] = (new DateTime(firstTicks, DateTimeKind.Utc), count);
                }
            }

            _log($"[PlayerEnforcement] Loaded {map.Count} kick count(s) from {_path}.");
            return map;
        }

        /// <summary>
        /// Rewrites the kick-count file from the supplied snapshot.
        /// </summary>
        public void Save(Dictionary<ulong, (DateTime FirstUtc, int Count)> counts)
        {
            if (counts == null)
                return;

            lock (_sync)
            {
                using StreamWriter sw = new(_path, false);
                sw.WriteLine("# CastleForge Dedicated Server kick counts");
                sw.WriteLine("# steamid|firstUtcTicks|count — never auto-reset; wipe with a reset command");

                foreach (var kv in counts)
                    sw.WriteLine(kv.Key + "|" + kv.Value.FirstUtc.Ticks + "|" + kv.Value.Count);
            }
        }

        /// <summary>
        /// Removes one player's stored count (manual forgiveness). Caller is
        /// responsible for then invoking Save with the live map.
        /// </summary>
        public static bool Remove(
            Dictionary<ulong, (DateTime FirstUtc, int Count)> counts, ulong steamId)
        {
            return counts != null && counts.Remove(steamId);
        }
    }
}
