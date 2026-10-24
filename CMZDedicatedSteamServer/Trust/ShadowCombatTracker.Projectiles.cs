/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;

namespace CMZDedicatedSteamServer.Trust
{
    /// <summary>
    /// Ballistic half of the shadow tracker. Accepted shots queue vanilla
    /// tracer rays (TracerManager.Tracer: Head=Tail=pos+dir*0.5, both
    /// velocities dir*Velocity, Y gravity 10, tail launch delay) instead of
    /// debiting estimated HP at shot time. AdvanceProjectiles steps them at
    /// bounded substeps against lerped shadow positions and deterministic
    /// voxel DDA terrain. Everything stays diagnostic - a bullet that cannot
    /// be fully modeled is abandoned, and ambiguity marks shadows unknown
    /// instead of producing a guessed hit.
    /// </summary>
    internal sealed partial class ShadowCombatTracker
    {
        private const double ProjectileSubstepSeconds = 1.0 / 60.0;
        private const int MaxProjectileSubsteps = 32;
        private const int MaxDdaCellsPerSegment = 512;
        private const int MaxTerrainQueriesPerBullet = 4096;
        private const int MaxTerrainCacheCells = 4096;

        private sealed class ShadowProjectile
        {
            public Vec3 Head, Tail;          // tracer endpoints (vanilla Init: pos+dir*0.5)
            public Vec3 HeadVel, TailVel;    // per-end velocities, gravity integrated
            public float Remaining;          // seconds of flight left
            public float TailTime;           // vanilla: FlightTime - 0.2
            public float Speed, Lifetime;    // weapon metadata (diagnostics)
            public float PerHit;             // bounded per-ray damage
            public byte ShooterId;
            public int WeaponId;
            public DateTime QueuedUtc;  // shot packet time - corpse eligibility key
            public DateTime LastUtc;
        }

        private enum CellEvent { None, Solid, Unknown }

        /// <summary>
        /// Advances every queued tracer from LastUtc to now at <=1/60 s
        /// substeps (vanilla ordering: expire, head move+gravity, delayed tail
        /// move+gravity, trace tail->head). Called at the end of AdvanceAll so
        /// bullets and enemies share the same tick timestamps. A projectile
        /// that survives keeps flying; any collision or unmodelable state
        /// removes it.
        /// </summary>
        private void AdvanceProjectiles(DateTime now)
        {
            if (_projectiles.Count == 0)
                return;
            // World snapshot is stable within one event - cache resolved
            // cells so overlapping bullet trails don't re-probe them.
            var cache = new Dictionary<long, int>();
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                ShadowProjectile p = _projectiles[i];
                double dt = (now - p.LastUtc).TotalSeconds;
                if (dt <= 0.0)
                    continue; // same tick or a backward event - keep LastUtc
                if (dt > MaxProjectileAdvanceSeconds)
                {
                    // Hits inside this gap cannot be reconstructed for THIS
                    // tracer - drop it. The shadow records stay modeled: a
                    // quiet period between shots is ordinary traffic, not
                    // a coverage failure for every enemy.
                    _projectiles.RemoveAt(i);
                    _log($"[ShadowCombat] tracer weapon {p.WeaponId} shooter {p.ShooterId} " +
                         $"dropped: {dt:F1}s observation gap mid-flight - " +
                         "possible hits unobserved (approx)");
                    continue;
                }
                int substeps = Math.Min(MaxProjectileSubsteps,
                                        Math.Max(1, (int)Math.Ceiling(dt / ProjectileSubstepSeconds)));
                float sdt = (float)(dt / substeps);
                int queries = 0;
                DateTime startUtc = p.LastUtc;
                bool alive = true;
                for (int s = 0; s < substeps && alive; s++)
                {
                    DateTime tEnd = startUtc + TimeSpan.FromSeconds(sdt * (s + 1));
                    alive = StepProjectile(p, sdt, tEnd, now, cache, ref queries);
                }
                if (alive)
                    p.LastUtc = now;
                else
                    _projectiles.RemoveAt(i);
            }
        }

