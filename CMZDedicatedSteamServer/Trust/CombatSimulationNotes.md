# Combat Simulation Notes (internal investigation)

Status: **a diagnostic-only prototype now exists** —
`Trust/ShadowCombatTracker.cs`, gated behind the unified `log-combat=false`
(default off) and fed only by already-accepted messages via the
`AllowMessage`/`AllowRelayedMessage` wrappers. It covers every spawnable
enemy type: zombies 0-17 (slow plus observed fast mode), archers 18-25
(same chase, but hold to shoot inside ~35 m with a clear ~1.5 m-eye
terrain trace — `ArcherSkeletonChase`), melee skeletons 26-49, and the
ZombieChase-subclass bosses Felguard 50 / Hell Lord 51 / Alien 52 —
all moved by a bounded
voxel-body approximation — `Trust/ShadowVoxelMotion.cs`; `GunshotMessage`
+ the 5-ray `ShotgunShotMessage` queue **ballistic tracers** —
`Trust/ShadowCombatTracker.Projectiles.cs` — modeled after vanilla's
head/tail flight with gravity, but stepped at coarse tick times, so this
is an approximation, not vanilla-faithful) and marks everything else
`unknown` rather than guessing. It remains strictly observability: `[ShadowCombat]` log lines
only, never kicks/denials — the existing damage-bank heuristic and other
combat checks are unchanged.

Source: `GameFiles/v1.9.9.8.5/CastleMinerZ/CastleMinerZ/` (read-only decompiled
vanilla reference; line numbers approximate).

## What the wire actually carries

- `Player.cs` `ProcessGunshotMessage` ~510-543: remote gunshots are simulated
  locally — normal guns build a tracer ray from `FPSCamera.WorldPosition`;
  laser guns use `GetGunTipPosition`. Shotguns (~545+) spawn **five**
  direction rays per trigger pull.
- `TracerManager.cs` `Tracer.Init` ~278-294 and update ~320: each tracer is
  a head/tail segment — `Head=Tail=pos+dir*0.5`, both velocities
  `dir*weapon.Velocity`, `TailTime=FlightTime-0.2`; per update the
  remaining life decrements, the head moves then takes `velY -= 10*dt`,
  and once elapsed > ~0.4 s the tail does the same; collision traces
  tail→head. Muzzle speeds/lifetimes: pistol 75/1 s, pump shotgun 50/0.4 s,
  bolt rifle 150/2 s, assault rifle 100/2 s, SMG/LMG 100/1 s
  (`ItemPolicy.TryGetGunBallistics`). `FPSRig` eye height is **1.4**
  (`EyePointHeight` / pitch pivot).
- `TracerManager.cs` ~464-471: the local sim calls `enemy.TakeDamage` for
  remote shots exactly as for local ones — using the *local* shared
  `itemClass.EnemyDamage` (`BaseZombie.cs` ~203-224), then sends
  `KillEnemyMessage` with the *sender* as author but `shooterID` as the
  credited killer.

## Key finding — caller vs killer

**No damage value is ever transmitted.** A death is credited purely by claim:
a modified observer client can generate `TakeDamage` calls on its own sim and
emit a `KillEnemyMessage` naming an *innocent* `shooterID`. Any evidence the
server derives about "the killer's" capability is therefore only as strong as
the claim binding — diagnostic tooling must warn that credited killer ≠ proven
shooter. The kill-claim evidence layer (`CheckKillClaimEvidence`) already
treats claims, not physics; this note records why a sim cannot fix that
binding — it can only bound plausibility.

## Why exact lockstep is unsupported

- `BaseZombie.cs` ctor ~30-63: health initializes from the enemy type;
  a seeded `Random` and init package initialize other simulation state.
  Server can reconstruct starting HP but not mid-fight state.
- `EnemyManager.cs` ~276-339: target resolves from sender or `PlayerName`;
  remote spawns can be skipped entirely at the ~45/50 enemy-count caps, so
  clients may instantiate different enemy sets from the same spawn traffic.
- `BaseZombie.cs` ~345-375: enemies despawn when their region unloads and tick
  with **client-side** elapsed time — server cannot know per-client tick order.
- `ZombieChase.cs` ~90-165: chase uses target velocity/ground state, local
  speed-up messaging, wall jumps; `BaseZombie.cs` ~380-445 does iterative
  terrain-sweep collision; `EnemyManager.cs` ~967-1055 does AABB `IsHittable`
  terrain clipping plus `ChanceOfBulletStrike` rolls on an **unseeded**
  `_rnd` (line ~53). Random and client-time inputs mean the server cannot
  reproduce trajectories exactly — only envelope approximations.

