/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;
using System.IO;

namespace CMZDedicatedSteamServer.Trust
{
    /// <summary>
    /// Diagnostic-only shadow combat tracker (see CombatSimulationNotes.md).
    /// Fed ONLY messages that already passed the trust gates and only when
    /// the unified log-combat debug switch is enabled. It maintains approximate enemy
    /// position/health shadows for a narrow subset - zombies
    /// (EnemyType 0-17, slow plus observed fast mode) and melee skeletons
    /// (EnemyType 26-49, the same ZombieChase movement family) moved by a
    /// bounded voxel-body approximation (ShadowVoxelMotion: gravity,
    /// axis-swept collision, wall-jump) - and logs what the wire claims imply.
    ///
    /// Hard rules (do not weaken):
    /// - Output is [ShadowCombat] log lines ONLY. Nothing here feeds
    ///   violations, kicks, denials, or the damage budget.
    /// - Every position and every estimated HP is an approximation: no
    ///   emergence model, approximate camera origin (eye height 1.4, no
    ///   network camera), ballistic tracers modeled as vanilla head/tail
    ///   segments with gravity but stepped at coarse tick times (not
    ///   frame-exact), client-side tick timing, and unseeded vanilla
    ///   randomness are all unobservable. Records are marked "approx" or
    ///   "unknown", never presented as truth.
    /// - Approximate HP is NEVER complete: vanilla has no dedicated
    ///   melee-vs-enemy packet at all (InGameHUD.Melee calls TakeDamage
    ///   locally; DigMessage is a block dig/place action, not a swing)
    ///   and lava / environmental damage is unmodeled.
    /// - Anything unsupported (unresolvable named targets, archers/bosses,
    ///   lasers, melee, explosions, invalid fast speeds, missing terrain,
    ///   observation gaps) marks records uncertain instead of guessing. Observed
    ///   SpeedUpEnemyMessage switches a record to its spawn-package FastSpeed
    ///   (approx claim) - emergence timing and speed-up triggers stay unmodeled.
    /// </summary>
    internal sealed partial class ShadowCombatTracker
    {
        // Minimal float vector - no XNA/System.Numerics dependency.
        private struct Vec3
        {
            public float X, Y, Z;
            public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vec3 operator +(Vec3 a, float s) => new Vec3(a.X + s, a.Y + s, a.Z + s);
            public bool IsFinite =>
                !(float.IsNaN(X) || float.IsInfinity(X) ||
                  float.IsNaN(Y) || float.IsInfinity(Y) ||
                  float.IsNaN(Z) || float.IsInfinity(Z));
            public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        }

        private sealed class ShadowEnemy
        {
            public int EnemyId;
            public int OwnerId;       // resolved spawn target / kill TargetID - ids are per-client
            public int SpawnSenderId; // diagnostic origin - who sent the spawn
            public int EnemyType;
            public int RandomSeed;
            public Vec3 Pos;        // shadow of Motion.X/Y/Z - kept for logs/tests
            public Vec3 PreviousPos;    // Pos before the last successful advance
            public DateTime PreviousUtc; // when PreviousPos applied - bullet-time lerp base
            public ShadowVoxelMotion.MotionState Motion;
            public float StartHp;
            public float EstimatedHp;
            public float SlowSpeed;
            public float FastSpeed;   // InitPkg.FastSpeed claim - diagnostic only
            public bool MovingFast;   // set only on observed SpeedUpEnemyMessage
            public bool Uncertain;   // permanent once set - never reset by later events
            public string UncertaintyReason; // first-transition reason, diagnostics only
            public bool Dead;        // kill claim seen - body kept briefly for in-flight rays
            public DateTime DeathUtc;
            public DateTime SpawnedUtc; // immutable - TTL anchored here, not on activity
            // First hit that drove EstimatedHp to/below zero. The real enemy
            // leaves the hittable set then and its kill claim is in flight
            // (or was eaten upstream); a zeroed record stops absorbing rays
            // entirely - the claim can still resolve against it whenever it
            // arrives, but later tracers pass through to other shadows.
            public DateTime ZeroCrossUtc;
            // Server-solid cells this record's claimed body overlapped that
            // were adopted as client-proven air (terrain fork), plus a flag
            // for the kill-claim log. The client's spawn picker ran on its
            // own terrain - cells it placed a body into were non-blocking
            // there (a denied/restored dig still exists as air client-side).
            public int ForkCellCount;
            public bool Forked;
            public DateTime LastUtc;
        }

        private sealed class ShadowPlayer
        {
            public Vec3 Pos;
            public DateTime LastUtc;
            public DateTime LastMissLogUtc; // bounds ray-miss diagnostic spam
        }

        // One stamp per accepted trigger pull (parsed Gunshot/Shotgun
        // packet) - a wire fact, not a model estimate. At kill-claim time
        // the count of the killer's pulls inside the victim's lifetime
        // bounds the damage that player could have dealt: 12 pulls of a
        // 1.3-max weapon cannot legitimately kill a 21.7hp enemy.
        private sealed class ShotStamp
        {
            public int ShooterId;
            public int WeaponId;
            public float MaxDmg;   // this pull's own weapon bound; <0 = unpriced
            public DateTime Utc;
        }

