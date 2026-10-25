/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System.Collections.Generic;
using System.Linq;
using System;
using CMZDedicatedServer.Plugins;

namespace CMZDedicatedSteamServer.Commands
{
    /// <summary>
    /// Metadata and handler for one registered in-game server command.
    /// </summary>
    internal sealed class ServerCommandDefinition(
        string name,
        string usage,
        string description,
        ServerCommandRank defaultRequiredRank,
        Action<ServerCommandContext, string[]> handler)
    {
        public string Name { get; } = ServerCommandParser.NormalizeCommandName(name);
        public string Usage { get; } = usage ?? string.Empty;
        public string Description { get; } = description ?? string.Empty;
        public ServerCommandRank DefaultRequiredRank { get; } = defaultRequiredRank;
        public Action<ServerCommandContext, string[]> Handler { get; } = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Central in-game server command registry, permission checker, and dispatcher.
    ///
    /// Transport-specific server code only needs to:
    /// - Detect that a chat packet contains a command.
    /// - Create a ServerCommandContext.
    /// - Call TryExecute(...).
    /// </summary>
    internal sealed class ServerCommandManager
    {
        private readonly Dictionary<string, ServerCommandDefinition> _commands =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ServerCommandPermissionStore _permissions;
        private readonly Action<string> _log;
        private readonly Func<string> _getPlayerListText;
        private readonly Func<string, string, bool> _kickPlayer;
        private readonly Func<string, string, bool> _banPlayer;
        private readonly Func<string, bool> _unbanPlayer;
        private readonly Action _reloadAll;
        private readonly Action _reloadPlugins;
        private readonly Action _restartServer;
        private readonly Func<string, ServerCommandTarget> _resolveTarget;
        private readonly Func<string, string> _getPlayerInfoText;

        public ServerCommandManager(
            string baseDir,
            Action<string> log,
            Func<string> getPlayerListText,
            Func<string, string, bool> kickPlayer,
            Func<string, string, bool> banPlayer,
            Func<string, bool> unbanPlayer,
            Action reloadAll,
            Action reloadPlugins,
            Action restartServer,
            Func<string, ServerCommandTarget> resolveTarget,
            Func<string, string> getPlayerInfoText)
        {
            _log = log ?? (_ => { });
            _getPlayerListText = getPlayerListText;
            _kickPlayer = kickPlayer;
            _banPlayer = banPlayer;
            _unbanPlayer = unbanPlayer;
            _reloadAll = reloadAll;
            _reloadPlugins = reloadPlugins;
            _restartServer = restartServer;
            _resolveTarget = resolveTarget;
            _getPlayerInfoText = getPlayerInfoText;

            _permissions = new ServerCommandPermissionStore(baseDir, _log);

            RegisterBuiltInCommands();
            Reload();
        }

        /// <summary>
        /// True when <paramref name="rawText"/> starts with one of the configured command prefixes.
        /// </summary>
        public bool IsCommand(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            if (!_permissions.Enabled)
                return false;

            string text = rawText.TrimStart();

            foreach (string prefix in _permissions.Prefixes.OrderByDescending(prefix => prefix.Length))
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the target is protected from moderation actions such as kick and ban.
        /// Currently, Admin-ranked / operator players are protected.
        /// </summary>
        private bool IsProtectedModerationTarget(ServerCommandTarget target)
        {
            if (target == null)
                return false;

            if (_permissions.GetRankForIdentityKey(target.RemoteKey) >= ServerCommandRank.Admin)
                return true;

            if (target.AdditionalRemoteKeys != null)
            {
                foreach (string key in target.AdditionalRemoteKeys)
                {
                    if (_permissions.GetRankForIdentityKey(key) >= ServerCommandRank.Admin)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reloads command permissions and player ranks from disk.
        /// </summary>
        public void Reload()
        {
            _permissions.Reload();
            _permissions.EnsureCommandDefaults(_commands.Values);
            _log("[Commands] Reloaded command permissions and player ranks.");
        }

        /// <summary>
        /// Registers a command. Existing command names are replaced so future plugins/systems can override safely.
        /// </summary>
        public void Register(
            string name,
            string usage,
            string description,
            ServerCommandRank defaultRequiredRank,
            Action<ServerCommandContext, string[]> handler)
        {
            ServerCommandDefinition command = new(
                name,
                usage,
                description,
                defaultRequiredRank,
                handler);

            _commands[command.Name] = command;
        }

        /// <summary>
        /// Executes a command from chat.
        /// Returns true if the chat message should be consumed and not relayed publicly.
        /// </summary>
        public bool TryExecute(ServerCommandContext context)
        {
            if (context == null || !_permissions.Enabled)
                return false;

            if (!ServerCommandParser.TryParse(
                context.RawText,
                _permissions.Prefixes,
                out string usedPrefix,
                out string commandName,
                out string[] args))
            {
                return false;
            }

            if (string.IsNullOrEmpty(usedPrefix))
                usedPrefix = _permissions.Prefix;

            if (!_commands.TryGetValue(commandName, out ServerCommandDefinition command))
            {
                context.SendReply("[Server] Unknown command. Use " + usedPrefix + "help.");

                context.WriteLog(
                    "[Commands] Unknown command from " +
                    BuildCommandActorLabel(context) +
                    ": " +
                    SanitizeCommandTextForLog(context.RawText) +
                    ".");

                return true;
            }

            ServerCommandRank senderRank = _permissions.GetPlayerRank(context);
            ServerCommandRank requiredRank = _permissions.GetRequiredRank(command.Name, command.DefaultRequiredRank);

            if (!ServerCommandRankHelper.HasAccess(senderRank, requiredRank))
            {
                context.SendReply(
                    "[Server] You need " +
                    ServerCommandRankHelper.ToConfigString(requiredRank) +
                    " to use " +
                    usedPrefix +
                    command.Name +
                    ".");

                context.WriteLog(
                    "[Commands] Denied " +
                    SanitizeCommandTextForLog(context.RawText) +
                    " from " +
                    BuildCommandActorLabel(context) +
                    ", rank=" +
                    ServerCommandRankHelper.ToConfigString(senderRank) +
                    ", required=" +
                    ServerCommandRankHelper.ToConfigString(requiredRank) +
                    ".");

                return true;
            }

            LogCommandExecution(context);

            try
            {
                command.Handler(context, args);
            }
            catch (Exception ex)
            {
                context.SendReply("[Server] Command failed: " + ex.Message);
                context.WriteLog("[Commands] Exception while executing " + usedPrefix + command.Name + ": " + ex);
            }

            return true;
        }

        /// <summary>
        /// Executes a server command from the dedicated server console.
        /// Console commands do not require the in-game prefix and always run as Admin.
        /// </summary>
        public bool TryExecuteConsole(string rawText, Action<string> reply, Action<string> broadcast)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            string commandText = rawText.Trim();
            string prefix = string.IsNullOrEmpty(_permissions.Prefix) ? "!" : _permissions.Prefix;

            // Console can type:
            // op Jacob
            // !op Jacob
            // /op Jacob
            if (!IsCommand(commandText))
                commandText = prefix + commandText;

            return TryExecute(new ServerCommandContext
            {
                PlayerId = 0,
                PlayerName = "Console",
                RemoteId = 0UL,
                RemoteKey = "console",
                RawText = commandText,
                RankOverride = ServerCommandRank.Admin,
                Reply = message =>
                {
                    reply?.Invoke(message);
                },
                Broadcast = broadcast,
                Log = _log
            });
        }

        private void RegisterBuiltInCommands()
        {
            Register(
                "help",
                "help [page]",
                "Shows available commands.",
                ServerCommandRank.Visitor,
                HandleHelp);

            Register(
                "rank",
                "rank",
                "Shows your command rank.",
                ServerCommandRank.Visitor,
                HandleRank);

            Register(
                "players",
                "players",
                "Lists connected players.",
                ServerCommandRank.Moderator,
                HandlePlayers);

            Register(
                "playerinfo",
                "playerinfo <player|id|steamid>",
                "Shows server-side inspection data for a connected player.",
                ServerCommandRank.Moderator,
                HandlePlayerInfo);

            Register(
                "status",
                "status",
                "Shows server status and information.",
                ServerCommandRank.Visitor,
                HandleStatus);

            Register(
                "kick",
                "kick <player|id> [reason]",
                "Drops a non-operator player from the server.",
                ServerCommandRank.Moderator,
                HandleKick);

            Register(
                "ban",
                "ban <player|id|steamid> [reason]",
                "Bans and drops a non-operator player from the server.",
                ServerCommandRank.Admin,
                HandleBan);

            Register(
                "unban",
                "unban <player|steamid|ip>",
                "Removes a persisted ban.",
                ServerCommandRank.Admin,
                HandleUnban);

            Register(
                "reload",
                "reload [plugins|commands|all]",
                "Reloads full server runtime config (default), plugins, or command files.",
                ServerCommandRank.Admin,
                HandleReload);

            Register(
                "restart",
                "restart",
                "Restarts the dedicated server process.",
                ServerCommandRank.Admin,
                HandleRestart);

            Register(
                "op",
                "op <player|steamid|key>",
                "Gives a player Admin command access.",
                ServerCommandRank.Admin,
                HandleOp);

            Register(
                "deop",
                "deop <player|steamid|key>",
                "Removes a player's explicit command rank.",
                ServerCommandRank.Admin,
                HandleDeop);

            Register(
                "setrank",
                "setrank <player|steamid|key> <Visitor|Member|Moderator|Admin>",
                "Sets a player's command rank.",
                ServerCommandRank.Admin,
                HandleSetRank);
        }

        #region Built-in Command Handlers

        private void HandleHelp(ServerCommandContext context, string[] args)
        {
            int page = 1;

            if (args != null && args.Length > 0)
                int.TryParse(args[0], out page);

            if (page < 1)
                page = 1;

            ServerCommandRank senderRank = _permissions.GetPlayerRank(context);

            List<ServerCommandDefinition> visible = [.. _commands.Values
                .Where(c => ServerCommandRankHelper.HasAccess(
                    senderRank,
                    _permissions.GetRequiredRank(c.Name, c.DefaultRequiredRank)))
                .OrderBy(c => c.Name)];

            int maxLines = Math.Max(1, _permissions.HelpMaxLines);
            int commandsPerPage = Math.Max(1, maxLines - 1);
            int totalPages = Math.Max(1, (int)Math.Ceiling(visible.Count / (double)commandsPerPage));

            if (page > totalPages)
                page = totalPages;

            context.SendReply("[Commands] Page " + page + "/" + totalPages + " (" + _permissions.Prefix + "help <page>)");

            foreach (ServerCommandDefinition command in visible.Skip((page - 1) * commandsPerPage).Take(commandsPerPage))
            {
                string required = ServerCommandRankHelper.ToConfigString(
                    _permissions.GetRequiredRank(command.Name, command.DefaultRequiredRank));

                context.SendReply(
                    _permissions.Prefix +
                    command.Usage +
                    " - " +
                    command.Description +
                    " [" +
                    required +
                    "]");
            }
        }

        private void HandlePlayerInfo(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                _log("[Commands] Usage: playerinfo <player|id|steamid>");
                return;
            }

            string text = _getPlayerInfoText != null
                ? _getPlayerInfoText(args[0])
                : "Player info is not available on this host.";
            if (!string.IsNullOrWhiteSpace(text))
                _log(text);
        }

        private void HandlePlayers(ServerCommandContext context, string[] args)
        {
            string text = _getPlayerListText != null
                ? _getPlayerListText()
                : "Player list is not available.";

            SendMultiline(context, text);
        }

        private void HandleRank(ServerCommandContext context, string[] args)
        {
            ServerCommandRank senderRank = _permissions.GetPlayerRank(context);
            context.SendReply(
                "[Server] " +
                SafeName(context.PlayerName, context.PlayerId) +
                " rank: " +
                ServerCommandRankHelper.ToConfigString(senderRank));
        }

        private void HandleStatus(ServerCommandContext context, string[] args)
        {
            string playerList = _getPlayerListText != null
                ? _getPlayerListText()
                : "Player list is not available.";

            context.SendReply("[Server] Server Status:");
            context.SendReply("[Server] " + playerList);
        }

        private void HandleKick(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "kick <player|id> [reason]");
                return;
            }

            string target = args[0];
            string reason = ServerCommandParser.JoinFrom(args, 1);

            ServerCommandTarget resolvedTarget = ResolveModerationTarget(target);

            if (IsProtectedModerationTarget(resolvedTarget))
            {
                context.SendReply("[Server] You cannot kick an operator/admin player.");
                context.WriteLog(
                    "[Commands] Blocked kick against protected target " +
                    (resolvedTarget != null ? resolvedTarget.DisplayName : target) +
                    " by " +
                    SafeName(context.PlayerName, context.PlayerId) +
                    ".");

                return;
            }

            bool ok = _kickPlayer != null && _kickPlayer(target, reason);

            context.SendReply(ok
                ? "[Server] Kicked " + target + "."
                : "[Server] Could not find player to kick: " + target);
        }

        private void HandleBan(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "ban <player|id|steamid> [reason]");
                return;
            }

            string target = args[0];
            string reason = ServerCommandParser.JoinFrom(args, 1);

            ServerCommandTarget resolvedTarget = ResolveModerationTarget(target);

            if (IsProtectedModerationTarget(resolvedTarget))
            {
                context.SendReply("[Server] You cannot ban an operator/admin player.");
                context.WriteLog(
                    "[Commands] Blocked ban against protected target " +
                    (resolvedTarget != null ? resolvedTarget.DisplayName : target) +
                    " by " +
                    SafeName(context.PlayerName, context.PlayerId) +
                    ".");

                return;
            }

            bool ok = _banPlayer != null && _banPlayer(target, reason);

            context.SendReply(ok
                ? "[Server] Banned " + target + "."
                : "[Server] Could not find player to ban: " + target);
        }