        /// <summary>One tracer substep. Returns false when the projectile is consumed.</summary>
        private bool StepProjectile(ShadowProjectile p, float sdt, DateTime tEnd,
                                    DateTime now, Dictionary<long, int> cache, ref int queries)
        {
            p.Remaining -= sdt;
            if (p.Remaining < 0f)
            {
                LogShotMiss(p, now, "expired in flight");
                return false;
            }
            p.Head = new Vec3(p.Head.X + p.HeadVel.X * sdt,
                              p.Head.Y + p.HeadVel.Y * sdt,
                              p.Head.Z + p.HeadVel.Z * sdt);
            p.HeadVel = new Vec3(p.HeadVel.X, p.HeadVel.Y - 10f * sdt, p.HeadVel.Z);
            if (p.Remaining < p.TailTime - 0.2f)
            {
                p.Tail = new Vec3(p.Tail.X + p.TailVel.X * sdt,
                                  p.Tail.Y + p.TailVel.Y * sdt,
                                  p.Tail.Z + p.TailVel.Z * sdt);
                p.TailVel = new Vec3(p.TailVel.X, p.TailVel.Y - 10f * sdt, p.TailVel.Z);
            }
            if (!p.Head.IsFinite || !p.Tail.IsFinite ||
                Math.Abs(p.Head.X) > 1e6f || Math.Abs(p.Head.Y) > 1e6f || Math.Abs(p.Head.Z) > 1e6f)
            {
                _log($"[ShadowCombat] tracer weapon {p.WeaponId} shooter {p.ShooterId} " +
                     "dropped: left modelable bounds");
                return false;
            }
            float dx = p.Head.X - p.Tail.X, dy = p.Head.Y - p.Tail.Y, dz = p.Head.Z - p.Tail.Z;
            float segLen = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (segLen < 1e-6f)
                return true; // tail not launched yet - no segment to trace
            var segDir = new Vec3(dx / segLen, dy / segLen, dz / segLen);

            // An uncertain record's last-known body still potentially blocks
            // the tracer - passing through it would fabricate a clean miss,
            // so the projectile becomes unmodelable instead. A record that
            // did not exist at bullet time can neither block nor be hit, and
            // neither can a corpse for rays queued after its death.
            foreach (ShadowEnemy e in _enemies.Values)
            {
                if (!e.Uncertain || tEnd < e.SpawnedUtc ||
                    (e.Dead && p.QueuedUtc > e.DeathUtc))
                    continue;
                if (SegmentAabb(p.Tail, segDir, segLen, e.Pos) >= 0f)
                {
                    LogShotMiss(p, now, "crossed unmodelable shadow - coverage lost");
                    return false;
                }
            }

            // Nearest eligible shadow body along the segment, interpolated to
            // the substep's own end time from the last two position snapshots.
            // A dead enemy remains a valid candidate for rays that were already
            // in flight when the kill claim arrived (the client saw the hit
            // before it sent the claim); rays queued after the death pass
            // through the corpse as if it were gone, matching vanilla where
            // the dead enemy leaves the hittable set.
            ShadowEnemy best = null;
            Vec3 bestPos = default(Vec3);
            float bestT = float.MaxValue;
            foreach (ShadowEnemy e in _enemies.Values)
            {
                // EstimatedHp <= 0 means the real enemy died client-side and
                // its kill claim is in flight or was eaten upstream - the
                // record stays for the claim but leaves the hittable set so
                // it cannot keep soaking rays meant for other shadows.
                if (e.Uncertain || e.EstimatedHp <= 0f ||
                    (e.Dead && p.QueuedUtc > e.DeathUtc))
                    continue;
                PosResult pr = EnemyPosAt(e, tEnd, out Vec3 epos);
                if (pr != PosResult.Ok)
                    // PreSpawn: the enemy did not exist at bullet time. NoHistory:
                    // this enemy's snapshot window does not reach that far back -
                    // either way no hit may be fabricated for it, but other
                    // records and their own validity are unaffected.
                    continue;
                float t = SegmentAabb(p.Tail, segDir, segLen, epos);
                if (t >= 0f && t < bestT)
                {
                    best = e;
                    bestPos = epos;
                    bestT = t;
                }
            }

            bool budgetHit = false;
            CellEvent terrain = DdaSegment(p.Tail, segDir, segLen, cache, ref queries,
                                           out float terrainT, out budgetHit);
            if (budgetHit)
            {
                LogShotMiss(p, now, "terrain query budget exceeded - coverage unavailable");
                return false;
            }
            if (terrain == CellEvent.Unknown && (best == null || terrainT <= bestT))
            {
                if (best != null)
                    MarkUncertain(best, "projectile terrain unknown");
                else
                    // An unresolvable cell on the path with no known body in
                    // reach - this tracer's coverage cannot be localized,
                    // but the records keep modeling.
                    LogShotMiss(p, now, "unresolvable terrain on path - coverage unavailable");
                return false;
            }
            if (terrain == CellEvent.Solid && (best == null || terrainT <= bestT))
            {
                LogShotMiss(p, now, "terrain blocked tracer");
                return false;
            }
            if (best != null)
            {
                // Body validity at IMPACT position (the interpolated cell set
                // the segment met), not merely the latest proven position.
                var impactBody = best.Motion;
                impactBody.X = bestPos.X;
                impactBody.Y = bestPos.Y;
                impactBody.Z = bestPos.Z;
                if (!ShadowVoxelMotion.TryValidateBody(impactBody, ProbeTerrain, IsSolid, out string bodySup))
                {
                    MarkUncertain(best, $"body unverifiable at impact: {bodySup}");
                    return false;
                }
                // First body before any blocking terrain: debit exactly once.
                float elapsed = p.Lifetime - p.Remaining;
                best.EstimatedHp -= p.PerHit;
                // Stamp the zero crossing for diagnostics. The eligibility
                // check already removed the record from the hittable set at
                // this point, so nothing else can debit it before its kill
                // claim resolves.
                if (best.EstimatedHp <= 0f && best.ZeroCrossUtc == DateTime.MinValue)
                    best.ZeroCrossUtc = now;
                _log($"[ShadowCombat] shot weapon {p.WeaponId} estimated projectile intersection " +
                     $"enemy {best.EnemyId} (target {best.OwnerId}) type {best.EnemyType}: " +
                     $"dist {bestT:F2}m elapsed {elapsed:F2}s " +
                     $"estHP {best.EstimatedHp:F2}/{best.StartHp:F2} " +
                     $"debit {p.PerHit:F2} (approximate)");
                return false;
            }
            return true;
        }

