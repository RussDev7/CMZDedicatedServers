/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServer - see LICENSE for details.
*/

using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.IO;
using System;

namespace CMZServerHost
{
    /// <summary>
    /// Runs a Lidgren NetPeer as a dedicated server using reflection against DNA.Common / game assemblies.
    ///
    /// Purpose:
    /// - Hosts a dedicated CastleMiner Z compatible server endpoint.
    /// - Accepts direct client connections by IP.
    /// - Handles discovery, approval, status changes, channel-0 game packets, and channel-1 internal packets.
    /// - Bridges dedicated-server state into the game's expected networking shapes.
    ///
    /// Notes:
    /// - Uses reflection heavily so the server can operate without compile-time references to all game-side types.
    /// - Game name defaults to "CastleMinerZSteam".
    /// - Network version defaults to 4.
    /// - Channel 0 is the normal gameplay relay / host-authoritative packet path.
    /// - Channel 1 is used for internal/system/bootstrap traffic.
    /// </summary>
    public class LidgrenServer
    {
        #region Fields: Server Identity / Session Settings

        /// <summary>
        /// Network game name used by discovery / connection validation.
        /// </summary>
        private readonly string _gameName;

        /// <summary>
        /// Network version used by discovery / connection validation.
        /// </summary>
        private readonly int _networkVersion;

        #endregion

        #region Fields: Save / World Configuration

        /// <summary>
        /// Relative world folder path (for example Worlds\{guid}).
        /// </summary>
        private readonly string _worldFolder;

        /// <summary>
        /// Absolute server save root.
        /// </summary>
        private readonly string _saveRoot;

        /// <summary>
        /// Steam user id used for save-device compatibility / encryption key derivation.
        /// </summary>
        private readonly ulong _steamUserId;

        #endregion

        #region Fields: Startup / Runtime Configuration

        /// <summary>
        /// Folder containing game binaries such as DNA.Common.dll.
        /// </summary>
        private readonly string _gamePath;

        /// <summary>
        /// Port the Lidgren server listens on.
        /// </summary>
        private readonly int _port;

        /// <summary>
        /// Local address to bind to. Defaults to IPAddress.Any.
        /// </summary>
        private readonly IPAddress _bindAddress;

        /// <summary>
        /// Maximum number of connected clients allowed.
        /// </summary>
        private readonly int _maxPlayers;

        /// <summary>
        /// Human-readable server name advertised to clients.
        /// </summary>
        private readonly string _serverName;

        /// <summary>
        /// Game mode value sent in discovery / server-info responses.
        /// </summary>
        private readonly int _gameMode;

        /// <summary>
        /// PVP state sent in discovery / server-info responses.
        /// </summary>
        private readonly int _pvpState;

        /// <summary>
        /// Difficulty value sent in discovery / server-info responses.
        /// </summary>
        private readonly int _difficulty;

        /// <summary>
        /// Logging callback used throughout server lifecycle.
        /// </summary>
        private readonly Action<string> _log;

        #endregion

        #region Fields: Networking Runtime State

        /// <summary>
        /// Reflected Lidgren NetPeer instance.
        /// </summary>
        private object _netPeer;

        /// <summary>
        /// Reflected connections collection from NetPeer.
        /// </summary>
        private object _connections;

        /// <summary>
        /// Next player GID assigned to newly joined remote clients.
        /// </summary>
        private byte _nextPlayerGid = 1;

        /// <summary>
        /// All currently connected remote gamer proxies.
        /// </summary>
        private readonly List<object> _allGamers = new List<object>();

        /// <summary>
        /// Connection -> gamer map for resolving senders / recipients.
        /// </summary>
        private readonly Dictionary<object, object> _connectionToGamer = new Dictionary<object, object>();

        /// <summary>
        /// Whether the server is currently running.
        /// </summary>
        private bool _running;

        /// <summary>
        /// Cached discovery-request message enum value.
        /// </summary>
        private object _discoveryRequestType;

        /// <summary>
        /// Loaded DNA.Common assembly.
        /// </summary>
        private Assembly _commonAsm;

        /// <summary>
        /// Dedicated world / chunk / inventory host handler.
        /// </summary>
        private ServerWorldHandler _worldHandler;

        /// <summary>
        /// Optional loaded game assembly used by ServerWorldHandler.
        /// </summary>
        private readonly Assembly _gameAsm;

        /// <summary>
        /// View radius used when initializing the world handler.
        /// </summary>
        private readonly int _viewRadiusChunks;

        #endregion

        #region Fields: Time Of Day Broadcast State

        /// <summary>
        /// Simulated time-of-day value broadcast to clients.
        /// </summary>
        private float _timeOfDay;

        /// <summary>
        /// Last time-of-day broadcast timestamp.
        /// </summary>
        private DateTime _lastTimeOfDaySend = DateTime.MinValue;

        #endregion

        #region Properties

        /// <summary>
        /// True while the server has been started and not yet stopped.
        /// </summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        #endregion

        #region Construction