        private const int MaxEnemies = 256;
        private const int MaxPlayers = 128;
        // Enemy positions never stream on the wire - the record IS the only
        // tracking, so a short TTL deletes live enemies before their kill
        // claims. Observation gaps and stale targets already mark records
        // uncertain; this is just a memory bound for the 256-entry cap.
        private static readonly TimeSpan EnemyTtl = TimeSpan.FromSeconds(300);
        private static readonly TimeSpan PlayerStaleAfter = TimeSpan.FromSeconds(2);
        // Longest tracer lifetime (bolt rifle, 2 s): a corpse must outlive
        // every ray that was already in flight when the kill claim arrived.
        private const double CorpseRetentionSec = 2.0;
        // Short packet gaps are common when nothing else crosses the wire.
        // A one-second envelope keeps ordinary chase coverage without letting
        // a stale target drive a shadow indefinitely.
        private const double MaxAdvanceSeconds = 1.0;
        // Same envelope as the enemy advance bound: across a longer quiet
        // gap an in-flight tracer's hit window cannot be reconstructed, so
        // the tracer drops (records are NOT invalidated - the next shot is
        // evaluated normally).
        private const double MaxProjectileAdvanceSeconds = 1.0;
        // Cap on client-proven air cells a single record may absorb: a body
        // box overlaps at most ~12 cells, so 48 covers a spawn pocket plus a
        // few later terrain-edit embeds without letting a wrong model
        // dissolve unlimited world.
        private const int ForkCellBudget = 48;
        // Integration substeps live in ShadowVoxelMotion (0.05 s each).
        // Position advancement runs the voxel sweep (bounded probes);
        // scheduling it on >=50ms ticks removes most of that cost. For a
        // static target the cumulative result is identical; for moving
        // targets it coarsens the chase samples - acceptable for a
        // diagnostic approximation. Combat events (shot/kill) still force
        // a fresh advance below.
        private static readonly TimeSpan AdvanceTickMin = TimeSpan.FromMilliseconds(50);
        private const int MaxProjectiles = 128;     // queued tracer rays, all shooters
        private const float StopDistance = 1f;      // vanilla chase switches to stationary attack/dig at ~1m
        // ArcherSkeletonChase: inside 35m horizontal, a clear ~1.5m-eye
        // terrain trace to the target switches the archer to its stationary
        // attack state; a blocked trace keeps it chasing.
        private const float ArcherHoldRange = 35f;
        private const float EyeHeight = 1.4f;       // FPSRig.EyePointHeight / pitch pivot y
        private const float ZombieHalfWidth = ShadowVoxelMotion.HalfWidth; // body AABB
        private const float ZombieHeight = ShadowVoxelMotion.Height;
        private const int CanaryIdBase = 0x40000000; // server-planted trap ids

        // Vanilla EnemyTypeEnum: every spawnable type is a land enemy in the
        // ZombieChase movement family with the same PlayerAABB - zombies
        // 0-17, archers 18-25, melee skeletons 26-49, Felguard 50, Hell Lord
        // 51, Alien 52 (Types[] has exactly 53 entries; enum slots 53-55 are
        // never instantiated, so nothing spawns them). Archers differ only
        // in holding to shoot inside ~35m with clear sight (ArcherHoldRange).
        private static bool IsSupportedEnemyType(int enemyType) =>
            enemyType >= 0 && enemyType <= 52;

        private readonly Action<string> _log;
        private readonly Func<int, float?> _enemyHp;       // enemyType -> StartingHealth
        private readonly Func<int, float?> _gunMaxDamage;  // weaponId -> per-trigger max
        private TerrainProbe.TryGetSavedBlockTypeDelegate _terrain;
        private readonly Dictionary<long, ShadowEnemy> _enemies = new Dictionary<long, ShadowEnemy>();
        private readonly Dictionary<byte, ShadowPlayer> _players = new Dictionary<byte, ShadowPlayer>();
        private readonly List<ShadowProjectile> _projectiles = new List<ShadowProjectile>();
        private const int MaxShotHistory = 1024;
        private readonly ShotStamp[] _shotHistory = new ShotStamp[MaxShotHistory];
        private int _shotHead;
        // When the ring overwrites a live pull, remember the newest eviction
        // time per shooter: at kill-claim time an eviction inside the enemy's
        // lifetime means the shot bound lost pulls and must be flagged.
        private readonly DateTime[] _shotEvictUtc = new DateTime[256];
        // Cells a claimed body overlapped while the server called them
        // solid: the client's sim ran that claim through ITS terrain, so a
        // server-solid overlap was non-blocking there (a dig the server
        // denied or restored still moves on the client). Shared across
        // records - client-proven air is air for every shadow and tracer;
        // absorption is bounded per record by ForkCellBudget.
        private readonly HashSet<long> _provenAirCells = new HashSet<long>();
        private DateTime _lastAdvanceUtc = DateTime.MinValue;

        public ShadowCombatTracker(
            Action<string> log,
            TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, float?> enemyHp,
            Func<int, float?> gunMaxDamage)
        {
            _log = log ?? (_ => { });
            _terrain = terrain;
            _enemyHp = enemyHp;
            _gunMaxDamage = gunMaxDamage;
        }

        /// <summary>Authoritative world lookup, refreshed by the caller per event.</summary>
        public TerrainProbe.TryGetSavedBlockTypeDelegate Terrain
        {
            get => _terrain;
            set => _terrain = value;
        }

        /// <summary>
        /// Resolves a spawn's named target to an announced player id
        /// (policy-side lookup; vanilla resolves the name to a gamer).
        /// Null/unresolved named spawns are logged and skipped.
        /// </summary>
        public Func<string, byte?> ResolveTargetName { get; set; }

