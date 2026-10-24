/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;

namespace CMZDedicatedSteamServer.Trust
{
    /// <summary>
    /// Approximate voxel-body motion for the diagnostic shadow model
    /// (ShadowCombatTracker - output is observability only, never enforcement).
    /// Not a port of vanilla ResolveCollsion: it uses an explicit bounded
    /// axis-sweep approximation over integer world cells.
    ///
    /// Model (constants from read-only vanilla source):
    /// - Body: vanilla PlayerAABB (-0.35, 0, -0.35)..(0.35, 1.65, 0.35),
    ///   feet-anchored at the state position.
    /// - Gravity: BasicPhysics.Gravity = (0, -20, 0), integrated as
    ///   vel.Y += -20*dt inside <=0.05 s substeps.
    /// - ZombieChase: desired horizontal speed applies fully while grounded
    ///   and at 0.5x while airborne (full while fast); OnGround &&
    ///   TouchingWall adds the jump impulse (slow +10, fast = the
    ///   type-derived EType.FastJumpSpeed supplied by the caller).
    /// - Per substep the body sweeps Y, then X, then Z (axis-separated
    ///   approximation - slides along whichever axis is free, not a true
    ///   vector sweep). A downward Y clamp sets Grounded and zeroes vy, an
    ///   upward clamp zeroes vy, and an X/Z clamp sets TouchingWall for the
    ///   next substep/step. Ordinary floor/wall contact is NOT an unknown.
    ///
    /// Uncertainty (false + reason) is only for states the model cannot
    /// prove: non-finite values, unresolvable probe cells, a body buried
    /// deeper than its bottom cell layer, or work that would exceed the
    /// query budget. A bottom-layer-only overlap snaps the feet onto that
    /// layer's top face instead (spawn floor disagreement/emerge overlap).
    /// Failed steps leave the position untouched apart from that snap.
    /// </summary>
    internal static class ShadowVoxelMotion
    {
        internal struct MotionState
        {
            public float X, Y, Z;      // feet position
            public float VelocityY;
            public bool Grounded;
            public bool TouchingWall;
        }

        internal const float HalfWidth = 0.35f;  // PlayerAABB x/z half extent
        internal const float Height = 1.65f;     // PlayerAABB y extent
        private const float Gravity = -20f;      // BasicPhysics.Gravity.Y
        private const float Substep = 0.05f;     // bounded integration step
        private const float Eps = 0.001f;        // strict-overlap epsilon
        private const float AirborneSpeedScale = 0.5f; // ZombieChase !OnGround slow factor
        private const float SupportProbe = 0.02f; // downward ground-contact probe
        private const int MaxQueries = 512;      // broadphase budget per TryStep

        /// <summary>
        /// Proves the current body is computable: finite, every cell it
        /// overlaps resolves, and none is solid. Support is NOT required -
        /// a body in known air is valid (it may simply be falling).
        /// </summary>
        internal static bool TryValidateBody(
            MotionState s, TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, bool> solid, out string reason)
        {
            reason = null;
            if (terrain == null)
            {
                reason = "terrain probe unavailable";
                return false;
            }
            if (!IsFinite(s.X) || !IsFinite(s.Y) || !IsFinite(s.Z) || !IsFinite(s.VelocityY))
            {
                reason = "non-finite motion state";
                return false;
            }
            int queries = 0;
            if (CheckBodyCells(s.X, s.Y, s.Z, terrain, solid, ref queries, out reason))
                return true;
            return TrySnapShallowEmbed(ref s, terrain, solid, ref queries, out reason);
        }