        /// <summary>
        /// Creates a new dedicated Lidgren server wrapper.
        ///
        /// Purpose:
        /// - Captures runtime settings.
        /// - Normalizes defaults / bounds.
        /// - Optionally prepares the world handler if enough save/world info is supplied.
        ///
        /// Notes:
        /// - The world handler is only created when gameAsm, worldFolder, saveRoot, and steamUserId are all available.
        /// - gameName defaults to "CastleMinerZSteam".
        /// - networkVersion defaults to 4.
        /// </summary>
        public LidgrenServer(
            string gamePath,
            int port,
            int maxPlayers,
            Action<string> log,
            Assembly gameAsm = null,
            string worldFolder = null,
            string saveRoot = null,
            ulong steamUserId = 0UL,
            IPAddress bindAddress = null,
            int viewRadiusChunks = 8,
            string serverName = null,
            int gameMode = 1,
            int pvpState = 0,
            int difficulty = 1,
            string gameName = "CastleMinerZSteam",
            int networkVersion = 4)
        {
            _gamePath = gamePath ?? throw new ArgumentNullException(nameof(gamePath));
            _port = port;
            _bindAddress = bindAddress ?? IPAddress.Any;
            _maxPlayers = maxPlayers;
            _serverName = string.IsNullOrWhiteSpace(serverName) ? "CMZ Server" : serverName;
            _gameMode = gameMode;
            _pvpState = pvpState < 0 ? 0 : (pvpState > 2 ? 2 : pvpState);
            _difficulty = difficulty < 0 ? 0 : (difficulty > 3 ? 3 : difficulty);
            _log = log ?? (_ => { });
            _gameAsm = gameAsm;

            _worldFolder = worldFolder;
            _saveRoot = saveRoot;
            _steamUserId = steamUserId;

            _viewRadiusChunks = viewRadiusChunks;
            _gameName = string.IsNullOrWhiteSpace(gameName) ? "CastleMinerZSteam" : gameName;
            _networkVersion = networkVersion > 0 ? networkVersion : 4;

            if (_gameAsm != null &&
                !string.IsNullOrWhiteSpace(_worldFolder) &&
                !string.IsNullOrWhiteSpace(_saveRoot) &&
                _steamUserId != 0UL)
            {
                _worldHandler = new ServerWorldHandler(
                    _gamePath,
                    _worldFolder,   // relative, e.g. Worlds\{guid}
                    _saveRoot,      // absolute server root
                    _steamUserId,
                    _log,
                    _viewRadiusChunks);
            }
        }
        #endregion

        #region Host Gamer State

        /// <summary>
        /// Synthetic local host gamer used to mirror vanilla host behavior where needed.
        /// </summary>
        private object _hostGamer;

        #endregion

        #region Lifecycle

        /// <summary>
        /// Starts the dedicated server.
        ///
        /// Purpose:
        /// - Loads DNA.Common and XNA.
        /// - Creates and configures the NetPeer.
        /// - Enables required incoming message types.
        /// - Starts the peer and initializes host/world state.
        ///
        /// Notes:
        /// - Throws when required runtime assemblies are missing.
        /// - Host gamer is created before world handler init.
        /// </summary>
        public void Start()
        {
            var commonPath = Path.Combine(_gamePath, "DNA.Common.dll");
            if (!File.Exists(commonPath))
            {
                throw new InvalidOperationException("DNA.Common.dll not found in game path: " + _gamePath);
            }

            _commonAsm = Assembly.LoadFrom(commonPath);
            if (_commonAsm == null)
                throw new InvalidOperationException("Failed to load DNA.Common");

            Assembly xnaAsm = null;
            try
            {
                // Try to load from the GAC (Global Assembly Cache)
                xnaAsm = Assembly.Load("Microsoft.Xna.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553");
                _log("Loaded Microsoft.Xna.Framework.dll from GAC.");
            }
            catch (Exception ex)
            {
                _log("Failed to load XNA Framework from GAC: " + ex.Message);
            }

            if (xnaAsm == null)
            {
                throw new InvalidOperationException("Could not load Microsoft.Xna.Framework.dll. Please ensure the XNA 4.0 Redistributable is installed.");
            }

            var configType = _commonAsm.GetType("DNA.Net.Lidgren.NetPeerConfiguration");
            if (configType == null)
                throw new InvalidOperationException("NetPeerConfiguration not found");

            var peerType = _commonAsm.GetType("DNA.Net.Lidgren.NetPeer");
            if (peerType == null)
                throw new InvalidOperationException("NetPeer not found");

            var config = Activator.CreateInstance(configType, _gameName);
            if (config == null)
                throw new InvalidOperationException("Failed to create NetPeerConfiguration");

            configType.GetProperty("LocalAddress").SetValue(config, _bindAddress);
            configType.GetProperty("Port").SetValue(config, _port);
            configType.GetProperty("AcceptIncomingConnections").SetValue(config, true);
            configType.GetProperty("MaximumConnections").SetValue(config, _maxPlayers);
            configType.GetProperty("UseMessageRecycling").SetValue(config, true);
            configType.GetProperty("NetworkThreadName").SetValue(config, "CMZ Server");

            var discoveryType = _commonAsm.GetType("DNA.Net.Lidgren.NetIncomingMessageType");
            if (discoveryType == null)
                throw new InvalidOperationException("NetIncomingMessageType not found");

            _discoveryRequestType = Enum.Parse(discoveryType, "DiscoveryRequest");
            var connApproval = Enum.Parse(discoveryType, "ConnectionApproval");
            var statusChanged = Enum.Parse(discoveryType, "StatusChanged");
            var dataVal = Enum.Parse(discoveryType, "Data");

            configType.GetMethod("EnableMessageType").Invoke(config, new[] { _discoveryRequestType });
            configType.GetMethod("EnableMessageType").Invoke(config, new[] { connApproval });
            configType.GetMethod("EnableMessageType").Invoke(config, new[] { statusChanged });
            configType.GetMethod("EnableMessageType").Invoke(config, new[] { dataVal });

            _netPeer = Activator.CreateInstance(peerType, config);
            try
            {
                peerType.GetMethod("Start").Invoke(_netPeer, null);
            }
            catch (System.Reflection.TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                throw new InvalidOperationException("NetPeer.Start failed: " + (inner?.Message ?? tex.Message), inner);
            }

            _connections = peerType.GetProperty("Connections").GetValue(_netPeer);

            CreateHostGamer(xnaAsm);

            if (_worldHandler != null && _gameAsm != null)
            {
                _worldHandler.Init(_gameAsm, _commonAsm);
            }

            _running = true;
            var bindStr = _bindAddress.Equals(IPAddress.Any) ? "0.0.0.0 (all interfaces)" : _bindAddress.ToString();
            _log($"Lidgren server started on {bindStr}:{_port}");
        }