        private static long Key(int ownerId, int enemyId) => ((long)ownerId << 32) | (uint)enemyId;

        /// <summary>
        /// Feeds one already-accepted message into the shadows. Parser
        /// exceptions for truncated/malformed payloads are absorbed here -
        /// diagnostics never propagate into gameplay.
        /// </summary>
        public void Observe(byte senderId, string typeName, byte[] data, DateTime now)
        {
            if (data == null)
                return;
            try
            {
                // Advance shadows to the event's timestamp BEFORE handling it:
                // a shot/kill must be evaluated against positions as of when
                // it happened, using the last data accepted before it.
                // Advance is scheduled on >=50ms ticks - dt is computed from
                // each record's own LastUtc, so skipped ticks still advance
                // the full elapsed time on the next run.
                if (now - _lastAdvanceUtc >= AdvanceTickMin)
                {
                    AdvanceAll(now);
                    _lastAdvanceUtc = now;
                }
                switch (typeName)
                {
                    case "DNA.CastleMinerZ.Net.PlayerUpdateMessage":
                        ObservePlayerUpdate(senderId, data, now);
                        break;
                    case "DNA.CastleMinerZ.Net.SpawnEnemyMessage":
                        ObserveSpawn(senderId, data, now);
                        break;
                    // Speed-up is an observed state change, not a guess: the
                    // pending advance runs at the SLOW speed first (the shot
                    // pattern), then the record switches to its spawn-
                    // package FastSpeed. Missing/uncertain records are left
                    // alone - a speed-up never heals an unknown record.
                    case "DNA.CastleMinerZ.Net.SpeedUpEnemyMessage":
                        if (data.Length >= 9)
                        {
                            if (now > _lastAdvanceUtc)
                            {
                                AdvanceAll(now);
                                _lastAdvanceUtc = now;
                            }
                            ShadowEnemy sp = Find(data, 1, 5);
                            if (sp == null || sp.Uncertain)
                                break;
                            // Vanilla only emits SpeedUpEnemyMessage for
                            // HasRunFast types (zombies 0-17) - one on any
                            // other type is fabricated, so the record's
                            // modeled speed can no longer be trusted.
                            if (sp.EnemyType > 17)
                            {
                                MarkUncertain(sp, "speed-up on non-fast type");
                                break;
                            }
                            if (!(sp.FastSpeed > 0f) || float.IsNaN(sp.FastSpeed) ||
                                float.IsInfinity(sp.FastSpeed))
                            {
                                MarkUncertain(sp, "invalid fast speed in spawn package");
                                break;
                            }
                            if (!sp.MovingFast)
                            {
                                sp.MovingFast = true;
                                _log($"[ShadowCombat] enemy {sp.EnemyId} (target {sp.OwnerId}) " +
                                     $"fast mode speed {sp.FastSpeed:F2} (approx)");
                            }
                        }
                        break;
                    // Combat events need positions as of the event, not of the
                    // last scheduled tick - force the pending advance first.
                    case "DNA.CastleMinerZ.Net.GunshotMessage":
                    case "DNA.CastleMinerZ.Net.ShotgunShotMessage":
                        if (now > _lastAdvanceUtc)
                        {
                            AdvanceAll(now);
                            _lastAdvanceUtc = now;
                        }
                        ObserveShot(senderId, data, now,
                            typeName.EndsWith("ShotgunShotMessage", StringComparison.Ordinal));
                        break;
                    case "DNA.CastleMinerZ.Net.KillEnemyMessage":
                        if (now > _lastAdvanceUtc)
                        {
                            AdvanceAll(now);
                            _lastAdvanceUtc = now;
                        }
                        ObserveKill(senderId, data, now);
                        break;
                    // Untracked damage sources: anything our shadows may have
                    // taken is unknown rather than silently missing damage.
                    // DigMessage is deliberately absent: vanilla sends it only
                    // for block dig/place (melee-vs-enemy has NO wire packet -
                    // InGameHUD.Melee calls TakeDamage directly), so a mundane
                    // block edit must not invalidate combat records. Coverage
                    // is still not complete: melee and lava/environmental
                    // damage never cross the wire.
                    case "DNA.CastleMinerZ.Net.MeleePlayerMessage":
                    case "DNA.CastleMinerZ.Net.GrenadeMessage":
                    case "DNA.CastleMinerZ.Net.FireRocketMessage":
                    case "DNA.CastleMinerZ.Net.DetonateGrenadeMessage":
                    case "DNA.CastleMinerZ.Net.DetonateRocketMessage":
                    case "DNA.CastleMinerZ.Net.DetonateExplosiveMessage":
                    case "DNA.CastleMinerZ.Net.DetonateFireballMessage":
                        InvalidateAll(typeName.Substring(typeName.LastIndexOf('.') + 1));
                        break;
                }
                Prune(now);
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
        }

        /// <summary>
        /// Drops a disconnecting player's shadow state: their target data,
        /// records keyed to them as target, and records they reported as
        /// spawn sender.
        /// </summary>
        public void RemovePlayer(byte playerId)
        {
            _players.Remove(playerId);
            _projectiles.RemoveAll(p => p.ShooterId == playerId);
            List<long> owned = null;
            foreach (KeyValuePair<long, ShadowEnemy> kv in _enemies)
                if (kv.Value.OwnerId == playerId || kv.Value.SpawnSenderId == playerId)
                    (owned ??= new List<long>()).Add(kv.Key);
            if (owned != null)
                foreach (long key in owned)
                    _enemies.Remove(key);
        }

        private ShadowEnemy Find(byte[] data, int idOffset, int targetOffset)
        {
            int enemyId = BitConverter.ToInt32(data, idOffset);
            int targetId = BitConverter.ToInt32(data, targetOffset);
            return _enemies.TryGetValue(Key(targetId, enemyId), out ShadowEnemy e) ? e : null;
        }

        private void ObservePlayerUpdate(byte senderId, byte[] data, DateTime now)
        {
            if (data.Length < 16)
                return;
            var pos = new Vec3(
                BitConverter.ToSingle(data, 1),
                BitConverter.ToSingle(data, 5),
                BitConverter.ToSingle(data, 9));
            if (!pos.IsFinite)
                return;
            if (_players.Count >= MaxPlayers && !_players.ContainsKey(senderId))
                PrunePlayers(now);
            if (_players.Count >= MaxPlayers)
                return;
            if (_players.TryGetValue(senderId, out ShadowPlayer existing))
            {
                existing.Pos = pos;
                existing.LastUtc = now;
            }
            else
            {
                _players[senderId] = new ShadowPlayer { Pos = pos, LastUtc = now };
            }
        }

        // [id][SpawnPos:12][SpawnerPos:12][type:1][EnemyID:4][Seed:4]
        // [SpawnValue:4][PlayerName:string][InitPkg:5 x int32 /1000]
        private void ObserveSpawn(byte senderId, byte[] data, DateTime now)
        {
            const int minLen = 1 + 12 + 12 + 1 + 4 + 4 + 4 + 1 + 20; // empty string = 1 len byte
            if (data.Length < minLen)
                return;
            int enemyId = BitConverter.ToInt32(data, 26);
            int enemyType = data[25];
            int seed = BitConverter.ToInt32(data, 30);
            using (var reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1)))
            {
                var pos = new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var spawner = new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                reader.ReadByte();                                              // EnemyTypeID
                reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32();     // id/seed/spawnValue
                string targetName = reader.ReadString();
                float slowSpeed = reader.ReadInt32() / 1000f;                   // InitPkg.SlowSpeed
                float fastSpeed = reader.ReadInt32() / 1000f;                   // InitPkg.FastSpeed
                reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt32();                         // rest of InitPkg

                if (enemyId >= CanaryIdBase)
                    return; // server-planted canary - not a real combat actor
                // Empty target = the sender is the owner; a named target is
                // resolved through the policy callback to the ANNOUNCED
                // player it names - the kill-claim TargetID is that target,
                // so the record must key on it, not the sender.
                int ownerId = senderId;
                if (targetName.Length > 0)
                {
                    byte? resolved = ResolveTargetName?.Invoke(targetName);
                    if (!resolved.HasValue)
                    {
                        _log($"[ShadowCombat] spawn enemy {enemyId} sender {senderId} skipped: " +
                             $"target name '{targetName}' unresolved (none or ambiguous)");
                        return;
                    }
                    ownerId = resolved.Value;
                }
                if (!IsSupportedEnemyType(enemyType))
                {
                    _log($"[ShadowCombat] spawn enemy {enemyId} skipped: unspawnable enemy type {enemyType}");
                    return;
                }
                if (!pos.IsFinite)
                {
                    _log($"[ShadowCombat] spawn enemy {enemyId} skipped: non-finite position");
                    return;
                }
                if (!(slowSpeed > 0f) || float.IsNaN(slowSpeed) || float.IsInfinity(slowSpeed))
                {
                    _log($"[ShadowCombat] spawn enemy {enemyId} skipped: invalid slow speed {slowSpeed}");
                    return;
                }
                float? hp = _enemyHp?.Invoke(enemyType);
                if (hp == null || !(hp.Value > 0f))
                {
                    _log($"[ShadowCombat] spawn enemy {enemyId} skipped: unknown starting health");
                    return;
                }

                long key = Key(ownerId, enemyId);
                if (_enemies.ContainsKey(key))
                    return; // duplicate spawn - must NOT reset estimated HP
                if (_enemies.Count >= MaxEnemies)
                {
                    Prune(now);
                    if (_enemies.Count >= MaxEnemies)
                        return;
                }
                var rec = new ShadowEnemy
                {
                    EnemyId = enemyId,
                    OwnerId = ownerId,
                    SpawnSenderId = senderId,
                    EnemyType = enemyType,
                    RandomSeed = seed,
                    Pos = pos,
                    PreviousPos = pos,
                    PreviousUtc = now,
                    Motion = new ShadowVoxelMotion.MotionState { X = pos.X, Y = pos.Y, Z = pos.Z },
                    StartHp = hp.Value,
                    EstimatedHp = hp.Value,
                    SlowSpeed = slowSpeed,
                    FastSpeed = fastSpeed,
                    Dead = false,
                    DeathUtc = DateTime.MinValue,
                    ZeroCrossUtc = DateTime.MinValue,
                    SpawnedUtc = now,
                    LastUtc = now,
                };
                // A spawn whose claimed body overlaps server-solid cells is a
                // client/server terrain fork, not a bad record: absorb those
                // cells before the first step so the claimed position stands.
                TryAbsorbBodyFork(rec);
                _enemies[key] = rec;
                _log($"[ShadowCombat] spawn shadow enemy {enemyId} type {enemyType} " +
                     $"target {ownerId} sender {senderId} " +
                     $"at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) " +
                     $"spawner ({spawner.X:F1},{spawner.Y:F1},{spawner.Z:F1}) " +
                     $"hp {hp.Value:F1} speed {slowSpeed:F2} seed {seed} (approx)");
            }
        }