        /// <summary>
        /// Advances the shadow body by one event interval. On false the state
        /// is unmodified and reason names the first unprovable cell/cause.
        /// fast mirrors ZombieChase IsMovingFast: airborne chase keeps full
        /// speed instead of the slow 0.5 factor, and the grounded wall-jump
        /// uses jumpImpulse (EType.FastJumpSpeed) rather than the slow +10.
        /// </summary>
        internal static bool TryStep(
            ref MotionState state, float desiredVX, float desiredVZ, float dt,
            TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, bool> solid, out string reason,
            bool fast = false, float jumpImpulse = 10f)
        {
            reason = null;
            if (terrain == null)
            {
                reason = "terrain probe unavailable";
                return false;
            }
            if (!IsFinite(state.X) || !IsFinite(state.Y) || !IsFinite(state.Z) || !IsFinite(state.VelocityY))
            {
                reason = "non-finite motion state";
                return false;
            }
            if (!IsFinite(desiredVX) || !IsFinite(desiredVZ) || !IsFinite(dt) || dt < 0f)
            {
                reason = "non-finite step input";
                return false;
            }
            if (!IsFinite(jumpImpulse) || jumpImpulse <= 0f)
            {
                reason = "invalid jump impulse";
                return false;
            }

            int queries = 0;
            // An already-embedded body is unprovable unless every solid
            // overlaps only the bottom cell layer: vanilla's spawn pickers
            // guarantee a two-air column, the emerge animation starts the
            // body overlapped with the ground, and client/server cave
            // floors disagree by a cell. That pattern snaps the feet onto
            // the layer's top face instead of discarding the record.
            if (!CheckBodyCells(state.X, state.Y, state.Z, terrain, solid, ref queries, out reason))
            {
                if (!TrySnapShallowEmbed(ref state, terrain, solid, ref queries, out string snapReason))
                {
                    reason = snapReason ?? reason;
                    return false;
                }
            }
            // Ground contact probe - read-only, never moves the state.
            if (!ProbeSupport(state, terrain, solid, ref queries, out bool groundedNow, out reason))
                return false;
            if (dt <= 0f)
            {
                state.Grounded = groundedNow;
                return true;
            }

            MotionState s = state;
            s.Grounded = groundedNow;
            int substeps = Math.Max(1, (int)Math.Ceiling(dt / Substep));
            float sdt = dt / substeps;
            for (int i = 0; i < substeps; i++)
            {
                // Vanilla: OnGround && TouchingWall adds the jump impulse
                // (fast zombies use EType.FastJumpSpeed); airborne chase runs
                // at half desired speed unless IsMovingFast.
                if (s.Grounded && s.TouchingWall && s.VelocityY <= 0f)
                    s.VelocityY += jumpImpulse;
                float speedScale = (s.Grounded || fast) ? 1f : AirborneSpeedScale;
                s.VelocityY += Gravity * sdt;
                bool grounded = false, wall = false;

                if (!SweepAxis(ref s, 1, s.VelocityY * sdt, terrain, solid,
                        ref queries, out bool hitY, out reason))
                    return false;
                if (hitY)
                {
                    if (s.VelocityY < 0f)
                        grounded = true;
                    s.VelocityY = 0f;
                }
                if (!SweepAxis(ref s, 0, desiredVX * speedScale * sdt, terrain, solid,
                        ref queries, out bool hitX, out reason))
                    return false;
                if (hitX)
                    wall = true;
                if (!SweepAxis(ref s, 2, desiredVZ * speedScale * sdt, terrain, solid,
                        ref queries, out bool hitZ, out reason))
                    return false;
                if (hitZ)
                    wall = true;

                s.Grounded = grounded;
                s.TouchingWall = wall;
            }
            state = s; // commit only after every substep proved
            return true;
        }

        /// <summary>
        /// Downward support probe: sweeps the body's column into the thin slab
        /// [feet-0.02, feet] without moving the state.
        /// </summary>
        private static bool ProbeSupport(
            MotionState s, TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, bool> solid, ref int queries, out bool grounded, out string reason)
        {
            grounded = false;
            reason = null;
            AxisRange(s.X - HalfWidth, s.X + HalfWidth, out int x0, out int x1);
            AxisRange(s.Y - SupportProbe, s.Y, out int y0, out int y1);
            AxisRange(s.Z - HalfWidth, s.Z + HalfWidth, out int z0, out int z1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!Query(terrain, x, y, z, "support", ref queries, out int t, out reason))
                            return false;
                        if (solid(t))
                        {
                            grounded = true;
                            return true;
                        }
                    }
            return true;
        }