        private void HandleUnban(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "unban <player|steamid|ip>");
                return;
            }

            string target = args[0];
            bool ok = _unbanPlayer != null && _unbanPlayer(target);

            context.SendReply(ok
                ? "[Server] Unbanned " + target + "."
                : "[Server] No ban matched: " + target);
        }

        private void HandleReload(ServerCommandContext context, string[] args)
        {
            string scope = args != null && args.Length > 0
                ? args[0].Trim().ToLowerInvariant()
                : "all";

            if (scope == "commands" || scope == "command")
            {
                Reload();
                context.SendReply("[Server] Reloaded command files.");
                return;
            }

            if (scope == "plugins" || scope == "plugin")
            {
                if (_reloadPlugins != null)
                {
                    _reloadPlugins();
                    context.SendReply("[Server] Reloaded plugins.");
                }
                else
                {
                    context.SendReply("[Server] Plugin reload is not available.");
                }

                return;
            }

            if (scope == "all" || scope == "server" || scope == "config")
            {
                if (_reloadAll != null)
                {
                    _reloadAll();
                    context.SendReply("[Server] Reloaded server config, plugins, and commands.");
                }
                else
                {
                    Reload();
                    context.SendReply("[Server] Reloaded command files.");
                }

                return;
            }

            context.SendReply("[Server] Usage: " + _permissions.Prefix + "reload [plugins|commands|all]");
        }

        private void HandleRestart(ServerCommandContext context, string[] args)
        {
            if (_restartServer == null)
            {
                context.SendReply("[Server] Restart is not available.");
                return;
            }

            context.SendReply("[Server] Restarting server...");
            _restartServer();
        }

        private void HandleOp(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "op <player|steamid|key>");
                return;
            }

            ServerCommandTarget target = ResolveRankTarget(args[0]);
            if (target == null || string.IsNullOrWhiteSpace(target.RemoteKey))
            {
                context.SendReply("[Server] Could not resolve target: " + args[0]);
                return;
            }

            _permissions.SetPlayerRank(target.RemoteKey, ServerCommandRank.Admin);
            context.SendReply("[Server] Opped " + target.DisplayName + " as Admin.");
            context.WriteLog("[Commands] " + SafeName(context.PlayerName, context.PlayerId) + " opped " + target.DisplayName + " (" + target.RemoteKey + ").");
        }

        private void HandleDeop(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "deop <player|steamid|key>");
                return;
            }

            ServerCommandTarget target = ResolveRankTarget(args[0]);
            string key = target != null && !string.IsNullOrWhiteSpace(target.RemoteKey)
                ? target.RemoteKey
                : ServerCommandPermissionStore.BuildIdentityKeyFromRawTarget(args[0]);

            bool removed = _permissions.RemovePlayerRank(key);

            context.SendReply(removed
                ? "[Server] Removed explicit rank for " + (target != null ? target.DisplayName : key) + "."
                : "[Server] No explicit rank existed for " + (target != null ? target.DisplayName : key) + ".");
        }

        private void HandleSetRank(ServerCommandContext context, string[] args)
        {
            if (args == null || args.Length < 2)
            {
                context.SendReply("[Server] Usage: " + _permissions.Prefix + "setrank <player|steamid|key> <Visitor|Member|Moderator|Admin>");
                return;
            }

            if (!ServerCommandRankHelper.TryParse(args[1], out ServerCommandRank rank))
            {
                context.SendReply("[Server] Unknown rank: " + args[1]);
                return;
            }

            ServerCommandTarget target = ResolveRankTarget(args[0]);
            string key = target != null && !string.IsNullOrWhiteSpace(target.RemoteKey)
                ? target.RemoteKey
                : ServerCommandPermissionStore.BuildIdentityKeyFromRawTarget(args[0]);

            if (string.IsNullOrWhiteSpace(key))
            {
                context.SendReply("[Server] Could not resolve target: " + args[0]);
                return;
            }

            _permissions.SetPlayerRank(key, rank);

            context.SendReply(
                "[Server] Set " +
                (target != null ? target.DisplayName : key) +
                " to " +
                ServerCommandRankHelper.ToConfigString(rank) +
                ".");
        }

        #endregion

        /// <summary>
        /// Resolves a connected player's saved rank for the built-in trust layer.
        /// Prefers the stable steam: identity key, then falls back to the name: key.
        /// </summary>
        public ServerCommandRank GetRankForPlayer(ulong remoteId, string playerName)
        {
            if (_permissions == null)
                return ServerCommandRank.Member;

            if (remoteId != 0UL &&
                _permissions.TryGetRankForIdentityKey(
                    "steam:" + remoteId,
                    out ServerCommandRank steamRank))
            {
                return steamRank;
            }

            if (!string.IsNullOrWhiteSpace(playerName))
                return _permissions.GetRankForIdentityKey("name:" + playerName.Trim());

            return ServerCommandRank.Member;
        }

        /// <summary>
        /// Ensures a connecting player has an explicit Member rank on record so every
        /// player holds a rank instead of being unranked.
        /// </summary>
        public void EnsureMemberRankAssigned(ulong remoteId, string playerName)
        {
            _permissions?.EnsureMemberRankAssigned(remoteId, playerName);
        }

        #region Helpers

        private ServerCommandTarget ResolveRankTarget(string rawTarget)
        {
            ServerCommandTarget target = null;

            try
            {
                if (_resolveTarget != null)
                    target = _resolveTarget(rawTarget);
            }
            catch
            {
                target = null;
            }

            if (target != null && !string.IsNullOrWhiteSpace(target.RemoteKey))
                return target;

            string key = ServerCommandPermissionStore.BuildIdentityKeyFromRawTarget(rawTarget);
            if (string.IsNullOrWhiteSpace(key))
                return target;

            return new ServerCommandTarget
            {
                IsOnline = false,
                PlayerName = rawTarget,
                RemoteKey = key
            };
        }

        private static void SendMultiline(ServerCommandContext context, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                context.SendReply(string.Empty);
                return;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
                context.SendReply(line);
        }

        private static string SafeName(string name, byte playerId)
        {
            return string.IsNullOrWhiteSpace(name) ? "Player" + playerId : name;
        }

        /// <summary>
        /// Resolves a kick/ban target so command-rank protection can be checked before moderation actions.
        /// Online players are resolved through the transport-specific target resolver.
        /// Raw identity keys such as steam:..., name:..., ip:..., or bare SteamIDs are also supported.
        /// </summary>
        private ServerCommandTarget ResolveModerationTarget(string rawTarget)
        {
            ServerCommandTarget target = null;

            try
            {
                if (_resolveTarget != null)
                    target = _resolveTarget(rawTarget);
            }
            catch
            {
                target = null;
            }

            if (target != null && !string.IsNullOrWhiteSpace(target.RemoteKey))
                return target;

            string key = ServerCommandPermissionStore.BuildIdentityKeyFromRawTarget(rawTarget);
            if (string.IsNullOrWhiteSpace(key))
                return target;

            return new ServerCommandTarget
            {
                IsOnline = false,
                PlayerName = rawTarget,
                RemoteKey = key
            };
        }

        /// <summary>
        /// Logs a successful command dispatch before the command handler runs.
        /// Summary: Gives the dedicated server console a clear audit trail of who ran which command.
        /// </summary>
        private static void LogCommandExecution(ServerCommandContext context)
        {
            if (context == null)
                return;

            context.WriteLog(
                "[Commands] " +
                BuildCommandActorLabel(context) +
                " ran " +
                SanitizeCommandTextForLog(context.RawText) +
                ".");
        }

        /// <summary>
        /// Builds a readable command sender label for console auditing.
        /// Steam shows steam:SteamID; Lidgren shows name: plus optional ip: fallback keys.
        /// </summary>
        private static string BuildCommandActorLabel(ServerCommandContext context)
        {
            if (context == null)
                return "Unknown";

            string name = SafeName(context.PlayerName, context.PlayerId);

            if (string.Equals(context.RemoteKey, "console", StringComparison.OrdinalIgnoreCase))
                return "Console";

            string key = string.IsNullOrWhiteSpace(context.RemoteKey)
                ? "unknown"
                : context.RemoteKey;

            string label =
                name +
                " (gid=" +
                context.PlayerId +
                ", " +
                key;

            if (context.AdditionalRemoteKeys != null && context.AdditionalRemoteKeys.Length > 0)
            {
                string[] extraKeys = [.. context.AdditionalRemoteKeys.Where(x => !string.IsNullOrWhiteSpace(x))];

                if (extraKeys.Length > 0)
                    label += ", alt=" + string.Join(",", extraKeys);
            }

            label += ")";

            return label;
        }

        /// <summary>
        /// Removes line breaks and redacts future sensitive command forms before writing to the console.
        /// </summary>
        private static string SanitizeCommandTextForLog(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return "<empty>";

            string text = rawText
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            while (text.Contains("  "))
                text = text.Replace("  ", " ");

            string firstToken = text.Split([' '], 2)[0]
                .TrimStart('!', '/', '.')
                .ToLowerInvariant();

            if (firstToken == "login" ||
                firstToken == "password" ||
                firstToken == "setpassword" ||
                firstToken == "auth")
            {
                int firstSpace = text.IndexOf(' ');
                return firstSpace < 0
                    ? text
                    : text.Substring(0, firstSpace) + " <redacted>";
            }

            return text;
        }

        /// <summary>
        /// Registers commands from a plugin that implements IServerCommandPlugin.
        /// </summary>
        public void RegisterPluginCommands(IServerCommandPlugin plugin)
        {
            if (plugin == null)
                return;

            try
            {
                List<PluginCommand> commands = plugin.GetCommands();
                if (commands == null || commands.Count == 0)
                    return;

                foreach (var cmd in commands)
                {
                    if (string.IsNullOrWhiteSpace(cmd.Name) ||
                        (cmd.Handler == null && cmd.ContextHandler == null))
                        continue;

                    string normalizedName = ServerCommandParser.NormalizeCommandName(cmd.Name);
                    if (_commands.ContainsKey(normalizedName))
                    {
                        _log($"[Commands] Plugin command '{cmd.Name}' conflicts with existing command, skipping.");
                        continue;
                    }

                    ServerCommandRank defaultRank = ServerCommandRank.Visitor;

                    if (!string.IsNullOrWhiteSpace(cmd.RequiredRank))
                    {
                        if (ServerCommandRankHelper.TryParseStrict(cmd.RequiredRank, out ServerCommandRank parsedRank))
                        {
                            defaultRank = parsedRank;
                        }
                        else
                        {
                            _log($"[Commands] Plugin command '{cmd.Name}' has unknown RequiredRank '{cmd.RequiredRank}', using Admin.");
                            defaultRank = ServerCommandRank.Admin;
                        }
                    }

                    // Wrap the plugin command handler to match the server command signature
                    Action<ServerCommandContext, string[]> wrappedHandler = (context, args) =>
                    {
                        try
                        {
                            if (cmd.ContextHandler != null)
                            {
                                cmd.ContextHandler(new PluginCommandContext
                                {
                                    PlayerId = context.PlayerId,
                                    PlayerName = context.PlayerName,
                                    RemoteId = context.RemoteId,
                                    RemoteKey = context.RemoteKey,
                                    SendReply = context.SendReply,
                                    Broadcast = context.Broadcast,
                                    Log = context.Log
                                }, args);
                            }
                            else
                            {
                                cmd.Handler(args);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log($"[Commands] Plugin command '{cmd.Name}' threw exception: {ex.Message}");
                        }
                    };

                    _commands[normalizedName] = new ServerCommandDefinition(
                        cmd.Name,
                        $"{cmd.Name} <args>",
                        cmd.Description ?? "Plugin command",
                        defaultRank,
                        wrappedHandler);

                    _log($"[Commands] Registered plugin command: !{cmd.Name}");
                }
            }
            catch (Exception ex)
            {
                _log($"[Commands] Failed to register commands from plugin: {ex.Message}");
            }
        }
        #endregion
    }
}