        private void ObserveShot(byte senderId, byte[] data, DateTime now, bool shotgun)
        {
            int rays = shotgun ? 5 : 1;
            int itemOffset = shotgun ? 61 : 13;
            if (data.Length < itemOffset + 2)
                return;
            int weaponId = BitConverter.ToInt16(data, itemOffset);
            string weaponName = ItemPolicy.GunNameForId(weaponId);
            float? maxDmg = weaponId > 0 ? _gunMaxDamage?.Invoke(weaponId) : null;

            // One stamp per parsed trigger pull, recorded BEFORE any
            // support/queue check - the wire fact (this client pulled the
            // trigger, with this weapon) is what bounds possible damage at
            // kill-claim time. Each pull carries its own weapon's
            // per-trigger bound, so switching guns mid-fight can't smuggle
            // damage past the summed bound. An overwritten live stamp marks
            // the shooter's history incomplete from the eviction time.
            ShotStamp ss = _shotHistory[_shotHead];
            if (ss == null)
            {
                _shotHistory[_shotHead] = ss = new ShotStamp();
            }
            else if (ss.Utc != DateTime.MinValue)
            {
                _shotEvictUtc[ss.ShooterId & 0xFF] = ss.Utc;
            }
            ss.ShooterId = senderId;
            ss.WeaponId = weaponId;
            ss.MaxDmg = maxDmg ?? -1f;
            ss.Utc = now;
            _shotHead = (_shotHead + 1) % MaxShotHistory;

            // Only ordinary bullet guns have known tracer ballistics
            // (ItemPolicy.TryGetGunBallistics). Lasers, launchers and space
            // weapons are unsupported: invalidate rather than silently miss
            // their damage.
            bool supported = ItemPolicy.TryGetGunBallistics(weaponId, out float speed, out float lifetime) &&
                             weaponId > 0 && maxDmg != null && maxDmg.Value > 0f;
            if (!supported)
            {
                // The volley did real damage client-side that we cannot
                // model - keep the records (their estimates simply omit
                // these hits) rather than discarding every shadow over one
                // gun's missing ballistics.
                _log($"[ShadowCombat] shot weapon {weaponId}" +
                     (weaponName != null ? $" ({weaponName})" : "") +
                     ": unsupported ballistics - volley not modeled (approx)");
                return;
            }

            // Origin = shooter's last accepted position + eye height
            // (approximation - the wire carries no camera position). With no
            // fresh origin the damage cannot be localized at all: drop this
            // volley only - the shadow records stay modeled.
            if (!_players.TryGetValue(senderId, out ShadowPlayer shooter) ||
                (now - shooter.LastUtc) > PlayerStaleAfter)
            {
                _log($"[ShadowCombat] shot weapon {weaponId} shooter {senderId} dropped: " +
                     "no fresh shooter position (approx)");
                return;
            }
            var eye = new Vec3(shooter.Pos.X, shooter.Pos.Y + EyeHeight, shooter.Pos.Z);
            if (!eye.IsFinite || Math.Abs(eye.X) > 1e6f || Math.Abs(eye.Y) > 1e6f || Math.Abs(eye.Z) > 1e6f)
            {
                _log($"[ShadowCombat] shot weapon {weaponId} shooter {senderId} dropped: " +
                     "shooter position out of bounds (approx)");
                return;
            }
            float perHit = shotgun ? maxDmg.Value / 5f : maxDmg.Value;

            // Validate EVERY ray direction before enqueuing any - a partial
            // shotgun volley would silently understate coverage.
            var dirs = new Vec3[rays];
            for (int r = 0; r < rays; r++)
            {
                int off = 1 + r * 12;
                var dir = new Vec3(
                    BitConverter.ToSingle(data, off),
                    BitConverter.ToSingle(data, off + 4),
                    BitConverter.ToSingle(data, off + 8));
                if (!dir.IsFinite || dir.Length < 1e-4f)
                {
                    _log($"[ShadowCombat] shot weapon {weaponId} shooter {senderId} dropped: " +
                         "invalid direction (approx)");
                    return;
                }
                float invLen = 1f / dir.Length;
                dirs[r] = new Vec3(dir.X * invLen, dir.Y * invLen, dir.Z * invLen);
            }

            // The volley enqueues atomically or not at all; a full queue
            // loses this volley's coverage only - records stay modeled.
            if (_projectiles.Count + rays > MaxProjectiles)
            {
                _log($"[ShadowCombat] shot weapon {weaponId} shooter {senderId} dropped: " +
                     $"projectile queue at {MaxProjectiles} - volley coverage lost (approx)");
                return;
            }
            for (int r = 0; r < rays; r++)
            {
                // Vanilla tracer init: Head=Tail=pos+dir*0.5, both velocities
                // dir*weapon.Velocity; tail launches ~0.4s after the shot.
                var spawn = new Vec3(
                    eye.X + dirs[r].X * 0.5f,
                    eye.Y + dirs[r].Y * 0.5f,
                    eye.Z + dirs[r].Z * 0.5f);
                _projectiles.Add(new ShadowProjectile
                {
                    Head = spawn,
                    Tail = spawn,
                    HeadVel = new Vec3(dirs[r].X * speed, dirs[r].Y * speed, dirs[r].Z * speed),
                    TailVel = new Vec3(dirs[r].X * speed, dirs[r].Y * speed, dirs[r].Z * speed),
                    Remaining = lifetime,
                    TailTime = lifetime - 0.2f,
                    Speed = speed,
                    Lifetime = lifetime,
                    ShooterId = senderId,
                    WeaponId = weaponId,
                    PerHit = perHit,
                    QueuedUtc = now,
                    LastUtc = now,
                });
            }
            _log($"[ShadowCombat] shot queued: weapon {weaponId}" +
                 (weaponName != null ? $" ({weaponName})" : "") +
                 $" {rays} ray(s) speed {speed:F0}m/s life {lifetime:F2}s " +
                 $"shooter {senderId} origin ({eye.X:F1},{eye.Y:F1},{eye.Z:F1}) (approx)");
            // With no usable records the volley's coverage is already known
            // to be unavailable - say so at shot time rather than waiting for
            // the tracers to expire (throttled 1/shooter/sec).
            int usable = 0;
            foreach (ShadowEnemy e in _enemies.Values)
                if (!e.Uncertain && !e.Dead && e.EstimatedHp > 0f)
                    usable++;
            if (usable == 0)
            {
                if ((now - shooter.LastMissLogUtc) >= TimeSpan.FromSeconds(1))
                {
                    shooter.LastMissLogUtc = now;
                    _log($"[ShadowCombat] shot weapon {weaponId} shooter {senderId}: " +
                         "no usable shadow records (diagnostic coverage unavailable)");
                }
            }
        }

