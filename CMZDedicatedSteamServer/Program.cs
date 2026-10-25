/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using CMZDedicatedSteamServer.Config;
using CMZDedicatedSteamServer.Hosting;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CMZDedicatedSteamServer
{
    /// <summary>
    /// Console entry point for the Steam-native CastleMiner Z dedicated server.
    /// </summary>
    /// <remarks>
    /// This host starts the Steam dedicated server runtime, applies optional runtime patches,
    /// loads configuration, and runs the main server update loop until shutdown.
    /// </remarks>
    internal static class Program
    {
        private const string GuiInstanceMutexName = "Local\\CMZDedicatedSteamServer.GuiSingleton";

        #region Command Audit Logging State

        /// <summary>
        /// Synchronizes command audit file writes so console and server threads cannot write over each other.
        /// </summary>
        private static readonly object CommandAuditLogSync = new object();

        /// <summary>
        /// Base directory where the server executable is running.
        /// Used to create Logs\commands-yyyy-MM-dd.log.
        /// </summary>
        private static string CommandAuditBaseDir = AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Controls whether command audit lines are mirrored to the dated log file.
        /// </summary>
        private static bool CommandAuditLogEnabled = true;

        #endregion

        #region Entry Point

        /// <summary>
        /// Program entry point for the Steam-native dedicated server host.
        /// </summary>
        /// <returns>
        /// 0 when the server exits normally; 1 when a fatal startup/runtime exception reaches Main.
        /// </returns>
        /// <remarks>
        /// Purpose:
        /// - Loads server configuration from disk.
        /// - Resolves the CastleMiner Z game/runtime assemblies.
        /// - Initializes the Steam API against the currently logged-in Steam client.
        /// - Creates a Steam lobby owned by that active account.
        /// - Enters the main update loop for packet processing and future world ticks.
        ///
        /// Notes:
        /// - This host intentionally does NOT launch the game window.
        /// - This host also does NOT support username/password Steam login from config.
        ///   Steam's normal client API path requires an already-running Steam client.
        /// </remarks>
        [STAThread]
        private static int Main(string[] args)
        {
            bool runConsole = args != null && args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
            if (runConsole)
                return RunConsoleHost();

            bool createdNew;
            using var guiMutex = new Mutex(true, GuiInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "CMZ Dedicated Steam Server is already running.",
                    "Already running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ServerGuiForm());
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        // ---- GUI <-> terminal switching (no server restart) ----

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint mode, uint flags, IntPtr templ);

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint id, uint flags);

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x0;
        private const uint MF_GRAYED = 0x1;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;

        private static bool _terminalMode;
        private static TextWriter _pipeOut;
        private static IntPtr _pipeOutHandle;
        private static IntPtr _pipeInHandle;
        private static SteamDedicatedServer _activeServer;
        private static Action<string> _activeLog;
        private static StreamWriter _conWriter;
        private static StreamReader _conReader;

        // Rolling history so a freshly attached terminal shows what the GUI saw.
        private static readonly ConcurrentQueue<string> _recentLogLines = new ConcurrentQueue<string>();
        private const int RecentLogLinesMax = 400;

        // Writes every line to the GUI pipe AND (except @@ control lines) the real console.
        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _pipe;
            private readonly TextWriter _console;

            public TeeTextWriter(TextWriter pipe, TextWriter console) { _pipe = pipe; _console = console; }

            public override Encoding Encoding => _pipe.Encoding;

            public override void Write(string value)
            {
                try { _pipe.Write(value); } catch { }
                if (!IsInternalLine(value))
                {
                    try { _console.Write(value); } catch { }
                }
            }

            public override void WriteLine(string value)
            {
                try { _pipe.WriteLine(value); } catch { }
                if (!IsInternalLine(value))
                {
                    try { _console.WriteLine(value); } catch { }
                }
            }

            public override void WriteLine()
            {
                try { _pipe.WriteLine(); } catch { }
                try { _console.WriteLine(); } catch { }
            }

            public override void Flush()
            {
                try { _pipe.Flush(); } catch { }
                try { _console.Flush(); } catch { }
            }

            private static bool IsInternalLine(string v) =>
                v != null && v.StartsWith("@@", StringComparison.Ordinal);
        }

        private static int RunConsoleHost()
        {
            // Windows sleeps in ~15.6 ms slices by default, so Thread.Sleep(16)
            // becomes ~31 ms and the loop caps near 32 ticks/s regardless of
            // tick-rate-hz. 1 ms resolution makes the configured rate real.
            timeBeginPeriod(1);

            try
            {
                Console.Title = "CMZ Dedicated Steam Server";
            }
            catch
            {
                // No attached console handle (e.g., launched by GUI with redirected streams).
            }

            _pipeOut = Console.Out;
            _pipeOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            _pipeInHandle = GetStdHandle(STD_INPUT_HANDLE);

            try
            {
                #region Resolve Base Paths

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                Log($"Config file       : {Path.Combine(baseDir, "server.properties")}");
                SteamServerConfig config = SteamServerConfig.Load(baseDir);

                // Ensure Plugins/Dependencies folder exists
                string pluginsDir = Path.Combine(baseDir, "Plugins");
                string dependenciesDir = Path.Combine(pluginsDir, "Dependencies");
                if (!Directory.Exists(dependenciesDir))
                {
                    Directory.CreateDirectory(dependenciesDir);
                    Log($"Created Dependencies folder: {dependenciesDir}");
                }

                CommandAuditBaseDir = baseDir;
                CommandAuditLogEnabled = config.LogCommandAudit;

                string gamePath = string.IsNullOrWhiteSpace(config.GamePath)
                    ? Path.Combine(baseDir, "Game")
                    : (Path.IsPathRooted(config.GamePath)
                        ? config.GamePath
                        : Path.GetFullPath(Path.Combine(baseDir, config.GamePath)));

                string libsPath = Path.Combine(baseDir, "Libs");

                #endregion

                #region Assembly Resolution

                AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
                {
                    var asmName = new AssemblyName(resolveArgs.Name);

                    // 1) Support libraries beside the server, inside /Libs.
                    string libsDllPath = Path.Combine(libsPath, asmName.Name + ".dll");
                    if (File.Exists(libsDllPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(libsDllPath);
                        }
                        catch (Exception ex)
                        {
                            Log($"[AssemblyResolve] Failed to load {asmName.Name} from Libs: {ex.Message}");
                        }
                    }

                    // 2) Plugin dependencies folder (only if it exists).
                    string pluginsDir = Path.Combine(baseDir, "Plugins");
                    string pluginsDepPath = Path.Combine(pluginsDir, "Dependencies", asmName.Name + ".dll");
                    if (Directory.Exists(Path.Combine(pluginsDir, "Dependencies")) && File.Exists(pluginsDepPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(pluginsDepPath);
                        }
                        catch (Exception ex)
                        {
                            Log($"[AssemblyResolve] Failed to load {asmName.Name} from Dependencies: {ex.Message}");
                        }
                    }

                    // 3) Game folder DLLs.
                    string gameDllPath = Path.Combine(gamePath, asmName.Name + ".dll");
                    if (File.Exists(gameDllPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(gameDllPath);
                        }
                        catch (Exception ex)
                        {
                            Log($"[AssemblyResolve] Failed to load {asmName.Name} from Game: {ex.Message}");
                        }
                    }

                    // 4) Game folder EXE assemblies.
                    string gameExeAsmPath = Path.Combine(gamePath, asmName.Name + ".exe");
                    if (File.Exists(gameExeAsmPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(gameExeAsmPath);
                        }
                        catch (Exception ex)
                        {
                            Log($"[AssemblyResolve] Failed to load {asmName.Name} from Game (EXE): {ex.Message}");
                        }
                    }

                    Log($"[AssemblyResolve] Could not resolve assembly: {asmName.Name}");
                    return null;
                };
                #endregion

                #region Apply Runtime Patches

                ServerPatches.ApplyAllPatches();

                #endregion

                while (true)
                {
                    using var server = new SteamDedicatedServer(baseDir, config, Log);

                    _activeServer = server;
                    _activeLog = Log;

                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        // Handlers stack per restart — only the live server instance acts.
                        if (!ReferenceEquals(server, _activeServer))
                            return;

                        // CTRL_CLOSE_EVENT (2) has no named enum member on net481.
                        if ((int)e.SpecialKey == 2 && _terminalMode)
                        {
                            ExitTerminalMode();
                            return;
                        }

                        Log("Ctrl+C received. Shutting down...");
                        server.Stop();
                    };

                    server.Start();

                    StartConsoleCommandThread(server, Log);

                    int sleepMs = Math.Max(1, 1000 / Math.Max(1, config.TickRateHz));
                    bool emitTelemetry = Console.IsOutputRedirected;
                    int tpsTicks = 0;
                    int tpsWindowMs = 1000;
                    var tpsWatch = System.Diagnostics.Stopwatch.StartNew();

                    while (server.IsRunning)
                    {
                        try
                        {
                            server.Update();
                            tpsTicks++;
                        }
                        catch (Exception ex)
                        {
                            Log("[MainLoop] " + ex);
                        }

                        if (emitTelemetry)
                        {
                            long elapsed = tpsWatch.ElapsedMilliseconds;
                            if (elapsed >= tpsWindowMs)
                            {
                                Console.Out.WriteLine("@@TPS " + (tpsTicks * 1000.0 / elapsed).ToString("0.0"));
                                tpsTicks = 0;
                                tpsWatch.Restart();
                                tpsWindowMs = 2000;
                            }
                        }

                        Thread.Sleep(sleepMs);
                    }

                    if (!server.RestartRequested)
                    {
                        timeEndPeriod(1);
                        return 0;
                    }

                    Log("[Server] Restarting dedicated host...");
                    Thread.Sleep(750);
                    config = SteamServerConfig.Load(baseDir);
                    CommandAuditLogEnabled = config.LogCommandAudit;
                }
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                timeEndPeriod(1);
                return 1;
            }
        }
        #endregion

        #region Console Commands

        /// <summary>
        /// Starts a background console command reader for runtime server commands.
        /// </summary>
        private static Thread StartConsoleCommandThread(SteamDedicatedServer server, Action<string> log)
        {
            Thread thread = new(() =>
            {
                while (server.IsRunning)
                {
                    string line;

                    try
                    {
                        line = Console.ReadLine();
                    }
                    catch
                    {
                        break;
                    }

                    if (line == null)
                        break;

                    if (line.Equals("@@TERMINAL", StringComparison.OrdinalIgnoreCase))
                    {
                        EnterTerminalMode();
                        continue;
                    }

                    // Silent GUI polls: emit the machine lines without logging
                    // the human-readable blocks into the console stream.
                    if (line.Equals("@@PLAYERS", StringComparison.OrdinalIgnoreCase))
                    {
                        server.GetPlayerListText();
                        continue;
                    }
                    if (line.StartsWith("@@PINFO ", StringComparison.OrdinalIgnoreCase))
                    {
                        server.GetPlayerInfoText(line.Substring(8));
                        continue;
                    }

                    HandleConsoleCommand(server, line, log);
                }
            })
            {
                IsBackground = true,
                Name = "ConsoleCommandThread"
            };
            thread.Start();

            return thread;
        }

        /// <summary>
        /// Attaches a real console window to this process. Server keeps running —
        /// output tees to the GUI pipe AND the console; commands type into the console.
        /// Closing the console window (X) returns to the GUI.
        /// </summary>
        private static void EnterTerminalMode()
        {
            if (_terminalMode)
                return;

            if (!AllocConsole())
            {
                try { _pipeOut?.WriteLine("[GUI] Could not open a terminal window."); } catch { }
                return;
            }

            _terminalMode = true;
            try { Console.Title = "CMZ Dedicated Steam Server - Terminal"; } catch { }

            // Closing a console window sends CTRL_CLOSE_EVENT which terminates the
            // process even when handled — grey out X so the server can't be
            // accidentally killed; "gui" command returns to the GUI instead.
            try
            {
                IntPtr conHwnd = GetConsoleWindow();
                if (conHwnd != IntPtr.Zero)
                    EnableMenuItem(GetSystemMenu(conHwnd, false), SC_CLOSE, MF_BYCOMMAND | MF_GRAYED);
            }
            catch
            {
            }

            IntPtr hOut = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            IntPtr hIn = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (hOut != IntPtr.Zero && hOut != new IntPtr(-1))
            {
                SetStdHandle(STD_OUTPUT_HANDLE, hOut);
                _conWriter = new StreamWriter(
                    new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(hOut, true), FileAccess.Write))
                { AutoFlush = true };
                Console.SetOut(new TeeTextWriter(_pipeOut, _conWriter));

                // Replay the log history the GUI already has so the terminal
                // doesn't open empty.
                foreach (string line in _recentLogLines)
                {
                    try { _conWriter.WriteLine(line); }
                    catch { break; }
                }
            }

            if (hIn != IntPtr.Zero && hIn != new IntPtr(-1))
            {
                SetStdHandle(STD_INPUT_HANDLE, hIn);
                _conReader = new StreamReader(
                    new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(hIn, true), FileAccess.Read));
                var input = new Thread(() => TerminalInputLoop(_conReader))
                {
                    IsBackground = true,
                    Name = "TerminalInputThread"
                };
                input.Start();
            }

            Log("Terminal attached. Type commands normally. Type 'gui' to return to the GUI window.");
        }

        private static void TerminalInputLoop(StreamReader reader)
        {
            try
            {
                while (_terminalMode)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        break;

                    if (line.Trim().Equals("gui", StringComparison.OrdinalIgnoreCase))
                    {
                        ExitTerminalMode();
                        break;
                    }

                    var server = _activeServer;
                    var log = _activeLog;
                    if (server != null && server.IsRunning && log != null)
                        HandleConsoleCommand(server, line, log);
                }
            }
            catch
            {
            }
        }

        private static void ExitTerminalMode()
        {
            if (!_terminalMode)
                return;

            _terminalMode = false;

            try { _pipeOut?.WriteLine("@@GUISHOW"); _pipeOut?.Flush(); } catch { }
            try { Console.SetOut(_pipeOut); } catch { }

            // Dispose the CONIN$/CONOUT$ streams — open handles keep the console
            // window alive after FreeConsole, and disposing CONIN$ wakes the
            // blocked ReadLine on the input thread.
            try { _conReader?.Dispose(); } catch { }
            try { _conWriter?.Dispose(); } catch { }
            _conReader = null;
            _conWriter = null;

            try { if (_pipeOutHandle != IntPtr.Zero) SetStdHandle(STD_OUTPUT_HANDLE, _pipeOutHandle); } catch { }
            try { if (_pipeInHandle != IntPtr.Zero) SetStdHandle(STD_INPUT_HANDLE, _pipeInHandle); } catch { }
            try { FreeConsole(); } catch { }

            Log("[GUI] Terminal closed - GUI window restored.");
        }

        /// <summary>
        /// Handles a single console command.
        /// </summary>
        private static void HandleConsoleCommand(SteamDedicatedServer server, string line, Action<string> log)
        {
            string command = (line ?? string.Empty).Trim();

            if (command.Length == 0)
                return;

            if (command.Equals("players", StringComparison.OrdinalIgnoreCase))
            {
                log(server.GetPlayerListText());
                return;
            }

            if (command.StartsWith("kick ", StringComparison.OrdinalIgnoreCase))
            {
                string args = command.Substring(5).Trim();

                if (!TryParseTargetAndReason(args, out string target, out string reason))
                {
                    log("Usage: kick <id|steamid|name> [reason]");
                    log("Example: kick 2 Being annoying");
                    log("Example: kick \"Jacob Smith\" Being annoying");
                    return;
                }

                server.KickPlayer(target, reason);
                return;
            }

            if (command.StartsWith("ban ", StringComparison.OrdinalIgnoreCase))
            {
                string args = command.Substring(4).Trim();

                if (!TryParseTargetAndReason(args, out string target, out string reason))
                {
                    log("Usage: ban <id|steamid|name> [reason]");
                    log("Example: ban 2 Griefing protected areas");
                    log("Example: ban \"Jacob Smith\" Griefing protected areas");
                    return;
                }

                server.BanPlayer(target, reason);
                return;
            }

            if (command.StartsWith("unban ", StringComparison.OrdinalIgnoreCase))
            {
                string target = command.Substring(6).Trim();

                if (target.StartsWith("\"") && target.EndsWith("\"") && target.Length > 1)
                    target = target.Substring(1, target.Length - 2).Trim();

                server.UnbanPlayer(target);
                return;
            }

            switch (command.ToLowerInvariant())
            {
                case "help":
                case "?":
                    log("Console commands:");
                    log("  reload                           Reload server.properties, plugins, and command files");
                    log("  reload properties                Reload runtime-safe server.properties values");
                    log("  reload plugins                   Reload plugin config/region/announcement files");
                    log("  reload commands                  Reload command permissions and player ranks");
                    log("  restart                          Restart the server process");
                    log("  stop                             Stop the server");
                    log("  players                          List connected players");
                    log("  bans                             List saved bans");
                    log("  kick <id|steamid|name> [reason]  Hard-kick a connected player");
                    log("  ban <id|steamid|name> [reason]   Hard-ban and drop a connected player");
                    log("  unban <steamid|name>             Remove a saved ban");
                    log("  op <player|steamid|name>         Give a player Admin command rank");
                    log("  deop <player|steamid|name>       Remove a player's saved command rank");
                    log("  setrank <player> <rank>          Set command rank: Default, Member, Moderator, Admin");
                    log("  help                             Show this command list");
                    break;

                case "reload":
                    server.Reload();
                    break;

                case "reload properties":
                case "properties reload":
                case "reload config":
                case "config reload":
                    server.ReloadServerProperties();
                    break;

                case "reload plugins":
                case "plugins reload":
                    server.ReloadPlugins();
                    break;

                case "stop":
                case "exit":
                case "quit":
                    log("Console stop command received. Shutting down...");
                    server.Stop();
                    break;

                case "restart":
                    log("Console restart command received. Restarting...");
                    server.RequestRestart();
                    break;

                default:
                    if (server.TryExecuteServerCommandFromConsole(command))
                        return;

                    log("Unknown command. Type 'help' for console commands.");
                    break;
            }
        }
        #endregion

        #region Parsing Helpers

        /// <summary>
        /// Parses commands that use:
        ///   command <target> [reason...]
        ///
        /// Supported examples:
        ///   kick 2 Being annoying
        ///   ban 76561198000000000 Griefing
        ///   kick "Jacob Smith" Being annoying
        ///   ban "Jacob Smith" Griefing protected areas
        ///
        /// Notes:
        /// - Quoted targets allow player names with spaces.
        /// - Numeric targets should be player IDs or SteamIDs.
        /// - If no reason is provided, the server will use its default kick/ban reason.
        /// </summary>
        private static bool TryParseTargetAndReason(string args, out string target, out string reason)
        {
            target = string.Empty;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(args))
                return false;

            args = args.Trim();

            // Quoted target: "Jacob Smith" optional reason here
            if (args.StartsWith("\""))
            {
                int closingQuote = args.IndexOf('"', 1);

                if (closingQuote <= 1)
                    return false;

                target = args.Substring(1, closingQuote - 1).Trim();

                if (closingQuote + 1 < args.Length)
                    reason = args.Substring(closingQuote + 1).Trim();

                return target.Length > 0;
            }

            // Normal target: first token is the target, everything after it is the reason.
            int firstSpace = args.IndexOf(' ');

            if (firstSpace < 0)
            {
                target = args;
                return target.Length > 0;
            }

            target = args.Substring(0, firstSpace).Trim();
            reason = args.Substring(firstSpace + 1).Trim();

            return target.Length > 0;
        }
        #endregion

        #region Logging

        /// <summary>
        /// Writes a timestamped server message to the console and mirrors audit/operational lines to dated files.
        /// </summary>
        /// <param name="message">Message to write.</param>
        // Fixed machine-tag -> human-label rewrites applied at the output
        // boundary. Exact message-start prefixes only: type names inside the
        // body, identifiers, paths and single-word tags are never rewritten.
        private static readonly (string From, string To)[] LogLabelMap =
        {
            ("[PlayerEnforcement]", "[Player Enforcement]"),
            ("[CombatEvidence]", "[Combat Evidence]"),
            ("[ShadowCombat]", "[Shadow Combat]"),
            ("[HostMsg]", "[Host Message]"),
            ("[ConnectionApproval]", "[Connection Approval]"),
            ("[AssemblyResolve]", "[Assembly Resolve]"),
            ("[MainLoop]", "[Main Loop]"),
            ("[StatusChanged]", "[Status Changed]"),
            ("[SteamLobby]", "[Steam Lobby]"),
        };

        private static string FormatLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            foreach ((string from, string to) in LogLabelMap)
            {
                if (message.StartsWith(from, StringComparison.Ordinal))
                    return to + message.Substring(from.Length);
            }
            // Delimited so a player named e.g. ItemPolicyX is not rewritten.
            if (message.StartsWith("[Trust] ItemPolicy:", StringComparison.Ordinal) ||
                message.StartsWith("[Trust] ItemPolicy ", StringComparison.Ordinal))
                return "[Trust] Item Policy" + message.Substring("[Trust] ItemPolicy".Length);
            if (message.StartsWith("ServerWorld:", StringComparison.Ordinal) ||
                message.StartsWith("ServerWorld ", StringComparison.Ordinal))
                return "Server World" + message.Substring("ServerWorld".Length);
            return message;
        }

        // Log output is produced on the game loop thread but written on a
        // dedicated background writer: a slow or saturated console handle
        // (redirected output, a tailing UI, disk stalls) must never block
        // packet processing. Bounded queue — when writers fall behind, lines
        // drop and the loss is reported instead of stalling the server.
        private const int LogQueueCapacity = 8192;
        private static readonly BlockingCollection<(string Stamped, string Safe)> LogQueue =
            new BlockingCollection<(string, string)>(new ConcurrentQueue<(string, string)>(), LogQueueCapacity);
        private static int _logDroppedCount;
        private static Thread _logWriterThread;
        private static readonly object LogWriterSync = new object();

        private static void EnsureLogWriter()
        {
            if (_logWriterThread != null)
                return;
            lock (LogWriterSync)
            {
                if (_logWriterThread != null)
                    return;
                _logWriterThread = new Thread(LogWriterLoop)
                {
                    IsBackground = true,
                    Name = "LogWriter"
                };
                _logWriterThread.Start();
            }
        }

        private static void LogWriterLoop()
        {
            foreach ((string stampedLine, string safeMessage) in LogQueue.GetConsumingEnumerable())
            {
                WriteLogLine(stampedLine, safeMessage);

                // Report dropped lines once per drain point, not per line.
                int dropped = Interlocked.Exchange(ref _logDroppedCount, 0);
                if (dropped > 0)
                {
                    string marker = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Log] {dropped} log line(s) dropped — writer saturated.";
                    try { Console.WriteLine(marker); } catch { }
                    if (CommandAuditLogEnabled)
                        AppendServerLogLine(marker);
                }
            }
        }

        private static void WriteLogLine(string stampedLine, string safeMessage)
        {
            try
            {
                Console.WriteLine(stampedLine);
            }
            catch
            {
                // A dead console handle must never kill the writer thread.
            }

            _recentLogLines.Enqueue(stampedLine);
            while (_recentLogLines.Count > RecentLogLinesMax)
                _recentLogLines.TryDequeue(out _);

            if (CommandAuditLogEnabled && IsCommandAuditLine(safeMessage))
                AppendCommandAuditLogLine(stampedLine);

            if (CommandAuditLogEnabled && IsSecurityAuditLine(safeMessage))
                AppendSecurityAuditLogLine(stampedLine);

            if (CommandAuditLogEnabled && IsServerLogLine(safeMessage))
                AppendServerLogLine(stampedLine);
        }

        private static void Log(string message)
        {
            string safeMessage = FormatLogMessage(message ?? string.Empty);
            string stampedLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {safeMessage}";

            EnsureLogWriter();
            if (!LogQueue.TryAdd((stampedLine, safeMessage)))
                Interlocked.Increment(ref _logDroppedCount);
        }

        /// <summary>
        /// Returns true when a console line belongs in the command audit log.
        /// </summary>
        private static bool IsCommandAuditLine(string message)
        {
            return !string.IsNullOrWhiteSpace(message) &&
                   message.StartsWith("[Commands]", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true when a console line belongs in the security audit log:
        /// trust violations, forged/dropped packets, and kick/ban outcomes.
        /// </summary>
        private static bool IsSecurityAuditLine(string message)
        {
            // Accept both the raw machine tag and the humanized display form
            // (callers may route messages before or after FormatLogMessage).
            return !string.IsNullOrWhiteSpace(message) &&
                   (message.StartsWith("[Trust]", StringComparison.Ordinal) ||
                    message.StartsWith("[Security]", StringComparison.Ordinal) ||
                    message.StartsWith("[PlayerEnforcement]", StringComparison.Ordinal) ||
                    message.StartsWith("[Player Enforcement]", StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns true when a console line belongs in the general server log:
        /// operational ServerWorld lines (world load/save, inventory serves)
        /// and the log-combat-gated diagnostic lines ([CombatEvidence],
        /// [ShadowCombat]) - emission is already gated by the config switch;
        /// this only routes them to disk when they are emitted.
        /// </summary>
        private static bool IsServerLogLine(string message)
        {
            // Legacy machine tags and their spaced display forms both route.
            return !string.IsNullOrWhiteSpace(message) &&
                   (message.StartsWith("ServerWorld", StringComparison.Ordinal) ||
                    message.StartsWith("Server World", StringComparison.Ordinal) ||
                    message.StartsWith("[CombatEvidence]", StringComparison.Ordinal) ||
                    message.StartsWith("[Combat Evidence]", StringComparison.Ordinal) ||
                    message.StartsWith("[ShadowCombat]", StringComparison.Ordinal) ||
                    message.StartsWith("[Shadow Combat]", StringComparison.Ordinal));
        }

        /// <summary>
        /// Appends one operational line to Logs\server-yyyy-MM-dd.log.
        /// </summary>
        private static void AppendServerLogLine(string line)
        {
            try
            {
                string logsDir = Path.Combine(CommandAuditBaseDir, "Logs");
                Directory.CreateDirectory(logsDir);

                string path = Path.Combine(logsDir, "server-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                lock (CommandAuditLogSync)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never let logging failures break the dedicated server loop.
            }
        }

        /// <summary>
        /// Appends one security line to Logs\security-yyyy-MM-dd.log.
        /// </summary>
        private static void AppendSecurityAuditLogLine(string line)
        {
            try
            {
                string logsDir = Path.Combine(CommandAuditBaseDir, "Logs");
                Directory.CreateDirectory(logsDir);

                string path = Path.Combine(logsDir, "security-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                lock (CommandAuditLogSync)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never let logging failures break the dedicated server loop.
            }
        }

        /// <summary>
        /// Appends one command audit line to Logs\commands-yyyy-MM-dd.log.
        /// </summary>
        private static void AppendCommandAuditLogLine(string line)
        {
            try
            {
                string logsDir = Path.Combine(CommandAuditBaseDir, "Logs");
                Directory.CreateDirectory(logsDir);

                string path = Path.Combine(logsDir, "commands-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                lock (CommandAuditLogSync)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Never let logging failures break the dedicated server loop.
            }
        }
        #endregion
    }
}