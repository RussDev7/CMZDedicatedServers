/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServer - see LICENSE for details.
*/

using System.Reflection;
using System.Threading;
using System.IO;
using System;

namespace CMZServerHost
{
    internal static class Program
    {
        #region Entry Point

        /// <summary>
        /// Program entry point for the dedicated CMZ server host.
        ///
        /// Purpose:
        /// - Resolves and loads the game/runtime assemblies from the local "game" folder.
        /// - Applies optional runtime patches before the server starts.
        /// - Loads server configuration from disk.
        /// - Creates and starts the Lidgren-backed server host.
        /// - Runs the fixed-rate update loop until Ctrl+C is pressed.
        ///
        /// Flow:
        /// 1) Validate the expected local game/runtime files.
        /// 2) Register AssemblyResolve so dependent assemblies can be loaded from /game.
        /// 3) Load CastleMinerZ.exe and DNA.Common.dll.
        /// 4) Apply runtime patches.
        /// 5) Load config and print a startup summary.
        /// 6) Construct and start the server.
        /// 7) Enter update loop until shutdown.
        ///
        /// Notes:
        /// - Return codes are intentionally preserved exactly as before.
        /// - No behavior or logic has been changed; this is documentation / organization only.
        /// - The update loop uses TickRateHz from config to derive the sleep interval.
        /// </summary>
        static int Main()
        {
            Console.Title = "CMZ Server Host";

            try
            {
                #region Resolve Base Paths

                // Root folder for the current executable.
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // Expected sub-folder containing CastleMinerZ.exe and companion assemblies.
                string gamePath = Path.Combine(baseDir, "game");

                #endregion

                #region Validate Required Game Folder / Executable

                if (!Directory.Exists(gamePath))
                {
                    Console.WriteLine("ERROR: Missing game folder.");
                    Console.WriteLine("Expected: " + gamePath);
                    return 1;
                }

                string exePath = Path.Combine(gamePath, "CastleMinerZ.exe");
                string commonPath = Path.Combine(gamePath, "DNA.Common.dll");

                if (!File.Exists(exePath))
                {
                    Console.WriteLine("ERROR: Missing CastleMinerZ.exe");
                    return 2;
                }
                #endregion

                #region Assembly Resolution

                /// <summary>
                /// Resolves missing dependent assemblies from the local "game" folder.
                ///
                /// Notes:
                /// - First checks for a matching DLL.
                /// - Then checks for a matching EXE assembly.
                /// - Returns null when the requested assembly cannot be resolved here,
                ///   allowing normal resolution to continue/fail naturally.
                /// </summary>
                AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
                {
                    var asmName = new AssemblyName(resolveArgs.Name);

                    string dllPath = Path.Combine(gamePath, asmName.Name + ".dll");
                    if (File.Exists(dllPath))
                        return Assembly.LoadFrom(dllPath);

                    string exeAsmPath = Path.Combine(gamePath, asmName.Name + ".exe");
                    if (File.Exists(exeAsmPath))
                        return Assembly.LoadFrom(exeAsmPath);

                    return null;
                };
                #endregion

                #region Load Runtime Assemblies

                // Main CastleMinerZ game assembly.
                Assembly gameAsm = Assembly.LoadFrom(exePath);

                // Optional shared/common assembly used by parts of the host/runtime.
                Assembly commonAsm = File.Exists(commonPath) ? Assembly.LoadFrom(commonPath) : null;

                #endregion

                #region Apply Runtime Patches

                // Optional runtime server patches.
                //
                // Notes:
                // - Intentionally executed before server construction/startup.
                // - Preserved exactly as-is.
                ServerPatches.ApplyAllPatches();

                #endregion

                #region Load Configuration

                ServerConfig config = ServerConfig.Load(baseDir);

                #endregion

                #region Print Startup Summary

                Console.WriteLine("CMZ Server Host");
                Console.WriteLine("---------------");
                Console.WriteLine($"GameName       : {config.GameName}");
                Console.WriteLine($"NetworkVersion : {config.NetworkVersion}");
                Console.WriteLine($"Bind           : {config.BindAddress}:{config.Port}");
                Console.WriteLine($"ServerName     : {config.ServerName}");
                Console.WriteLine($"MaxPlayers     : {config.MaxPlayers}");
                Console.WriteLine($"SteamUserId    : {config.SteamUserId}");
                Console.WriteLine($"WorldGuid      : {config.WorldGuid}");
                Console.WriteLine($"WorldFolder    : {config.WorldFolder}");
                Console.WriteLine($"WorldPath      : {config.WorldPath}");
                Console.WriteLine($"WorldInfo file : {Path.Combine(config.WorldPath, "world.info")}");
                Console.WriteLine($"World loaded   : {File.Exists(Path.Combine(config.WorldPath, "world.info"))}");
                Console.WriteLine();

                #endregion

                #region Create Server Instance

                /// <summary>
                /// Construct the dedicated server host.
                ///
                /// Notes:
                /// - All constructor arguments are preserved exactly.
                /// - worldFolder remains nullable when config.WorldFolder is blank/whitespace.
                /// - saveRoot continues to point at the executable base directory.
                /// </summary>
                LidgrenServer server = new LidgrenServer(
                    gamePath: gamePath,
                    port: config.Port,
                    maxPlayers: config.MaxPlayers,
                    log: Console.WriteLine,
                    gameAsm: gameAsm,
                    worldFolder: string.IsNullOrWhiteSpace(config.WorldFolder) ? null : config.WorldFolder,
                    saveRoot: baseDir,
                    steamUserId: config.SteamUserId,
                    bindAddress: config.BindAddress,
                    viewRadiusChunks: config.ViewDistanceChunks,
                    serverName: config.ServerName,
                    gameMode: config.GameMode,
                    pvpState: config.PvpState,
                    difficulty: config.Difficulty,
                    gameName: config.GameName,
                    networkVersion: config.NetworkVersion);

                #endregion

                #region Start Server

                server.Start();

                Console.WriteLine();
                Console.WriteLine("Server started.");
                Console.WriteLine($"Local test target: 127.0.0.1:{config.Port}");
                Console.WriteLine("Press Ctrl+C to stop.");

                #endregion

                #region Shutdown Signal Handling

                bool running = true;

                /// <summary>
                /// Ctrl+C handler.
                ///
                /// Notes:
                /// - Cancels default process termination.
                /// - Allows the main loop to exit cleanly and call server.Stop().
                /// </summary>
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    running = false;
                };
                #endregion

                #region Tick Timing

                // Derive loop sleep interval from configured tick rate.
                //
                // Notes:
                // - Preserves the original defensive Math.Max usage.
                // - Prevents division by zero and enforces a minimum 1 ms sleep.
                int sleepMs = Math.Max(1, 1000 / Math.Max(1, config.TickRateHz));

                #endregion

                #region Main Server Loop

                /// <summary>
                /// Main server update loop.
                ///
                /// Purpose:
                /// - Calls server.Update() repeatedly until shutdown is requested.
                /// - Catches per-tick exceptions so a single update failure does not
                ///   terminate the process immediately.
                ///
                /// Notes:
                /// - Thread.Sleep cadence is preserved exactly.
                /// - Any update exception is logged and the loop continues.
                /// </summary>
                while (running)
                {
                    try
                    {
                        server.Update();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Server] Update error: " + ex);
                    }

                    Thread.Sleep(sleepMs);
                }
                #endregion

                #region Graceful Stop

                server.Stop();

                return 0;

                #endregion
            }
            catch (Exception ex)
            {
                #region Fatal Startup / Runtime Failure

                // Top-level fatal exception handler.
                //
                // Notes:
                // - Preserves existing return code and message format.
                // - Intended to catch anything not handled by the per-tick loop.
                Console.WriteLine("FATAL: " + ex);
                return 99;

                #endregion
            }
        }
        #endregion
    }
}