        private void ObserveKill(byte senderId, byte[] data, DateTime now)
        {
            if (data.Length < 12)
                return;
            int enemyId = BitConverter.ToInt32(data, 1);
            int targetId = BitConverter.ToInt32(data, 5);
            int weaponId = BitConverter.ToInt16(data, 9);
            int killerId = data[11];
            if (_enemies.TryGetValue(Key(targetId, enemyId), out ShadowEnemy e) && !e.Dead)
            {
                int pending = 0;
                foreach (ShadowProjectile p in _projectiles)
                    if (p.ShooterId == killerId)
                        pending++;
                // Wire-fact bound: the killer's accepted trigger pulls
                // inside this enemy's lifetime, priced by each pull's own
                // weapon max. The sum bounds the damage that player alone
                // could have dealt even across weapon switching (co-damage
                // from other players is outside it). "+Nu" = pulls whose
                // weapon had no damage entry; "+ev" = the ring evicted a
                // pull inside the lifetime, so the bound is incomplete.
                int killerShots = 0, unpricedShots = 0;
                float killerMaxDmg = 0f;
                foreach (ShotStamp s in _shotHistory)
                {
                    if (s == null || s.ShooterId != killerId || s.Utc < e.SpawnedUtc)
                        continue;
                    killerShots++;
                    if (s.MaxDmg > 0f)
                        killerMaxDmg += s.MaxDmg;
                    else
                        unpricedShots++;
                }
                bool histGap = _shotEvictUtc[killerId & 0xFF] >= e.SpawnedUtc;
                double lifeSec = (now - e.SpawnedUtc).TotalSeconds;
                _log($"[ShadowCombat] kill claim enemy {enemyId} target {targetId} " +
                     $"reporter {senderId} killer {killerId} weapon {weaponId}: " +
                     $"estHP {e.EstimatedHp:F2}/{e.StartHp:F2} " +
                     $"status {(e.Uncertain ? $"unknown({e.UncertaintyReason})" : "approx")}" +
                     $"{(e.Forked ? " forked" : "")} pendingShots={pending} " +
                     $"lifeSec={lifeSec:F1} killerShots={killerShots} " +
                     $"killerMaxDmg={killerMaxDmg:F1}" +
                     $"{(unpricedShots > 0 ? $"+{unpricedShots}u" : "")}" +
                     $"{(histGap ? "+ev" : "")}");
                // The corpse is kept (see StepProjectile): the killing tracer
                // may still be in flight in our sim when the claim arrives, and
                // must be allowed to debit this enemy rather than a shadow
                // standing behind it. Rays queued after DeathUtc ignore it.
                e.Dead = true;
                e.DeathUtc = now;
            }
            else if (e != null)
            {
                _log($"[ShadowCombat] kill claim enemy {enemyId} target {targetId} " +
                     $"reporter {senderId} killer {killerId}: already dead (duplicate claim)");
            }
            else
            {
                _log($"[ShadowCombat] kill claim enemy {enemyId} target {targetId} " +
                     $"reporter {senderId} killer {killerId}: no shadow record");
            }
        }

