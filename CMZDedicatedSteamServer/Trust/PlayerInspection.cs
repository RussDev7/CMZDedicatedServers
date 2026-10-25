/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CMZDedicatedSteamServer.Trust
{
    /// <summary>
    /// Server-owned inspection snapshot of one connected player: trust state
    /// (position, violations, announced flag) plus the server's copy of their
    /// inventory. Data-only — the policy fills it, PlayerInspection formats it.
    /// </summary>
    internal sealed class PlayerTrustSnapshot
    {
        public byte Gid;
        public ulong SteamId;
        public string Name = "?";
        public string Rank = "Member";
        public bool Announced;
        public bool WasDead;
        public bool HasBaseline;
        public float X, Y, Z;
        public int StrikeTotal;
        public string StrikeBreakdown = "none";
        public bool HasInventory;
        public List<KeyValuePair<int, int>> Inventory = new List<KeyValuePair<int, int>>();
        // Peer traffic (filled by the host, not the policy):
        public bool HasTraffic;
        public double LastGapMs, AvgGapMs, IdleMs;
        public int MaxPacketBytes;
        public long PacketCount;
    }

    /// <summary>
    /// Formats PlayerTrustSnapshot for the playerinfo inspection surface:
    /// a human-readable block for consoles and the @@PINFO machine line the
    /// desktop GUI feeds its player cards.
    /// </summary>
    internal static class PlayerInspection
    {
        public static string FormatDisplay(PlayerTrustSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("Player '").Append(s.Name).Append("' (").Append(s.SteamId).Append(')');
            sb.Append("  GID=").Append(s.Gid);
            sb.Append('\n').Append("  Rank: ").Append(s.Rank);
            sb.Append('\n').Append("  Announced: ").Append(s.Announced ? "yes" : "no");
            if (s.WasDead)
                sb.Append("   (dead)");
            sb.Append('\n').Append("  Position: ").Append(FormatPosition(s));
            sb.Append('\n').Append("  Traffic: ").Append(FormatTraffic(s));
            sb.Append('\n').Append("  Strikes: ").Append(s.StrikeBreakdown);
            sb.Append('\n').Append("  Inventory: ").Append(FormatInventory(s));
            return sb.ToString();
        }

        // Pipe-delimited machine line; the GUI splits on '|':
        // <steamid>|<name>|<rank>|<announced>|<pos>|<strikes>|<inventory>|<traffic>
        public static string FormatMachine(PlayerTrustSnapshot s)
        {
            string safeName = (s.Name ?? "?").Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ');
            string invMachine = FormatInventory(s).Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
            string trafficMachine = FormatTraffic(s).Replace('|', '/');
            return
                "@@PINFO " + s.SteamId +
                "|" + safeName +
                "|" + s.Rank +
                "|" + (s.Announced ? "1" : "0") +
                "|" + FormatPosition(s) +
                "|" + s.StrikeBreakdown +
                "|" + invMachine +
                "|" + trafficMachine;
        }

        public static string FormatPosition(PlayerTrustSnapshot s)
            => s.HasBaseline
                ? $"{s.X:0.0}, {s.Y:0.0}, {s.Z:0.0}"
                : "no position yet";

        public static string FormatTraffic(PlayerTrustSnapshot s)
        {
            if (!s.HasTraffic)
                return "no packets seen";
            string gap = s.LastGapMs >= 0
                ? $"gap {s.LastGapMs:0}ms"
                : "first packet";
            string avg = s.AvgGapMs >= 0 ? $"~{s.AvgGapMs:0}ms avg" : "no avg yet";
            return $"{gap}, {avg} | idle {(int)IdleRounded(s.IdleMs)}ms | max {s.MaxPacketBytes}B | {s.PacketCount} pkts";
        }

        private static double IdleRounded(double ms) => ms < 0 ? 0 : ms;

        public static string FormatInventory(PlayerTrustSnapshot s)
        {
            if (!s.HasInventory)
                return "no inventory snapshot yet";
            var items = s.Inventory
                .Where(kv => kv.Value > 0)
                .Select(kv => DescribeItem(kv.Key) + " x" + kv.Value)
                .ToList();
            return items.Count == 0 ? "empty" : string.Join(", ", items);
        }

        /// <summary>
        /// Display name for an inventory id (enum member) or "#id" for
        /// out-of-range ids.
        /// </summary>
        internal static string DescribeItem(int itemId)
        {
            try
            {
                if (ItemPolicy.ItemIdEnumType != null)
                {
                    string name = Enum.GetName(ItemPolicy.ItemIdEnumType, itemId);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            catch { }
            return "#" + itemId;
        }
    }
}