## Narrow safe prototype design (implemented as ShadowCombatTracker)

Diagnostic-only all-enemy tracking with bounded voxel motion (types 0-52 —
the full `EnemyType.Types[]` table; enum slots 53-55 are never instantiated
so nothing spawns them):

- Ingest already-accepted spawn/update/shot/speed-up/death messages.
- Key shadows by the existing registry's (target, enemy-identity) pair —
  **enemy ids are per-client counters, never assume globally unique**.
- Named-target spawns ARE supported when the name resolves uniquely to an
  announced, un-kicked player (ordinal `Equals`, same as vanilla
  `GetNetworkPlayerByName`); the record keys on the *resolved target* —
  the kill claim's TargetID — while the spawn sender is retained as a
  separate diagnostic field. Unresolvable names are logged and skipped,
  never guessed.
- Motion is `ShadowVoxelMotion.TryStep`: a vanilla PlayerAABB body
  (±0.35 x/z, 1.65 tall, feet-anchored) integrated in ≤0.05 s substeps
  with `BasicPhysics` gravity (−20), sweeping **Y then X then Z** as an
  axis-separated approximation — the body slides along whichever axis is
  free rather than resolving a true vector sweep. Chase remains
  horizontal (a distant higher/lower target still attracts, per
  `ZombieChase`), desired speed is halved while airborne, and the vanilla
  `OnGround && TouchingWall` slow wall-jump (+10 vel.Y) is applied.
  Ordinary floor/wall contact stops or slides the body — it does NOT
  mark uncertainty. Falls, ledges, single-step climbs and ceiling stops
  are modeled. At vanilla's ~1 m attack range (`ZombieChase`), the
  stationary attack/dig states are approximated as zero horizontal chase;
  a higher/lower close target no longer invalidates the record, while
  emergence timing and actual dig outcomes remain unmodeled.
- **Fast zombies**: the spawn package's `FastSpeed` claim is parsed but
  does nothing until an accepted `SpeedUpEnemyMessage` for that exact
  (target, enemy) key arrives — matching `BaseZombie.SpeedUp` setting
  `IsMovingFast`/`CurrentSpeed` — and the pending advance first completes
  at the *slow* speed (no retroactive fast motion). While `MovingFast`:
  desired speed is the package's fast value, airborne chase keeps full
  speed (slow halves), and the wall-jump uses the type-derived
  `FastJumpSpeed` `13 + 3*type/16` (types 0..17) instead of +10. All
  speeds are client claims recorded as approximations — no timers,
  emergence, or unobserved speed changes are guessed. An invalid
  (non-finite/non-positive) `FastSpeed` marks the record unknown at the
  speed-up — spawn accepts the package untouched. A speed-up for a
  missing/uncertain key does nothing; uncertainty is never healed. A
  speed-up on a non-zombie type marks the record unknown outright —
  vanilla only emits `SpeedUpEnemyMessage` for `HasRunFast` types
  (zombies), so one on a skeleton/archer/boss is fabricated.
- **Ballistic tracers**: accepted normal-gun shots queue head/tail rays
  (max 128 total, shotgun volleys of five atomically — a volley that
  cannot fit loses coverage rather than enqueuing partially). Rays
  advance in ≤1/60 s substeps with vanilla ordering (expire → head
  move+gravity → delayed tail move+gravity → trace tail→head) against a
  bounded voxel DDA (≤512 cells/segment, ≤4096 probes/bullet-advance,
  per-call cell cache) and enemy AABBs interpolated between the last two
  advance snapshots at the bullet's own substep time. The first solid
  cell or body stops the ray; a hit debits the bounded per-ray damage
  once and logs distance/elapsed. Coverage failures are PER-TRACER or
  PER-VOLLEY, never record-wide: an unresolvable path cell, a >1 s
  in-flight observation gap, out-of-bounds flight, or an unsatisfiable
  position-history window drops just that tracer (records stay modeled);
  an unsupported weapon, stale/missing shooter position, malformed ray
  direction, or a saturated queue drops just that volley. Lasers and
  launchers stay unsupported. Tick quantization, the ~50 ms advance
  schedule, camera origin (position+1.4, no wire camera), and client
  frame timing all remain approximations. Kill claims log the credited
  killer's in-flight ray count plus WIRE-FACT bounds: `lifeSec`
  (observed spawn→claim), `killerShots` (the killer's accepted
  trigger pulls inside that lifetime, ring-buffered at 1024), and
  `killerMaxDmg` — each pull priced by ITS OWN weapon's per-trigger
  bound (`TryGetGunMaxEnemyDamage` at stamp time), so weapon switching
  cannot smuggle damage past the sum; `+Nu` counts pulls with no
  damage entry and `+ev` flags the ring having evicted a pull inside
  the enemy's lifetime (the bound is then incomplete, never silently
  low). Since no damage value ever crosses the wire, `killerMaxDmg`
  bounds what that player alone could have dealt — a 21.7hp enemy
  killed inside ~5s by a shooter whose pulls sum to ~15 max is
  impossible under vanilla without co-damage, while a 1hp zombie at
  full estHP is only coverage noise (one missed tracer reads
  identically). Estimated HP is never presented as certain while
  shots are pending.