        /// <summary>
        /// Stops the dedicated server and shuts down the underlying NetPeer.
        /// </summary>
        public void Stop()
        {
            _running = false;
            if (_netPeer != null)
            {
                try
                {
                    _netPeer.GetType().GetMethod("Shutdown").Invoke(_netPeer, new object[] { "Server stopped" });
                }
                catch
                {
                }

                _netPeer = null;
            }
        }

        /// <summary>
        /// Pumps incoming messages and performs periodic server-side tasks.
        ///
        /// Purpose:
        /// - Reads and dispatches all pending Lidgren messages.
        /// - Recycles processed messages.
        /// - Periodically broadcasts time-of-day updates to connected clients.
        ///
        /// Notes:
        /// - Message dispatch is reflection-driven.
        /// - Non-data message types are logged for visibility.
        /// </summary>
        public void Update()
        {
            if (_netPeer == null || !_running)
                return;

            var peerType = _netPeer.GetType();
            var readMsg = peerType.GetMethod("ReadMessage");
            var incomingMsgType = _commonAsm.GetType("DNA.Net.Lidgren.NetIncomingMessage");
            var recycle = peerType.GetMethod("Recycle", new[] { incomingMsgType });

            object msg;
            while ((msg = readMsg.Invoke(_netPeer, null)) != null)
            {
                try
                {
                    var msgType = msg.GetType();
                    var messageType = msgType.GetProperty("MessageType").GetValue(msg);
                    var msgTypeEnum = messageType.GetType();
                    var connApprovalVal = Enum.Parse(msgTypeEnum, "ConnectionApproval");
                    var statusChangedVal = Enum.Parse(msgTypeEnum, "StatusChanged");
                    var dataVal = Enum.Parse(msgTypeEnum, "Data");
                    var discoveryVal = Enum.Parse(msgTypeEnum, "DiscoveryRequest");

                    try
                    {
                        string msgName = messageType != null ? messageType.ToString() : "(null)";

                        if (msgName != "Data")
                            _log("[Server] Incoming MessageType = " + msgName);
                    }
                    catch
                    {
                    }

                    if (messageType.Equals(discoveryVal))
                    {
                        _log("Discovery message received, handling...");
                        HandleDiscoveryRequest(msg);
                    }
                    else if (messageType.Equals(connApprovalVal))
                    {
                        HandleConnectionApproval(msg);
                    }
                    else if (messageType.Equals(statusChangedVal))
                    {
                        HandleStatusChanged(msg);
                    }
                    else if (messageType.Equals(dataVal))
                    {
                        HandleDataMessage(msg);
                    }
                }
                catch (Exception ex)
                {
                    _log($"Message handling error: {ex.GetType().Name}: {ex.Message}");
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        foreach (var line in ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            _log($"  {line.Trim()}");
                    }
                }
                finally
                {
                    recycle?.Invoke(_netPeer, new[] { msg });
                }
            }

            // Time of day: advance and broadcast every ~2 seconds
            if (_worldHandler != null && _connections != null)
            {
                _timeOfDay += 1f / 3600f; // ~1 real minute per full day at 60 Hz
                if (_timeOfDay >= 1f)
                    _timeOfDay -= 1f;

                if ((DateTime.UtcNow - _lastTimeOfDaySend).TotalSeconds >= 2.0)
                {
                    _lastTimeOfDaySend = DateTime.UtcNow;
                    var payload = _worldHandler.BuildTimeOfDayPayload(_timeOfDay);
                    if (payload != null && payload.Length > 0)
                    {
                        var reliableOrdered = Enum.Parse(_commonAsm.GetType("DNA.Net.Lidgren.NetDeliveryMethod"), "ReliableOrdered");
                        foreach (var conn in (System.Collections.IEnumerable)_connections)
                        {
                            if (!_connectionToGamer.TryGetValue(conn, out var gamer))
                                continue;

                            var gamerId = (byte)gamer.GetType().GetProperty("Id").GetValue(gamer);
                            var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                            WriteChannel0Packet(om, gamerId, 0, payload);
                            conn.GetType().GetMethod("SendMessage", new[] { om.GetType(), reliableOrdered.GetType(), typeof(int) })?.Invoke(conn, new[] { om, reliableOrdered, 0 });
                        }
                    }
                }
            }
        }
        #endregion

        #region Host Gamer Creation