        private enum PosResult { Ok, PreSpawn, NoHistory }

        /// <summary>
        /// Enemy position at bullet time: lerp between the last two position
        /// snapshots, current Pos at/past the latest. PreSpawn = the record
        /// did not exist yet (caller skips it entirely); NoHistory = it
        /// existed but the bullet's window precedes its snapshot history
        /// (cannot localize - never a guessed hit).
        /// </summary>
        private static PosResult EnemyPosAt(ShadowEnemy e, DateTime tEnd, out Vec3 pos)
        {
            pos = e.Pos;
            if (tEnd < e.SpawnedUtc)
                return PosResult.PreSpawn;
            if (e.LastUtc <= e.PreviousUtc || tEnd >= e.LastUtc)
                return PosResult.Ok;
            if (tEnd < e.PreviousUtc)
                return PosResult.NoHistory;
            float f = (float)((tEnd - e.PreviousUtc).TotalSeconds /
                              (e.LastUtc - e.PreviousUtc).TotalSeconds);
            pos = new Vec3(e.PreviousPos.X + (e.Pos.X - e.PreviousPos.X) * f,
                           e.PreviousPos.Y + (e.Pos.Y - e.PreviousPos.Y) * f,
                           e.PreviousPos.Z + (e.Pos.Z - e.PreviousPos.Z) * f);
            return PosResult.Ok;
        }