- Uncertainty is for what the model cannot prove: unresolvable terrain
  cells (support slab, body volume, or swept volume), non-finite state,
  stale/missing targets, >1 s enemy observation gaps, and the bounded
  512-query voxel budget being exceeded (long advances are split into
  ≤0.5 s TryStep chunks so sparse packets do not inflate one call).
  In-flight tracers share the same 1 s envelope — a longer quiet gap
  drops the tracer only. Known air below the body is a fall, never an
  unknown.
- **Terrain fork tolerance (client-proven air)**: vanilla's spawn pickers
  (`FindSafeStartLocation`/`FindTopmostGroundLocation`) ran on the
  CLIENT's terrain and guarantee at most a 2-air column — cells a claimed
  body overlaps were provably non-blocking there, so a server-solid
  overlap is a dig the server denied/restored (or a generation view
  difference), never a burial. `TryAbsorbBodyFork` adopts those
  overlapped cells into a shared proven-air set (`_provenAirCells`,
  bounded per record by `ForkCellBudget`=48) and keeps the claimed
  position — a skeleton spawned in a client-excavated pocket stays
  modeled at its real spawn point instead of dying "embedded". The
  overlay feeds motion, impact validation, and tracer occlusion alike.
  Absorption only covers cells the body actually overlaps; unproven wall
  cells still clamp/jump normally, and unresolvable cells stay unknown.
  A body whose ONLY solid overlap is its bottom cell layer and arrived
  mid-life (world edit under the feet) still snaps onto the layer's top
  face via `TrySnapShallowEmbed`; `ZombieEmerge` overlaps ground while
  `IsHittable=false`.
- Records live up to 300 s (`EnemyTtl`): enemy positions never stream,
  so the record is the only tracking — a 30 s TTL deleted live enemies
  before their kill claims arrived. It is a memory bound, not a
  freshness check.
- Zeroed records leave the hittable set: the first debit driving
  `EstimatedHp` ≤0 stamps `ZeroCrossUtc`, and the eligibility check
  stops ALL later tracers from hitting it — the real enemy died
  client-side and its kill claim is in flight or was eaten upstream.
  The record still resolves its kill claim whenever it arrives (claims
  can lag seconds), and a `forked` marker distinguishes fork-tolerated
  records in the claim log. Corpses (dead ≤2 s) keep accepting only
  pre-death tracers, then prune logs "corpse retention elapsed" — no
  longer mislabeled as the 300 s TTL.
- Retain accepted trajectory with timestamps and explicit origin uncertainty.
- Explicit skip/unknown states: melee, explosions, missing terrain,
  unsupported enemy types, packet gaps — never guess.
- **Shadow HP is NEVER complete.** Vanilla has NO dedicated
  melee-vs-enemy packet at all — `InGameHUD.Melee` (~1054-1078) calls
  `TakeDamage` locally on the probed enemy, while `DigMessage` is only
  sent for block dig/place (`Dig` ~1096, `placing` ~1235). Dig messages
  therefore do NOT invalidate records — a mundane block edit is not
  evidence of enemy damage — but that means melee damage is entirely
  invisible, as is lava/environmental damage, which produces no wire
  traffic either. Any estimate can only drift downward from unseen
  damage; world-block changes themselves remain checked through the
  motion probes (an embedded/unsupported body marks unknown).
- **Never feed approximate shadow HP into kicks/denials** — output is
  observability (`[ShadowCombat]` log lines) only.

## Proposed validation

Recorded paired vanilla traces (client ground truth vs server ingest) for:
stationary and moving targets, wall/jump paths, latency and frame-rate
variation, normal vs laser vs shotgun tracers, and co-op cases where reporter
≠ killer. **No accuracy claim may be made without paired client ground
truth** — wire-order alone cannot establish sim fidelity.