        /// <summary>
        /// Creates the synthetic host gamer object used by the dedicated server.
        ///
        /// Purpose:
        /// - Mimics the host-side gamer shape expected by connected clients.
        /// - Ensures the host has a non-null PlayerID payload compatible with peer serialization.
        ///
        /// Notes:
        /// - Uses a random 16-byte host hash because some client-side gamer reads expect non-null PlayerID.Data.
        /// </summary>
        private void CreateHostGamer(Assembly xnaAsm)
        {
            var networkGamerType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkGamer");
            var simpleGamerType = _commonAsm.GetType("DNA.Net.GamerServices.SimpleGamer");
            var signedInGamerType = _commonAsm.GetType("DNA.Net.GamerServices.SignedInGamer");
            var playerIndexType = xnaAsm.GetType("Microsoft.Xna.Framework.PlayerIndex");
            var playerIDType = _commonAsm.GetType("DNA.PlayerID");

            var playerIndex = Enum.ToObject(playerIndexType, 0);

            // Host must have non-null PlayerID.Data (16 bytes like clients) or Write(Gamer) emits -1
            // and client ReadGamer fails.
            byte[] hostHash = Guid.NewGuid().ToByteArray();
            var playerID = Activator.CreateInstance(playerIDType, new object[] { hostHash });

            var serverGamer = Activator.CreateInstance(signedInGamerType, new object[] { playerIndex, playerID, "Server" });

            _hostGamer = Activator.CreateInstance(networkGamerType, new object[] { serverGamer, null, true, true, (byte)0 });
        }
        #endregion

        #region Discovery Handling

        /// <summary>
        /// Handles discovery requests and replies with host/session information.
        ///
        /// Purpose:
        /// - Validates incoming discovery requests against game name and network version.
        /// - Returns current session metadata such as player count, max players, and session properties.
        ///
        /// Notes:
        /// - Uses HostDiscoveryResponseMessage.
        /// - PasswordProtected is currently always false.
        /// </summary>
        private void HandleDiscoveryRequest(object msg)
        {
            try
            {
                var msgType = msg.GetType();
                var senderEndPoint = msgType.GetProperty("SenderEndPoint")?.GetValue(msg) as IPEndPoint;
                if (senderEndPoint == null)
                    return;

                var lidgrenExt = _commonAsm.GetType("DNA.Net.GamerServices.LidgrenExtensions");
                var readDiscovery = lidgrenExt?.GetMethod("ReadDiscoveryRequestMessage", BindingFlags.Public | BindingFlags.Static);
                if (readDiscovery == null)
                {
                    _log("Discovery: ReadDiscoveryRequestMessage method not found");
                    return;
                }

                var request = readDiscovery.Invoke(null, new object[] { msg, _gameName, _networkVersion });
                if (request == null)
                    return;

                var readResult = request.GetType().GetField("ReadResult", BindingFlags.Public | BindingFlags.Instance)?.GetValue(request);
                var successEnum = Enum.Parse(readResult?.GetType(), "Success");
                if (readResult == null || !readResult.Equals(successEnum))
                {
                    _log("Discovery: request read failed or validation failed (ReadResult=" + (readResult?.ToString() ?? "null") + ")");
                    return;
                }

                var requestId = (int)(request.GetType().GetField("RequestID", BindingFlags.Public | BindingFlags.Instance)?.GetValue(request) ?? 0);
                _log("Discovery request from " + senderEndPoint + " RequestID=" + requestId + ", sending response (server name='" + _serverName + "')");

                var resultCodeType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkSession+ResultCode");
                var succeeded = Enum.ToObject(resultCodeType, 0); // Succeeded = 0

                var sessionPropsType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkSessionProperties");
                var sessionProps = Activator.CreateInstance(sessionPropsType);
                if (sessionProps != null)
                {
                    var itemProp = sessionPropsType.GetProperty("Item", new[] { typeof(int) });
                    itemProp?.SetValue(sessionProps, (int?)_gameMode, new object[] { 2 });
                    itemProp?.SetValue(sessionProps, (int?)_difficulty, new object[] { 3 });
                    itemProp?.SetValue(sessionProps, (int?)_pvpState, new object[] { 5 });
                }

                var responseType = _commonAsm.GetType("DNA.Net.GamerServices.HostDiscoveryResponseMessage");
                var response = Activator.CreateInstance(responseType);
                if (response == null)
                    return;

                responseType.GetField("Result", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, succeeded);
                responseType.GetField("RequestID", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, requestId);
                responseType.GetField("SessionID", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, 1);
                responseType.GetField("CurrentPlayers", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, _allGamers.Count);
                responseType.GetField("MaxPlayers", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, _maxPlayers);
                responseType.GetField("Message", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, _serverName);
                responseType.GetField("HostUsername", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, _serverName);
                responseType.GetField("PasswordProtected", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, false);
                responseType.GetField("SessionProperties", BindingFlags.Public | BindingFlags.Instance)?.SetValue(response, sessionProps);

                var peerType = _netPeer.GetType();
                var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                var writeExt = lidgrenExt?.GetMethod(
                    "Write",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { _commonAsm.GetType("DNA.Net.Lidgren.NetBuffer"), responseType, typeof(string), typeof(int) },
                    null);

                writeExt?.Invoke(null, new object[] { om, response, _gameName, _networkVersion });

                var sendDiscovery = peerType.GetMethod("SendDiscoveryResponse", new[] { om.GetType(), typeof(IPEndPoint) });
                if (sendDiscovery == null)
                {
                    _log("Discovery: SendDiscoveryResponse method not found");
                }
                else
                {
                    sendDiscovery.Invoke(_netPeer, new[] { om, senderEndPoint });
                    _log("Discovery response sent to " + senderEndPoint);
                }
            }
            catch (Exception ex)
            {
                _log("Discovery response error: " + ex.Message);
            }
        }
        #endregion

        #region Connection Approval