        /// <summary>
        /// Slab test of a segment (start + dir*t, t in [0,maxT]) against the
        /// approximate zombie AABB at boxPos. Returns entry t or -1.
        /// </summary>
        private static float SegmentAabb(Vec3 o, Vec3 d, float maxT, Vec3 boxPos)
        {
            float tmin = 0f, tmax = maxT;
            float[] mn = { boxPos.X - ZombieHalfWidth, boxPos.Y, boxPos.Z - ZombieHalfWidth };
            float[] mx = { boxPos.X + ZombieHalfWidth, boxPos.Y + ZombieHeight, boxPos.Z + ZombieHalfWidth };
            float[] oo = { o.X, o.Y, o.Z };
            float[] dd = { d.X, d.Y, d.Z };
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(dd[i]) < 1e-8f)
                {
                    if (oo[i] < mn[i] || oo[i] > mx[i])
                        return -1f;
                    continue;
                }
                float inv = 1f / dd[i];
                float t0 = (mn[i] - oo[i]) * inv, t1 = (mx[i] - oo[i]) * inv;
                if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }
                tmin = Math.Max(tmin, t0);
                tmax = Math.Min(tmax, t1);
                if (tmin > tmax)
                    return -1f;
            }
            return tmin;
        }

        /// <summary>
        /// Bounded voxel DDA (Amanatides-Woo, supercover) from start along
        /// dir up to maxDist. Checks the start cell, then at each boundary
        /// crossing probes EVERY cell the segment touches: on a corner tie
        /// (axes whose tMax are equal within 1e-6) all nonempty subsets of
        /// the tied axes are resolved at the same entry distance (cache
        /// dedupes repeats) before all tied axes advance together. This is
        /// conservative at exact grazes - a segment that clips a cell corner
        /// counts that cell. Reports the first solid or unresolvable cell
        /// with its entry distance. Capped at MaxDdaCellsPerSegment cells
        /// per call and MaxTerrainQueriesPerBullet real probes per bullet
        /// advance - no infinite walks on any input.
        /// </summary>
        private CellEvent DdaSegment(Vec3 start, Vec3 dir, float maxDist,
                                     Dictionary<long, int> cache, ref int queries,
                                     out float hitT, out bool budgetHit)
        {
            hitT = 0f;
            budgetHit = false;
            int cx = (int)Math.Floor(start.X);
            int cy = (int)Math.Floor(start.Y);
            int cz = (int)Math.Floor(start.Z);
            int stepX = dir.X > 1e-9f ? 1 : (dir.X < -1e-9f ? -1 : 0);
            int stepY = dir.Y > 1e-9f ? 1 : (dir.Y < -1e-9f ? -1 : 0);
            int stepZ = dir.Z > 1e-9f ? 1 : (dir.Z < -1e-9f ? -1 : 0);
            float tMaxX = stepX > 0 ? (cx + 1 - start.X) / dir.X
                        : stepX < 0 ? (cx - start.X) / dir.X : float.MaxValue;
            float tMaxY = stepY > 0 ? (cy + 1 - start.Y) / dir.Y
                        : stepY < 0 ? (cy - start.Y) / dir.Y : float.MaxValue;
            float tMaxZ = stepZ > 0 ? (cz + 1 - start.Z) / dir.Z
                        : stepZ < 0 ? (cz - start.Z) / dir.Z : float.MaxValue;
            float tDeltaX = stepX != 0 ? Math.Abs(1f / dir.X) : float.MaxValue;
            float tDeltaY = stepY != 0 ? Math.Abs(1f / dir.Y) : float.MaxValue;
            float tDeltaZ = stepZ != 0 ? Math.Abs(1f / dir.Z) : float.MaxValue;

            float t = 0f;
            for (int walked = 0; walked < MaxDdaCellsPerSegment; walked++)
            {
                int cell = ResolveCell(cx, cy, cz, cache, ref queries);
                if (cell == -2)
                {
                    budgetHit = true;
                    return CellEvent.Unknown;
                }
                if (cell == -1)
                {
                    hitT = t;
                    return CellEvent.Unknown;
                }
                if (IsSolid(cell))
                {
                    hitT = t;
                    return CellEvent.Solid;
                }
                float tNext = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                if (tNext > maxDist || tNext == float.MaxValue)
                    return CellEvent.None;
                bool tx = Math.Abs(tMaxX - tNext) < 1e-6f;
                bool ty = Math.Abs(tMaxY - tNext) < 1e-6f;
                bool tz = Math.Abs(tMaxZ - tNext) < 1e-6f;
                int nTied = (tx ? 1 : 0) + (ty ? 1 : 0) + (tz ? 1 : 0);
                if (nTied > 1)
                {
                    // Corner graze: probe every cell sharing the corner -
                    // all nonempty subsets of the tied axes (2..7 cells).
                    CellEvent ev = ProbeTieCells(cx, cy, cz, stepX, stepY, stepZ,
                                                 tx, ty, tz, cache, ref queries,
                                                 out bool qBudget);
                    if (qBudget)
                    {
                        budgetHit = true;
                        return CellEvent.Unknown;
                    }
                    if (ev != CellEvent.None)
                    {
                        hitT = tNext;
                        return ev;
                    }
                }
                t = tNext;
                if (tx) { tMaxX += tDeltaX; cx += stepX; }
                if (ty) { tMaxY += tDeltaY; cy += stepY; }
                if (tz) { tMaxZ += tDeltaZ; cz += stepZ; }
            }
            budgetHit = true; // segment longer than the walk bound
            return CellEvent.Unknown;
        }

        /// <summary>
        /// At a boundary crossing shared by 2+ axes, resolve every cell the
        /// corner touches: all nonempty subsets of the tied axes applied to
        /// the base cell. Returns the first solid/unknown found (both are
        /// conservative stops); None when every touched cell is clear.
        /// </summary>
        private CellEvent ProbeTieCells(int cx, int cy, int cz,
                                      int stepX, int stepY, int stepZ,
                                      bool tx, bool ty, bool tz,
                                      Dictionary<long, int> cache, ref int queries,
                                      out bool budgetHit)
        {
            budgetHit = false;
            bool unknown = false;
            // Axis masks: bit0=x, bit1=y, bit2=z; subsets of the tied axes.
            int tiedMask = (tx ? 1 : 0) | (ty ? 2 : 0) | (tz ? 4 : 0);
            for (int m = 1; m < 8; m++)
            {
                if ((m & tiedMask) != m)
                    continue; // only subsets built from tied axes
                int qx = cx + ((m & 1) != 0 ? stepX : 0);
                int qy = cy + ((m & 2) != 0 ? stepY : 0);
                int qz = cz + ((m & 4) != 0 ? stepZ : 0);
                if (qx == cx && qy == cy && qz == cz)
                    continue;
                int cell = ResolveCell(qx, qy, qz, cache, ref queries);
                if (cell == -2)
                {
                    budgetHit = true;
                    return CellEvent.Unknown;
                }
                if (cell == -1)
                    unknown = true; // keep probing - a solid cell outranks unknown
                else if (IsSolid(cell))
                    return CellEvent.Solid;
            }
            return unknown ? CellEvent.Unknown : CellEvent.None;
        }

        /// <summary>
        /// Resolve one voxel through the per-call cache, counting real probes
        /// against the bullet's budget. -2 = budget exhausted, -1 = cell
        /// unresolvable, otherwise the block type.
        /// </summary>
        private int ResolveCell(int x, int y, int z, Dictionary<long, int> cache, ref int queries)
        {
            long k = (x & 0x1FFFFFL) | (((long)y & 0x1FFFFFL) << 21) | (((long)z & 0x1FFFFFL) << 42);
            if (cache.TryGetValue(k, out int cached))
                return cached;
            if (++queries > MaxTerrainQueriesPerBullet)
                return -2;
            if (!ProbeTerrain(x, y, z, out int bt))
                return -1;
            if (cache.Count < MaxTerrainCacheCells)
                cache[k] = bt;
            return bt;
        }

        /// <summary>
        /// Bounded in-flight outcome diagnostic - at most 1/shooter/sec.
        /// Miss text distinguishes "records existed but the tracer met none"
        /// from "nothing usable was being modeled".
        /// </summary>
        private void LogShotMiss(ShadowProjectile p, DateTime now, string outcome)
        {
            if (_players.TryGetValue(p.ShooterId, out ShadowPlayer sp))
            {
                if ((now - sp.LastMissLogUtc) < TimeSpan.FromSeconds(1))
                    return;
                sp.LastMissLogUtc = now;
            }
            int usable = 0;
            foreach (ShadowEnemy e in _enemies.Values)
                if (!e.Uncertain && e.EstimatedHp > 0f)
                    usable++;
            _log($"[ShadowCombat] shot weapon {p.WeaponId} shooter {p.ShooterId}: " +
                 (usable > 0 ? $"ray missed modeled enemies ({outcome})"
                             : $"no usable shadow records ({outcome})") +
                 " (approx; not a violation)");
        }
    }
}
