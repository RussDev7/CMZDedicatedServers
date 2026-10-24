/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;

namespace CMZDedicatedLidgrenServer.Commands
{
    /// <summary>
    /// Permission tiers used by in-game dedicated-server commands.
    /// Higher numeric values inherit access to lower-rank commands.
    /// Every connected player holds a rank: new joiners are assigned Member,
    /// Visitor is a restricted demotion rank below Member, and Moderator/Admin
    /// are operator ranks that bypass trust enforcement.
    /// </summary>
    internal enum ServerCommandRank
    {
        Visitor = 5,
        Member = 10,
        Moderator = 50,
        Admin = 100
    }

    /// <summary>
    /// Shared helpers for parsing, normalizing, and formatting command ranks.
    /// </summary>
    internal static class ServerCommandRankHelper
    {
        /// <summary>
        /// Attempts to parse a rank name from chat or the player-rank file.
        /// Legacy "Default"/"Defualt" entries mean "ordinary player" in that
        /// context, so they map to Member.
        /// </summary>
        public static bool TryParse(string value, out ServerCommandRank rank)
        {
            rank = ServerCommandRank.Member;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim();

            if (string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Defualt", StringComparison.OrdinalIgnoreCase))
            {
                rank = ServerCommandRank.Member;
                return true;
            }

            if (TryParseStrict(normalized, out rank))
                return true;

            // Hard cap: nothing may sit above Admin ("op"). Numeric values past
            // the cap (e.g. a tampered "999" rank) are sent back to Member.
            if (int.TryParse(normalized, out int numeric) && numeric > (int)ServerCommandRank.Admin)
            {
                rank = ServerCommandRank.Member;
                return true;
            }

            rank = ServerCommandRank.Member;
            return false;
        }

        /// <summary>
        /// Strict parse for config values: only defined rank names/values are
        /// accepted; over-cap input is rejected so callers keep safe defaults.
        /// Legacy "Default"/"Defualt" in a required-rank position meant "anyone
        /// may use this", which maps to Visitor - the new lowest rank.
        /// </summary>
        public static bool TryParseStrict(string value, out ServerCommandRank rank)
        {
            rank = ServerCommandRank.Member;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim();

            if (string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Defualt", StringComparison.OrdinalIgnoreCase))
            {
                rank = ServerCommandRank.Visitor;
                return true;
            }

            if (string.Equals(normalized, "Operator", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Op", StringComparison.OrdinalIgnoreCase))
            {
                rank = ServerCommandRank.Admin;
                return true;
            }

            if (!Enum.TryParse(normalized, true, out rank) ||
                !Enum.IsDefined(typeof(ServerCommandRank), rank))
            {
                rank = ServerCommandRank.Member;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Enforces the rank ceiling: values above Admin are sent back to Member.
        /// Applied on every store read/write so no player can hold an over-cap rank.
        /// </summary>
        public static ServerCommandRank ClampRank(ServerCommandRank rank)
        {
            return rank > ServerCommandRank.Admin ? ServerCommandRank.Member : rank;
        }

        /// <summary>
        /// Returns true when <paramref name="actual"/> is high enough for <paramref name="required"/>.
        /// </summary>
        public static bool HasAccess(ServerCommandRank actual, ServerCommandRank required)
        {
            return (int)actual >= (int)required;
        }

        /// <summary>
        /// Converts a rank to the exact config spelling this server writes.
        /// </summary>
        public static string ToConfigString(ServerCommandRank rank)
        {
            return rank switch
            {
                ServerCommandRank.Visitor => "Visitor",
                ServerCommandRank.Member => "Member",
                ServerCommandRank.Moderator => "Moderator",
                ServerCommandRank.Admin => "Admin",
                _ => "Member",
            };
        }
    }
}