        /// <summary>
        /// Handles incoming connection approval.
        ///
        /// Purpose:
        /// - Reads the RequestConnectToHost message.
        /// - Validates read result / version compatibility.
        /// - Copies the approved gamer object into the connection Tag.
        /// - Approves or denies the connection.
        ///
        /// Notes:
        /// - The approval path preserves the incoming gamer object until post-connect handling.
        /// - "unknow ghost" is normalized to "Player".
        /// </summary>
        private void HandleConnectionApproval(object msg)
        {
            var senderConn = msg.GetType().GetProperty("SenderConnection").GetValue(msg);
            if (senderConn == null)
            {
                _log("ConnectionApproval: SenderConnection is null");
                return;
            }

            var lidgrenExt = _commonAsm.GetType("DNA.Net.GamerServices.LidgrenExtensions");
            var netBufferType = _commonAsm.GetType("DNA.Net.Lidgren.NetBuffer");
            var readMethod = lidgrenExt?.GetMethod(
                "ReadRequestConnectToHostMessage",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { netBufferType, typeof(string), typeof(int) },
                null);

            if (readMethod == null)
                return;

            var crm = readMethod.Invoke(null, new object[] { msg, _gameName, _networkVersion });
            if (crm == null)
                return;

            var readResultField = crm.GetType().GetField("ReadResult", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            var readResult = readResultField?.GetValue(crm);
            if (readResult == null)
            {
                _log("ConnectionApproval: ReadResult is null");
                return;
            }

            var successVal = Enum.Parse(readResult.GetType(), "Success");

            var connType = _commonAsm.GetType("DNA.Net.Lidgren.NetConnection");
            if (connType == null)
            {
                _log("NetConnection type not found");
                return;
            }

            if (!readResult.Equals(successVal))
            {
                var denyReason = readResult.ToString();
                connType.GetMethod("Deny", new[] { typeof(string) }).Invoke(senderConn, new object[] { denyReason });
                _log($"Connection denied: {denyReason}");
                return;
            }

            var gamer = crm.GetType().GetField("Gamer")?.GetValue(crm);
            if (gamer == null)
            {
                _log("ConnectionApproval: Gamer is null");
                return;
            }

            var gamerType = gamer.GetType();
            var gamerTag = gamerType.GetProperty("Gamertag")?.GetValue(gamer);
            var displayName = gamerType.GetProperty("DisplayName")?.GetValue(gamer);
            _log($"ConnectionApproval: Gamer object received. Gamertag: {gamerTag}, DisplayName: {displayName}");

            if (gamerTag as string == "unknow ghost")
            {
                gamerType.GetProperty("Gamertag")?.SetValue(gamer, "Player");
                _log("ConnectionApproval: Overwrote 'unknow ghost' with 'Player'");
            }

            var tagProp = connType.GetProperty("Tag", BindingFlags.Public | BindingFlags.Instance);
            if (tagProp == null)
            {
                _log("ConnectionApproval: Tag property not found");
                return;
            }

            tagProp.SetValue(senderConn, gamer);

            connType.GetMethod("Approve", Type.EmptyTypes).Invoke(senderConn, null);
            _log($"Connection approved from {gamer?.GetType().GetProperty("Gamertag")?.GetValue(gamer)}");
        }
        #endregion

        #region Status / Join / Leave Handling

        /// <summary>
        /// Handles connection status changes.
        ///
        /// Purpose:
        /// - Removes disconnected players from runtime collections.
        /// - Builds and sends the ConnectedMessage for newly connected players.
        /// - Sends server-info and initial time-of-day bootstrap data.
        /// - Broadcasts NewPeer messages to already connected clients.
        ///
        /// Notes:
        /// - Status 7 is treated as disconnect.
        /// - Status 5 is treated as connected.
        /// - ConnectedMessage is intentionally built before adding the new remote gamer to _allGamers,
        ///   matching vanilla ordering expectations and avoiding duplicate self-proxy creation on clients.
        /// </summary>
        private void HandleStatusChanged(object msg)
        {
            var msgType = msg.GetType();
            var readByte = msgType.GetMethod("ReadByte", Type.EmptyTypes);
            if (readByte == null)
                return;

            var status = (byte)readByte.Invoke(msg, null);

            if (status == 7)
            {
                var conn = msgType.GetProperty("SenderConnection").GetValue(msg);
                if (conn != null && _connectionToGamer.TryGetValue(conn, out var disconnectedGamer))
                {
                    _connectionToGamer.Remove(conn);
                    _allGamers.Remove(disconnectedGamer);

                    if (_worldHandler != null)
                        _worldHandler.OnClientDisconnected((byte)disconnectedGamer.GetType().GetProperty("Id").GetValue(disconnectedGamer));

                    _log($"Player disconnected, {_allGamers.Count} remaining");

                    if (_allGamers.Count == 0)
                        _nextPlayerGid = 1;
                }

                return;
            }

            if (status != 5)
                return;

            var senderConn = msgType.GetProperty("SenderConnection").GetValue(msg);
            if (senderConn == null)
            {
                _log("StatusChanged: SenderConnection is null");
                return;
            }

            var gamer = senderConn.GetType().GetProperty("Tag").GetValue(senderConn);
            if (gamer == null)
            {
                _log("StatusChanged: Tag (gamer) is null");
                return;
            }

            var networkGamerType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkGamer");
            var sessionType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkSession");
            var providerType = _commonAsm.GetType("DNA.Net.GamerServices.NetworkSessionProvider");
            var ngCtor = networkGamerType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { _commonAsm.GetType("DNA.Net.GamerServices.Gamer"), sessionType, typeof(bool), typeof(bool), typeof(byte), typeof(System.Net.IPAddress) },
                null);

            var remoteEp = senderConn.GetType().GetProperty("RemoteEndPoint").GetValue(senderConn);
            if (remoteEp == null)
            {
                _log("StatusChanged: RemoteEndPoint is null");
                return;
            }

            var addr = remoteEp.GetType().GetProperty("Address").GetValue(remoteEp);

            object remoteGamer = null;
            if (ngCtor != null)
            {
                try
                {
                    remoteGamer = ngCtor.Invoke(new object[] { gamer, null, false, false, _nextPlayerGid, addr });
                }
                catch (Exception ex)
                {
                    _log($"NetworkGamer create failed: {ex.Message}");
                }
            }

            if (remoteGamer == null)
                return;

            networkGamerType.GetField("NetConnectionObject")?.SetValue(remoteGamer, senderConn);
            // Tag stays as approval Gamer until after ConnectedMessage — matches game host order.

            if (_hostGamer == null)
            {
                _log("Host gamer is null; cannot send ConnectedMessage");
                return;
            }

            // Game host sends ConnectedMessage with SetPeerList(_allGamers) BEFORE AddRemoteGamer — peer list
            // must NOT include the joining client or client does AddLocalGamer(..., gid) + AddProxyGamer(self, gid) → duplicate _idToGamer.
            var netBufferType = _commonAsm.GetType("DNA.Net.Lidgren.NetBuffer");
            var connectedMsgType = _commonAsm.GetType("DNA.Net.GamerServices.ConnectedMessage");
            var connectedMsg = Activator.CreateInstance(connectedMsgType);
            connectedMsgType.GetField("PlayerGID")?.SetValue(connectedMsg, _nextPlayerGid);

            var gamerType = _commonAsm.GetType("DNA.Net.GamerServices.Gamer");

            // Peers = host + existing remotes only (joiner added after send).
            var peerCount = 1 + _allGamers.Count;
            var peersArray = Array.CreateInstance(gamerType, peerCount);
            var idsArray = new byte[peerCount];

            peersArray.SetValue(_hostGamer, 0);
            idsArray[0] = 0;

            for (int i = 0; i < _allGamers.Count; i++)
            {
                var g = _allGamers[i];
                peersArray.SetValue(g, i + 1);
                idsArray[i + 1] = (byte)g.GetType().GetProperty("Id").GetValue(g);
            }

            connectedMsgType.GetField("Peers")?.SetValue(connectedMsg, peersArray);
            connectedMsgType.GetField("ids")?.SetValue(connectedMsg, idsArray);

            var peerType = _netPeer.GetType();
            var createMsg = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
            var createMsgType = createMsg.GetType();
            createMsgType.GetMethod("Write", new[] { typeof(byte) }).Invoke(createMsg, new object[] { (byte)1 });

            var lidgrenExt = _commonAsm.GetType("DNA.Net.GamerServices.LidgrenExtensions");
            var writeConnected = lidgrenExt?.GetMethod("Write", BindingFlags.Public | BindingFlags.Static, null, new[] { netBufferType, connectedMsgType }, null);
            writeConnected?.Invoke(null, new object[] { createMsg, connectedMsg });

            // Hex dump of payload (channel 1) for comparing to real host — read-only diagnostic
            try
            {
                var peekData = createMsgType.GetMethod("PeekDataBuffer", Type.EmptyTypes)?.Invoke(createMsg, null) as byte[];
                var lenObj = createMsgType.GetProperty("LengthBytes")?.GetValue(createMsg);
                if (peekData != null && lenObj is int len && len > 0)
                {
                    int n = Math.Min(len, 96);
                    var sb = new System.Text.StringBuilder(n * 3);
                    for (int i = 0; i < n; i++)
                        sb.Append(peekData[i].ToString("X2")).Append(i + 1 < n ? " " : "");
                    _log($"ConnectedMessage payload hex ({n}/{len} bytes): {sb}");
                }
            }
            catch
            {
                /* ignore log failures */
            }

            _log($"ConnectedMessage Contents:");
            _log($"  PlayerGID: {connectedMsgType.GetField("PlayerGID")?.GetValue(connectedMsg)}");

            var peers = connectedMsgType.GetField("Peers")?.GetValue(connectedMsg) as Array;
            if (peers != null)
            {
                _log($"  Peers ({peers.Length}):");
                for (int i = 0; i < peers.Length; i++)
                {
                    var peer = peers.GetValue(i);
                    var gamerTag = peer.GetType().GetProperty("Gamertag")?.GetValue(peer);
                    var id = peer.GetType().GetProperty("Id")?.GetValue(peer);
                    _log($"    - Gamertag: {gamerTag}, Id: {id}");
                }
            }

            _log($"Sending ConnectedMessage: PlayerGID={_nextPlayerGid}, PeerCount={peersArray.Length}");

            var reliableOrdered = Enum.Parse(_commonAsm.GetType("DNA.Net.Lidgren.NetDeliveryMethod"), "ReliableOrdered");
            var sendMsgMethod = senderConn.GetType().GetMethod("SendMessage", new[] { createMsg.GetType(), reliableOrdered.GetType(), typeof(int) });
            sendMsgMethod?.Invoke(senderConn, new[] { createMsg, reliableOrdered, 1 });

            // Send server info (name, max players, game mode) so client can cache it.
            // Channel-1 type 255 = CMZ server info.
            try
            {
                var serverInfoMsg = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                var siType = serverInfoMsg.GetType();
                siType.GetMethod("Write", new[] { typeof(byte) }).Invoke(serverInfoMsg, new object[] { (byte)255 });
                siType.GetMethod("Write", new[] { typeof(string) }).Invoke(serverInfoMsg, new object[] { _serverName ?? "" });
                siType.GetMethod("Write", new[] { typeof(int) }).Invoke(serverInfoMsg, new object[] { _maxPlayers });
                siType.GetMethod("Write", new[] { typeof(int) }).Invoke(serverInfoMsg, new object[] { _gameMode });
                siType.GetMethod("Write", new[] { typeof(int) }).Invoke(serverInfoMsg, new object[] { _difficulty });
                sendMsgMethod?.Invoke(senderConn, new[] { serverInfoMsg, reliableOrdered, 1 });
                _log("Sent server info to client: name='" + _serverName + "' max=" + _maxPlayers + " gameMode=" + _gameMode + " difficulty=" + _difficulty);
            }
            catch (Exception ex)
            {
                _log("Send server info: " + ex.Message);
            }

            // Now add joiner and set Tag — same order as game host after send.
            _allGamers.Add(remoteGamer);
            _connectionToGamer[senderConn] = remoteGamer;

            // Send current time of day so joiner sees correct time immediately.
            try
            {
                if (_worldHandler != null)
                {
                    var todPayload = _worldHandler.BuildTimeOfDayPayload(_timeOfDay);
                    if (todPayload != null && todPayload.Length > 0)
                    {
                        var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                        WriteChannel0Packet(om, _nextPlayerGid, 0, todPayload);
                        sendMsgMethod?.Invoke(senderConn, new[] { om, reliableOrdered, 0 });
                    }
                }
            }
            catch (Exception ex)
            {
                _log("Send time of day on join: " + ex.Message);
            }

            _commonAsm.GetType("DNA.Net.Lidgren.NetConnection").GetProperty("Tag", BindingFlags.Public | BindingFlags.Instance)?.SetValue(senderConn, remoteGamer);

            // NewPeer: AddNewPeer uses ReadByte() for type then id — must write bytes, not int.
            foreach (var conn in (System.Collections.IEnumerable)_connections)
            {
                if (conn == senderConn)
                    continue;

                var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                var omType = om.GetType();
                omType.GetMethod("Write", new[] { typeof(byte) }).Invoke(om, new object[] { (byte)0 });
                omType.GetMethod("Write", new[] { typeof(byte) }).Invoke(om, new object[] { _nextPlayerGid });

                var writeGamer = lidgrenExt?.GetMethod("Write", BindingFlags.Public | BindingFlags.Static, null, new[] { netBufferType, gamerType }, null);
                writeGamer?.Invoke(null, new object[] { om, remoteGamer });

                sendMsgMethod?.Invoke(conn, new[] { om, reliableOrdered, 1 });
            }

            _nextPlayerGid++;
            _log($"Player {_nextPlayerGid - 1} joined");
        }
        #endregion