        private void InvalidateAll(string reason)
        {
            foreach (ShadowEnemy e in _enemies.Values)
                MarkUncertain(e, reason); // per-record lines only on new transitions
        }

        /// <summary>
        /// Marks a record permanently uncertain with the FIRST reason that
        /// got it there - later causes never overwrite, and the transition
        /// logs exactly one line keyed by target/enemy.
        /// </summary>
        private void MarkUncertain(ShadowEnemy e, string reason)
        {
            if (e.Uncertain)
                return;
            e.Uncertain = true;
            e.UncertaintyReason = reason;
            _log($"[ShadowCombat] enemy {e.EnemyId} (target {e.OwnerId}) now unknown: {reason}");
        }

        /// <summary>
        /// Advances every live shadow toward its target's latest accepted
        /// position through ShadowVoxelMotion's bounded voxel sweep: chase is
        /// horizontal (a distant higher/lower target still attracts), gravity
        /// drops unsupported bodies onto the next solid plane, and ordinary
        /// wall/floor contact slides or stops the body. Inside vanilla's
        /// ~1m attack range the zombie switches to the stationary attack or
        /// dig state, so a higher/lower close target does NOT invalidate the
        /// record - the step simply settles gravity with no chase desire.
        /// Only states the model cannot prove (stale/missing target, big time
        /// gap, unknown terrain cells, embedded or non-finite bodies, or
        /// query-budget overrun) mark the record uncertain.
        /// </summary>
        private void AdvanceAll(DateTime now)
        {
            foreach (ShadowEnemy e in _enemies.Values)
            {
                if (e.Uncertain || e.Dead)
                    continue; // dead records stay frozen for in-flight tracers
                double dt = (now - e.LastUtc).TotalSeconds;
                if (dt <= 0.0)
                    continue;
                if (dt > MaxAdvanceSeconds)
                {
                    MarkUncertain(e, $"observation gap {dt:F1}s"); // permanent
                    continue;
                }
                if (!_players.TryGetValue((byte)e.OwnerId, out ShadowPlayer target))
                {
                    MarkUncertain(e, "target position missing");
                    continue;
                }
                if ((now - target.LastUtc) > PlayerStaleAfter)
                {
                    MarkUncertain(e, "target position stale >2s");
                    continue;
                }

                float dx = target.Pos.X - e.Pos.X;
                float dz = target.Pos.Z - e.Pos.Z;
                float hd = (float)Math.Sqrt(dx * dx + dz * dz);
                float dvx = 0f, dvz = 0f;
                if (hd > StopDistance)
                {
                    // Unit direction is fixed for the event (the target
                    // position is the latest accepted one, so it doesn't move
                    // mid-advance); desired speed is capped so total travel
                    // this event cannot overshoot vanilla's ~1m stop range.
                    // Speed is the spawn package's slow/fast claim - fast only
                    // engages after an observed SpeedUpEnemyMessage.
                    float allowed = hd - StopDistance;
                    float speed = Math.Min(e.MovingFast ? e.FastSpeed : e.SlowSpeed,
                                           allowed / (float)dt);
                    // Archers (types 18-25) hold position to shoot once the
                    // target is inside 35m with a clear terrain trace; a
                    // blocked or unproven trace keeps them chasing.
                    if (e.EnemyType >= 18 && e.EnemyType <= 25 &&
                        hd <= ArcherHoldRange && HasClearArcherSight(e, target))
                        speed = 0f;
                    dvx = dx / hd * speed;
                    dvz = dz / hd * speed;
                }
                // hd <= StopDistance: vanilla enters the stationary attack or
                // dig state (ZombieChase ~81-92; both grounded states zero
                // velocity). Elevation does not invalidate the record; the
                // step still settles gravity and checks the body.

                // EType.FastJumpSpeed for zombie types 0..17: 13 + 3*type/16.
                // TryStep's broadphase budget is per call; split quiet-gap
                // advances into <=0.5s chunks so a legitimate 1s interval gets
                // the same bounded work as normal traffic instead of tripping
                // the budget solely because packets were sparse.
                var terr = _terrain == null ? (TerrainProbe.TryGetSavedBlockTypeDelegate)null
                                            : new TerrainProbe.TryGetSavedBlockTypeDelegate(ProbeTerrain);
                double remaining = dt;
                string mReason = null;
                while (remaining > 0.0)
                {
                    float stepDt = (float)Math.Min(remaining, 0.5);
                    if (ShadowVoxelMotion.TryStep(
                            ref e.Motion, dvx, dvz, stepDt, terr, IsSolid, out mReason,
                            e.MovingFast,
                            e.MovingFast ? 13f + 3f * (e.EnemyType / 16f) : 10f))
                    {
                        remaining -= stepDt;
                        continue;
                    }
                    // A body embed is not proof of anything: the client's
                    // sim ran this spawn through ITS terrain. Cells the
                    // claimed body overlaps join the proven-air overlay and
                    // the step retries once with them as air; anything else
                    // stays an unprovable unknown.
                    if (mReason != null &&
                        mReason.StartsWith("embedded", StringComparison.Ordinal) &&
                        TryAbsorbBodyFork(e) &&
                        ShadowVoxelMotion.TryStep(
                            ref e.Motion, dvx, dvz, stepDt, terr, IsSolid, out mReason,
                            e.MovingFast,
                            e.MovingFast ? 13f + 3f * (e.EnemyType / 16f) : 10f))
                    {
                        remaining -= stepDt;
                        continue;
                    }
                    MarkUncertain(e, mReason);
                    break;
                }
                if (mReason != null || e.Uncertain)
                    continue;
                // Keep the last two position snapshots so projectiles can be
                // intersected against the enemy's location at the bullet's own
                // substep time rather than the latest tick position.
                e.PreviousPos = e.Pos;
                e.PreviousUtc = e.LastUtc;
                e.Pos = new Vec3(e.Motion.X, e.Motion.Y, e.Motion.Z);
                e.LastUtc = now;
            }
            AdvanceProjectiles(now);
        }