        /// <summary>
        /// Recovers a body whose only solid overlap sits in its bottom cell
        /// layer. FindSafeStartLocation/FindTopmostGroundLocation promise a
        /// two-air column on the CLIENT's terrain, so one solid layer under
        /// the feet is a floor disagreement (or emerge overlap), never a
        /// burial. Feet rise onto that layer's top face and the raised body
        /// is re-proven strictly; any solid cell higher up stays unprovable.
        /// </summary>
        private static bool TrySnapShallowEmbed(
            ref MotionState s, TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, bool> solid, ref int queries, out string reason)
        {
            reason = null;
            AxisRange(s.X - HalfWidth, s.X + HalfWidth, out int x0, out int x1);
            AxisRange(s.Y, s.Y + Height, out int y0, out int y1);
            AxisRange(s.Z - HalfWidth, s.Z + HalfWidth, out int z0, out int z1);
            bool bottomSolid = false;
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!Query(terrain, cx, cy, cz, "body", ref queries, out int t, out reason))
                            return false;
                        if (!solid(t))
                            continue;
                        if (cy > y0)
                        {
                            reason = $"embedded in solid cell ({cx},{cy},{cz}) type {t}";
                            return false;
                        }
                        bottomSolid = true;
                    }
            if (!bottomSolid)
                return true;
            s.Y = y0 + 1f;
            return CheckBodyCells(s.X, s.Y, s.Z, terrain, solid, ref queries, out reason);
        }

        /// <summary>
        /// Visits each integer cell the body strictly overlaps (same bounds
        /// as the body checks). Return false from visit to stop early.
        /// </summary>
        internal static void ForEachBodyCell(
            float x, float y, float z, Func<int, int, int, bool> visit)
        {
            AxisRange(x - HalfWidth, x + HalfWidth, out int x0, out int x1);
            AxisRange(y, y + Height, out int y0, out int y1);
            AxisRange(z - HalfWidth, z + HalfWidth, out int z0, out int z1);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                        if (!visit(cx, cy, cz))
                            return;
        }

        /// <summary>Every body-overlapped cell resolves and is non-solid.</summary>
        private static bool CheckBodyCells(
            float x, float y, float z, TerrainProbe.TryGetSavedBlockTypeDelegate terrain,
            Func<int, bool> solid, ref int queries, out string reason)
        {
            reason = null;
            AxisRange(x - HalfWidth, x + HalfWidth, out int x0, out int x1);
            AxisRange(y, y + Height, out int y0, out int y1);
            AxisRange(z - HalfWidth, z + HalfWidth, out int z0, out int z1);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!Query(terrain, cx, cy, cz, "body", ref queries, out int t, out reason))
                            return false;
                        if (solid(t))
                        {
                            reason = $"embedded in solid cell ({cx},{cy},{cz}) type {t}";
                            return false;
                        }
                    }
            return true;
        }

        /// <summary>
        /// Sweeps the body along one axis by delta, clamping to the nearest
        /// solid cell plane so it can stop against but never tunnel through
        /// geometry. The swept union of current+goal AABBs (strict epsilon on
        /// the still axes) is the broadphase; unknown cells or budget overrun
        /// fail the whole step.
        /// </summary>
        private static bool SweepAxis(
            ref MotionState s, int axis, float delta,
            TerrainProbe.TryGetSavedBlockTypeDelegate terrain, Func<int, bool> solid,
            ref int queries, out bool hit, out string reason)
        {
            hit = false;
            reason = null;
            if (delta == 0f)
                return true;

            Bounds(s, out float bMinX, out float bMaxX, out float bMinY, out float bMaxY, out float bMinZ, out float bMaxZ);
            float bMinA = axis == 0 ? bMinX : axis == 1 ? bMinY : bMinZ;
            float bMaxA = axis == 0 ? bMaxX : axis == 1 ? bMaxY : bMaxZ;
            // Union of the current and goal AABB along the moving axis.
            AxisRange(Math.Min(bMinA, bMinA + delta), Math.Max(bMaxA, bMaxA + delta), out int a0, out int a1);
            AxisRange(bMinX, bMaxX, out int x0, out int x1);
            AxisRange(bMinY, bMaxY, out int y0, out int y1);
            AxisRange(bMinZ, bMaxZ, out int z0, out int z1);
            if (axis == 0) { x0 = a0; x1 = a1; }
            else if (axis == 1) { y0 = a0; y1 = a1; }
            else { z0 = a0; z1 = a1; }

            long cellCount = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cellCount > MaxQueries || queries + cellCount > MaxQueries)
            {
                reason = "voxel query budget exceeded";
                return false;
            }

            float allowed = delta;
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!Query(terrain, cx, cy, cz, "swept", ref queries, out int t, out reason))
                            return false;
                        if (!solid(t))
                            continue;
                        int cellAxis = axis == 0 ? cx : axis == 1 ? cy : cz;
                        if (delta > 0f)
                        {
                            float lim = cellAxis - bMaxA - Eps;
                            if (lim < allowed)
                                allowed = Math.Max(0f, lim);
                        }
                        else
                        {
                            float lim = cellAxis + 1f - bMinA + Eps;
                            if (lim > allowed)
                                allowed = Math.Min(0f, lim);
                        }
                    }

            if (allowed != delta)
                hit = true;
            if (axis == 0) s.X += allowed;
            else if (axis == 1) s.Y += allowed;
            else s.Z += allowed;
            return true;
        }

        private static void Bounds(
            MotionState s, out float minX, out float maxX,
            out float minY, out float maxY, out float minZ, out float maxZ)
        {
            minX = s.X - HalfWidth; maxX = s.X + HalfWidth;
            minY = s.Y;             maxY = s.Y + Height;
            minZ = s.Z - HalfWidth; maxZ = s.Z + HalfWidth;
        }

        /// <summary>Integer cells strictly overlapped by [min,max] under epsilon.</summary>
        private static void AxisRange(float min, float max, out int c0, out int c1)
        {
            c0 = (int)Math.Floor(min + Eps);
            c1 = (int)Math.Floor(max - Eps);
        }

        private static bool Query(
            TerrainProbe.TryGetSavedBlockTypeDelegate terrain, int x, int y, int z,
            string kind, ref int queries, out int type, out string reason)
        {
            type = 0;
            reason = null;
            if (++queries > MaxQueries)
            {
                reason = "voxel query budget exceeded";
                return false;
            }
            if (!terrain(x, y, z, out type))
            {
                reason = $"{kind} cell ({x},{y},{z}) terrain unknown";
                return false;
            }
            return true;
        }

        private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));
    }
}