        #region Packet Writing Helpers

        /// <summary>
        /// Writes a channel-0 packet in the shape the client expects:
        /// recipient byte, sender byte, byte-array payload.
        ///
        /// Notes:
        /// - Client-side channel-0 reading expects ReadByte, ReadByte, then ReadByteArray.
        /// - LidgrenExtensions.WriteArray is preferred because raw NetBuffer.Write(byte[]) does not prepend the length.
        /// - The fallback path is only a last resort and may not match the exact expected wire shape.
        /// </summary>
        private void WriteChannel0Packet(object om, byte recipient, byte sender, byte[] payload)
        {
            var omType = om.GetType();
            omType.GetMethod("Write", new[] { typeof(byte) })?.Invoke(om, new object[] { recipient });
            omType.GetMethod("Write", new[] { typeof(byte) })?.Invoke(om, new object[] { sender });

            var netBufferType = _commonAsm.GetType("DNA.Net.Lidgren.NetBuffer");
            var lidgrenExt = _commonAsm.GetType("DNA.Net.GamerServices.LidgrenExtensions");
            var writeArray = lidgrenExt?.GetMethod("WriteArray", BindingFlags.Public | BindingFlags.Static, null, new[] { netBufferType, typeof(byte[]) }, null);

            if (writeArray != null && payload != null)
                writeArray.Invoke(null, new object[] { om, payload });
            else if (payload != null)
                omType.GetMethod("Write", new[] { typeof(byte[]) })?.Invoke(om, new object[] { payload }); // wrong wire shape; last resort
        }
        #endregion

