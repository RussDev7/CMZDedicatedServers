/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;

namespace CMZDedicatedSteamServer.Hosting
{
    /// <summary>
    /// Per-peer packet traffic counters for the playerinfo inspection surface.
    /// Fed once per inbound packet from ProcessPacket (main loop); read from
    /// the command thread — all access is locked on the map.
    /// Tracks: inter-arrival gap (how fast a player's packets come in), the
    /// largest payload seen, total packet count, and idle time since last.
    /// </summary>
    internal sealed class PeerTrafficMonitor
    {
        private sealed class Stats
        {
            public DateTime LastUtc = DateTime.MinValue;
            public double LastGapMs = -1;
            public double AvgGapMs = -1;
            public int MaxBytes;
            public long Count;
        }

        private readonly Dictionary<ulong, Stats> _bySteamId = new Dictionary<ulong, Stats>();

        public void Record(ulong steamId, int bytes)
        {
            DateTime now = DateTime.UtcNow;
            lock (_bySteamId)
            {
                if (!_bySteamId.TryGetValue(steamId, out Stats s))
                    s = new Stats();

                if (s.LastUtc != DateTime.MinValue)
                {
                    double gap = (now - s.LastUtc).TotalMilliseconds;
                    s.LastGapMs = gap;
                    s.AvgGapMs = s.AvgGapMs < 0 ? gap : s.AvgGapMs * 0.9 + gap * 0.1;
                }
                if (bytes > s.MaxBytes)
                    s.MaxBytes = bytes;
                s.LastUtc = now;
                s.Count++;
                _bySteamId[steamId] = s;
            }
        }

        public bool TryGet(ulong steamId, out double lastGapMs, out double avgGapMs,
            out int maxBytes, out long count, out double idleMs)
        {
            lock (_bySteamId)
            {
                if (_bySteamId.TryGetValue(steamId, out Stats s) && s.LastUtc != DateTime.MinValue)
                {
                    lastGapMs = s.LastGapMs;
                    avgGapMs = s.AvgGapMs;
                    maxBytes = s.MaxBytes;
                    count = s.Count;
                    idleMs = (DateTime.UtcNow - s.LastUtc).TotalMilliseconds;
                    return true;
                }
            }
            lastGapMs = avgGapMs = idleMs = 0;
            maxBytes = 0;
            count = 0;
            return false;
        }

        public void Remove(ulong steamId)
        {
            lock (_bySteamId)
                _bySteamId.Remove(steamId);
        }
    }
}