        private static bool IsSolid(int blockType)
        {
            return ItemPolicy.Ready ? ItemPolicy.IsPlayerBlockingBlockType(blockType) : blockType != 0;
        }

        private static long CellKey(int x, int y, int z) =>
            (x & 0x1FFFFFL) | (((long)y & 0x1FFFFFL) << 21) | (((long)z & 0x1FFFFFL) << 42);

        /// <summary>
        /// ArcherSkeletonChase's attack precondition: a terrain trace from
        /// the archer's eye (+1.5m) to the target's eye (+1.5m) must be
        /// clear before the archer holds to shoot. Samples the segment via
        /// the overlay-aware probe (client-proven air is air here too); an
        /// unknown cell cannot prove clear, so it blocks the hold and the
        /// record keeps chasing - matching vanilla only stopping on a
        /// provably-clear trace.
        /// </summary>
        private bool HasClearArcherSight(ShadowEnemy e, ShadowPlayer target)
        {
            float ax = e.Motion.X, ay = e.Motion.Y + 1.5f, az = e.Motion.Z;
            float bx = target.Pos.X, by = target.Pos.Y + 1.5f, bz = target.Pos.Z;
            float dx = bx - ax, dy = by - ay, dz = bz - az;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            // ~0.45m sampling approximates the voxel trace - fine for a
            // hold/chase boundary that is itself an approximation.
            int steps = Math.Max(1, (int)(dist / 0.45f));
            for (int i = 1; i < steps; i++)
            {
                float t = (float)i / steps;
                int cx = (int)Math.Floor(ax + dx * t);
                int cy = (int)Math.Floor(ay + dy * t);
                int cz = (int)Math.Floor(az + dz * t);
                if (!ProbeTerrain(cx, cy, cz, out int cell) || IsSolid(cell))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Terrain lookup with the fork overlay applied: cells a claimed
        /// body proved non-blocking on the client's terrain read as air.
        /// </summary>
        private bool ProbeTerrain(int x, int y, int z, out int t)
        {
            if (_provenAirCells.Count != 0 && _provenAirCells.Contains(CellKey(x, y, z)))
            {
                t = 0;
                return true;
            }
            t = 0;
            return _terrain != null && _terrain(x, y, z, out t);
        }

        /// <summary>
        /// The claimed body overlapped cells the server's terrain calls
        /// solid. The client's sim ran this spawn through ITS terrain where
        /// they were non-blocking (the spawn pickers guarantee air there; a
        /// dig the server denied or restored still exists as air client-
        /// side). Absorb provably-overlapped solid cells into the shared
        /// proven-air set - bounded per record by ForkCellBudget - and keep
        /// the client's claimed position instead of snapping to a surface
        /// the client does not have. An unresolvable cell stays unprovable.
        /// Returns true when the body is fully explainable this way.
        /// </summary>
        private bool TryAbsorbBodyFork(ShadowEnemy e)
        {
            int overlapped = 0, added = 0;
            bool provable = true;
            ShadowVoxelMotion.ForEachBodyCell(e.Motion.X, e.Motion.Y, e.Motion.Z, (x, y, z) =>
            {
                if (_terrain == null || !_terrain(x, y, z, out int t))
                {
                    provable = false;
                    return false;
                }
                if (!IsSolid(t))
                    return true;
                overlapped++;
                long k = CellKey(x, y, z);
                if (_provenAirCells.Contains(k))
                    return true;
                if (e.ForkCellCount >= ForkCellBudget)
                {
                    provable = false;
                    return false;
                }
                e.ForkCellCount++;
                _provenAirCells.Add(k);
                added++;
                return true;
            });
            if (overlapped > 0)
                e.Forked = true;
            if (provable && overlapped > 0)
                _log($"[ShadowCombat] enemy {e.EnemyId} (target {e.OwnerId}) claimed body " +
                     $"overlaps {overlapped} server-solid cell(s) - client/server terrain " +
                     "fork; tracking the claimed position (approx; not a violation)");
            return provable && overlapped > 0;
        }

        private void Prune(DateTime now)
        {
            List<long> expired = null;
            foreach (KeyValuePair<long, ShadowEnemy> kv in _enemies)
            {
                ShadowEnemy e = kv.Value;
                // Corpses are retained just long enough for every tracer
                // queued before the death to finish its <=2 s flight; after
                // that they can no longer be hit and are dropped.
                if (now - e.SpawnedUtc > EnemyTtl ||
                    (e.Dead && (now - e.DeathUtc).TotalSeconds > CorpseRetentionSec))
                    (expired ??= new List<long>()).Add(kv.Key);
            }
            if (expired != null)
                foreach (long key in expired)
                {
                    ShadowEnemy e = _enemies[key];
                    _log($"[ShadowCombat] enemy {e.EnemyId} (target {e.OwnerId}) expired: " +
                         (e.Dead
                             ? $"corpse retention elapsed ({CorpseRetentionSec:F0}s post-claim)"
                             : $"{EnemyTtl.TotalSeconds:F0}s diagnostic lifetime"));
                    _enemies.Remove(key);
                }
            PrunePlayers(now);
        }

        private void PrunePlayers(DateTime now)
        {
            List<byte> stale = null;
            foreach (KeyValuePair<byte, ShadowPlayer> kv in _players)
                if (now - kv.Value.LastUtc > PlayerStaleAfter)
                    (stale ??= new List<byte>()).Add(kv.Key);
            if (stale != null)
                foreach (byte id in stale)
                    _players.Remove(id);
        }
    }
}