        #region Data Message Dispatch

        /// <summary>
        /// Handles incoming data messages for both primary packet channels.
        ///
        /// Channel 0:
        /// - recipient 0 => host-authoritative messages (inventory/world/system)
        /// - recipient X => direct peer relay
        ///
        /// Channel 1:
        /// - internal/system packets, including world bootstrap/chunk requests
        ///
        /// Notes:
        /// - Channel 1 uses a custom broadcast-to-host wrapper layout.
        /// - Channel 0 uses the normal recipient/sender/byte-array payload layout.
        /// - Host-directed packets are offered to the world handler before any fallback relay occurs.
        /// </summary>
        private void HandleDataMessage(object msg)
        {
            var msgType = msg.GetType();
            var seqChannel = msgType.GetProperty("SequenceChannel")?.GetValue(msg);

            #region Channel 1: Internal / System Packets

            // ------------------------------------------------------------
            // CHANNEL 1
            // ------------------------------------------------------------
            // Client -> host internal/system packets.
            // Layout:
            //   byte 4      = broadcast-to-host opcode
            //   byte flags
            //   byte senderId
            //   int  len
            //   byte[len] payload
            // ------------------------------------------------------------
            if (seqChannel is int ch && ch == 1)
            {
                var rb = msgType.GetMethod("ReadByte", Type.EmptyTypes);
                var ri = msgType.GetMethod("ReadInt32", Type.EmptyTypes);
                var rbytes = msgType.GetMethod("ReadBytes", new[] { typeof(int) });
                if (rb == null || ri == null || rbytes == null)
                    return;

                var b0 = (byte)rb.Invoke(msg, null);
                if (b0 != 4)
                    return; // Only handling host-directed internal broadcast packets here.

                rb.Invoke(msg, null); // flags
                var sndId = (byte)rb.Invoke(msg, null);
                var dataSize = (int)ri.Invoke(msg, null);

                byte[] payloadBytes =
                    dataSize > 0
                        ? rbytes.Invoke(msg, new object[] { dataSize }) as byte[]
                        : System.Array.Empty<byte>();

                if (payloadBytes == null || payloadBytes.Length < 1)
                    return;

                var senderConn = msgType.GetProperty("SenderConnection")?.GetValue(msg);
                if (senderConn == null || !_connectionToGamer.TryGetValue(senderConn, out _))
                    return;

                var delivery = Enum.Parse(_commonAsm.GetType("DNA.Net.Lidgren.NetDeliveryMethod"), "ReliableOrdered");

                void SendToClient(object conn, byte[] payload, byte recipient)
                {
                    var peerType = _netPeer.GetType();
                    var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                    WriteChannel0Packet(om, recipient, 0, payload);

                    conn.GetType()
                        .GetMethod("SendMessage", new[] { om.GetType(), delivery.GetType(), typeof(int) })
                        ?.Invoke(conn, new[] { om, delivery, 0 });
                }

                // 1) Let world bootstrap / chunk logic consume it first.
                if (_worldHandler != null &&
                    _worldHandler.TryHandleHostMessage(0, sndId, payloadBytes, senderConn, _netPeer, _connections, _connectionToGamer, SendToClient))
                {
                    return;
                }

                return;
            }
            #endregion

            #region Channel 0: Standard Game Packets

            // ------------------------------------------------------------
            // CHANNEL 0
            // ------------------------------------------------------------
            // Normal game packet path:
            //   byte recipientId
            //   byte senderId
            //   int  len
            //   byte[len] payload
            // ------------------------------------------------------------
            var readByte = msgType.GetMethod("ReadByte", Type.EmptyTypes);
            var readInt32 = msgType.GetMethod("ReadInt32", Type.EmptyTypes);
            var readBytes = msgType.GetMethod("ReadBytes", new[] { typeof(int) });
            if (readByte == null || readInt32 == null || readBytes == null)
                return;

            var recipientId = (byte)readByte.Invoke(msg, null);
            var senderId = (byte)readByte.Invoke(msg, null);

            int len = (int)readInt32.Invoke(msg, null);
            byte[] data;
            if (len == -1)
                data = null;
            else if (len == 0)
                data = Array.Empty<byte>();
            else
                data = readBytes.Invoke(msg, new object[] { len }) as byte[];

            if (data == null || data.Length < 1)
                return;

            var reliableOrdered = Enum.Parse(_commonAsm.GetType("DNA.Net.Lidgren.NetDeliveryMethod"), "ReliableOrdered");

            // Host-directed packet:
            // give authoritative handlers a chance BEFORE relay.
            if (recipientId == 0)
            {
                var senderConn = msgType.GetProperty("SenderConnection")?.GetValue(msg);
                if (senderConn != null && _connectionToGamer.TryGetValue(senderConn, out _))
                {
                    void SendToClient(object conn, byte[] payload, byte recipient)
                    {
                        var peerType = _netPeer.GetType();
                        var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);
                        WriteChannel0Packet(om, recipient, 0, payload);

                        var sendMsg = conn.GetType()
                            .GetMethod("SendMessage", new[] { om.GetType(), reliableOrdered.GetType(), typeof(int) });

                        sendMsg?.Invoke(conn, new[] { om, reliableOrdered, 0 });
                    }

                    // 1) world/chunk bootstrap
                    if (_worldHandler != null &&
                        _worldHandler.TryHandleHostMessage(recipientId, senderId, data, senderConn, _netPeer, _connections, _connectionToGamer, SendToClient))
                    {
                        return;
                    }
                }
            }
            #endregion

            #region Fallback Relay

            // ------------------------------------------------------------
            // FALLBACK RELAY
            // ------------------------------------------------------------
            foreach (var conn in (System.Collections.IEnumerable)_connections)
            {
                if (!_connectionToGamer.TryGetValue(conn, out var gamer))
                    continue;

                var gamerId = (byte)gamer.GetType().GetProperty("Id").GetValue(gamer);

                if (gamerId == senderId)
                    continue;

                if (recipientId != 0 && gamerId != recipientId)
                    continue;

                var peerType = _netPeer.GetType();
                var om = peerType.GetMethod("CreateMessage", Type.EmptyTypes).Invoke(_netPeer, null);

                WriteChannel0Packet(om, gamerId, senderId, data);

                conn.GetType()
                    .GetMethod("SendMessage", new[] { om.GetType(), reliableOrdered.GetType(), typeof(int) })
                    ?.Invoke(conn, new[] { om, reliableOrdered, 0 });
            }
            #endregion
        }
        #endregion
    }
}