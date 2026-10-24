/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace CMZDedicatedSteamServer
{
    /// <summary>
    /// Result of a terrain support lookup under a player position.
    /// Unknown means the column could not be evaluated and must not be punished.
    /// </summary>
    internal enum SupportProbeResult
    {
        Unknown,
        Supported,
        Elevated
    }

    /// <summary>
    /// Server-side terrain truth probe used by the trust layer.
    ///
    /// How it works:
    /// - The vanilla world is deterministic: clients generate untouched chunks from
    ///   WorldInfo.Seed via CastleMinerZBuilder. This probe runs the exact same
    ///   generator code headless, so its view of terrain matches what clients see.
    /// - BlockTerrain's constructor loads shaders/textures, but generation only needs
    ///   its int[] _blocks array, _worldMin, WaterLevel and _resetRequested. The
    ///   instance is created uninitialized and only those fields are populated.
    /// - Biome code reads CastleMinerZGame.Instance.GameMode and
    ///   Instance.CurrentWorld (a WorldInfo). A shell CastleMinerZGame instance is
    ///   created uninitialized and wired to those fields only.
    /// - HellFloorBiome mutates WorldInfo.HellBossesSpawned/MaxHellBossSpawns during
    ///   generation; both are snapshotted and restored per call so probing never
    ///   drifts the authoritative world info.
    /// - Player edits overlay on top: saved chunk deltas are consulted per queried
    ///   block, so placed towers and dug-out blocks count correctly.
    ///
    /// If any reflection step fails, Enabled stays false and callers receive Unknown.
    /// </summary>
    internal sealed class TerrainProbe
    {
        private const int TerrainMinY = -64;
        private const int TerrainHeight = 128;
        private const int WindowXZ = 384;
        private const int BlocksLength = 18874368; // 384 * 128 * 384
        private const int BitmapBytesPerChunk = 16 * 16 * TerrainHeight / 8;
        private const int MaxCachedChunks = 512;
        private const float SeaLevelWorldY = 1.5f; // BuildWorldChunk sets WaterLevel = 1.5f

        /// <summary>Column solidity bitmap (bit per y) + generated block ids + per-column surface height.</summary>
        private sealed class ChunkProbeData
        {
            public byte[] Bitmap;
            public byte[] BlockTypes;     // block type id per (lx,ly,lz), 0 = Empty
            public sbyte[] SurfaceLocalY; // local y of highest solid generated block, -128 = all air
        }

        /// <summary>Matches ServerWorldHandler.TryGetSavedBlockType (chunk-delta overlay lookup).</summary>
        internal delegate bool TryGetSavedBlockTypeDelegate(int worldX, int worldY, int worldZ, out int blockTypeValue);

        private readonly Assembly _gameAsm;
        private readonly Assembly _commonAsm;
        private readonly object _worldInfo;
        private readonly TryGetSavedBlockTypeDelegate _tryGetSavedBlock;
        private readonly Action<string> _log;
        private readonly int _gameMode;
        private readonly object _sync = new object();

        private readonly Dictionary<long, ChunkProbeData> _columnCache = new Dictionary<long, ChunkProbeData>();
        private readonly LinkedList<long> _cacheOrder = new LinkedList<long>();
        private readonly Dictionary<long, LinkedListNode<long>> _cacheNodes = new Dictionary<long, LinkedListNode<long>>();

        private object _terrain;
        private object _builder;
        private object _shellGame;
        private MethodInfo _buildWorldChunk;
        private MethodInfo _setWorldMin;
        private FieldInfo _blocksField;
        private FieldInfo _resetField;
        private FieldInfo _waterLevelField;
        private FieldInfo _isWaterWorldField;
        private FieldInfo _gameModeField;
        private FieldInfo _currentWorldField;
        private FieldInfo _gameInstanceField;
        private FieldInfo _hellBossesSpawnedField;
        private FieldInfo _maxHellBossSpawnsField;
        private ConstructorInfo _intVector3Ctor;
        private int _numberOfBlocks = 256;
        private bool _initAttempted;
        private long _genFailures;

        public bool Enabled { get; private set; }

        public TerrainProbe(Assembly gameAsm, Assembly commonAsm, object worldInfo, int gameMode, Action<string> log, TryGetSavedBlockTypeDelegate tryGetSavedBlock)
        {
            _gameAsm = gameAsm;
            _commonAsm = commonAsm;
            _worldInfo = worldInfo;
            _gameMode = gameMode;
            _log = log ?? (_ => { });
            _tryGetSavedBlock = tryGetSavedBlock;
        }

        /// <summary>
        /// Builds the headless generator on first use. Any failure disables the probe
        /// permanently so callers degrade gracefully instead of spamming errors.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_initAttempted)
                return;
            _initAttempted = true;

            try
            {
                Type blockTerrainType = _gameAsm?.GetType("DNA.CastleMinerZ.Terrain.BlockTerrain");
                Type builderType = _gameAsm?.GetType("DNA.CastleMinerZ.Terrain.WorldBuilders.CastleMinerZBuilder");
                // IntVector3 ships in DNA.Common.dll, not the game exe - probe both assemblies.
                Type intVector3Type = _gameAsm?.GetType("DNA.CastleMinerZ.IntVector3")
                    ?? _commonAsm?.GetType("DNA.CastleMinerZ.IntVector3")
                    ?? _commonAsm?.GetType("DNA.Common.IntVector3")
                    ?? _commonAsm?.GetType("DNA.IntVector3")
                    ?? _commonAsm?.GetType("DNA.Common.Mathematics.IntVector3")
                    ?? _gameAsm?.GetType("DNA.Common.IntVector3")
                    ?? _gameAsm?.GetType("DNA.IntVector3");
                Type gameType = _gameAsm?.GetType("DNA.CastleMinerZ.CastleMinerZGame");
                Type gameModeTypes = _gameAsm?.GetType("DNA.CastleMinerZ.UI.GameModeTypes");
                Type blockTypeEnum = _gameAsm?.GetType("DNA.CastleMinerZ.Terrain.BlockTypeEnum");

                if (blockTerrainType == null || builderType == null || intVector3Type == null ||
                    gameType == null || gameModeTypes == null || blockTypeEnum == null)
                {
                    _log("[Trust] TerrainProbe disabled: required game types not found.");
                    return;
                }

                _numberOfBlocks = Convert.ToInt32(Enum.Parse(blockTypeEnum, "NumberOfBlocks"));

                _blocksField = blockTerrainType.GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _resetField = blockTerrainType.GetField("_resetRequested", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _waterLevelField = blockTerrainType.GetField("WaterLevel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _isWaterWorldField = blockTerrainType.GetField("IsWaterWorld", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _setWorldMin = blockTerrainType.GetMethod("SetWorldMin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                _gameModeField = gameType.GetField("GameMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _currentWorldField = gameType.GetField("CurrentWorld", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _gameInstanceField = gameType.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                if (_blocksField == null || _resetField == null ||
                    _waterLevelField == null || _setWorldMin == null || _gameModeField == null ||
                    _currentWorldField == null || _gameInstanceField == null)
                {
                    _log("[Trust] TerrainProbe disabled: required game members not found.");
                    return;
                }

                _intVector3Ctor = intVector3Type.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
                if (_intVector3Ctor == null)
                {
                    _log("[Trust] TerrainProbe disabled: IntVector3 ctor not found.");
                    return;
                }

                _buildWorldChunk = builderType.GetMethod("BuildWorldChunk", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_buildWorldChunk == null)
                {
                    _log("[Trust] TerrainProbe disabled: BuildWorldChunk not found.");
                    return;
                }

                // WorldInfo fields HellFloorBiome increments during generation.
                if (_worldInfo != null)
                {
                    Type worldInfoType = _worldInfo.GetType();
                    _hellBossesSpawnedField = worldInfoType.GetField("HellBossesSpawned", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _maxHellBossSpawnsField = worldInfoType.GetField("MaxHellBossSpawns", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                // Bare-bones game shell so biome code can read GameMode + CurrentWorld.
                _shellGame = FormatterServices.GetUninitializedObject(gameType);
                _gameModeField.SetValue(_shellGame, Enum.ToObject(gameModeTypes, _gameMode));
                _currentWorldField.SetValue(_shellGame, _worldInfo);

                // Uninitialized BlockTerrain: skip the graphics-loading constructor and
                // populate only the fields biome generation actually touches.
                _terrain = FormatterServices.GetUninitializedObject(blockTerrainType);
                _blocksField.SetValue(_terrain, new int[BlocksLength]);
                _resetField.SetValue(_terrain, false);
                _waterLevelField.SetValue(_terrain, 1.5f);
                _isWaterWorldField?.SetValue(_terrain, false);
                _setWorldMin.Invoke(_terrain, new[] { MakeIntVector3(-192, TerrainMinY, -192) });

                // Builder ctor constructs all biome noise functions from WorldInfo.Seed
                // and calls caves.SetLootModifiersByGameMode() (needs Instance.GameMode).
                object previousInstance = _gameInstanceField.GetValue(null);
                try
                {
                    _gameInstanceField.SetValue(null, _shellGame);
                    _builder = Activator.CreateInstance(
                        builderType,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { _worldInfo },
                        null);
                }
                finally
                {
                    _gameInstanceField.SetValue(null, previousInstance);
                }

                if (_builder == null)
                {
                    _log("[Trust] TerrainProbe disabled: could not construct CastleMinerZBuilder.");
                    return;
                }

                Enabled = true;
                _log("[Trust] TerrainProbe enabled: generating real terrain from the world seed.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                _log("[Trust] TerrainProbe disabled: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// Checks whether any space-occupying block exists within scanDepth blocks
        /// below the player's feet (generated terrain overlaid with saved deltas).
        /// Water at ocean surface counts as support so swimmers are never flagged.
        /// </summary>
        public SupportProbeResult ProbeSupport(float feetX, float feetY, float feetZ, int scanDepth)
        {
            lock (_sync)
            {
                if (!Enabled)
                {
                    EnsureInitialized();
                    if (!Enabled)
                        return SupportProbeResult.Unknown;
                }

                int bx = (int)Math.Floor(feetX);
                int bz = (int)Math.Floor(feetZ);
                int topY = (int)Math.Floor(feetY);

                // Swimming on the ocean surface is legitimate: if this column's
                // generated floor sits below sea level and the player is at the
                // water line, treat it as supported.
                int surfaceLocalY = GetSurfaceLocalY(bx, bz);
                if (surfaceLocalY > sbyte.MinValue)
                {
                    int surfaceWorldY = surfaceLocalY + TerrainMinY;
                    if (surfaceWorldY < SeaLevelWorldY &&
                        feetY <= SeaLevelWorldY + 1.5f &&
                        feetY >= SeaLevelWorldY - 1.0f)
                    {
                        return SupportProbeResult.Supported;
                    }
                }

                for (int dy = 1; dy <= scanDepth; dy++)
                {
                    int by = topY - dy;

                    // Below the generated band is bedrock/void; treat as grounded.
                    if (by < TerrainMinY)
                        return SupportProbeResult.Supported;

                    // Player edits override generated terrain (dug blocks read back
                    // as Empty, placed towers read as solid).
                    if (_tryGetSavedBlock != null &&
                        _tryGetSavedBlock(bx, by, bz, out int savedType))
                    {
                        if (savedType > 0 && savedType < _numberOfBlocks)
                            return SupportProbeResult.Supported;
                        continue;
                    }

                    if (by >= TerrainMinY + TerrainHeight)
                        continue; // above generated band: air

                    int bit = GetGeneratedBit(bx, by, bz);
                    if (bit < 0)
                        return SupportProbeResult.Unknown;
                    if (bit > 0)
                        return SupportProbeResult.Supported;
                }

                return SupportProbeResult.Elevated;
            }
        }

        /// <summary>
        /// Resolves the effective block type at a world position: saved player edits
        /// override generated terrain. Returns false when nothing can be proven
        /// (probe off, generation failed, or outside the generated band with no edit).
        /// </summary>
        public bool TryProbeBlockType(int worldX, int worldY, int worldZ, out int blockType)
        {
            blockType = 0;
            lock (_sync)
            {
                // A saved player edit is always authoritative when present.
                if (_tryGetSavedBlock != null && _tryGetSavedBlock(worldX, worldY, worldZ, out int savedType))
                {
                    blockType = savedType;
                    return true;
                }

                if (!Enabled)
                {
                    EnsureInitialized();
                    if (!Enabled)
                        return false;
                }

                if (worldY < TerrainMinY || worldY >= TerrainMinY + TerrainHeight)
                    return false; // outside the generated band and unedited

                int chunkX = MakeChunkCorner(worldX);
                int chunkZ = MakeChunkCorner(worldZ);

                long key = ((long)chunkX << 32) | (uint)chunkZ;
                if (!_columnCache.TryGetValue(key, out ChunkProbeData data))
                {
                    data = GenerateChunk(chunkX, chunkZ);
                    if (data == null)
                        return false;
                    AddToCache(key, data);
                }
                else
                {
                    TouchCacheEntry(key);
                }

                int lx = worldX - chunkX;
                int lz = worldZ - chunkZ;
                int ly = worldY - TerrainMinY;
                if (lx < 0 || lx > 15 || lz < 0 || lz > 15)
                    return false;

                blockType = data.BlockTypes[(lx * 16 + lz) * TerrainHeight + ly];
                return true;
            }
        }

        /// <summary>
        /// Returns the local-y solidity bit for a generated block, or -1 when the
        /// containing chunk could not be generated.
        /// </summary>
        private int GetGeneratedBit(int worldX, int worldY, int worldZ)
        {
            int chunkX = MakeChunkCorner(worldX);
            int chunkZ = MakeChunkCorner(worldZ);

            long key = ((long)chunkX << 32) | (uint)chunkZ;
            if (!_columnCache.TryGetValue(key, out ChunkProbeData data))
            {
                data = GenerateChunk(chunkX, chunkZ);
                if (data == null)
                    return -1;
                AddToCache(key, data);
            }
            else
            {
                TouchCacheEntry(key);
            }

            int lx = worldX - chunkX;
            int lz = worldZ - chunkZ;
            int ly = worldY - TerrainMinY;
            if (lx < 0 || lx > 15 || lz < 0 || lz > 15 || ly < 0 || ly >= TerrainHeight)
                return 0;

            int bitIndex = (lx * 16 + lz) * TerrainHeight + ly;
            return (data.Bitmap[bitIndex >> 3] >> (bitIndex & 7)) & 1;
        }

        /// <summary>Highest generated solid block local-y for the column, or sbyte.MinValue.</summary>
        private int GetSurfaceLocalY(int worldX, int worldZ)
        {
            int chunkX = MakeChunkCorner(worldX);
            int chunkZ = MakeChunkCorner(worldZ);

            long key = ((long)chunkX << 32) | (uint)chunkZ;
            if (!_columnCache.TryGetValue(key, out ChunkProbeData data))
            {
                data = GenerateChunk(chunkX, chunkZ);
                if (data == null)
                    return sbyte.MinValue;
                AddToCache(key, data);
            }
            else
            {
                TouchCacheEntry(key);
            }

            int lx = worldX - chunkX;
            int lz = worldZ - chunkZ;
            if (lx < 0 || lx > 15 || lz < 0 || lz > 15)
                return sbyte.MinValue;

            return data.SurfaceLocalY[lx * 16 + lz];
        }

        /// <summary>
        /// Runs the real CastleMinerZBuilder on one 16x16 column chunk and packs the
        /// result into a per-y solidity bitmap + per-column surface height.
        /// HellFloorBiome's world-info mutations are restored after the call.
        /// </summary>
        private ChunkProbeData GenerateChunk(int chunkX, int chunkZ)
        {
            try
            {
                int savedBosses = 0, savedMaxBosses = 0;
                bool snapshotBosses = _hellBossesSpawnedField != null && _maxHellBossSpawnsField != null;
                if (snapshotBosses)
                {
                    savedBosses = (int)_hellBossesSpawnedField.GetValue(_worldInfo);
                    savedMaxBosses = (int)_maxHellBossSpawnsField.GetValue(_worldInfo);
                }

                // Biome code reads CastleMinerZGame.Instance.GameMode/CurrentWorld
                // during generation, so install the shell for the duration of the call.
                int[] blocks = (int[])_blocksField.GetValue(_terrain);
                object previousInstance = _gameInstanceField.GetValue(null);
                try
                {
                    _gameInstanceField.SetValue(null, _shellGame);
                    _setWorldMin.Invoke(_terrain, new[] { MakeIntVector3(chunkX, TerrainMinY, chunkZ) });
                    _resetField.SetValue(_terrain, false);

                    // _blocks is a 384-wide sliding window reused across chunks.
                    // Biome columns never touch cells above the ground, so without
                    // this reset a flat chunk inherits whatever a taller chunk left
                    // at those local offsets - a solid phantom ceiling that reads
                    // as players buried in rock. Clear the chunk's 16x16x128 slab
                    // to Empty: the game's own "uninitialized" marker decodes as
                    // Dirt under the type mask, so it cannot be used here.
                    for (int lz = 0; lz < 16; lz++)
                    {
                        Array.Clear(blocks, lz * TerrainHeight * WindowXZ, 16 * TerrainHeight);
                    }

                    _buildWorldChunk.Invoke(_builder, new[] { _terrain, MakeIntVector3(chunkX, TerrainMinY, chunkZ) });
                }
                finally
                {
                    _gameInstanceField.SetValue(null, previousInstance);
                    if (snapshotBosses)
                    {
                        _hellBossesSpawnedField.SetValue(_worldInfo, savedBosses);
                        _maxHellBossSpawnsField.SetValue(_worldInfo, savedMaxBosses);
                    }
                }

                var data = new ChunkProbeData
                {
                    Bitmap = new byte[BitmapBytesPerChunk],
                    BlockTypes = new byte[16 * 16 * TerrainHeight],
                    SurfaceLocalY = new sbyte[256]
                };

                for (int lz = 0; lz < 16; lz++)
                {
                    for (int lx = 0; lx < 16; lx++)
                    {
                        int colBase = lx * TerrainHeight + lz * TerrainHeight * WindowXZ;
                        int surface = -1;
                        for (int ly = 0; ly < TerrainHeight; ly++)
                        {
                            int packed = blocks[colBase + ly];
                            int blockType = (packed >> 12) & 0xFF;
                            int cellIndex = (lx * 16 + lz) * TerrainHeight + ly;
                            data.BlockTypes[cellIndex] = (byte)blockType;
                            if (blockType > 0 && blockType < _numberOfBlocks)
                            {
                                data.Bitmap[cellIndex >> 3] |= (byte)(1 << (cellIndex & 7));
                                surface = ly;
                            }
                        }
                        data.SurfaceLocalY[lx * 16 + lz] = (sbyte)Math.Max(surface, -128);
                    }
                }

                return data;
            }
            catch (Exception ex)
            {
                // One failed chunk must not kill terrain checks for the session -
                // probing hostile coordinates is exactly what a cheater would try
                // to trip. Stay enabled; log sparingly so floods don't fill logs.
                _genFailures++;
                if (_genFailures <= 10 || _genFailures % 500 == 0)
                    _log($"[Trust] TerrainProbe chunk generation failed ({_genFailures}x): {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        private object MakeIntVector3(int x, int y, int z)
        {
            return _intVector3Ctor.Invoke(new object[] { x, y, z });
        }

        private static int MakeChunkCorner(int worldCoord)
        {
            return (int)(Math.Floor(worldCoord / 16.0) * 16.0);
        }

        private void AddToCache(long key, ChunkProbeData data)
        {
            _columnCache[key] = data;

            if (_cacheNodes.TryGetValue(key, out LinkedListNode<long> existing))
                _cacheOrder.Remove(existing);

            LinkedListNode<long> node = _cacheOrder.AddLast(key);
            _cacheNodes[key] = node;

            while (_cacheOrder.Count > MaxCachedChunks)
            {
                LinkedListNode<long> first = _cacheOrder.First;
                if (first == null)
                    break;
                _columnCache.Remove(first.Value);
                _cacheNodes.Remove(first.Value);
                _cacheOrder.RemoveFirst();
            }
        }

        private void TouchCacheEntry(long key)
        {
            if (!_cacheNodes.TryGetValue(key, out LinkedListNode<long> node) || node == null)
                return;
            _cacheOrder.Remove(node);
            _cacheOrder.AddLast(node);
        }
    }
}
