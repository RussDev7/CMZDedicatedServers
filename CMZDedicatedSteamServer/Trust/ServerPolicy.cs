/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Collections.Generic;
using System.IO;
using CMZDedicatedSteamServer.Commands;
using CMZDedicatedSteamServer.Config;

namespace CMZDedicatedSteamServer.Trust
{
    /// <summary>
    /// Built-in server-authoritative trust layer for inbound gameplay messages.
    ///
    /// Purpose:
    /// - The server never trusts client-reported game mode, position, or authority.
    ///   A modified client can flip CastleMinerZGame.GameMode locally or forge any
    ///   payload, so legality is judged only against the server's own configured
    ///   game mode/difficulty and measured movement between accepted updates.
    ///
    /// Enforcement model:
    /// - Illegal messages are dropped (consumed before apply/relay), so cheating is
    ///   never visible to other players and never mutates host state.
    /// - Dropped packets add violations; enough violations kick, and optionally ban.
    ///
    /// Movement rules:
    /// - Flying is only legal when the server game mode is Creative
    ///   (GameModeTypes.Creative == 3). Every other mode enforces vertical speed
    ///   caps and sustained stationary-rise detection.
    /// - Position jumps beyond TrustTeleportDistance count as teleports.
    ///   Vanilla offers the Teleport menu in every mode except Endurance, and never
    ///   on HARDCORE difficulty. In allowed modes teleports are rate-limited per
    ///   player; in forbidden modes only TrustTeleportOpRank+ may teleport.
    /// - Visitor-ranked players cannot send world-mutation messages at all.
    ///
    /// Known positional limits:
    /// - Slow flight that mimics stair-climbing speed and pure hovering cannot be
    ///   distinguished from standing/walking without terrain height data. The
    ///   checks here target unmistakable physics violations.
    /// </summary>
    internal sealed class ServerPolicy
    {
        private const string PlayerUpdateTypeName = "DNA.CastleMinerZ.Net.PlayerUpdateMessage";
        private const string PlayerExistsTypeName = "DNA.CastleMinerZ.Net.PlayerExistsMessage";
        private const string InventoryStoreTypeName = "DNA.CastleMinerZ.Net.InventoryStoreOnServerMessage";
        private const string CreatePickupTypeName = "DNA.CastleMinerZ.Net.CreatePickupMessage";
        private const string ItemCrateTypeName = "DNA.CastleMinerZ.Net.ItemCrateMessage";

        private const string MeleePlayerTypeName = "DNA.CastleMinerZ.Net.MeleePlayerMessage";
        private const string GunshotTypeName = "DNA.CastleMinerZ.Net.GunshotMessage";
        private const string ShotgunShotTypeName = "DNA.CastleMinerZ.Net.ShotgunShotMessage";
        private const string FireRocketTypeName = "DNA.CastleMinerZ.Net.FireRocketMessage";
        private const string GrenadeTypeName = "DNA.CastleMinerZ.Net.GrenadeMessage";
        private const string ChangeCarriedItemTypeName = "DNA.CastleMinerZ.Net.ChangeCarriedItemMessage";
        private const string DetonateExplosiveTypeName = "DNA.CastleMinerZ.Net.DetonateExplosiveMessage";
        private const string DetonateFireballTypeName = "DNA.CastleMinerZ.Net.DetonateFireballMessage";
        private const string DetonateGrenadeTypeName = "DNA.CastleMinerZ.Net.DetonateGrenadeMessage";
        private const string DetonateRocketTypeName = "DNA.CastleMinerZ.Net.DetonateRocketMessage";

        /// <summary>
        /// Player-authored claims that can arrive either host-bound or as relays.
        /// KillEnemyMessage is intentionally absent: the client simulating the
        /// enemy reports kills, so KillerID legitimately differs from senderId.
        /// </summary>
        private static readonly HashSet<string> CombatClaimNames = new HashSet<string>(StringComparer.Ordinal)
        {
            MeleePlayerTypeName,
            GunshotTypeName,
            ShotgunShotTypeName,
            FireRocketTypeName,
            GrenadeTypeName,
            ChangeCarriedItemTypeName,
            DetonateExplosiveTypeName,
            DetonateFireballTypeName,
            DetonateGrenadeTypeName,
            DetonateRocketTypeName,
        };

        /// <summary>
        /// Messages a client can only legitimately send after announcing itself
        /// with PlayerExistsMessage. CastleWalls-style ghost mode suppresses the
        /// announcement so other clients never spawn the avatar - any of these
        /// world/interaction/combat messages arriving first is ghost behavior.
        /// Join-flow messages (world info, chunks, inventory requests) stay open.
        /// </summary>
        private static readonly HashSet<string> GhostActionMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.AlterBlockMessage",
            "DNA.CastleMinerZ.Net.DigMessage",
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            CreatePickupTypeName,
            "DNA.CastleMinerZ.Net.RequestPickupMessage",
            ItemCrateTypeName,
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.DestroyCustomBlockMessage",
            InventoryStoreTypeName,
            GunshotTypeName,
            ShotgunShotTypeName,
            FireRocketTypeName,
            GrenadeTypeName,
            MeleePlayerTypeName,
            "DNA.CastleMinerZ.Net.KillEnemyMessage",
            "DNA.CastleMinerZ.Net.KillDragonMessage",
            ChangeCarriedItemTypeName,
            DetonateExplosiveTypeName,
            DetonateFireballTypeName,
            DetonateGrenadeTypeName,
            DetonateRocketTypeName,
            "DNA.CastleMinerZ.Net.SpawnEnemyMessage",
            "DNA.CastleMinerZ.Net.SpawnDragonMessage",
            "DNA.CastleMinerZ.Net.UpdateDragonMessage",
            "DNA.CastleMinerZ.Net.RemoveDragonMessage",
            "DNA.CastleMinerZ.Net.RequestDragonMessage",
            "DNA.CastleMinerZ.Net.EnemyGiveUpMessage",
            "DNA.CastleMinerZ.Net.SpeedUpEnemyMessage",
            "DNA.CastleMinerZ.Net.DoorOpenCloseMessage",
        };

        /// <summary>
        /// Instant voluntary actions a flagged-dead client cannot legitimately
        /// produce. Delayed resolutions are absent on purpose: in-flight
        /// explosives, enemy sim ownership, kill credit, death drops, and
        /// autosaves can all resolve after death in vanilla.
        /// </summary>
        private static readonly HashSet<string> DeadActionMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.AlterBlockMessage",
            "DNA.CastleMinerZ.Net.DigMessage",
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            ItemCrateTypeName,
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.DestroyCustomBlockMessage",
            GunshotTypeName,
            ShotgunShotTypeName,
            FireRocketTypeName,
            GrenadeTypeName,
            MeleePlayerTypeName,
            ChangeCarriedItemTypeName,
            "DNA.CastleMinerZ.Net.DoorOpenCloseMessage",
        };

        // GameModeTypes / GameDifficultyTypes values from the decompiled game source.
        private const int EnduranceGameMode = 0;
        private const int DragonEnduranceGameMode = 2;
        private const int CreativeGameMode = 3;
        private const int HardcoreDifficulty = 3;

        // PlayerUpdateMessage wire layout (message id byte excluded):
        //   [1..12] Vector3 LocalPosition (3x float32 LE)
        //   [13] torso pitch byte
        //   [14] yaw byte
        //   [15] flags (bit2 = Dead)
        //   [16] PlayerMode byte
        //   [17] packed movement nibble byte
        //   [18..23] WorldVelocity (3x half-float, client-reported, unused here)
        private const int MinUpdatePayload = 16;
        private const byte DeadFlag = 0x02;
        private const byte ReloadingFlag = 0x08;

        /// <summary>
        /// Mutation messages that carry a world-space target position and get a
        /// distance check against the sender's last accepted position.
        /// </summary>
        private static readonly HashSet<string> ReachCheckedMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.AlterBlockMessage",
            "DNA.CastleMinerZ.Net.DigMessage",
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            "DNA.CastleMinerZ.Net.DestroyCustomBlockMessage",
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.ItemCrateMessage",
            "DNA.CastleMinerZ.Net.DetonateExplosiveMessage",
            "DNA.CastleMinerZ.Net.CreatePickupMessage",
            "DNA.CastleMinerZ.Net.SpawnEnemyMessage",
        };

        /// <summary>
        /// Messages whose target position legitimately lands away from the player:
        /// thrown explosives travel before their blast applies, and crates break
        /// from blast radius as well as pickaxes. These get the generous blast cap
        /// instead of arm's reach; cross-map forgeries still die.
        /// </summary>
        private static readonly HashSet<string> BlastReachMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.DetonateExplosiveMessage",
            "DNA.CastleMinerZ.Net.DetonateFireballMessage",
            "DNA.CastleMinerZ.Net.DetonateGrenadeMessage",
            "DNA.CastleMinerZ.Net.DetonateRocketMessage",
        };

        /// <summary>
        /// Host-destined mutation messages a Visitor-ranked player may not send.
        /// Anything that changes blocks, crates, spawns, or entity control is read-only.
        /// </summary>
        private static readonly HashSet<string> VisitorBlockedMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.AlterBlockMessage",
            "DNA.CastleMinerZ.Net.DigMessage",
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            "DNA.CastleMinerZ.Net.DestroyCustomBlockMessage",
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.ItemCrateMessage",
            "DNA.CastleMinerZ.Net.DetonateExplosiveMessage",
            "DNA.CastleMinerZ.Net.CreatePickupMessage",
            "DNA.CastleMinerZ.Net.SpawnEnemyMessage",
            "DNA.CastleMinerZ.Net.RequestDragonMessage",
            "DNA.CastleMinerZ.Net.KillDragonMessage",
            "DNA.CastleMinerZ.Net.RemoveDragonMessage",
        };

        /// <summary>
        /// Per-player rolling-window limits for abusable host messages. Over the
        /// cap the message is silently dropped (consumed) without a violation -
        /// bursts are fine, floods simply stop existing.
        /// </summary>
        private static readonly (string TypeName, int Limit, double WindowSeconds)[] RateLimits =
        {
            (CreatePickupTypeName, 600, 30.0),      // legit mining bursts (high-tier tools, blasts) spawn drops fast; spam is not
            ("DNA.CastleMinerZ.Net.SpawnEnemyMessage", 30, 30.0),
            ("DNA.CastleMinerZ.Net.RequestWorldInfoMessage", 5, 60.0),
            ("DNA.CastleMinerZ.Net.RequestInventoryMessage", 5, 60.0),
            ("DNA.CastleMinerZ.Net.ClientReadyForChunksMessage", 10, 60.0),
            ("DNA.CastleMinerZ.Net.RequestChunkMessage", 400, 30.0),
            ("DNA.CastleMinerZ.Net.RequestPickupMessage", 600, 30.0), // keep pace with legitimate mining pickup bursts
            (ItemCrateTypeName, 90, 30.0),
            ("DNA.CastleMinerZ.Net.DetonateExplosiveMessage", 20, 30.0),
            // World-edit bursts: legit mining/building is a few blocks per second.
            ("DNA.CastleMinerZ.Net.AlterBlockMessage", 150, 30.0),
            ("DNA.CastleMinerZ.Net.DigMessage", 150, 30.0),
            // Kill-all-mob sweeps send kill claims in a burst; real combat is a few a minute.
            ("DNA.CastleMinerZ.Net.KillEnemyMessage", 30, 30.0),
            ("DNA.CastleMinerZ.Net.EnemyGiveUpMessage", 60, 30.0),
            ("DNA.CastleMinerZ.Net.SpeedUpEnemyMessage", 30, 30.0),
            ("DNA.CastleMinerZ.Net.KillDragonMessage", 5, 30.0),
            ("DNA.CastleMinerZ.Net.RemoveDragonMessage", 10, 30.0),
            ("DNA.CastleMinerZ.Net.RemoveBlocksMessage", 60, 30.0),
            // Combat claim floods: fastest legit swing/trigger rates are a few per second.
            ("DNA.CastleMinerZ.Net.MeleePlayerMessage", 90, 30.0),
            // Laser SMG fires on a 0.06s period (~16.7 shots/sec sustained) -
            // 900/30s covers it with headroom; provenance/ammo checks still apply.
            ("DNA.CastleMinerZ.Net.GunshotMessage", 900, 30.0),
            ("DNA.CastleMinerZ.Net.ShotgunShotMessage", 40, 30.0),
            ("DNA.CastleMinerZ.Net.FireRocketMessage", 20, 30.0),
            ("DNA.CastleMinerZ.Net.GrenadeMessage", 30, 30.0),
            // Chat is human-typed; even a talkative player stays under this.
            ("DNA.CastleMinerZ.Net.BroadcastTextMessage", 8, 10.0),
        };

        /// <summary>
        /// World mutations that are never legitimately peer-directed in vanilla -
        /// every edit flows client->host. A relayed one is crafted: it skips
        /// dig/reach checks AND never persists to the delta store, letting a
        /// sender desync a victim's world (a hole only the victim sees is an
        /// embed-kick waiting to happen). Request types are deliberately absent:
        /// joining clients fire queries peer-directed before they learn the
        /// host's routing id, and an idempotent request can't desync anything.
        /// </summary>
        private static readonly HashSet<string> HostBoundOnlyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "DNA.CastleMinerZ.Net.AlterBlockMessage",
            "DNA.CastleMinerZ.Net.DigMessage",
            "DNA.CastleMinerZ.Net.RemoveBlocksMessage",
            "DNA.CastleMinerZ.Net.DestroyCustomBlockMessage",
            "DNA.CastleMinerZ.Net.DestroyCrateMessage",
            "DNA.CastleMinerZ.Net.ItemCrateMessage",
            "DNA.CastleMinerZ.Net.DoorOpenCloseMessage",
            "DNA.CastleMinerZ.Net.CreatePickupMessage",
            "DNA.CastleMinerZ.Net.DetonateExplosiveMessage",
            "DNA.CastleMinerZ.Net.DetonateFireballMessage",
            "DNA.CastleMinerZ.Net.DetonateGrenadeMessage",
            "DNA.CastleMinerZ.Net.DetonateRocketMessage",
            "DNA.CastleMinerZ.Net.InventoryStoreOnServerMessage",
            "DNA.CastleMinerZ.Net.SpawnDragonMessage",
            "DNA.CastleMinerZ.Net.KillDragonMessage",
            "DNA.CastleMinerZ.Net.RemoveDragonMessage",
            "DNA.CastleMinerZ.Net.UpdateDragonMessage",
        };

        private sealed class PendingDrop
        {
            // null = the removed cell could not be resolved against world state;
            // any world-spawnable item is accepted rather than punishing desync.
            public HashSet<int> ItemIds;
            public int Remaining;
            public int Consumed;
            public bool Confirmed;
            // True only for records created by a real DigMessage - a record made
            // by an allowed removal can't legitimize its neighbors, or removal
            // rights would chain outward across a whole connected structure.
            public bool RemovalSeed;
            // One dig authorizes exactly ONE removal: whichever write claims
            // the seed - its own cell or a verified off-by-one neighbor -
            // spends it, so a chopper burst cannot turn one swing into two
            // authorized cells.
            public bool RemovalSpent;
            // Block type the DigMessage claimed it was digging. Neighbor-pass
            // requires the removed cell's real type to match - a dig that
            // claimed a trunk can't legitimize removing a leaf.
            public int ClaimedType;
            // Original DigMessage hit point + face normal. The face-math
            // off-by-one can only land on a tangent axis, and only when the
            // hit point sits inside the removed cell's expanded bounds.
            public bool HasDigHit;
            public float DigHitX, DigHitY, DigHitZ;
            public float DigNormalX, DigNormalY, DigNormalZ;
            public DateTime ExpiryUtc;
        }

        private sealed class PlayerTrustState
        {
            public bool IdentityResolved;
            public ulong RemoteId;
            public string Name = "?";
            public ServerCommandRank Rank = ServerCommandRank.Member;
            public DateTime RankRefreshedUtc = DateTime.MinValue;

            public bool HasBaseline;
            public float LastX;
            public float LastY;
            public float LastZ;
            public DateTime LastUpdateUtc;
            public bool WasDead;

            public DateTime LastTeleportUtc = DateTime.MinValue;
            public double RiseSeconds;
            public int OverSpeedStreak;
            public double AirborneSeconds;
            public double EmbeddedSeconds;
            public float FallPeakY = float.MinValue;
            public float LastSupportedY = float.MinValue;
            public double AboveCeilingSeconds;
            public double LavaSeconds;
            // Lethal-blast exposure: a living player inside a detonation's
            // kill radius when it processes takes flat 1.0 damage in vanilla
            // and cannot survive. Presence across the process instant is
            // proven by updates inside the ring on BOTH sides of the blast -
            // BlastPending* carries the pre-blast sighting until the first
            // post-blast update confirms or clears it; LethalBlastUtc is the
            // armed exposure, evaluated once the death flag has had time.
            public DateTime LethalBlastUtc = DateTime.MinValue;
            public string LethalBlastDesc;
            public DateTime BlastPendingUntil = DateTime.MinValue;
            public float BlastPendingX;
            public float BlastPendingY;
            public float BlastPendingZ;
            public float BlastPendingRange;
            public string BlastPendingDesc;
            // Shadow of vanilla's stamina pool (InGameHUD.PlayerStamina): max 1,
            // sprint drains 0.6/s, regen 0.25/s only after 1s of no use. Jumps
            // (0.25 each) are invisible here, so the model underestimates real
            // spend - a generous direction that can never falsely accuse.
            public double StaminaPool = 1.0;
            public double StaminaRestSeconds;
            public double ImpossibleSprintSeconds;
            public DateTime LastVisitorWarnUtc = DateTime.MinValue;

            // Provenance: what the server believes this player owns.
            // Owned = last known-good inventory (id -> total stack).
            // Credits = gains the server authorized but that are not saved yet
            //           (granted pickups, spilled crate contents about to land).
            // Spill  = expected CreatePickup spawns from a crate this player broke.
            public Dictionary<int, int> Owned;
            public Dictionary<int, int> Credits = new Dictionary<int, int>();
            public Dictionary<int, int> Spill = new Dictionary<int, int>();
            // Largest rejected claim total per item - retries of the same forged
            // inventory drop silently instead of stacking violations each autosave.
            public Dictionary<int, int> DeniedClaims = new Dictionary<int, int>();
            // Rounds fired since the last authoritative inventory snapshot.
            // Owned/Credits are the pool truth; a loaded clip hides at most
            // ClipCapacity extra rounds, which the shot check grants as grace.
            public Dictionary<int, int> AmmoSpent = new Dictionary<int, int>();
            // Cells where this player's edit was rejected. The client may now
            // show a cleared pocket the server still counts as solid, so
            // world-state checks (embedded, env) must defer inside them.
            public List<(int X, int Y, int Z, DateTime Until)> DisputedCells =
                new List<(int X, int Y, int Z, DateTime Until)>();
            // Cells this player claims to be removing/has removed, mapped to the
            // item ids that cell can legitimately drop. A CreatePickup for an
            // item no nearby removed cell can yield is forged. Unconfirmed
            // records come from DigMessage (sent per swing); AlterBlock-to-Empty
            // confirms them. A pickup consumed against a cell whose removal
            // never arrives is punished when the record expires.
            public Dictionary<long, PendingDrop> PendingDrops = new Dictionary<long, PendingDrop>();
            // (x,y,z, expiry, remaining) - accepted enemy/dragon kills. Enemy-loot
            // pickups must spawn near a real kill claim, not just anywhere.
            public List<(float X, float Y, float Z, DateTime ExpiryUtc, int Remaining)> RecentKills =
                new List<(float X, float Y, float Z, DateTime ExpiryUtc, int Remaining)>();
            // Accepted detonation claims (pos, expiry, remaining). Blast-harvest
            // pickups (ores/walls only) may land near a real blast before the
            // RemoveBlocksMessage confirms the destroyed cells.
            public List<(float X, float Y, float Z, DateTime ExpiryUtc, int Remaining, int Loot)> RecentBlasts =
                new List<(float X, float Y, float Z, DateTime ExpiryUtc, int Remaining, int Loot)>();
            // Cells the last accepted RemoveBlocksMessage authorized. The world
            // handler consumes entries as it applies removals; replaced whole on
            // each new message so forged dumps can't accumulate rights.
            public HashSet<long> AuthorizedRemovalCells;
            // Cells AllowMessage proved belong to a live spawner's clear-box.
            // AllowEditAt consumes an entry once to widen reach to spawn range:
            // the box sits around the marker, not the player, so its far corner
            // is legitimately past arm's reach. One-shot so the widened reach
            // can't be replayed on unrelated writes.
            public HashSet<long> SpawnerEditCells;
            // Spawner-clear rate exemption: cell -> when its once-per-window
            // exemption was spent. A proven clear-box cell may skip the
            // generic AlterBlock bucket once per window; repeats inside the
            // window never refresh the entry.
            public Dictionary<long, DateTime> SpawnerRateCells;
            // The last removal write that passed the dig gate (cell + type +
            // when). A denied removal burst right behind it is the chopper
            // signature: the dig cell gets queued for rollback so the batch
            // loses every block it touched - including the one real dig.
            public int LastRemovalX, LastRemovalY, LastRemovalZ, LastRemovalType;
            public DateTime LastRemovalUtc = DateTime.MinValue;
            // Cells the world handler must write back to the delta and relay
            // to every client once drained.
            public List<(int X, int Y, int Z, int Type)> PendingRestores;
            // Accepted placements this player made recently. A cheater's
            // scatter-blocks (shoot-blocks / lava physics) are legitimate writes
            // until the strike threshold kicks them - on kick the tail of the
            // burst is rolled back so the world isn't littered with mod blocks.
            public List<(int X, int Y, int Z, int Type, DateTime Utc)> RecentPlacements;
            // Sliding windows of correlation denies (seedless removals, orphan
            // pickup spawns). Upstream packet loss (FloodGuard blackhole, rate
            // drop) eats the claim message and leaves a desync exactly like a
            // forged claim - the write is denied either way, but a few in a
            // window is tolerated while bursts still accumulate violations.
            public (DateTime Start, int Count) RemovalDenials;
            public (DateTime Start, int Count) PickupDenials;
            public (DateTime Start, int Count) EnemyClaimDenials;
            // Enemy ids this client claimed but the server never saw spawned -
            // remembered briefly so every control message for the same eaten
            // SpawnEnemyMessage counts once, not per claim.
            public Dictionary<int, DateTime> UnknownEnemyIds;
            // Item ids whose ownership claim was denied (pickup grant lost to
            // upstream packet drop, denied spawn the client still holds
            // locally). Repeat claims for the same id deny silently.
            public Dictionary<int, DateTime> DeniedItemClaims;
            public (DateTime Start, int Count) ItemClaimDenials;
            public (DateTime Start, int Count) UnconfirmedConsumeDenials;
            // Sliding window of spawn->kill claims that landed too fast to be
            // real combat (proximity auto-kill / "no enemies" mods).
            public (DateTime Start, int Count) InstantKillDenials;
            public (DateTime Start, int Count) RepeatKillDenials;
            public Dictionary<int, (int Count, DateTime ExpiryUtc)> KillClaimCounts;
            // Last valid ChangeCarriedItem claim: vanilla can only fire/throw/
            // swing the held item, so combat claims naming a different weapon
            // are forged (shoot-anything mods). -1 = never reported; such
            // clients can't be proven wrong and are left alone.
            public int HeldItemId = -1;
            public (DateTime Start, int Count) HeldItemMismatchDenials;
            // Clip shadow: rounds fired on the currently-held weapon since the
            // last observed reload. Vanilla streams PlayerUpdateMessage flags
            // bit3 (Reloading) the entire reload animation (reliable when set)
            // and auto-raises it when a clip empties - a pinned-clip mod that
            // never reloads can never produce it. Resets on ChangeCarriedItem
            // (each inventory item carries its own RoundsInClip).
            public int ClipWeaponId = -1;
            public int ClipShotsFired;
            public (DateTime Start, int Count) ClipOverflowDenials;
            // Reload cycle tracking: the flag must stay up for the held gun's
            // vanilla ReloadTime before the clip counts as refilled - a mod
            // that fakes the bit for one frame gets no reset, and any shot
            // while the flag is up is impossible in vanilla (ProcessInput
            // requires !Reloading to fire).
            public bool ReloadingActive;
            public DateTime ReloadStartUtc;
            // Flag-on updates stream every frame on the reliable channel
            // while the client is really inside the reload, but the flag-OFF
            // edge rides the lossy channel and can be dropped - a stale
            // LastReloadFlagUtc means the reload ended unobserved, so a shot
            // is not provably inside it.
            public DateTime LastReloadFlagUtc;
            public bool ReloadRefilled;
            // Damage budget: every accepted shot banks its weapon's per-
            // trigger max enemy damage (ItemPolicy.TryGetGunMaxEnemyDamage -
            // PumpShotgun 3x weakness-stack bound, all other guns 2.5x
            // headshot); each claimed enemy kill spends that enemy's real
            // StartingHealth from the KILLER's bank. Deeply negative = more
            // HP destroyed than their trigger pulls could deal - the
            // damage-hack signature that spawn-age TTK misses (enemies
            // killed long after spawning).
            public float DamageBudget;
            public (DateTime Start, int Count) DamageDeficitDenials;
            // Per-ammo set of gun ids actually fired this session. The save
            // reconcile's chambered-rounds grace = the sum of THEIR clips -
            // a pistol user can't legitimately hold the LMG's 100-round slack.
            public Dictionary<int, HashSet<int>> AmmoGunsSeen = new Dictionary<int, HashSet<int>>();
            // Gun ids this client has claimed a shot with at any point this
            // session (launcher ShotFired variants normalized by family). A
            // kill claim naming a gun never observed firing is impossible -
            // vanilla sims can't kill without shooting.
            public HashSet<int> GunsFired = new HashSet<int>();
            // Kill-interval bound for single-target guns: one trigger pull
            // kills at most one enemy, so two claims tighter than the claimed
            // weapon's fire interval can't both be real. Shotgun pellets and
            // launcher splash legitimately multi-kill - excluded.
            public DateTime LastSingleKillUtc = DateTime.MinValue;
            public (DateTime Start, int Count) KillIntervalDenials;
            // Non-gun kill-claim rate window (melee/tool kills are unverifiable
            // past swing speed - there is no wire message for swinging at an
            // enemy).
            public (DateTime Start, int Count) MeleeKillClaims;
            public (DateTime Start, int Count) MeleeKillDenials;
            // Vanilla streams InventoryStoreOnServerMessage every ~10s in-game
            // (InGameHUD._periodicSaveTimer). A client that never sends one
            // (CastleWallsMk2 ghost mode patches the Send outright) starves
            // the reconcile that proves phantom ammo.
            public DateTime LastInventoryStoreUtc = DateTime.MinValue;
            // Fire-rate leaky bucket: the next UTC a shot claim may arrive.
            // Rapid-fire mods zero the client-side cooldown; packet timing is
            // the one thing the sender cannot fake sustained.
            public DateTime ShotNextAllowedUtc = DateTime.MinValue;
            // Separate bucket for MeleePlayerMessage/digging DigMessage swings
            // so a shoot->mine transition can't inherit shot schedule debt.
            public DateTime SwingNextAllowedUtc = DateTime.MinValue;
            public (DateTime Start, int Count) FireRateDenials;
            public int MalformedPackets;
            // Enemy-dig cell throttle: a sliding window cap on top of the
            // per-message cap - enemy digging chews a few cells per action.
            public DateTime EnemyDigWindowStartUtc;
            public int EnemyDigCellCount;
            public bool BaselineKnown;

            // Per-message-type rolling window counters for the spam guard.
            public Dictionary<string, (DateTime Start, int Count)> RateWindows =
                new Dictionary<string, (DateTime, int)>();

            // Per-kind strike buckets - each violation kind decays and counts
            // independently, and a kick needs the threshold in ONE kind.
            public Dictionary<string, (int Count, DateTime LastUtc)> ViolationBuckets =
                new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);

            // Diagnostic aggregates only (sum of all bucket counts + last
            // strike time) - kept for existing regression tooling, NEVER read
            // by enforcement.
            public int Violations;
            public DateTime LastViolationUtc;
            public bool KickIssued;
            // Set when the peer's PlayerExistsMessage arrives; ghost-mode clients
            // never send it, so world actions before it are denied and counted.
            public bool Announced;
            // When this state was created - unannounced-timeout reference.
            public DateTime FirstSeenUtc = DateTime.UtcNow;
        }

        private readonly SteamServerConfig _config;
        private readonly Action<string> _log;
        private readonly Func<ulong, string, ServerCommandRank> _rankLookup;
        private readonly Func<string, string, bool> _kick;
        private readonly Func<string, string, bool> _ban;
        private readonly Action<string> _broadcast;
        private readonly Func<float, float, float, int, SupportProbeResult> _supportProbe;
        private readonly Dictionary<byte, PlayerTrustState> _players = new Dictionary<byte, PlayerTrustState>();
        private DateTime _lastGhostSweepUtc = DateTime.MinValue;
        // Vanilla saves inventory every ~10s in-game; 12 misses is a client
        // that patched the send outright, not network trouble.
        private const double SaveSilenceSeconds = 120.0;
        // Kick history keyed by SteamID: a kicked cheater that rejoins and
        // reoffends escalates into a persisted ban instead of bouncing forever.
        // Counts never auto-reset — persistence is wired via SetKickCountStore
        // so a restart reloads them, and only a manual wipe clears them.
        private readonly Dictionary<ulong, (DateTime FirstUtc, int Count)> _kickCounts =
            new Dictionary<ulong, (DateTime, int)>();
        private Action<Dictionary<ulong, (DateTime FirstUtc, int Count)>> _saveKickCounts;

        /// <summary>
        /// Supplies a player's previously persisted inventory (remoteId, name) ->
        /// item-id totals. Wired by the world handler to the on-disk .inv files.
        /// </summary>
        private Func<ulong, string, Dictionary<int, int>> _baselineProvider;

        /// <summary>
        /// Saved block lookup (worldX, worldY, worldZ) -> block type; wired by the
        /// world handler to its chunk-delta overlay. Used to verify a detonation
        /// claim targets a block the server actually has on record as explosive.
        /// </summary>
        private TerrainProbe.TryGetSavedBlockTypeDelegate _savedBlockLookup;

        /// <summary>
        /// Full world block lookup (saved edits overlaid on generated terrain);
        /// wired to TerrainProbe.TryProbeBlockType. Used to verify dig targets and
        /// to flag claimed positions embedded inside solid blocks (noclip).
        /// </summary>
        private TerrainProbe.TryGetSavedBlockTypeDelegate _worldBlockProbe;

        private readonly bool _flightAllowed;
        private readonly bool _teleportModeAllowed;
        private readonly double _staminaDrainPerSec;

        public ServerPolicy(
            SteamServerConfig config,
            Action<string> log,
            Func<ulong, string, ServerCommandRank> rankLookup,
            Func<string, string, bool> kick,
            Func<string, string, bool> ban,
            Action<string> broadcast,
            Func<float, float, float, int, SupportProbeResult> supportProbe = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? (_ => { });
            _rankLookup = rankLookup ?? ((id, name) => ServerCommandRank.Member);
            _kick = kick ?? ((target, reason) => false);
            _ban = ban ?? ((target, reason) => false);
            _broadcast = broadcast ?? (_ => { });
            _supportProbe = supportProbe;

            // Authority comes from the server's own configuration, never the client.
            _flightAllowed = config.GameMode == CreativeGameMode;
            _teleportModeAllowed = config.GameMode != EnduranceGameMode && config.Difficulty != HardcoreDifficulty;
            // Endurance modes disable stamina gating entirely (InGameHUD.
            // CanUseStamina returns false -> the sprint branch can never run),
            // so any sprint-speed movement there is impossible by definition.
            _staminaDrainPerSec =
                config.GameMode == EnduranceGameMode || config.GameMode == DragonEnduranceGameMode
                    ? double.MaxValue
                    : 0.6;
        }

        /// <summary>
        /// Returns false when the message must be consumed (dropped, never applied or relayed).
        /// Identity delegates are invoked lazily only for message types under enforcement.
        /// </summary>
        public bool AllowMessage(
            byte senderId,
            string typeName,
            byte[] data,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            bool allowed = AllowMessageCore(senderId, typeName, data, resolveName, resolveRemoteId);
            // Diagnostics observe only what enforcement accepted.
            if (allowed && _config.LogCombat)
                ObserveShadowMessage(senderId, typeName, data);
            return allowed;
        }

        private bool AllowMessageCore(
            byte senderId,
            string typeName,
            byte[] data,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            // Player id 0 is the host's own id; never enforced.
            if (senderId == 0)
            {
                // Host-originated spawns still register, so enemy ids issued by
                // the server validate like client ones in later kill claims.
                if (typeName == "DNA.CastleMinerZ.Net.SpawnEnemyMessage" &&
                    data != null && data.Length >= 30)
                    RegisterEnemySpawn(senderId, BitConverter.ToInt32(data, 26), data[25]);
                return true;
            }

            // Ghost-mode counter: a peer that never sent PlayerExistsMessage is
            // invisible to other clients while still on the wire. Vanilla always
            // announces before it can act, so world-affecting messages arriving
            // before the announcement are denied and counted. Position updates
            // are dropped silently - a legit client retries them next tick.
            PlayerTrustState announceState = GetState(senderId, resolveName, resolveRemoteId);
            if (typeName == PlayerExistsTypeName)
            {
                announceState.Announced = true;
            }
            else if (!IsExempt(announceState))
            {
                if (!announceState.Announced)
                {
                    if (typeName == PlayerUpdateTypeName)
                        return false;
                    if (GhostActionMessages.Contains(typeName))
                        return Violation(announceState, "ghost", $"{ShortName(typeName)} while unannounced");
                }

                // A flagged-dead client can only wait for respawn; instant
                // voluntary actions in that window are impossible (ghost mode
                // spoofs the dead flag, so this also catches acting ghosts).
                if (announceState.WasDead && DeadActionMessages.Contains(typeName))
                    return Violation(announceState, "dead-action", ShortName(typeName));
            }

            // Ghost sweep, piggybacked on traffic and throttled to 10s: a peer
            // that never announced, or an announced peer whose position updates
            // went silent, cannot legitimately remain - both are ghost tells.
            DateTime sweepNow = DateTime.UtcNow;
            if ((sweepNow - _lastGhostSweepUtc).TotalSeconds >= 10.0)
            {
                _lastGhostSweepUtc = sweepNow;
                foreach (PlayerTrustState p in _players.Values)
                {
                    if (p.KickIssued || IsExempt(p))
                        continue;
                    if (!p.Announced)
                    {
                        if ((sweepNow - p.FirstSeenUtc).TotalSeconds > 60.0)
                            Violation(p, "ghost", "never announced");
                    }
                    else if (p.HasBaseline && (sweepNow - p.LastUpdateUtc).TotalSeconds > 45.0)
                    {
                        Violation(p, "ghost", "position updates stopped");
                    }
                    if (p.Announced && ItemPolicy.Ready)
                    {
                        // Saves stream every ~10s in-game; >120s of silence
                        // after the grace period means a client that patched
                        // the store send (ghost mode) - the reconcile that
                        // proves phantom ammo can never see their inventory.
                        DateTime saveAnchor = p.LastInventoryStoreUtc == DateTime.MinValue
                            ? p.FirstSeenUtc : p.LastInventoryStoreUtc;
                        if ((sweepNow - saveAnchor).TotalSeconds > SaveSilenceSeconds)
                            Violation(p, "ghost", "no inventory save heartbeat");
                    }
                }
            }

            // Kill-claim evidence runs before the spam guard: "no enemies" mods
            // fire KillEnemyMessage every tick for every loaded enemy, so the
            // flood outlives the rate limiter. Dropped claims are still
            // evidence - the drop only stops the kill from relaying.
            if (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage" &&
                data != null && data.Length >= 5)
            {
                // Wire: [0]=id [1-4]=EnemyID [5-8]=TargetID [9-10]=WeaponID.
                if (CheckKillClaimEvidence(
                    GetState(senderId, resolveName, resolveRemoteId),
                    BitConverter.ToInt32(data, 1),
                    data.Length >= 11 ? BitConverter.ToInt16(data, 9) : 0,
                    data.Length >= 12 ? data[11] : senderId))
                {
                    return false; // provably fabricated - never relays
                }
            }

            // Spam guard: silently drop floods of abusable host messages.
            foreach ((string type, int limit, double windowSec) in RateLimits)
            {
                if (type != typeName)
                    continue;
                PlayerTrustState rateState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(rateState) &&
                    !TryUseSpawnerClearRateAllowance(rateState, typeName, data, DateTime.UtcNow) &&
                    !BumpRate(rateState, typeName, limit, windowSec))
                {
                    // A rate-dropped dig leaves a pocket the client sees as
                    // cleared but the server counts as solid - shield it from
                    // world-state checks so the desync can't embed-kick.
                    if (TryReadDigClaim(typeName, data, out int rx, out int ry, out int rz,
                        out _, out _, out _, out _, out _, out _, out _))
                        NoteDisputedCell(rateState, rx, ry, rz);
                    return false;
                }
                break;
            }

            // Sudo/impersonation: spam tools send "{victimGamertag}: {text}"
            // from the cheater's client so the line displays as the victim.
            // The speaker prefix must never name a DIFFERENT connected player;
            // vanilla system strings use "name <verb>" (no colon), GPS sends
            // the sender's own name, and "[Server]" is the server's prefix.
            if (typeName == "DNA.CastleMinerZ.Net.BroadcastTextMessage" &&
                TryReadBroadcastText(data, out string chatText))
            {
                PlayerTrustState chatState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(chatState) && IsImpersonating(chatState, chatText))
                    return Violation(chatState, "relay", "chat message forged in another player's name");
            }

            if (typeName == PlayerUpdateTypeName)
                return CheckPlayerUpdate(senderId, data, resolveName, resolveRemoteId);

            // Save heartbeat stamps on arrival regardless of validation -
            // the client always sends these in-game; silence is the tell.
            if (typeName == InventoryStoreTypeName)
                GetState(senderId, resolveName, resolveRemoteId).LastInventoryStoreUtc = DateTime.UtcNow;

            if (_config.TrustItemValidation && ItemPolicy.Ready &&
                (typeName == InventoryStoreTypeName || typeName == CreatePickupTypeName || typeName == ItemCrateTypeName))
            {
                PlayerTrustState itemState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(itemState))
                {
                    ItemPolicy.ItemVerdict verdict = CheckItemMessage(
                        typeName, data, itemState, out string reason);
                    if (verdict == ItemPolicy.ItemVerdict.InvalidItem)
                        return Violation(itemState, "item", reason);

                    if (verdict == ItemPolicy.ItemVerdict.DeniedDesync)
                        return false; // correlation miss inside grace: dropped, not punished

                    if (verdict == ItemPolicy.ItemVerdict.DeniedRepeat)
                        return false; // already-penalized claim retrying; drop quietly

                    if (verdict == ItemPolicy.ItemVerdict.Malformed)
                    {
                        _log($"[Trust] {itemState.Name}: malformed item payload dropped ({reason})");
                        return false; // dropped, but not a punishable violation
                    }
                }
            }

            // Placement: the block type must exist in vanilla, and setting it
            // consumes the matching item once an inventory baseline exists.
            if (!_flightAllowed && ItemPolicy.Ready &&
                TryReadPlacedBlockType(typeName, data, out int placedBlockType))
            {
                PlayerTrustState placeState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(placeState))
                {
                    if (!ItemPolicy.IsKnownBlockType(placedBlockType))
                    {
                        return Violation(
                            placeState,
                            "item",
                            $"placed unknown block type {placedBlockType}");
                    }

                    // Idempotent rewrites aren't placements: vanilla re-sends
                    // AlterBlock for saved TeleportStation/SpawnPoint restores on
                    // inventory load (PlayerInventory.Read) without consuming the
                    // item. When the world cell already holds that same type the
                    // write changes nothing, so it must not spend either.
                    bool hasCell = TryReadEditTargetCell(typeName, data,
                        out int cellX, out int cellY, out int cellZ);
                    bool alreadySame = hasCell &&
                        TryGetWorldBlock(cellX, cellY, cellZ, out int existingType) && existingType == placedBlockType;

                    EnsureInventoryBaseline(placeState);
                    // A block type with NO placement items can never be placed by
                    // a legit client - vanilla places blocks only by spending the
                    // matching held item. Lava (type 12) is the real case: the
                    // PhysicsEngine-style mods spray it via AlterBlock writes.
                    bool placeable = ItemPolicy.TryGetPlacementItems(placedBlockType, out List<int> candidates);
                    if (!alreadySame &&
                        placeState.BaselineKnown &&
                        (!placeable || !TrySpendAny(placeState, candidates, 1)))
                    {
                        // A reconcile already proved the required item phantom —
                        // materializing it into the world is a confirmed cheat,
                        // not a desync: kick immediately.
                        if (placeable && candidates != null)
                        {
                            int phantomItem = -1;
                            foreach (int cid in candidates)
                            {
                                if (placeState.DeniedClaims.TryGetValue(cid, out int da) && da > 0)
                                {
                                    phantomItem = cid;
                                    break;
                                }
                            }
                            if (phantomItem >= 0)
                            {
                                string ptarget = placeState.RemoteId != 0UL
                                    ? placeState.RemoteId.ToString() : placeState.Name;
                                IssueKick(placeState, "item",
                                    $"placed block type {placedBlockType} from proven-phantom item {phantomItem}",
                                    ptarget);
                                return false;
                            }
                        }

                        // Denied either way; only DISTINCT unowned block types
                        // count as violations - a phantom block (grant lost
                        // upstream) retries the same placement silently, while
                        // a block spawner spraying types trips the window fast.
                        // The key is namespaced above every real item id.
                        if (!NoteDeniedItemClaim(placeState, placedBlockType | PlacementClaimKeyBit) ||
                            !DenialCountsAsViolation(ref placeState.ItemClaimDenials,
                                ItemClaimDenialGrace, ItemClaimDenialWindow))
                            return false;
                        return Violation(
                            placeState,
                            "item",
                            placeable
                                ? $"placed block type {placedBlockType} without owning the item"
                                : $"placed block type {placedBlockType} which no inventory item can place");
                    }

                    // Accepted placement: remember it so a later kick can roll
                    // the burst back. A still-later deny can't have applied the
                    // write, and !alreadySame guarantees the old world type
                    // differs - the rollback's type match skips unapplied cells.
                    if (!alreadySame && hasCell)
                        RecordPlacement(placeState, cellX, cellY, cellZ, placedBlockType);
                }
            }

            // Dig swings ride the held tool's CoolDownTime (>=0.25 s on every
            // vanilla mining tool); a DigMessage(digging) stream tighter than
            // that is a rapid-swing mod - the "rapid fire" cheat applied to
            // tools and bare hands. Denied swings never reach the seeding
            // below, so the flood can't mint removal rights either.
            if (typeName == "DNA.CastleMinerZ.Net.DigMessage" &&
                data != null && data.Length >= 30 && data[1] == 0)
            {
                PlayerTrustState swingState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(swingState) &&
                    !CheckUseRate(swingState, ref swingState.SwingNextAllowedUtc,
                        SwingIntervalSec, $"dig swing faster than {SwingIntervalSec:F2}s interval"))
                    return false;
            }

            // Dig claims: a block can only be removed if it is diggable in vanilla
            // (bedrock/air never are). DigMessage names the removed block directly;
            // AlterBlock-to-Empty is resolved against world state when provable.
            if (ItemPolicy.Ready && TryReadDigClaim(typeName, data,
                out int dx, out int dy, out int dz, out int dugBlockType,
                out float dhx, out float dhy, out float dhz,
                out float dnx, out float dny, out float dnz))
            {
                PlayerTrustState digState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(digState))
                {
                    if (dugBlockType > 0 && ItemPolicy.IsUndiggableBlockType(dugBlockType))
                    {
                        NoteDisputedCell(digState, dx, dy, dz, longLived: true);
                        return Violation(digState, "item", $"dug undiggable block type {dugBlockType}");
                    }

                    // Server-side air can't be "dug": the client saw a block the
                    // server already counts empty - a desync, not a cheat, and
                    // nothing can be mined out of it. Undiggable types only count
                    // as a violation when the server agrees a real block is there.
                    if (dugBlockType == 0 && TryGetWorldBlock(dx, dy, dz, out int actualType) &&
                        actualType > 0 && ItemPolicy.IsUndiggableBlockType(actualType))
                    {
                        NoteDisputedCell(digState, dx, dy, dz, longLived: true);
                        return Violation(digState, "item", $"dug undiggable block type {actualType}");
                    }
                }

                // Seed the expected drop for this removal. DigMessage arrives per
                // swing BEFORE the pickup and the AlterBlock-to-Empty confirm, so
                // it seeds an unconfirmed record; the AlterBlock confirms it.
                bool isRemovalWrite = typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage";
                long cellKey = PackCell(dx, dy, dz);
                bool resolved = TryGetWorldBlock(dx, dy, dz, out int removedType);

                // An Empty write on a real diggable block with no dig-seed came
                // from a client that never dug it - chopper-style mass removal.
                // Deny the write so the world block survives the kick. The seed
                // check covers the 6 face-neighbors too: the dig claim derives
                // its cell from the face hit point minus half a normal, which
                // can floor into the adjacent cell on edge hits.
                bool spawnerEvent = false;
                if (isRemovalWrite && !IsExempt(digState) &&
                    resolved && removedType != 0 &&
                    !HasPendingSeedNear(digState, dx, dy, dz, removedType))
                {
                    // A triggered spawner writes Empty on two kinds of cells,
                    // all authored by the triggering client's sim, never a dig:
                    // its own marker transitions, and GameUtils.
                    // ClearSurroundingBlocks erases a 6x3x6 box above the
                    // marker (x/z +-3, y +1..+3) - skipping spawn markers and
                    // loot blocks. Legit only where a real marker exists.
                    spawnerEvent = ItemPolicy.IsRemoteAlterBlockType(removedType)
                        ? _spawnerLocator != null &&
                          _spawnerLocator(dx + 0.5f, dy + 0.5f, dz + 0.5f)
                        : !ItemPolicy.IsLootAlterBlockType(removedType) &&
                          HasSpawnerMarkerBelow(dx, dy, dz);
                    if (spawnerEvent &&
                        (digState.SpawnerEditCells ??= new HashSet<long>()).Count < 512)
                    {
                        digState.SpawnerEditCells.Add(cellKey);
                    }
                    if (!spawnerEvent)
                    {
                        NoteDisputedCell(digState, dx, dy, dz, longLived: true);
                        QueueChopperRestore(digState, dx, dy, dz);
                        // Denied either way; only bursts count as violations - a
                        // lone seedless removal is most likely a dig claim eaten
                        // upstream (FloodGuard/rate drop), not a forged write.
                        if (!DenialCountsAsViolation(
                                ref digState.RemovalDenials, RemovalDenialGrace, RemovalDenialWindow))
                        {
                            SeedDeniedRemovalDrop(digState, cellKey, removedType);
                            return false;
                        }
                        return Violation(digState, "item",
                            $"removed block type {removedType} at {dx},{dy},{dz} without digging it");
                    }
                }

                if (resolved && removedType != 0)
                {
                    RecordDigSeed(digState, dx, dy, dz, removedType, confirm: isRemovalWrite,
                        removalSeed: !isRemovalWrite, claimedType: isRemovalWrite ? 0 : dugBlockType,
                        hx: dhx, hy: dhy, hz: dhz, nx: dnx, ny: dny, nz: dnz);
                    // A removal that passed on a neighbor's seed still confirms
                    // the dig that made it - without this, the pickup consumed
                    // the off-by-one cell's record and its expiry looks like a
                    // "dug but never removed" forgery.
                    if (isRemovalWrite)
                    {
                        ConfirmSeedsNear(digState, dx, dy, dz);
                        // Spawner-event removals don't arm the chopper
                        // rollback - a nearby denial must never resurrect a
                        // legitimately consumed spawn marker or cleared block.
                        if (!spawnerEvent && !ItemPolicy.IsRemoteAlterBlockType(removedType))
                        {
                            digState.LastRemovalX = dx;
                            digState.LastRemovalY = dy;
                            digState.LastRemovalZ = dz;
                            digState.LastRemovalType = removedType;
                            digState.LastRemovalUtc = DateTime.UtcNow;
                        }
                    }
                    SeedDependents(digState, dx, dy, dz, removedType);
                }
                else if (!resolved && isRemovalWrite &&
                    !digState.PendingDrops.ContainsKey(cellKey))
                {
                    // Unresolvable cell: record an "any world-spawnable" drop so a
                    // real desync can't forge "nothing drops here".
                    digState.PendingDrops[cellKey] = new PendingDrop
                    {
                        ItemIds = null,
                        Remaining = PendingDropAllowance,
                        Confirmed = true,
                        ExpiryUtc = DateTime.UtcNow + ConfirmedDropTtl,
                    };
                }
            }

            // RemoveBlocksMessage: [id][NumBlocks:4][IntVector3 cells][DoDigEffects:1]
            // Every listed cell must be authorized before the world handler may
            // apply it: explosion removals (flag clear) need a live blast record
            // nearby; enemy digging (flag set) needs the sender to own a live
            // enemy and stays under a tight cell cap. Nothing else removes blocks.
            if (typeName == "DNA.CastleMinerZ.Net.RemoveBlocksMessage" &&
                data != null && data.Length >= 6)
            {
                int num = BitConverter.ToInt32(data, 1);
                if (num > 0 && num <= 4096 && data.Length >= 6 + num * 12)
                {
                    bool enemyDigging = data[5 + num * 12] != 0;
                    PlayerTrustState rmState = GetState(senderId, resolveName, resolveRemoteId);
                    bool exempt = IsExempt(rmState);
                    var authorized = new HashSet<long>();
                    int denied = 0;
                    for (int i = 0; i < num; i++)
                    {
                        int off = 5 + i * 12;
                        int cx = BitConverter.ToInt32(data, off);
                        int cy = BitConverter.ToInt32(data, off + 4);
                        int cz = BitConverter.ToInt32(data, off + 8);
                        bool ok = exempt ||
                            (enemyDigging
                                ? AllowEnemyDigCell(rmState, senderId, authorized.Count)
                                : HasRecentBlastNear(rmState, cx, cy, cz));
                        if (ok)
                        {
                            authorized.Add(PackCell(cx, cy, cz));
                        }
                        else
                        {
                            NoteDisputedCell(rmState, cx, cy, cz);
                            if (denied++ == 0)
                                QueueChopperRestore(rmState, cx, cy, cz);
                        }
                    }
                    rmState.AuthorizedRemovalCells = authorized;
                    if (denied > 0 && !exempt &&
                        DenialCountsAsViolation(
                            ref rmState.RemovalDenials, RemovalDenialGrace, RemovalDenialWindow))
                    {
                        Violation(rmState, "item",
                            $"removed {denied} block(s) without a blast or enemy source");
                    }

                    // Explosion removals can harvest ore/wall cells; enemy
                    // digging never yields pickups, so those cells seed nothing.
                    if (!enemyDigging && ItemPolicy.Ready)
                    {
                        for (int i = 0; i < num; i++)
                        {
                            int off = 5 + i * 12;
                            int cx = BitConverter.ToInt32(data, off);
                            int cy = BitConverter.ToInt32(data, off + 4);
                            int cz = BitConverter.ToInt32(data, off + 8);
                            // A forged removal list with no preceding detonation
                            // claim seeds nothing - every legit blast cell sits
                            // near its blast.
                            if (!HasRecentBlastNear(rmState, cx, cy, cz))
                                continue;
                            if (!TryGetWorldBlock(cx, cy, cz, out int removedType))
                                continue;
                            var ids = new HashSet<int>();
                            ItemPolicy.CollectExplosionDropIds(removedType, ids);
                            // Blast dependents (attached/door blocks) drop their item;
                            // a destroyed loot block rolls the whole loot table
                            // (ProcessLootBlockOutput) - weapons, tools, ammo.
                            if (ItemPolicy.IsFacingBlockType(removedType) ||
                                ItemPolicy.IsDoorBlockType(removedType) ||
                                ItemPolicy.IsLootBlockType(removedType))
                            {
                                ItemPolicy.CollectDigDropIds(removedType, ids);
                            }
                            if (ids.Count == 0)
                                continue;
                            if (rmState.PendingDrops.Count >= MaxPendingDrops)
                                break;
                            rmState.PendingDrops[PackCell(cx, cy, cz)] = new PendingDrop
                            {
                                ItemIds = ids,
                                Remaining = PendingBlastAllowance,
                                Confirmed = true,
                                ExpiryUtc = DateTime.UtcNow + ConfirmedDropTtl,
                            };
                        }
                    }
                }
            }

            if (_config.TrustVisitorEditBlock && VisitorBlockedMessages.Contains(typeName))
            {
                PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
                if (state.Rank == ServerCommandRank.Visitor && !IsExempt(state))
                {
                    WarnVisitor(state);
                    return Violation(state, "visitor-edit", ShortName(typeName));
                }
            }

            if (CombatClaimNames.Contains(typeName))
            {
                PlayerTrustState combatState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(combatState) && !CheckCombatClaim(typeName, data, combatState))
                    return false;
            }

            // KillEnemyMessage: [id][EnemyID:4][TargetID:4][WeaponID:2][KillerID:1]
            // When the sender claims the kill, the claimed weapon must be one they own.
            if (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage" &&
                data != null && data.Length >= 12 && data[11] == senderId)
            {
                PlayerTrustState killState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(killState) &&
                    !CheckClaimedWeapon(killState, BitConverter.ToInt16(data, 9), allowUnknown: false))
                    return false;
            }

            // KillDragonMessage: [id][Location:12][WeaponID:2][KillerID:1]
            // Weapon claim plus a sanity position check on the claimed kill spot.
            if (typeName == "DNA.CastleMinerZ.Net.KillDragonMessage" &&
                data != null && data.Length >= 16 && data[15] == senderId)
            {
                PlayerTrustState killState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(killState))
                {
                    if (!CheckClaimedPosition(killState, data, 1, _config.TrustSpawnReach, "dragon kill"))
                        return false;
                    if (!CheckClaimedWeapon(killState, BitConverter.ToInt16(data, 13), allowUnknown: false))
                        return false;
                }
                if (killState.HasBaseline)
                {
                    RecordEnemyKill(killState, BitConverter.ToSingle(data, 1),
                        BitConverter.ToSingle(data, 5), BitConverter.ToSingle(data, 9),
                        DragonKillDropAllowance);
                }
            }
            // Live-enemy registry. SpawnEnemyMessage:
            // [0]=id | [1..12]=SpawnPosition | [13..24]=SpawnerPosition |
            // [25]=EnemyType | [26..29]=EnemyID
            // Accepted spawns are recorded; control claims naming an EnemyID the
            // server never saw spawned are forged by definition. EnemyIDs are
            // per-client counters, so records group by the bare id.
            if (typeName == "DNA.CastleMinerZ.Net.SpawnEnemyMessage" &&
                data != null && data.Length >= 30)
            {
                RegisterEnemySpawn(senderId, BitConverter.ToInt32(data, 26), data[25]);
            }
            else if ((typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage" ||
                      typeName == "DNA.CastleMinerZ.Net.EnemyGiveUpMessage" ||
                      typeName == "DNA.CastleMinerZ.Net.SpeedUpEnemyMessage") &&
                     data != null && data.Length >= 5)
            {
                PlayerTrustState enemyState = GetState(senderId, resolveName, resolveRemoteId);
                int enemyId = BitConverter.ToInt32(data, 1);
                if (!IsExempt(enemyState) && !EnemyKnown(enemyId))
                {
                    return HandleUnknownEnemyClaim(enemyState, enemyId);
                }
                if (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage")
                {
                    // Canary claims were already struck in CheckKillClaimEvidence;
                    // denying keeps the plant alive on every other client's sim.
                    if (!IsExempt(enemyState) && IsCanaryEnemy(enemyId))
                        return false;
                    // Vanilla runs the drop roll on the CREDITED killer's
                    // client (data[11]); the reporter can be any client that
                    // observed the kill. Seed only on a live, non-canary
                    // record - dead-grace duplicates and trap kills mint
                    // nothing.
                    bool wasLive = EnemyKnown(enemyId) && !IsCanaryEnemy(enemyId);
                    DeregisterEnemy(enemyId);
                    byte killerId = data.Length >= 12 ? data[11] : senderId;
                    PlayerTrustState killerState = killerId == senderId
                        ? enemyState
                        : GetState(killerId, null, null);
                    if (wasLive)
                        RecordEnemyKill(killerState,
                            killerState.LastX, killerState.LastY, killerState.LastZ);
                }
            }

            return true;
        }

        /// <summary>
        /// Gate for messages that are relayed peer-to-peer (not host-bound), e.g.
        /// melee sent directly to the victim. Only invoked for genuinely
        /// peer-directed routes (CH1 OP3, CH0 with a nonzero recipient); host-bound
        /// traffic is covered by AllowMessage and passing it through here would
        /// double-count rates and ammo.
        /// </summary>
        public bool AllowRelayedMessage(
            byte senderId,
            string typeName,
            byte[] data,
            bool isPeerDirected,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            bool allowed = AllowRelayedMessageCore(senderId, typeName, data, isPeerDirected, resolveName, resolveRemoteId);
            // Only genuinely peer-directed traffic reaches the tracker here;
            // host-bound traffic already came through AllowMessage.
            if (allowed && isPeerDirected && _config.LogCombat)
                ObserveShadowMessage(senderId, typeName, data);
            return allowed;
        }

        private bool AllowRelayedMessageCore(
            byte senderId,
            string typeName,
            byte[] data,
            bool isPeerDirected,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            if (!isPeerDirected)
                return true;

            // Same evidence-before-spam-guard ordering as AllowMessage.
            if (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage" &&
                data != null && data.Length >= 5)
            {
                if (CheckKillClaimEvidence(
                    GetState(senderId, resolveName, resolveRemoteId),
                    BitConverter.ToInt32(data, 1),
                    data.Length >= 11 ? BitConverter.ToInt16(data, 9) : 0,
                    data.Length >= 12 ? data[11] : senderId))
                {
                    return false; // provably fabricated - never relays
                }
            }

            // Peer-directed traffic bypasses AllowMessage entirely, so the full
            // check suite runs here - starting with the same flood limits as
            // host-bound traffic.
            // without them a peer-directed shot/control burst is unbounded.
            foreach ((string type, int limit, double windowSec) in RateLimits)
            {
                if (type != typeName)
                    continue;
                if (senderId != 0)
                {
                    PlayerTrustState rateState = GetState(senderId, resolveName, resolveRemoteId);
                    if (!IsExempt(rateState) &&
                        !TryUseSpawnerClearRateAllowance(rateState, typeName, data, DateTime.UtcNow) &&
                        !BumpRate(rateState, typeName, limit, windowSec))
                        return false;
                }
                break;
            }

            // Enemy traffic may travel either path; spawns register on both
            // (deduped by spawner+id) and control claims are registry-checked
            // on both so a path change can't launder a forged id.
            if (typeName == "DNA.CastleMinerZ.Net.SpawnEnemyMessage" &&
                data != null && data.Length >= 30)
            {
                RegisterEnemySpawn(senderId, BitConverter.ToInt32(data, 26), data[25]);
                return true;
            }
            if (senderId != 0 && HostBoundOnlyNames.Contains(typeName))
            {
                PlayerTrustState relayState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(relayState))
                    return Violation(relayState, "relay",
                        $"forged peer-directed {ShortName(typeName)}");
                return true;
            }
            if (senderId != 0 &&
                (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage" ||
                 typeName == "DNA.CastleMinerZ.Net.EnemyGiveUpMessage" ||
                 typeName == "DNA.CastleMinerZ.Net.SpeedUpEnemyMessage") &&
                data != null && data.Length >= 5)
            {
                PlayerTrustState enemyState = GetState(senderId, resolveName, resolveRemoteId);
                int enemyId = BitConverter.ToInt32(data, 1);
                if (!IsExempt(enemyState) && !EnemyKnown(enemyId))
                {
                    return HandleUnknownEnemyClaim(enemyState, enemyId);
                }
                if (typeName == "DNA.CastleMinerZ.Net.KillEnemyMessage")
                {
                    // Canary claims were already struck in CheckKillClaimEvidence;
                    // denying keeps the plant alive on every other client's sim.
                    if (!IsExempt(enemyState) && IsCanaryEnemy(enemyId))
                        return false;
                    // Same credited-killer rule as the host-bound path:
                    // allowance goes to data[11], only on a live, non-canary
                    // record.
                    bool wasLive = EnemyKnown(enemyId) && !IsCanaryEnemy(enemyId);
                    DeregisterEnemy(enemyId);
                    byte killerId = data.Length >= 12 ? data[11] : senderId;
                    PlayerTrustState killerState = killerId == senderId
                        ? enemyState
                        : GetState(killerId, null, null);
                    if (wasLive)
                        RecordEnemyKill(killerState,
                            killerState.LastX, killerState.LastY, killerState.LastZ);
                }
                return true;
            }

            // Same impersonation rule on the peer path - a sudo sent directly
            // to the victim must not bypass the host-bound check.
            if (senderId != 0 &&
                typeName == "DNA.CastleMinerZ.Net.BroadcastTextMessage" &&
                TryReadBroadcastText(data, out string relayText))
            {
                PlayerTrustState chatState = GetState(senderId, resolveName, resolveRemoteId);
                if (!IsExempt(chatState) && IsImpersonating(chatState, relayText))
                    return Violation(chatState, "relay", "chat message forged in another player's name");
            }

            if (senderId == 0 || !CombatClaimNames.Contains(typeName))
                return true;

            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            if (IsExempt(state))
                return true;

            return CheckCombatClaim(typeName, data, state);
        }

        /// <summary>
        /// A client authored a server-only message type (KickMessage,
        /// AppointServer, TimeOfDay, world-data serves...). Vanilla clients can
        /// NEVER emit these - corruption fails type-name lookup upstream - so
        /// there is no desync story and no grace window: kick immediately.
        /// This is what makes a "kick everyone" tool die on its first forged
        /// KickMessage instead of after the 5-violation count.
        /// </summary>
        public void NotifyForgedHostAuthority(
            byte senderId,
            string typeName,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            if (senderId == 0)
                return;

            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            Violation(state, "host-forgery", ShortName(typeName));

            if (state.KickIssued)
                return;
            string target = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
            IssueKick(state, "host-forgery", ShortName(typeName), target);
            _log($"[Trust] {state.Name}: kicked instantly for forging server-authority {ShortName(typeName)}.");
        }

        /// <summary>
        /// A client sent a packet whose declared layout does not fit the bytes
        /// on the wire (bad length prefix, truncated payload). Steam's reliable
        /// transport delivers messages intact, so this is always crafted traffic
        /// - the crash tools' stock in trade. Counts as a protocol violation;
        /// the packet itself is already dropped upstream.
        /// </summary>
        public void NotifyMalformedPacket(
            byte senderId,
            string detail,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            if (senderId == 0)
                return;

            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            if (IsExempt(state))
                return;

            state.MalformedPackets++;
            if (state.MalformedPackets <= 3 || state.MalformedPackets % 200 == 0)
                _log($"[Security] Dropped malformed packet from {state.Name} (#{state.MalformedPackets}): {detail}.");

            Violation(state, "protocol", $"malformed wire packet ({detail})");
        }

        /// <summary>
        /// Validates client-authored combat/explosive claims against server state:
        /// claimed positions must be near the sender's last accepted position, and
        /// claimed weapons must be items the player provably owns.
        /// </summary>
        private bool CheckCombatClaim(string typeName, byte[] data, PlayerTrustState state)
        {
            if (data == null)
                return false;

            // MeleePlayerMessage: [0]=id | [1..2]=weapon | [3..14]=claimed DamageSource
            if (typeName == MeleePlayerTypeName)
            {
                if (data.Length < 15)
                    return false;
                if (!CheckClaimedPosition(state, data, 3, _config.TrustMeleeReach, "melee origin"))
                    return false;
                int weaponId = BitConverter.ToInt16(data, 1);
                if (!CheckHeldItem(state, weaponId, "melee swing"))
                    return false;
                if (!CheckUseRate(state, ref state.SwingNextAllowedUtc, SwingIntervalSec,
                        $"melee swing faster than {SwingIntervalSec:F2}s interval"))
                    return false;
                return CheckClaimedWeapon(state, weaponId, allowUnknown: false);
            }

            // GunshotMessage: [0]=id | [1..12]=Direction | [13..14]=weapon
            if (typeName == GunshotTypeName)
            {
                if (data.Length < 15)
                    return false;
                if (!CheckDirectionSane(state, data, 1, "gunshot"))
                    return false;
                int weaponId = BitConverter.ToInt16(data, 13);
                state.GunsFired.Add(weaponId);
                if (!CheckHeldItem(state, weaponId, "gunshot"))
                    return false;
                if (!CheckClaimedWeapon(state, weaponId, allowUnknown: false))
                    return false;
                if (!CheckFireRate(state, weaponId, "gunshot"))
                    return false;
                return CheckShotAmmo(state, weaponId, "gunshot");
            }

            // ShotgunShotMessage: [0]=id | [1..60]=5 directions | [61..62]=weapon
            if (typeName == ShotgunShotTypeName)
            {
                if (data.Length < 63)
                    return false;
                for (int i = 0; i < 5; i++)
                {
                    if (!CheckDirectionSane(state, data, 1 + i * 12, "shotgun pellet"))
                        return false;
                }
                int weaponId = BitConverter.ToInt16(data, 61);
                state.GunsFired.Add(weaponId);
                if (!CheckHeldItem(state, weaponId, "shotgun shot"))
                    return false;
                if (!CheckClaimedWeapon(state, weaponId, allowUnknown: false))
                    return false;
                if (!CheckFireRate(state, weaponId, "shotgun shot"))
                    return false;
                return CheckShotAmmo(state, weaponId, "shotgun shot");
            }

            // FireRocketMessage: [0]=id | [1..12]=Direction | [13..24]=Position | [25]=Guided | [26..27]=weapon
            if (typeName == FireRocketTypeName)
            {
                if (data.Length < 28)
                    return false;
                if (!CheckDirectionSane(state, data, 1, "rocket"))
                    return false;
                if (!CheckClaimedPosition(state, data, 13, _config.TrustMaxReach, "rocket origin"))
                    return false;
                int weaponId = BitConverter.ToInt16(data, 26);
                state.GunsFired.Add(weaponId);
                if (!CheckHeldItem(state, weaponId, "rocket launch"))
                    return false;
                if (!CheckClaimedWeapon(state, weaponId, allowUnknown: false))
                    return false;
                if (!CheckFireRate(state, weaponId, "rocket launch"))
                    return false;
                return CheckShotAmmo(state, weaponId, "rocket launch");
            }

            // GrenadeMessage: [0]=id | [1..12]=Direction | [13..24]=Position | [25]=type | [26..29]=fuse
            if (typeName == GrenadeTypeName)
            {
                if (data.Length < 26)
                    return false;
                if (!CheckDirectionSane(state, data, 1, "grenade"))
                    return false;
                if (!CheckClaimedPosition(state, data, 13, _config.TrustMaxReach, "grenade origin"))
                    return false;
                // A thrown grenade consumes the grenade item itself - no clip
                // concept. The held item must be that grenade, except Sticky
                // rounds from a launcher: the launcher class emits this message
                // itself (even on equip) and pays its own ammo instead.
                if (ItemPolicy.Ready &&
                    ItemPolicy.TryGetGrenadeItem(data[25], out int grenadeItemId))
                {
                    if (state.HeldItemId >= 0 &&
                        ItemPolicy.IsGrenadeLauncherHeldId(state.HeldItemId) &&
                        data[25] == ItemPolicy.LauncherGrenadeType)
                        return true;
                    if (state.HeldItemId >= 0 && state.HeldItemId != grenadeItemId &&
                        !CheckHeldItemDeny(state, grenadeItemId, "grenade throw"))
                        return false;
                    return CheckThrownAmmo(state, grenadeItemId, "grenade throw");
                }
                return true;
            }

            // ChangeCarriedItemMessage: [0]=id | [1..2]=claimed held item
            if (typeName == ChangeCarriedItemTypeName)
            {
                if (data.Length < 3)
                    return false;
                int heldId = BitConverter.ToInt16(data, 1);
                if (!CheckClaimedWeapon(state, heldId, allowUnknown: false))
                    return false;
                state.HeldItemId = heldId;
                // Each inventory item instance owns its RoundsInClip; a legit
                // swap means whatever the client loads next is a fresh clip.
                state.ClipWeaponId = heldId;
                state.ClipShotsFired = 0;
                state.ReloadingActive = false;
                return true;
            }

            // DetonateExplosiveMessage: [0]=id | [1..12]=IntVector3 location | ...
            // Blast positions land where thrown explosives settled - blast cap, not arm's reach.
            // The claimed position must hold a saved TNT/C4 block: vanilla sends this
            // while the explosive is still placed, so a mod block (whose placement
            // was already rejected) or bare terrain can never be detonated.
            if (typeName == DetonateExplosiveTypeName)
            {
                if (data.Length < 13)
                    return false;

                int bx = BitConverter.ToInt32(data, 1);
                int by = BitConverter.ToInt32(data, 5);
                int bz = BitConverter.ToInt32(data, 9);

                if (!CheckClaimedPosition(state, bx, by, bz, MaxReachFor(typeName), "detonation"))
                    return false;

                if (ItemPolicy.Ready && _savedBlockLookup != null &&
                    (!TryGetSavedBlock(bx, by, bz, out int blockAt) || !ItemPolicy.IsExplosiveBlockType(blockAt)))
                {
                    return Violation(state, "item", "detonation claim has no explosive block at position");
                }

                RecordBlast(state, bx, by, bz);
                // [14] = ExplosiveType byte: inside the kill radius this is a
                // guaranteed-kill blast for every living player standing there.
                if (data.Length >= 15)
                    ArmLethalBlast(bx + 0.5f, by + 0.5f, bz + 0.5f, data[14]);
                return true;
            }

            // DetonateFireballMessage / DetonateGrenadeMessage / DetonateRocketMessage:
            // [0]=id | [1..12]=Vector3 location | ...
            if (typeName == DetonateFireballTypeName ||
                typeName == DetonateGrenadeTypeName ||
                typeName == DetonateRocketTypeName)
            {
                if (data.Length < 13)
                    return false;
                if (!CheckClaimedPosition(state, data, 1, MaxReachFor(typeName), "detonation"))
                    return false;
                float blastX = BitConverter.ToSingle(data, 1);
                float blastY = BitConverter.ToSingle(data, 5);
                float blastZ = BitConverter.ToSingle(data, 9);
                RecordBlast(state, blastX, blastY, blastZ);
                if (typeName == DetonateFireballTypeName)
                {
                    SeedFireballDependents(state, data);
                }
                else if (typeName == DetonateRocketTypeName)
                {
                    // [13]=HitDragon (dragon damage channel, no player splash)
                    // [14]=ExplosiveType
                    if (data.Length >= 15 && data[13] == 0)
                        ArmLethalBlast(blastX, blastY, blastZ, data[14]);
                }
                else if (data.Length >= 14 && (data[13] == 0 || data[13] == 3))
                {
                    // GrenadeType HE(0)/Sticky(3) both detonate as HEGrenade;
                    // Smoke/Flash never damage players.
                    ArmLethalBlast(blastX, blastY, blastZ, 4);
                }
                return true;
            }

            return true;
        }

        /// <summary>
        /// The claimed world position must be near where the server last accepted
        /// the sender to be. Skipped until a position baseline exists.
        /// </summary>
        private bool CheckClaimedPosition(PlayerTrustState state, byte[] data, int offset, float maxDist, string what)
        {
            return CheckClaimedPosition(
                state,
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8),
                maxDist,
                what);
        }

        private bool CheckClaimedPosition(PlayerTrustState state, float x, float y, float z, float maxDist, string what)
        {
            if (!state.HasBaseline)
                return true;

            double dx = x - state.LastX;
            double dy = y - state.LastY;
            double dz = z - state.LastZ;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= maxDist + StaleReachSlack(state))
                return true;

            return Violation(state, "reach", $"{what} {dist:F0}m away (max {maxDist:F0}m)");
        }

        /// <summary>
        /// Reach uncertainty from a stale last-position: while updates are
        /// eaten upstream (FloodGuard blackhole, rate drop) the frozen baseline
        /// drifts from the client's true position at ~walk/sprint speed. The
        /// slack grows with staleness and caps at the ~3s blackhole envelope -
        /// a forger can only widen reach by silencing their own updates, and
        /// edits never refresh the baseline so it can't be walked outward.
        /// </summary>
        private static float StaleReachSlack(PlayerTrustState state)
        {
            if (state.LastUpdateUtc == DateTime.MinValue)
                return 0f;
            double ageSec = (DateTime.UtcNow - state.LastUpdateUtc).TotalSeconds;
            if (ageSec <= 0)
                return 0f;
            return (float)Math.Min(ageSec * 6.0, 18.0);
        }

        /// <summary>
        /// A claimed weapon/item must exist in the registry and be owned (or
        /// credited) by the sender. itemId <= 0 is the empty-hands sentinel and
        /// follows allowUnknown; unknown ids are only dropped for weapon-typed
        /// claims. Skipped until an inventory baseline exists.
        /// </summary>
        private bool CheckClaimedWeapon(PlayerTrustState state, int itemId, bool allowUnknown)
        {
            if (!ItemPolicy.Ready)
                return true;

            // Bare hands (id <= 0) is always a legal claim - it means no item.
            if (itemId <= 0)
                return true;

            if (!ItemPolicy.IsKnownItemId(itemId))
                return allowUnknown;

            EnsureInventoryBaseline(state);
            if (!state.BaselineKnown)
                return true;

            int owned = state.Owned != null && state.Owned.TryGetValue(itemId, out int o) ? o : 0;
            int credited = state.Credits.TryGetValue(itemId, out int c) ? c : 0;
            if (owned + credited > 0)
                return true;

            // Freshly crafted items are claimed before the first save that would
            // prove them - vanilla never sends a "crafted" notification, so the
            // server can't see the result until reconcile. Provisional rule: the
            // claim is legal when a real recipe could have produced it from the
            // ingredients the player provably owns. The next save still enforces
            // truth - ingredients must actually be gone for the result to count,
            // so a forged item can only defer its flag, never escape it.
            if (CouldHaveCrafted(state, itemId))
                return true;

            // A reconcile already proved this exact id phantom — equipping,
            // firing, or swinging it is a confirmed cheat, not a desync:
            // kick immediately.
            if (state.DeniedClaims.TryGetValue(itemId, out int deniedId) && deniedId > 0)
            {
                string ctarget = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
                IssueKick(state, "item",
                    $"used proven-phantom item {itemId} (denied {deniedId})", ctarget);
                return false;
            }

            // Denied either way, but only DISTINCT unowned ids count as
            // violations: an item the client provably picked up (pickup grant
            // or spawn lost upstream) re-claims on every swing/switch, while a
            // forge spray hits many distinct ids fast.
            if (!NoteDeniedItemClaim(state, itemId))
                return false;
            if (!DenialCountsAsViolation(ref state.ItemClaimDenials,
                    ItemClaimDenialGrace, ItemClaimDenialWindow))
                return false;
            return Violation(state, "item", $"claimed item id {itemId} not owned");
        }

        /// <summary>
        /// True when any real recipe for itemId can be satisfied from the
        /// player's provable inventory (owned baseline + authorized credits).
        /// Used to tolerate held-item claims for not-yet-saved crafts.
        /// </summary>
        private static bool CouldHaveCrafted(PlayerTrustState state, int itemId)
        {
            var available = new Dictionary<int, int>();
            if (state.Owned != null)
            {
                foreach (KeyValuePair<int, int> kv in state.Owned)
                    available[kv.Key] = kv.Value;
            }
            foreach (KeyValuePair<int, int> kv in state.Credits)
            {
                available.TryGetValue(kv.Key, out int cur);
                available[kv.Key] = cur + kv.Value;
            }
            return TryExplainByCraft(itemId, 1, available);
        }

        // Held-item gating: vanilla can only fire/throw/swing the active item,
        // so a combat claim naming a weapon the client isn't holding is forged
        // (shoot-anything mods fire rockets from a pistol). A stale held id
        // from an eaten ChangeCarriedItem denies silently a few times; bursts
        // strike.
        private const int HeldMismatchGrace = 5;
        private static readonly TimeSpan HeldMismatchWindow = TimeSpan.FromSeconds(45);

        // Over-rate shot denials: a stalled stream can bunch a few shots past
        // the burst slack, so a handful within the window is tolerated.
        private const int FireRateGrace = 5;
        private static readonly TimeSpan FireRateWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Scheduled-arrival leaky bucket keyed to the weapon's vanilla
        /// CoolDownTime: each claim schedules the next allowed shot at
        /// interval in the future, and up to three intervals of burst slack
        /// absorb packet bunching. A sustained over-rate stream keeps
        /// overdrawing the schedule, so each further shot is denied (and only
        /// past the grace window does it count as a violation). Denied shots
        /// never reach the ammo check, so a flood can't drain the pool either.
        /// </summary>
        private bool CheckFireRate(PlayerTrustState state, int weaponId, string what)
        {
            if (!ItemPolicy.Ready ||
                !ItemPolicy.TryGetGunFireInterval(weaponId, out double interval))
                return true;
            return CheckUseRate(state, ref state.ShotNextAllowedUtc, interval,
                $"{what} faster than item {weaponId} fire rate ({interval:F2}s)");
        }

        // Vanilla swing cooldowns: knife 0.25-0.5 s, picks/axes/chainsaw 0.3 s,
        // bare hands 0.5 s - a melee or dig-swing stream tighter than this is a
        // rapid-swing mod (covers "rapid fire" applied to tools and hands).
        // Own bucket: a legit shoot->dig transition must not inherit shot debt.
        private const double SwingIntervalSec = 0.22;

        /// <summary>
        /// The bucket core shared by gun shots and melee/dig swings: each claim
        /// schedules the next allowed use at `interval` in the future.
        /// </summary>
        private bool CheckUseRate(PlayerTrustState state, ref DateTime nextAllowedUtc,
            double interval, string detail)
        {
            DateTime now = DateTime.UtcNow;
            double slack = Math.Max(interval * 3.0, 0.35);
            if (nextAllowedUtc < now)
                nextAllowedUtc = now;
            nextAllowedUtc += TimeSpan.FromSeconds(interval);
            if ((nextAllowedUtc - now).TotalSeconds <= slack)
                return true;

            nextAllowedUtc = now + TimeSpan.FromSeconds(slack);
            if (DenialCountsAsViolation(ref state.FireRateDenials, FireRateGrace, FireRateWindow))
                Violation(state, "combat", detail);
            return false;
        }

        private bool CheckHeldItem(PlayerTrustState state, int claimedItemId, string what)
        {
            if (state.HeldItemId < 0)
                return true; // never reported - can't prove a mismatch
            if (state.HeldItemId == claimedItemId)
                return true;
            // Launcher ShotFired ids stand in for their base launcher: vanilla
            // swaps the reported held item when the last rocket is spent.
            if (ItemPolicy.Ready &&
                ItemPolicy.IsRocketLauncherHeldId(state.HeldItemId) &&
                ItemPolicy.IsRocketLauncherHeldId(claimedItemId))
                return true;
            return CheckHeldItemDeny(state, claimedItemId, what);
        }

        private bool CheckHeldItemDeny(PlayerTrustState state, int claimedItemId, string what)
        {
            if (DenialCountsAsViolation(ref state.HeldItemMismatchDenials,
                    HeldMismatchGrace, HeldMismatchWindow))
            {
                Violation(state, "combat",
                    $"{what} claims item {claimedItemId} while holding item {state.HeldItemId}");
            }
            return false;
        }

        /// <summary>
        /// Projectile directions are unit-ish rays in vanilla. Non-finite or wildly
        /// scaled vectors can corrupt physics on receiving clients and only exist
        /// in forged packets.
        /// </summary>
        private bool CheckDirectionSane(PlayerTrustState state, byte[] data, int offset, string what)
        {
            float dx = BitConverter.ToSingle(data, offset);
            float dy = BitConverter.ToSingle(data, offset + 4);
            float dz = BitConverter.ToSingle(data, offset + 8);
            if (float.IsNaN(dx) || float.IsNaN(dy) || float.IsNaN(dz) ||
                float.IsInfinity(dx) || float.IsInfinity(dy) || float.IsInfinity(dz))
            {
                return Violation(state, "combat", $"{what} direction not finite");
            }
            double mag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (mag < 0.25 || mag > 4.0)
                return Violation(state, "combat", $"{what} direction magnitude {mag:F2} impossible");
            return true;
        }

        /// <summary>
        /// A shot is legal only while rounds fired stay under everything the server
        /// knows the player ever had: owned pool + authorized credits + one clip of
        /// grace (rounds loaded before our baseline are invisible to it).
        /// </summary>
        private bool CheckShotAmmo(PlayerTrustState state, int weaponId, string what)
        {
            if (!ItemPolicy.Ready)
                return true;

            // Clip check runs BEFORE spending ammo - a denied shot must not
            // burn a real bullet for a legit edge case. It only needs the
            // wire stream (reload flags), so it precedes the baseline gate.
            if (!CheckClipRound(state, weaponId, what))
                return false;

            if (!ItemPolicy.TryGetGunAmmo(weaponId, out int ammoId, out int clip))
            {
                CreditShotDamage(state, weaponId);
                return true; // ammo-less weapon
            }

            EnsureInventoryBaseline(state);
            if (!ConsumeAmmo(state, ammoId, Math.Max(0, clip), what))
                return false;

            if (!state.AmmoGunsSeen.TryGetValue(ammoId, out HashSet<int> gunsSeen))
                state.AmmoGunsSeen[ammoId] = gunsSeen = new HashSet<int>();
            gunsSeen.Add(weaponId);
            CreditShotDamage(state, weaponId);
            return true;
        }

        /// <summary>
        /// A shot that passed every gate banks the most damage vanilla could
        /// ever deal with it: the weapon's per-trigger max enemy damage
        /// (ItemPolicy.TryGetGunMaxEnemyDamage). Capped so stockpiling can't
        /// launder a later deficit.
        /// </summary>
        private static void CreditShotDamage(PlayerTrustState state, int weaponId)
        {
            if (ItemPolicy.TryGetGunMaxEnemyDamage(weaponId, out float dmg) && dmg > 0f)
            {
                state.DamageBudget = Math.Min(DamageBudgetCap,
                    state.DamageBudget + dmg);
            }
        }

        private const float DamageBudgetCap = 500f;
        private const float DamageDeficitLimit = -100f;
        // Co-op last-hit grace: a teammate finishing softened enemies runs a
        // small deficit legitimately; only a sustained one-shot spree trips.
        private const int DamageDeficitGrace = 3;
        // Matches the violation-bucket decay: a cheater hovering around the
        // limit keeps accumulating strikes instead of resetting the count
        // every time cheap kills + spray pull the bank back above the line.
        private static readonly TimeSpan DamageDeficitWindow = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Shadow clip accounting: vanilla lets exactly ClipCapacity rounds
        /// fire between reloads, and the Reloading flag streams (reliably) for
        /// the whole reload + auto-raises on clip-empty. A pinned-clip mod
        /// (8/9999 forever) fires unbounded rounds without ever producing that
        /// flag. Slack of one extra clip covers dual-wield swaps whose
        /// ChangeCarriedItem was eaten upstream; past it every shot is denied
        /// and feeds the shared denial-grace -> violation path.
        /// </summary>
        /// <summary>
        /// Marks the shadow clip refilled once the Reloading flag has been up
        /// for the held gun's vanilla ReloadTime (x0.8 jitter margin). Called
        /// on every flagged update and at the flag-off edge - a reload that
        /// ran its full duration refills either way; an early cancel (vanilla:
        /// firing aborts reload) or a faked one-frame flag just doesn't reset.
        /// </summary>
        private void TryCompleteClipReload(PlayerTrustState state, DateTime now)
        {
            if (state.ReloadRefilled)
                return;
            double need = 0.57; // smallest vanilla ReloadTime (launchers/shotguns)
            if (state.HeldItemId >= 0 &&
                ItemPolicy.TryGetGunReloadSeconds(state.HeldItemId, out double sec))
            {
                need = sec;
            }
            if (now - state.ReloadStartUtc >= TimeSpan.FromSeconds(need * 0.8))
            {
                state.ClipShotsFired = 0;
                if (state.HeldItemId >= 0)
                    state.ClipWeaponId = state.HeldItemId;
                state.ReloadRefilled = true;
            }
        }

        // Flag-on updates stream every frame via ReliableInOrder for the
        // whole reload; the flag-OFF edge is lossy InOrder and can drop, so
        // only a fresh flag-on sighting proves the client is still inside
        // the reload window. Real reloads refresh in <50ms; a 350ms gap
        // means the reload ended unobserved (or updates stopped entirely).
        private static readonly TimeSpan ReloadFlagFreshWindow = TimeSpan.FromMilliseconds(350);

        private bool CheckClipRound(PlayerTrustState state, int weaponId, string what)
        {
            // Vanilla can't fire while the Reloading flag is up at all.
            if (state.ReloadingActive)
            {
                if ((DateTime.UtcNow - state.LastReloadFlagUtc) <= ReloadFlagFreshWindow)
                {
                    if (DenialCountsAsViolation(ref state.ClipOverflowDenials,
                            ClipOverflowGrace, ClipOverflowWindow))
                    {
                        return Violation(state, "item",
                            $"{what} while reload flag set");
                    }
                    return false;
                }
                // Stale flag: the flag-off update was eaten - treat the
                // reload as ended unobserved rather than deny honest shots.
                state.ReloadingActive = false;
            }

            if (!ItemPolicy.TryGetGunClipCapacity(weaponId, out int capacity) || capacity <= 0)
                return true; // no clip model for this weapon

            if (state.ClipWeaponId != weaponId)
            {
                // First shot claim for a weapon we never saw carried - the
                // ChangeCarriedItem may be lost upstream; start a fresh clip
                // rather than deny on missing state.
                state.ClipWeaponId = weaponId;
                state.ClipShotsFired = 0;
            }

            state.ClipShotsFired++;
            if (state.ClipShotsFired <= capacity * 2)
                return true;

            if (DenialCountsAsViolation(ref state.ClipOverflowDenials,
                    ClipOverflowGrace, ClipOverflowWindow))
            {
                return Violation(state, "item",
                    $"{what} past clip capacity {capacity} with no reload observed");
            }
            return false;
        }

        private const int ClipOverflowGrace = 3;
        private static readonly TimeSpan ClipOverflowWindow = TimeSpan.FromSeconds(30);

        /// <summary>Thrown consumables spend the item itself with no clip grace.</summary>
        private bool CheckThrownAmmo(PlayerTrustState state, int itemId, string what)
        {
            EnsureInventoryBaseline(state);
            if (!state.BaselineKnown)
                return true;
            return ConsumeAmmo(state, itemId, 0, what);
        }

        private bool ConsumeAmmo(PlayerTrustState state, int ammoId, int clipGrace, string what)
        {
            // Ledger the observed spend unconditionally: a baseline that
            // loads or adopts later compares the claimed stack against it.
            // Without this, shots fired before the baseline arrived were
            // invisible to the save reconcile.
            state.AmmoSpent.TryGetValue(ammoId, out int spent);
            spent += 1;
            state.AmmoSpent[ammoId] = spent;
            if (!state.BaselineKnown)
                return true;

            int owned = state.Owned != null && state.Owned.TryGetValue(ammoId, out int o) ? o : 0;
            int credited = state.Credits.TryGetValue(ammoId, out int c) ? c : 0;
            if (spent <= owned + credited + clipGrace)
                return true;

            // A reconcile already proved this ammo id phantom — rounds past the
            // provable pool are the phantom stack being spent: instant kick.
            if (state.DeniedClaims.TryGetValue(ammoId, out int deniedAmmo) && deniedAmmo > 0)
            {
                string atarget = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
                IssueKick(state, "item",
                    $"{what} spending proven-phantom ammo item {ammoId} (denied {deniedAmmo})",
                    atarget);
                return false;
            }

            // Denied shots never existed - don't burn the reserve ledger.
            state.AmmoSpent[ammoId] = spent - 1;
            return Violation(state, "item",
                $"{what} with no ammo item {ammoId} left ({spent} spent vs {owned + credited} + clip {clipGrace})");
        }

        /// <summary>
        /// Lazily pulls the persisted inventory baseline for ownership checks.
        /// Leaves BaselineKnown false when the server has nothing authoritative -
        /// a client still syncing must not be punished for looking unowned.
        /// </summary>
        private void EnsureInventoryBaseline(PlayerTrustState state)
        {
            if (state.BaselineKnown)
                return;

            Dictionary<int, int> provided = _baselineProvider?.Invoke(state.RemoteId, state.Name);
            if (provided == null)
                return;

            state.Owned = provided;
            state.AmmoSpent.Clear();
            state.BaselineKnown = true;
        }

        /// <summary>
        /// Extracts the serialized item region for a given message type and runs
        /// ItemPolicy over it, then applies provenance rules (ownership on
        /// drops, world-spawnable sets on loot spawns, reconciliation on saves).
        /// </summary>
        private ItemPolicy.ItemVerdict CheckItemMessage(
            string typeName,
            byte[] data,
            PlayerTrustState state,
            out string reason)
        {
            reason = null;
            int maxStack = _config.TrustMaxItemStack;
            bool teleportAllowed = _teleportModeAllowed;

            if (data == null)
            {
                reason = "null payload";
                return ItemPolicy.ItemVerdict.Malformed;
            }

            if (typeName == InventoryStoreTypeName)
            {
                // [0]=msgId | [1..len-3)=PlayerInventory.Save blob | [len-2]=finalSave | [len-1]=checksum
                if (data.Length < 4)
                {
                    reason = "inventory store payload too small";
                    return ItemPolicy.ItemVerdict.Malformed;
                }

                int invLen = data.Length - 3;
                var inv = new byte[invLen];
                Buffer.BlockCopy(data, 1, inv, 0, invLen);

                var items = new List<ItemPolicy.ParsedItem>();
                ItemPolicy.ItemVerdict verdict =
                    ItemPolicy.ParseInventoryBlob(inv, teleportAllowed, maxStack, items, out reason);
                if (verdict != ItemPolicy.ItemVerdict.Ok)
                    return verdict;

                if (ProvenanceApplies)
                    return ReconcileStore(state, items, out reason);

                return ItemPolicy.ItemVerdict.Ok;
            }

            if (typeName == CreatePickupTypeName)
            {
                // [msgId][SpawnPos:12][SpawnVec:12][PickupID:4][Item][Dropped:1][Display:1][checksum]
                if (data.Length < 32)
                {
                    reason = "pickup payload too small";
                    return ItemPolicy.ItemVerdict.Malformed;
                }

                int itemLen = data.Length - 3 - 29;
                if (itemLen <= 0)
                    return ItemPolicy.ItemVerdict.Ok; // no item bytes, nothing to check

                ItemPolicy.ItemVerdict verdict =
                    ItemPolicy.ValidateItemBytes(data, 29, itemLen, teleportAllowed, maxStack, out reason);
                if (verdict != ItemPolicy.ItemVerdict.Ok)
                    return verdict;

                if (ProvenanceApplies)
                    return CheckPickupSpawn(state, data, itemLen, out reason);

                return ItemPolicy.ItemVerdict.Ok;
            }

            if (typeName == ItemCrateTypeName)
            {
                // [msgId][Loc:12][Index:4][HasItem:1][Item][checksum]
                if (data.Length < 19)
                {
                    reason = "crate payload too small";
                    return ItemPolicy.ItemVerdict.Malformed;
                }

                bool hasItem = data[17] != 0;
                int itemLen = data.Length - 1 - 18;
                if (!hasItem)
                    return itemLen == 0 ? ItemPolicy.ItemVerdict.Ok : ItemPolicy.ItemVerdict.Malformed;

                if (itemLen <= 0)
                {
                    reason = "crate declared an item but carried none";
                    return ItemPolicy.ItemVerdict.Malformed;
                }

                ItemPolicy.ItemVerdict verdict =
                    ItemPolicy.ValidateItemBytes(data, 18, itemLen, teleportAllowed, maxStack, out reason);
                if (verdict != ItemPolicy.ItemVerdict.Ok)
                    return verdict;

                // Ownership of the crate transfer is enforced by the world
                // handler via AllowCrateTransfer, which sees both the old and
                // new slot contents and settles the net debit/credit after the
                // packet and crate state are fully validated.
                return ItemPolicy.ItemVerdict.Ok;
            }

            return ItemPolicy.ItemVerdict.Ok;
        }

        /// <summary>Provenance tracking is skipped in Creative - infinite items by design.</summary>
        private bool ProvenanceApplies =>
            _config.TrustProvenance && !_flightAllowed;

        /// <summary>
        /// Rolling-window counter; false once the window is over its limit.
        /// Logs once per window per type instead of on every dropped packet.
        /// </summary>
        private bool BumpRate(PlayerTrustState state, string typeName, int limit, double windowSec)
        {
            DateTime now = DateTime.UtcNow;
            if (!state.RateWindows.TryGetValue(typeName, out var w) ||
                (now - w.Start).TotalSeconds > windowSec)
            {
                w = (now, 0);
            }

            w.Count++;
            state.RateWindows[typeName] = w;
            if (w.Count <= limit)
                return true;

            if (w.Count == limit + 1)
                _log($"[Trust] {state.Name}: rate limit hit for {ShortName(typeName)} ({limit}/{windowSec:F0}s); dropping extras.");
            return false;
        }

        /// <summary>
        /// Provenance gate for CreatePickupMessage: a "dropped" pickup must be an
        /// item the player actually owns; a world spawn must be a crate-spill
        /// expectation or an item id vanilla code legitimately drops.
        /// </summary>
        private ItemPolicy.ItemVerdict CheckPickupSpawn(
            PlayerTrustState state,
            byte[] data,
            int itemLen,
            out string reason)
        {
            reason = null;
            if (!ItemPolicy.TryParseItem(data, 29, itemLen, out ItemPolicy.ParsedItem item))
                return ItemPolicy.ItemVerdict.Ok;

            bool dropped = data.Length >= 3 && data[data.Length - 3] != 0;
            if (dropped)
            {
                EnsureInventoryBaseline(state);
                if (state.BaselineKnown && !TrySpendOrCraft(state, item.Id, item.Stack))
                {
                    // A reconcile already proved this exact item id phantom —
                    // materializing it into the world is a confirmed cheat,
                    // not a desync: kick immediately.
                    if (state.DeniedClaims.TryGetValue(item.Id, out int deniedAmt) && deniedAmt > 0)
                    {
                        string ktarget = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
                        IssueKick(state, "item",
                            $"dropped {item.Stack}x proven-phantom item {item.Id} (denied {deniedAmt})", ktarget);
                        reason = $"dropped {item.Stack}x proven-phantom item {item.Id}";
                        return ItemPolicy.ItemVerdict.DeniedRepeat;
                    }

                    reason = $"dropped {item.Stack}x item {item.Id} without owning it";
                    // A phantom item (grant lost upstream) that the player
                    // sheds still fails the spend, but only DISTINCT ids count
                    // toward violations - same desync rule as held-item claims.
                    if (!NoteDeniedItemClaim(state, item.Id) ||
                        !DenialCountsAsViolation(ref state.ItemClaimDenials,
                            ItemClaimDenialGrace, ItemClaimDenialWindow))
                        return ItemPolicy.ItemVerdict.DeniedDesync;
                    return ItemPolicy.ItemVerdict.InvalidItem;
                }
                return ItemPolicy.ItemVerdict.Ok;
            }

            // Crate spill: items the breaking player's crate provably contained.
            if (item.Stack > 0 &&
                state.Spill.TryGetValue(item.Id, out int spill) &&
                spill >= item.Stack)
            {
                int left = spill - item.Stack;
                if (left <= 0) state.Spill.Remove(item.Id); else state.Spill[item.Id] = left;
                return ItemPolicy.ItemVerdict.Ok;
            }

            // World pickups must trace to something this player actually removed
            // or killed: [id][SpawnPosition:12][SpawnVector:12][PickupID:4][item]
            if (data.Length >= 13 && ItemPolicy.HasDropTables)
            {
                float px = BitConverter.ToSingle(data, 1);
                float py = BitConverter.ToSingle(data, 5);
                float pz = BitConverter.ToSingle(data, 9);
                if (TryConsumePendingDrop(state, px, py, pz, item.Id) ||
                    TryConsumeBlastDrop(state, px, py, pz, item.Id) ||
                    TryConsumeKillDrop(state, px, py, pz, item.Id))
                {
                    return ItemPolicy.ItemVerdict.Ok;
                }
            }
            else if (ItemPolicy.IsWorldSpawnable(item.Id))
            {
                // No drop tables loaded - degrade to the broad check.
                return ItemPolicy.ItemVerdict.Ok;
            }

            if (!ItemPolicy.IsWorldSpawnable(item.Id))
            {
                reason = $"spawned world pickup of non-world item {item.Id}";
                return ItemPolicy.ItemVerdict.InvalidItem;
            }

            reason = $"spawned pickup of item {item.Id} with no matching dig, blast, or kill";
            // Diagnostic-only: explain WHICH correlation state denied the
            // pickup (compute cost only when the log is enabled).
            if (_config.LogCombat)
            {
                float px2 = 0f, py2 = 0f, pz2 = 0f;
                if (data.Length >= 13)
                {
                    px2 = BitConverter.ToSingle(data, 1);
                    py2 = BitConverter.ToSingle(data, 5);
                    pz2 = BitConverter.ToSingle(data, 9);
                }
                DateTime now2 = DateTime.UtcNow;
                int liveKills = 0, expiredKills = 0;
                double nearestKillDist = -1;
                foreach ((float kx, float ky, float kz, DateTime expiry, int remaining) in state.RecentKills)
                {
                    if (expiry < now2) { expiredKills++; continue; }
                    if (remaining <= 0) continue;
                    liveKills++;
                    double kdx = px2 - kx, kdy = py2 - ky, kdz = pz2 - kz;
                    double kd = Math.Sqrt(kdx * kdx + kdy * kdy + kdz * kdz);
                    if (nearestKillDist < 0 || kd < nearestKillDist)
                        nearestKillDist = kd;
                }
                // Pending-drop side of the same correlation: live record count
                // plus the nearest in-range record's state and one bounded
                // probe of its cell - exposes seedless and stale-item-set
                // denials without a second switch.
                int pendingLive = 0;
                long bestDropKey = 0;
                float bestDropD2 = float.MaxValue;
                PendingDrop bestDrop = null;
                foreach (KeyValuePair<long, PendingDrop> kv in state.PendingDrops)
                {
                    if (kv.Value.ExpiryUtc < now2)
                        continue;
                    pendingLive++;
                    UnpackCell(kv.Key, out int dcx, out int dcy, out int dcz);
                    float ddx = px2 - (dcx + 0.5f), ddy = py2 - (dcy + 0.5f), ddz = pz2 - (dcz + 0.5f);
                    float dd2 = ddx * ddx + ddy * ddy + ddz * ddz;
                    if (dd2 < bestDropD2)
                    {
                        bestDropD2 = dd2;
                        bestDropKey = kv.Key;
                        bestDrop = kv.Value;
                    }
                }
                string pendInfo = "none";
                if (bestDrop != null && bestDropD2 <= DropCellRadiusSq)
                {
                    UnpackCell(bestDropKey, out int bx, out int by, out int bz);
                    string blk = TryGetWorldBlock(bx, by, bz, out int bt) ? bt.ToString() : "?";
                    pendInfo = $"cell({bx},{by},{bz}) remain {bestDrop.Remaining} " +
                               $"confirmed {(bestDrop.Confirmed ? 1 : 0)} " +
                               $"ids {(bestDrop.ItemIds?.Count ?? -1)} " +
                               $"hasItem {(bestDrop.ItemIds?.Contains(item.Id) == true ? 1 : 0)} " +
                               $"block {blk}";
                }
                _log($"[CombatEvidence] loot denied item {item.Id} x{item.Stack} " +
                     $"spawn ({px2:F1},{py2:F1},{pz2:F1}) " +
                     $"player ({state.LastX:F1},{state.LastY:F1},{state.LastZ:F1}) " +
                     $"recentKills {liveKills} expired {expiredKills} " +
                     $"nearestKill {(nearestKillDist < 0 ? "none" : $"{nearestKillDist:F1}m")} " +
                     $"pending {pendingLive} nearestDrop {pendInfo}");
            }
            // Deny the spawn either way; only bursts count as violations - a
            // lone orphan is most likely a dig/blast/kill claim eaten upstream.
            if (!DenialCountsAsViolation(
                    ref state.PickupDenials, PickupDenialGrace, PickupDenialWindow))
                return ItemPolicy.ItemVerdict.DeniedDesync;
            return ItemPolicy.ItemVerdict.InvalidItem;
        }

        #region Pickup drop-correlation

        private const int MaxPendingDrops = 160;
        private const int PendingDropAllowance = 4;
        private const int PendingBlastAllowance = 2;
        private const int KillDropAllowance = 12;
        // Vanilla EnemyManager.SpawnDragonPickups rolls dropcount + two
        // explosive rolls; 25 is a conservative inclusive bound for it
        // (25m radius / 30s TTL unchanged).
        private const int DragonKillDropAllowance = 25;
        private const int BlastDropAllowance = 24;
        // A blast envelope (radius <=3) catches at most a few loot blocks, each
        // rolling 1-2 items; 8 bounds it with room while keeping a claimed blast
        // next to a surviving loot block from yielding infinite weapon claims.
        private const int BlastLootAllowance = 8;
        private const int MaxRecentKills = 8;
        private const int MaxRecentBlasts = 8;
        private static readonly TimeSpan DigSeedTtl = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ConfirmedDropTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan KillDropTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BlastDropTtl = TimeSpan.FromSeconds(8);
        private const float DropCellRadiusSq = 8f;      // ~2.8 cells around the removed block
        private const float KillDropRadiusSq = 625f;    // 25m - shots can kill at range
        private const float BlastDropRadiusSq = 144f;   // 12m blast scatter

        private static long PackCell(int x, int y, int z)
        {
            return ((long)(x + 0x100000) << 42) | ((long)(y + 0x100000) << 21) | (long)(z + 0x100000);
        }

        private static void UnpackCell(long key, out int x, out int y, out int z)
        {
            x = (int)(key >> 42) - 0x100000;
            y = (int)((key >> 21) & 0x1FFFFF) - 0x100000;
            z = (int)(key & 0x1FFFFF) - 0x100000;
        }

        /// <summary>
        /// Seeds/refreshes the expected drop for a cell being removed. DigMessage
        /// seeds unconfirmed (vanilla sends it per swing, before the pickup);
        /// the AlterBlock-to-Empty write confirms the removal actually happened.
        /// </summary>
        private void RecordDigSeed(PlayerTrustState state, int x, int y, int z, int removedType, bool confirm, bool removalSeed, int claimedType,
            float hx = 0f, float hy = 0f, float hz = 0f, float nx = 0f, float ny = 0f, float nz = 0f)
        {
            long key = PackCell(x, y, z);
            if (state.PendingDrops.TryGetValue(key, out PendingDrop drop))
            {
                if (confirm)
                    drop.Confirmed = true;
                if (removalSeed)
                {
                    drop.RemovalSeed = true;
                    drop.ClaimedType = claimedType;
                    // A fresh dig re-arms the ticket: one removal may claim it.
                    drop.RemovalSpent = false;
                    // And it re-arms the drop allowance: a cell re-dug inside
                    // the record's TTL (replaced block, second pass of a swing)
                    // keeps producing real pickups - the allowance is per dig,
                    // not per record lifetime.
                    drop.Remaining = PendingDropAllowance;
                    // Same for the drop set: the cell's block may have changed
                    // inside the TTL (rock replaced by a loot block), so the
                    // stale set would deny the fresh dig's real drops. Always
                    // the SERVER-resolved removed type - never the client claim.
                    var freshIds = new HashSet<int>();
                    ItemPolicy.CollectDigDropIds(removedType, freshIds);
                    drop.ItemIds = freshIds;
                    drop.DigHitX = hx; drop.DigHitY = hy; drop.DigHitZ = hz;
                    drop.DigNormalX = nx; drop.DigNormalY = ny; drop.DigNormalZ = nz;
                    drop.HasDigHit = true;
                }
                drop.ExpiryUtc = DateTime.UtcNow + (confirm ? ConfirmedDropTtl : DigSeedTtl);
                return;
            }
            SeedCell(state, key, removedType, confirm, removalSeed, claimedType);
            if (removalSeed && state.PendingDrops.TryGetValue(key, out PendingDrop fresh))
            {
                fresh.DigHitX = hx; fresh.DigHitY = hy; fresh.DigHitZ = hz;
                fresh.DigNormalX = nx; fresh.DigNormalY = ny; fresh.DigNormalZ = nz;
                fresh.HasDigHit = true;
            }
        }

        /// <summary>
        /// Creates a pending-drop record for a removed cell. The record exists even
        /// when the block drops nothing (leaves, tall grass): with no record the
        /// removal gate has no dig-seed to match and denies a legitimate dig.
        /// </summary>
        private void SeedCell(PlayerTrustState state, long key, int removedType, bool confirm, bool removalSeed, int claimedType = 0)
        {
            var ids = new HashSet<int>();
            ItemPolicy.CollectDigDropIds(removedType, ids);

            if (state.PendingDrops.Count >= MaxPendingDrops)
            {
                PrunePendingDrops(state);
                if (state.PendingDrops.Count >= MaxPendingDrops)
                    return;
            }
            state.PendingDrops[key] = new PendingDrop
            {
                ItemIds = ids,
                Remaining = PendingDropAllowance,
                Confirmed = confirm,
                RemovalSeed = removalSeed,
                ClaimedType = claimedType,
                ExpiryUtc = DateTime.UtcNow + (confirm ? ConfirmedDropTtl : DigSeedTtl),
            };
        }

        // BlockFace order: POSX, NEGZ, NEGX, POSZ, POSY, NEGY.
        private static readonly int[] FaceOffX = { 1, 0, -1, 0, 0, 0 };
        private static readonly int[] FaceOffY = { 0, 0, 0, 0, 1, -1 };
        private static readonly int[] FaceOffZ = { 0, -1, 0, 1, 0, 0 };

        /// <summary>
        /// Vanilla's dig cascade removes face-attached neighbors (torches on the
        /// dug face, wall items) and door halves - each producing a pickup that
        /// precedes its own AlterBlock-to-Empty. Pre-seed those cells so their
        /// pickups match instead of flagging as forged.
        /// </summary>
        private void SeedDependents(PlayerTrustState state, int x, int y, int z, int dugType)
        {
            for (int face = 0; face < 6; face++)
            {
                int nx = x + FaceOffX[face], ny = y + FaceOffY[face], nz = z + FaceOffZ[face];
                if (!TryGetWorldBlock(nx, ny, nz, out int neighborType))
                    continue;
                if (!ItemPolicy.TryGetBlockFacing(neighborType, out int facing) || facing != face)
                    continue;
                long key = PackCell(nx, ny, nz);
                if (!state.PendingDrops.ContainsKey(key))
                    SeedCell(state, key, neighborType, confirm: false, removalSeed: false);
            }
            if (ItemPolicy.IsDoorBlockType(dugType))
            {
                SeedDoorHalf(state, x, y + 1, z);
                SeedDoorHalf(state, x, y - 1, z);
            }
        }

        private void SeedDoorHalf(PlayerTrustState state, int x, int y, int z)
        {
            if (!TryGetWorldBlock(x, y, z, out int halfType) || !ItemPolicy.IsDoorBlockType(halfType))
                return;
            long key = PackCell(x, y, z);
            if (!state.PendingDrops.ContainsKey(key))
                SeedCell(state, key, halfType, confirm: false, removalSeed: false);
        }

        // How far outside the removed cell's bounds the stored dig hit may sit
        // and still count as an edge-hit off-by-one. Client/server raycasts
        // disagree by float error, never by more than a hair of a block.
        private const float DigHitEdgeTolerance = 0.05f;

        /// <summary>
        /// A removal write needs one unspent seed to pass. Its own cell accepts
        /// any live record (a dig seed, or a cascade-seeded dependent cell);
        /// face-neighbors only pass for a real DigMessage seed when the stored
        /// hit point could have produced this cell: the off-by-one can only
        /// occur on a tangent axis (the normal axis has a half-block margin),
        /// and the hit must lie inside the removed cell's expanded bounds.
        /// Either way the seed is spent - one dig, one removal.
        /// </summary>
        private static bool HasPendingSeedNear(PlayerTrustState state, int x, int y, int z, int removedType)
        {
            DateTime now = DateTime.UtcNow;
            if (state.PendingDrops.TryGetValue(PackCell(x, y, z), out PendingDrop own)
                && own.ExpiryUtc >= now && !own.RemovalSpent
                && (own.ClaimedType == 0 || own.ClaimedType == removedType))
            {
                own.RemovalSpent = true;
                return true;
            }
            for (int face = 0; face < 6; face++)
            {
                if (!state.PendingDrops.TryGetValue(PackCell(x + FaceOffX[face], y + FaceOffY[face], z + FaceOffZ[face]), out PendingDrop n)
                    || n.ExpiryUtc < now || !n.RemovalSeed || n.RemovalSpent
                    || n.ClaimedType != removedType || !n.HasDigHit)
                    continue;

                // Faces 0/2 offset X, 1/3 offset Z, 4/5 offset Y. A neighbor
                // along the dig's normal axis can't be a tangent off-by-one.
                float na = face == 0 || face == 2 ? n.DigNormalX : face == 1 || face == 3 ? n.DigNormalZ : n.DigNormalY;
                if (Math.Abs(na) > 0.5f)
                    continue;

                if (n.DigHitX < x - DigHitEdgeTolerance || n.DigHitX > x + 1f + DigHitEdgeTolerance ||
                    n.DigHitY < y - DigHitEdgeTolerance || n.DigHitY > y + 1f + DigHitEdgeTolerance ||
                    n.DigHitZ < z - DigHitEdgeTolerance || n.DigHitZ > z + 1f + DigHitEdgeTolerance)
                    continue;

                n.RemovalSeed = false;
                n.RemovalSpent = true;
                return true;
            }
            return false;
        }

        /// <summary>Marks live records at the cell and its face-neighbors confirmed.</summary>
        private static void ConfirmSeedsNear(PlayerTrustState state, int x, int y, int z)
        {
            for (int face = -1; face < 6; face++)
            {
                long key = face < 0
                    ? PackCell(x, y, z)
                    : PackCell(x + FaceOffX[face], y + FaceOffY[face], z + FaceOffZ[face]);
                if (state.PendingDrops.TryGetValue(key, out PendingDrop drop) &&
                    drop.ExpiryUtc >= DateTime.UtcNow)
                {
                    drop.Confirmed = true;
                }
            }
        }

        private const int MaxSpawnerRateCells = 512;
        private static readonly TimeSpan SpawnerRateCellTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Rate-bucket exemption for the AlterBlock-to-Empty flood a real
        /// spawner activation produces: GameUtils.ClearSurroundingBlocks
        /// writes the whole 6x3x6 box (108 cells, air included) above the
        /// marker, so a second activation inside the generic 150/30s window
        /// would trip the limit legitimately. A cell bypasses the generic
        /// bucket once per window only when the SERVER's world state proves a
        /// live marker sits within the inverse clear-box - client coordinates
        /// alone prove nothing. Duplicates inside the window, unresolvable
        /// cells, loot blocks and markers (vanilla skips both) all fall
        /// through to the ordinary bucket. This is rate accounting only:
        /// the removal-write/dig-seed/drop gates downstream still apply.
        /// </summary>
        private bool TryUseSpawnerClearRateAllowance(
            PlayerTrustState state, string typeName, byte[] data, DateTime now)
        {
            if (typeName != "DNA.CastleMinerZ.Net.AlterBlockMessage" ||
                data == null || data.Length < 17 ||
                BitConverter.ToInt32(data, 13) != 0)
                return false;
            int x = BitConverter.ToInt32(data, 1);
            int y = BitConverter.ToInt32(data, 5);
            int z = BitConverter.ToInt32(data, 9);
            if (!TryGetWorldBlock(x, y, z, out int removedType) ||
                ItemPolicy.IsLootBlockType(removedType) ||
                ItemPolicy.IsRemoteAlterBlockType(removedType) ||
                !HasSpawnerMarkerBelow(x, y, z))
                return false;

            var cells = state.SpawnerRateCells ??= new Dictionary<long, DateTime>();
            long key = PackCell(x, y, z);
            if (cells.TryGetValue(key, out DateTime spent) && spent > now)
                return false; // once-per-window exemption already consumed
            List<long> dead = null;
            foreach (KeyValuePair<long, DateTime> kv in cells)
                if (kv.Value <= now)
                    (dead ??= new List<long>()).Add(kv.Key);
            if (dead != null)
                foreach (long k in dead)
                    cells.Remove(k);
            if (cells.Count >= MaxSpawnerRateCells)
                return false;
            cells[key] = now + SpawnerRateCellTtl;
            return true;
        }

        /// <summary>
        /// Inverse of GameUtils.ClearSurroundingBlocks(Location, 3): a cell is
        /// inside a spawner's clear-box when a spawn marker sits at mx in
        /// [x-2..x+3], my in [y-3..y-1], mz in [z-2..z+3]. Checks authoritative
        /// world state, not client claims - covers spawners placed by players
        /// that never reached the worldInfo registry.
        /// </summary>
        private bool HasSpawnerMarkerBelow(int x, int y, int z)
        {
            for (int my = y - 3; my <= y - 1; my++)
            {
                for (int mx = x - 2; mx <= x + 3; mx++)
                {
                    for (int mz = z - 2; mz <= z + 3; mz++)
                    {
                        if (TryGetWorldBlock(mx, my, mz, out int markerType) &&
                            ItemPolicy.IsRemoteAlterBlockType(markerType))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Desync-heal for denied removals: the world write stays blocked (the
        /// dig was never proven), but the cell provably held a real block and
        /// the sender is most likely a digger whose claim was eaten upstream.
        /// Seed a small record filtered to world-spawnable items so the
        /// pickups the dig legitimately produced can still correlate instead
        /// of vanishing client-side (denied CreatePickups never appear).
        /// Confirmed+RemovalSpent pins it to pickup-absorption only: it can
        /// never authorize a second removal on the cell, and expiring it
        /// can't raise the forged-drop violation. Never for loot blocks or
        /// spawn markers - seeding that table on an unproven removal mints
        /// weapons; markers drop nothing vanilla players need healed.
        /// </summary>
        private const int DeniedDropAllowance = 2;

        private void SeedDeniedRemovalDrop(PlayerTrustState state, long cellKey, int removedType)
        {
            if (ItemPolicy.IsLootBlockType(removedType) ||
                ItemPolicy.IsRemoteAlterBlockType(removedType) ||
                state.PendingDrops.ContainsKey(cellKey) ||
                state.PendingDrops.Count >= MaxPendingDrops)
            {
                return;
            }

            var ids = new HashSet<int>();
            ItemPolicy.CollectDigDropIds(removedType, ids);
            ids.RemoveWhere(id => !ItemPolicy.IsWorldSpawnable(id));
            if (ids.Count == 0)
            {
                return;
            }

            state.PendingDrops[cellKey] = new PendingDrop
            {
                ItemIds = ids,
                Remaining = DeniedDropAllowance,
                Confirmed = true,
                RemovalSpent = true,
                ExpiryUtc = DateTime.UtcNow + ConfirmedDropTtl,
            };
        }

        // A denied removal within this window/range of the last accepted dig is
        // treated as part of a chopper batch: the batch loses the dig too.
        private static readonly TimeSpan ChopperRestoreWindow = TimeSpan.FromSeconds(5);
        private const int ChopperRestoreRadius = 32;

        // Desync grace: correlation denies (seedless removals, orphan pickups)
        // are always denied, but only count as violations past this many in a
        // window. Upstream loss (FloodGuard blackhole, rate drop) can eat the
        // claim packet and produce a legit-looking desync; a chopper or item
        // spammer bursts past the grace instantly.
        private const int RemovalDenialGrace = 3;
        private static readonly TimeSpan RemovalDenialWindow = TimeSpan.FromSeconds(4);
        private const int PickupDenialGrace = 3;
        private static readonly TimeSpan PickupDenialWindow = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Sliding-window counter for correlation denies. Returns true once the
        /// count exceeds the grace allowance - i.e. the denial should also
        /// count as a violation rather than a silent desync drop.
        /// </summary>
        private static bool DenialCountsAsViolation(
            ref (DateTime Start, int Count) window, int grace, TimeSpan span)
        {
            DateTime now = DateTime.UtcNow;
            if (now - window.Start > span)
                window = (now, 0);
            window.Count++;
            return window.Count > grace;
        }

        /// <summary>
        /// Queues the player's last accepted removal cell for rollback when a
        /// denied removal lands close behind it - the chopper batch signature.
        /// Fires once per accepted dig: after the queue the stamp clears, so a
        /// 40-cell burst still restores only the single dig that seeded it.
        /// </summary>
        private void QueueChopperRestore(PlayerTrustState state, int deniedX, int deniedY, int deniedZ)
        {
            if (state.LastRemovalUtc == DateTime.MinValue ||
                DateTime.UtcNow - state.LastRemovalUtc > ChopperRestoreWindow)
                return;
            if (Math.Abs(deniedX - state.LastRemovalX) > ChopperRestoreRadius ||
                Math.Abs(deniedY - state.LastRemovalY) > ChopperRestoreRadius ||
                Math.Abs(deniedZ - state.LastRemovalZ) > ChopperRestoreRadius)
                return;
            // Only roll back when the cell is still empty - never stomp a block
            // placed there in the window between dig and denial.
            if (!TryGetWorldBlock(state.LastRemovalX, state.LastRemovalY, state.LastRemovalZ, out int current) ||
                current != 0)
                return;
            (state.PendingRestores ??= new List<(int X, int Y, int Z, int Type)>())
                .Add((state.LastRemovalX, state.LastRemovalY, state.LastRemovalZ, state.LastRemovalType));
            state.LastRemovalUtc = DateTime.MinValue;
        }

        private const int MaxRecentPlacements = 64;
        private static readonly TimeSpan PlacementRollbackWindow = TimeSpan.FromSeconds(45);

        private static void RecordPlacement(PlayerTrustState state, int x, int y, int z, int type)
        {
            var list = state.RecentPlacements ??=
                new List<(int X, int Y, int Z, int Type, DateTime Utc)>();
            DateTime cutoff = DateTime.UtcNow - PlacementRollbackWindow;
            list.RemoveAll(p => p.Utc < cutoff);
            if (list.Count >= MaxRecentPlacements)
                list.RemoveAt(0);
            list.Add((x, y, z, type, DateTime.UtcNow));
        }

        /// <summary>
        /// On kick, rolls back this player's recent accepted placements: a cheat
        /// that scattered mod blocks (shoot-blocks, lava physics) leaves them
        /// behind otherwise. Only cells still holding the placed type revert -
        /// anything a later write already changed is left alone.
        /// </summary>
        private void QueuePlacementRollback(PlayerTrustState state)
        {
            if (state.RecentPlacements == null)
                return;
            DateTime cutoff = DateTime.UtcNow - PlacementRollbackWindow;
            foreach (var p in state.RecentPlacements)
            {
                if (p.Utc < cutoff)
                    continue;
                if (TryGetWorldBlock(p.X, p.Y, p.Z, out int current) && current == p.Type)
                    (state.PendingRestores ??=
                        new List<(int X, int Y, int Z, int Type)>()).Add((p.X, p.Y, p.Z, 0));
            }
            state.RecentPlacements = null;
        }

        /// <summary>
        /// Drains rollback cells queued by QueueChopperRestore/QueuePlacementRollback
        /// for EVERY player. The world handler writes each back to the terrain
        /// delta and relays a synthetic AlterBlockMessage so every client sees
        /// the block restored. Draining all players (not just the denied sender)
        /// matters for kick rollbacks: a kicked client may disconnect before its
        /// next denied packet gives the drain a chance to run.
        /// </summary>
        public List<(int X, int Y, int Z, int Type)> DrainPendingRestores(byte senderId)
        {
            List<(int X, int Y, int Z, int Type)> all = null;
            foreach (PlayerTrustState s in _players.Values)
            {
                if (s.PendingRestores == null || s.PendingRestores.Count == 0)
                    continue;
                (all ??= new List<(int X, int Y, int Z, int Type)>()).AddRange(s.PendingRestores);
                s.PendingRestores = null;
            }
            return all;
        }

        /// <summary>
        /// Last accepted horizontal positions of connected, announced players.
        /// The dragon scheduler uses these for the &gt;3600u skeleton trigger
        /// and to pick the AI controller (vanilla assigns the requester).
        /// </summary>
        public List<(byte Id, float X, float Y, float Z)> SnapshotPlayerPositions()
        {
            var list = new List<(byte, float, float, float)>();
            foreach (KeyValuePair<byte, PlayerTrustState> kv in _players)
            {
                PlayerTrustState s = kv.Value;
                if (s == null || s.KickIssued || !s.Announced)
                    continue;
                list.Add((kv.Key, s.LastX, s.LastY, s.LastZ));
            }
            return list;
        }

        /// <summary>
        /// Reads the length-prefixed UTF-8 string after the BroadcastTextMessage
        /// id byte. Returns false for malformed payloads (they drop elsewhere).
        /// </summary>
        private static bool TryReadBroadcastText(byte[] data, out string text)
        {
            text = null;
            if (data == null || data.Length < 2)
                return false;
            int len = 0, shift = 0, i = 1;
            while (i < data.Length && shift < 35)
            {
                byte b = data[i++];
                len |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            if (len <= 0 || i + len > data.Length)
                return false;
            try { text = System.Text.Encoding.UTF8.GetString(data, i, len); }
            catch { return false; }
            return true;
        }

        /// <summary>
        /// True when the chat text opens with "name:" where name is another
        /// connected player's gamertag, or uses the server's "[Server]" prefix.
        /// </summary>
        private bool IsImpersonating(PlayerTrustState sender, string text)
        {
            if (text.StartsWith("[Server]", StringComparison.OrdinalIgnoreCase))
                return true;
            int colon = text.IndexOf(':');
            if (colon <= 0)
                return false;
            string claimed = text.Substring(0, colon).Trim();
            if (claimed.Length == 0)
                return false;
            foreach (PlayerTrustState p in _players.Values)
            {
                if (ReferenceEquals(p, sender) || string.IsNullOrEmpty(p.Name))
                    continue;
                if (claimed.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RecordEnemyKill(PlayerTrustState state, float x, float y, float z,
            int allowance = KillDropAllowance)
        {
            if (!state.HasBaseline)
                return;
            state.RecentKills.Add((x, y, z, DateTime.UtcNow + KillDropTtl, allowance));
            if (state.RecentKills.Count > MaxRecentKills)
                state.RecentKills.RemoveAt(0);
        }

        private void RecordBlast(PlayerTrustState state, float x, float y, float z)
        {
            state.RecentBlasts.Add((x, y, z, DateTime.UtcNow + BlastDropTtl, BlastDropAllowance, BlastLootAllowance));
            if (state.RecentBlasts.Count > MaxRecentBlasts)
                state.RecentBlasts.RemoveAt(0);
        }

        /// <summary>True when a live blast record sits within blast radius of the cell.</summary>
        private static bool HasRecentBlastNear(PlayerTrustState state, float x, float y, float z)
        {
            DateTime now = DateTime.UtcNow;
            foreach ((float bx, float by, float bz, DateTime expiry, int remaining, int loot) in state.RecentBlasts)
            {
                if (expiry < now || remaining <= 0)
                    continue;
                float dx = x - bx, dy = y - by, dz = z - bz;
                if (dx * dx + dy * dy + dz * dz <= BlastDropRadiusSq)
                    return true;
            }
            return false;
        }

        // Vanilla Explosive.cKillRanges indexed by (int)ExplosiveTypes
        // (TNT, C4, Rocket, Laser, HEGrenade, Harvest): inside this radius the
        // blast applies flat 1.0 damage to the local player - a full health
        // bar, no LOS discount (LOS only scales the falloff band), and no
        // shooter exemption (shooterID only credits zombie kills). A living
        // player provably inside the kill ring when the detonation processes
        // MUST die, so surviving it is only possible with godmode-style
        // invulnerability.
        private static readonly float[] BlastKillRanges = { 3f, 6f, 4f, 1f, 2.5f, 1f };
        private static readonly string[] BlastKillLabels =
            { "TNT", "C4", "rocket", "laser", "grenade", "harvest" };
        private const double LethalBlastEvalSeconds = 1.25;
        private const double BlastPendingWindowSeconds = 0.6;

        /// <summary>
        /// A lethal blast just processed: mark every living, non-exempt player
        /// whose last reported position was inside the kill radius as
        /// pre-blast-inside (BlastPending*). The mark only becomes an armed
        /// exposure when the player's FIRST post-blast update still shows them
        /// inside the ring - inside before AND inside after brackets the
        /// process instant with continuous presence (the segment between two
        /// points inside a sphere stays inside). Entering late, leaving early,
        /// or lagging out of the window all fail to arm: missed detection,
        /// never a false strike.
        /// </summary>
        private void ArmLethalBlast(float bx, float by, float bz, int explosiveType)
        {
            if (explosiveType < 0 || explosiveType >= BlastKillRanges.Length)
                return;
            float killRange = BlastKillRanges[explosiveType];
            DateTime now = DateTime.UtcNow;
            foreach (PlayerTrustState p in _players.Values)
            {
                if (!p.HasBaseline || p.WasDead || p.KickIssued || IsExempt(p))
                    continue;
                if ((now - p.LastUpdateUtc).TotalSeconds > 0.5)
                    continue;
                float dx = p.LastX - bx;
                float dy = (p.LastY + 1f) - by;
                float dz = p.LastZ - bz;
                if (dx * dx + dy * dy + dz * dz >= killRange * killRange)
                    continue;
                p.BlastPendingUntil = now + TimeSpan.FromSeconds(BlastPendingWindowSeconds);
                p.BlastPendingX = bx;
                p.BlastPendingY = by;
                p.BlastPendingZ = bz;
                p.BlastPendingRange = killRange;
                p.BlastPendingDesc =
                    $"{BlastKillLabels[explosiveType]} blast at ({bx:F1},{by:F1},{bz:F1}), " +
                    $"kill radius {killRange:F1}";
            }
        }

        /// <summary>
        /// Blast exposure state machine, run on the victim's position updates:
        /// a pending pre-blast mark promotes to an armed exposure when this
        /// update still places them inside the ring (first post-blast update
        /// decides); an armed exposure clears on any dead update and strikes
        /// godmode once the death-flag window elapses with them still alive.
        /// </summary>
        private bool CheckLethalBlastExposure(
            PlayerTrustState state, float x, float y, float z, DateTime now, bool dead)
        {
            if (state.BlastPendingUntil != DateTime.MinValue)
            {
                if (!dead && now <= state.BlastPendingUntil)
                {
                    float pdx = x - state.BlastPendingX;
                    float pdy = (y + 1f) - state.BlastPendingY;
                    float pdz = z - state.BlastPendingZ;
                    float pInside = state.BlastPendingRange - 0.5f;
                    if (pdx * pdx + pdy * pdy + pdz * pdz < pInside * pInside)
                    {
                        state.LethalBlastUtc = now;
                        state.LethalBlastDesc = state.BlastPendingDesc;
                    }
                }
                state.BlastPendingUntil = DateTime.MinValue;
            }

            if (state.LethalBlastUtc == DateTime.MinValue)
                return true;
            if (dead)
            {
                state.LethalBlastUtc = DateTime.MinValue;
                state.LethalBlastDesc = null;
                return true;
            }
            if ((now - state.LethalBlastUtc).TotalSeconds < LethalBlastEvalSeconds)
                return true;
            string desc = state.LethalBlastDesc;
            state.LethalBlastUtc = DateTime.MinValue;
            state.LethalBlastDesc = null;
            return Violation(state, "godmode", $"survived lethal {desc}");
        }

        // Enemy digging (DoDigEffects=1): a RemoveBlocksMessage cell is only
        // real if the sender owns a live enemy - enemy positions aren't synced,
        // so volume caps carry the rest: ~6-20 cells per enemy action in vanilla,
        // a forged dump fails the caps and gets every cell denied.
        private const int MaxEnemyDigCellsPerMessage = 64;
        private const int MaxEnemyDigCellsPerWindow = 120;
        private const double EnemyDigWindowSec = 10.0;

        private bool AllowEnemyDigCell(PlayerTrustState state, byte senderId, int authorizedSoFar)
        {
            if (authorizedSoFar >= MaxEnemyDigCellsPerMessage)
                return false;
            if (!HasLiveEnemyOwnedBy(senderId))
                return false;
            DateTime now = DateTime.UtcNow;
            if ((now - state.EnemyDigWindowStartUtc).TotalSeconds > EnemyDigWindowSec)
            {
                state.EnemyDigWindowStartUtc = now;
                state.EnemyDigCellCount = 0;
            }
            if (state.EnemyDigCellCount >= MaxEnemyDigCellsPerWindow)
                return false;
            state.EnemyDigCellCount++;
            return true;
        }

        private bool HasLiveEnemyOwnedBy(byte senderId)
        {
            foreach (KeyValuePair<int, List<EnemyRecord>> kv in _enemiesById)
            {
                if (kv.Value.Exists(r => r.SpawnerId == senderId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Per-cell authorization for RemoveBlocksMessage application. Cells are
        /// authorized during AllowMessage (blast-correlated or enemy-owned) and
        /// consumed here so a denied or replayed cell can never reach the world.
        /// </summary>
        public bool AllowRemoveBlockCell(
            byte senderId, int x, int y, int z,
            Func<string> resolveName, Func<ulong> resolveRemoteId)
        {
            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            if (IsExempt(state))
                return true;
            return state.AuthorizedRemovalCells != null &&
                state.AuthorizedRemovalCells.Remove(PackCell(x, y, z));
        }

        /// <summary>
        /// DetonateFireballMessage: [id][Location:12][Index:4][EType:1][NumBlocks:1][cells:N*12]
        /// Dragon/enemy fireballs destroy blocks; only attached dependents (and
        /// doors) drop their item - blast-destroyed terrain yields nothing.
        /// Runs only after the detonation claim itself passed validation.
        /// </summary>
        private void SeedFireballDependents(PlayerTrustState state, byte[] data)
        {
            if (!ItemPolicy.Ready || data == null || data.Length < 18)
                return;
            int num = data[17];
            if (num <= 0 || data.Length < 18 + num * 12)
                return;
            for (int i = 0; i < num; i++)
            {
                int off = 18 + i * 12;
                int cx = BitConverter.ToInt32(data, off);
                int cy = BitConverter.ToInt32(data, off + 4);
                int cz = BitConverter.ToInt32(data, off + 8);
                if (!TryGetWorldBlock(cx, cy, cz, out int removedType))
                    continue;
                if (!ItemPolicy.IsFacingBlockType(removedType) &&
                    !ItemPolicy.IsDoorBlockType(removedType))
                    continue;
                var ids = new HashSet<int>();
                ItemPolicy.CollectDigDropIds(removedType, ids);
                if (ids.Count == 0 || state.PendingDrops.Count >= MaxPendingDrops)
                    continue;
                state.PendingDrops[PackCell(cx, cy, cz)] = new PendingDrop
                {
                    ItemIds = ids,
                    Remaining = PendingBlastAllowance,
                    Confirmed = true,
                    ExpiryUtc = DateTime.UtcNow + ConfirmedDropTtl,
                };
            }
        }

        /// <summary>
        /// The pickup position must sit near a cell this player removed, and the
        /// item must be one that cell can drop. Expired records that were consumed
        /// while still unconfirmed mean the claimed dig never completed - that
        /// pickup was forged against a swing that broke nothing.
        /// </summary>
        private bool TryConsumePendingDrop(PlayerTrustState state, float px, float py, float pz, int itemId)
        {
            DateTime now = DateTime.UtcNow;
            List<long> expired = null;
            PendingDrop found = null;
            long foundKey = 0;
            float bestDist = float.MaxValue;
            foreach (KeyValuePair<long, PendingDrop> kv in state.PendingDrops)
            {
                PendingDrop drop = kv.Value;
                if (drop.ExpiryUtc < now)
                {
                    (expired ??= new List<long>()).Add(kv.Key);
                    continue;
                }
                if (drop.Remaining <= 0)
                    continue;
                UnpackCell(kv.Key, out int cx, out int cy, out int cz);
                float ddx = px - (cx + 0.5f), ddy = py - (cy + 0.5f), ddz = pz - (cz + 0.5f);
                float distSq = ddx * ddx + ddy * ddy + ddz * ddz;
                if (distSq > DropCellRadiusSq || distSq >= bestDist)
                    continue;
                if (drop.ItemIds == null ? !ItemPolicy.IsWorldSpawnable(itemId) : !drop.ItemIds.Contains(itemId))
                    continue;
                // Nearest matching cell wins: a stale seed from an abandoned
                // dig next to a live one must not absorb its pickups, or the
                // expired record reads as a "consumed but never removed" mint.
                found = drop;
                foundKey = kv.Key;
                bestDist = distSq;
            }

            if (expired != null)
            {
                foreach (long key in expired)
                {
                    if (state.PendingDrops.TryGetValue(key, out PendingDrop dead))
                        RemovePendingDrop(state, key, dead);
                    else
                        state.PendingDrops.Remove(key);
                }
            }

            if (found == null)
                return false;
            found.Remaining--;
            found.Consumed++;
            // Keep the record: the confirming AlterBlock may still be in flight,
            // and the removal-write gate only needs the key to exist.
            return true;
        }

        /// <summary>
        /// Removing a record whose pickups were consumed while the removal write
        /// never arrived: the dig seed was real (only a DigMessage creates one)
        /// but the confirming AlterBlock never landed - the same eaten-packet
        /// desync as the other correlation gates, plus legit abandoned digs
        /// (a seed consumed by a neighbor cell's drops). A forger minting drops
        /// (dig + pickup, no removal) repeats fast, so only excess counts.
        /// </summary>
        private const int UnconfirmedConsumeGrace = 3;
        private static readonly TimeSpan UnconfirmedConsumeWindow = TimeSpan.FromSeconds(60);

        private void RemovePendingDrop(PlayerTrustState state, long key, PendingDrop drop)
        {
            if (drop.Consumed > 0 && !drop.Confirmed &&
                DenialCountsAsViolation(ref state.UnconfirmedConsumeDenials,
                    UnconfirmedConsumeGrace, UnconfirmedConsumeWindow))
            {
                UnpackCell(key, out int ux, out int uy, out int uz);
                Violation(state, "item",
                    $"pickup consumed for a dig that never removed the block at {ux},{uy},{uz}");
            }
            state.PendingDrops.Remove(key);
        }

        /// <summary>Blast-harvest pickups may precede their RemoveBlocksMessage.</summary>
        private bool TryConsumeBlastDrop(PlayerTrustState state, float px, float py, float pz, int itemId)
        {
            bool harvest = ItemPolicy.IsExplosionHarvestItem(itemId);
            // Loot-table items (weapons/tools) are only legit when the blast
            // destroys a loot block - and those pickups are created inside the
            // explosion's cell loop, BEFORE RemoveBlocksMessage goes out, so the
            // destroyed cell isn't proven yet. The world still shows the loot
            // block at pickup time: require one inside the blast envelope.
            // Post-removal pickups match the loot-block cell's own seed instead.
            bool loot = !harvest && ItemPolicy.IsLootDropItem(itemId);
            if (!harvest && !loot)
                return false;

            DateTime now = DateTime.UtcNow;
            for (int i = state.RecentBlasts.Count - 1; i >= 0; i--)
            {
                (float bx, float by, float bz, DateTime expiry, int remaining, int lootLeft) = state.RecentBlasts[i];
                if (expiry < now || remaining <= 0)
                {
                    state.RecentBlasts.RemoveAt(i);
                    continue;
                }
                float dx = px - bx, dy = py - by, dz = pz - bz;
                if (dx * dx + dy * dy + dz * dz > BlastDropRadiusSq)
                    continue;
                if (loot)
                {
                    if (lootLeft <= 0 || !LootBlockNearBlast(bx, by, bz))
                        continue;
                    state.RecentBlasts[i] = (bx, by, bz, expiry, remaining - 1, lootLeft - 1);
                }
                else
                {
                    state.RecentBlasts[i] = (bx, by, bz, expiry, remaining - 1, lootLeft);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when a loot block exists within the explosion's destruction
        /// envelope (max blast range is 3 cells; a little margin for scatter).
        /// </summary>
        private const int LootBlockBlastScanRadius = 5;

        private bool LootBlockNearBlast(float bx, float by, float bz)
        {
            int cx = (int)Math.Floor(bx), cy = (int)Math.Floor(by), cz = (int)Math.Floor(bz);
            for (int dx = -LootBlockBlastScanRadius; dx <= LootBlockBlastScanRadius; dx++)
            for (int dy = -LootBlockBlastScanRadius; dy <= LootBlockBlastScanRadius; dy++)
            for (int dz = -LootBlockBlastScanRadius; dz <= LootBlockBlastScanRadius; dz++)
            {
                if (TryGetWorldBlock(cx + dx, cy + dy, cz + dz, out int t) &&
                    ItemPolicy.IsLootBlockType(t))
                    return true;
            }
            return false;
        }

        /// <summary>Enemy loot pickups must spawn near a kill this player claimed.</summary>
        private bool TryConsumeKillDrop(PlayerTrustState state, float px, float py, float pz, int itemId)
        {
            if (!ItemPolicy.IsEnemyDropItem(itemId))
                return false;

            DateTime now = DateTime.UtcNow;
            for (int i = state.RecentKills.Count - 1; i >= 0; i--)
            {
                (float kx, float ky, float kz, DateTime expiry, int remaining) = state.RecentKills[i];
                if (expiry < now || remaining <= 0)
                {
                    state.RecentKills.RemoveAt(i);
                    continue;
                }
                float dx = px - kx, dy = py - ky, dz = pz - kz;
                if (dx * dx + dy * dy + dz * dz > KillDropRadiusSq)
                    continue;
                state.RecentKills[i] = (kx, ky, kz, expiry, remaining - 1);
                return true;
            }
            return false;
        }

        private void PrunePendingDrops(PlayerTrustState state)
        {
            DateTime now = DateTime.UtcNow;
            List<long> expired = null;
            foreach (KeyValuePair<long, PendingDrop> kv in state.PendingDrops)
            {
                if (kv.Value.ExpiryUtc < now || kv.Value.Remaining <= 0)
                    (expired ??= new List<long>()).Add(kv.Key);
            }
            if (expired != null)
                foreach (long key in expired)
                    RemovePendingDrop(state, key, state.PendingDrops[key]);
        }

        #endregion

        /// <summary>
        /// Reconciles a saving player's inventory against what the server believes
        /// they own: gains must be covered by authorized credits or explainable
        /// as crafting (ingredients lost, product gained per the real recipe book).
        /// </summary>
        private ItemPolicy.ItemVerdict ReconcileStore(
            PlayerTrustState state,
            List<ItemPolicy.ParsedItem> items,
            out string reason)
        {
            reason = null;

            var newCounts = new Dictionary<int, int>();
            foreach (ItemPolicy.ParsedItem p in items)
            {
                newCounts.TryGetValue(p.Id, out int cur);
                newCounts[p.Id] = cur + p.Stack;
            }

            if (!state.BaselineKnown)
            {
                state.BaselineKnown = true;
                state.Owned = _baselineProvider?.Invoke(state.RemoteId, state.Name);
                if (state.Owned == null)
                {
                    // No served snapshot and no prior save on disk: adopt the
                    // claim once - legality checks already ran over it. The
                    // claimed stack already nets out pre-save consumption, so
                    // observed spending resets with the new baseline.
                    state.Owned = newCounts;
                    state.AmmoSpent.Clear();
                    _log($"[Trust] {state.Name}: inventory baseline adopted ({newCounts.Count} item kinds).");
                    return ItemPolicy.ItemVerdict.Ok;
                }
            }

            state.Owned ??= new Dictionary<int, int>();

            // Provable pool = owned baseline + authorized credits. Ingredients
            // spent from credits (picked up, crafted before ever saving) are
            // real losses too - the pool must cover both or "picked up wood ->
            // crafted sticks" reads as an unearned gain.
            var provable = new Dictionary<int, int>(state.Owned);
            foreach (KeyValuePair<int, int> kv in state.Credits)
            {
                provable.TryGetValue(kv.Key, out int cur);
                provable[kv.Key] = cur + kv.Value;
            }

            // Rounds burned in-flight are real losses too: vanilla consumes
            // ammo at reload, so a saved stack that never decremented despite
            // hundreds of observed shots is reporting phantom reserve
            // (infinite-ammo pins the count client-side). The grace = clips
            // of guns actually fired (a pistol user holds 8, not the LMG's
            // 100) - covers rounds loaded-but-not-yet-consumed at save time.
            foreach (KeyValuePair<int, int> kv in state.AmmoSpent)
            {
                int clipGrace;
                if (state.AmmoGunsSeen.TryGetValue(kv.Key, out HashSet<int> gunsSeen) &&
                    gunsSeen.Count > 0)
                {
                    clipGrace = 0;
                    foreach (int gun in gunsSeen)
                        if (ItemPolicy.TryGetGunClipCapacity(gun, out int gc))
                            clipGrace += gc;
                }
                else
                {
                    ItemPolicy.TryGetAmmoMaxClip(kv.Key, out clipGrace);
                }
                int burn = kv.Value - Math.Max(0, clipGrace);
                if (burn <= 0)
                    continue;
                provable.TryGetValue(kv.Key, out int cur);
                provable[kv.Key] = cur - burn;
            }

            // Losses are always fine - they fund craft explanations.
            var losses = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in provable)
            {
                newCounts.TryGetValue(kv.Key, out int now);
                int lost = kv.Value - now;
                if (lost > 0)
                    losses[kv.Key] = lost;
            }

            // Gains explain against a fresh pool copy per pass, and the whole
            // save is retried with reversed recipe preference when the greedy
            // order strands a shared ingredient (iron smelted with the only
            // coal starving a torch craft). The pass leaving fewer unexplained
            // gains wins; denied-claim bookkeeping applies to its leftovers.
            Dictionary<int, int> bestLosses = null;
            var craftConsumed = new Dictionary<int, int>();
            List<KeyValuePair<int, int>> unexplained = null;
            for (int rev = 0; rev < 2; rev++)
            {
                var attemptLosses = new Dictionary<int, int>(losses);
                var attemptConsumed = new Dictionary<int, int>();
                var attemptUnexplained = new List<KeyValuePair<int, int>>();
                foreach (KeyValuePair<int, int> kv in newCounts)
                {
                    provable.TryGetValue(kv.Key, out int have);
                    int gain = kv.Value - have;
                    if (gain <= 0)
                        continue;

                    if (!TryExplainByCraft(kv.Key, gain, attemptLosses, attemptConsumed, 0, rev == 1))
                        attemptUnexplained.Add(new KeyValuePair<int, int>(kv.Key, gain));
                }

                if (unexplained == null || attemptUnexplained.Count < unexplained.Count)
                {
                    unexplained = attemptUnexplained;
                    bestLosses = attemptLosses;
                    craftConsumed = attemptConsumed;
                }
                if (attemptUnexplained.Count == 0)
                    break;
            }

            string unearnedReason = null;
            bool newEscalation = false;
            bool phantomAmmo = false;
            foreach (KeyValuePair<int, int> kv in unexplained)
            {
                int gain = kv.Value;
                // The client can't shed a rejected claim - its next autosave
                // retries the same inventory with a bigger total as pickups
                // land. Track the unexplained remainder: only genuinely NEW
                // unexplained gains count; retries go silent. Every item is
                // still checked so a retry can't smuggle a fresh forgery in.
                state.Credits.TryGetValue(kv.Key, out int cred);
                state.Owned.TryGetValue(kv.Key, out int old);
                unearnedReason ??= $"unearned +{gain} of item {kv.Key} (claimed {newCounts[kv.Key]}, owned {old}, credited {cred})";
                state.DeniedClaims.TryGetValue(kv.Key, out int denied);
                if (denied < gain)
                {
                    state.DeniedClaims[kv.Key] = gain;
                    newEscalation = true;
                    // Phantom ammunition is the infinite-ammo signature: the
                    // stack the client keeps saving was already spent by
                    // shots the server counted.
                    if (ItemPolicy.IsGunAmmoId(kv.Key))
                        phantomAmmo = true;
                }
            }

            if (unearnedReason != null)
            {
                reason = unearnedReason;
                if (newEscalation && phantomAmmo)
                {
                    // Double weight: ammo saves only arrive every ~10 s, so
                    // each escalating phantom-ammo save counts twice and the
                    // kick lands in ~3 saves instead of ~5.
                    Violation(state, "item", $"phantom ammunition in save: {unearnedReason}");
                }
                return newEscalation
                    ? ItemPolicy.ItemVerdict.InvalidItem
                    : ItemPolicy.ItemVerdict.DeniedRepeat;
            }

            // The save becomes the new truth. Credits survive only as the
            // still-unaccounted excess of the provable pool - folded-in grants
            // and ingredients spent by explained crafts are consumed.
            state.Owned = newCounts;
            state.DeniedClaims.Clear();
            var consumedIds = new List<int>(state.Credits.Keys);
            foreach (int id in consumedIds)
            {
                provable.TryGetValue(id, out int prov);
                newCounts.TryGetValue(id, out int now);
                craftConsumed.TryGetValue(id, out int spent);
                // Clamp to the original credit: a deficit the pool can't explain
                // is a loss of owned items, never fresh credit.
                int left = Math.Min(state.Credits[id], Math.Max(0, prov - now - spent));
                if (left <= 0) state.Credits.Remove(id); else state.Credits[id] = left;
            }
            state.AmmoSpent.Clear();
            return ItemPolicy.ItemVerdict.Ok;
        }

        /// <summary>
        /// Explains a gain of `need` result items by consuming units from the
        /// losses pool per the real recipe book. Pool units of the result itself
        /// count first; any shortfall is crafted, with ingredients consumed
        /// sequentially so a shared material can never be counted twice. Each
        /// candidate recipe applies atomically: a failed attempt can't strand
        /// spent ingredients, and a batch surplus (crafting 4 sticks to use 2)
        /// stays in the pool as a real leftover. `consumed` accumulates per-id
        /// draw-down so reconcile can also drain the credits spent ingredients
        /// used. Bounded by a 16-deep recursion cap and a shared step budget.
        /// </summary>
        private static bool TryExplainByCraft(int resultId, int need, Dictionary<int, int> losses)
            => TryExplainByCraft(resultId, need, losses, null, 0);

        private static bool TryExplainByCraft(
            int resultId, int need, Dictionary<int, int> losses,
            Dictionary<int, int> consumed, int depth, bool reverseRecipes = false)
        {
            if (need <= 0)
                return true;
            var attempt = new Dictionary<int, int>(losses);
            int budget = 4096;
            if (!TryConsumeCraftItem(resultId, need, attempt, new HashSet<int>(), depth, ref budget, reverseRecipes))
                return false;
            if (consumed != null)
            {
                foreach (KeyValuePair<int, int> kv in losses)
                {
                    attempt.TryGetValue(kv.Key, out int left);
                    int used = Math.Max(0, kv.Value - left);
                    if (used > 0)
                    {
                        consumed.TryGetValue(kv.Key, out int prior);
                        consumed[kv.Key] = checked(prior + used);
                    }
                }
            }
            losses.Clear();
            foreach (KeyValuePair<int, int> kv in attempt)
                if (kv.Value > 0)
                    losses[kv.Key] = kv.Value;
            return true;
        }

        /// <summary>
        /// Transactionally takes `need` of itemId out of `pool`, crafting the
        /// shortfall through the recipe book. Consumes a candidate pool;
        /// TryExplainByCraft commits it only on success. Each recipe alternative
        /// operates on its own copy. `path` blocks recipe cycles,
        /// `depth`/`budget` bound the search.
        /// </summary>
        private static bool TryConsumeCraftItem(
            int itemId, int need, Dictionary<int, int> pool,
            HashSet<int> path, int depth, ref int budget, bool reverseRecipes = false)
        {
            if (need <= 0)
                return true;
            if (depth > 16 || --budget < 0)
                return false;

            pool.TryGetValue(itemId, out int have);
            int take = Math.Min(have, need);
            if (take > 0)
            {
                pool[itemId] = have - take;
                need -= take;
            }
            if (need == 0)
                return true;

            if (!path.Add(itemId))
                return false;
            try
            {
                List<ItemPolicy.RecipeInfo> recipes = ItemPolicy.GetRecipesFor(itemId);
                if (recipes == null)
                    return false;
                for (int i = 0; i < recipes.Count; i++)
                {
                    ItemPolicy.RecipeInfo recipe = recipes[reverseRecipes ? recipes.Count - 1 - i : i];
                    if (recipe.ResultCount <= 0 || recipe.Ingredients.Count == 0)
                        continue;
                    long crafts = ((long)need + recipe.ResultCount - 1) / recipe.ResultCount;
                    var attempt = new Dictionary<int, int>(pool);
                    bool ok = true;
                    foreach (KeyValuePair<int, int> ing in recipe.Ingredients)
                    {
                        long want = crafts * ing.Value;
                        if (ing.Value <= 0 || want > int.MaxValue ||
                            !TryConsumeCraftItem(ing.Key, (int)want, attempt, path, depth + 1, ref budget, reverseRecipes))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok)
                        continue;
                    long surplus = crafts * recipe.ResultCount - need;
                    attempt.TryGetValue(itemId, out int remaining);
                    if (surplus + remaining > int.MaxValue)
                        continue;
                    attempt[itemId] = remaining + (int)surplus;
                    pool.Clear();
                    foreach (KeyValuePair<int, int> kv in attempt)
                        if (kv.Value > 0)
                            pool[kv.Key] = kv.Value;
                    return true;
                }

                // Mixed-recipe fallback: vanilla lets the same result cook from
                // different recipes (iron smelts with coal OR a log); a legit
                // autosave can split one result across them. No single recipe
                // covers the whole shortfall, so apply the largest feasible
                // batch of each in turn until the need is met. Batch count is
                // binary-searched per recipe - feasibility is monotone in the
                // batch size - and each search step runs on its own copy, so a
                // failed probe can't strand spent ingredients.
                var mixed = new Dictionary<int, int>(pool);
                long remainingNeed = need;
                for (int pass = 0; pass < 32 && remainingNeed > 0; pass++)
                {
                    bool progress = false;
                    for (int i = 0; i < recipes.Count && remainingNeed > 0; i++)
                    {
                        ItemPolicy.RecipeInfo recipe = recipes[reverseRecipes ? recipes.Count - 1 - i : i];
                        if (recipe.ResultCount <= 0 || recipe.Ingredients.Count == 0)
                            continue;

                        long lo = 0;
                        long hi = (remainingNeed + recipe.ResultCount - 1) / recipe.ResultCount;
                        Dictionary<int, int> best = null;
                        while (lo < hi)
                        {
                            long mid = (lo + hi + 1) / 2;
                            var probe = new Dictionary<int, int>(mixed);
                            bool ok = true;
                            foreach (KeyValuePair<int, int> ing in recipe.Ingredients)
                            {
                                long want = mid * ing.Value;
                                if (ing.Value <= 0 || want > int.MaxValue ||
                                    !TryConsumeCraftItem(ing.Key, (int)want, probe, path, depth + 1, ref budget, reverseRecipes))
                                {
                                    ok = false;
                                    break;
                                }
                            }
                            if (ok)
                            {
                                lo = mid;
                                best = probe;
                            }
                            else
                            {
                                hi = mid - 1;
                            }
                        }
                        if (lo <= 0)
                            continue;

                        mixed.Clear();
                        foreach (KeyValuePair<int, int> kv in best)
                            if (kv.Value > 0)
                                mixed[kv.Key] = kv.Value;

                        long produced = lo * recipe.ResultCount;
                        long surplus = produced - remainingNeed;
                        remainingNeed = Math.Max(0, remainingNeed - produced);
                        if (surplus > 0)
                        {
                            mixed.TryGetValue(itemId, out int have2);
                            if (have2 < int.MaxValue)
                                mixed[itemId] = have2 + (int)Math.Min(surplus, int.MaxValue - have2);
                        }
                        progress = true;
                    }
                    if (!progress)
                        break;
                }
                if (remainingNeed > 0)
                    return false;
                pool.Clear();
                foreach (KeyValuePair<int, int> kv in mixed)
                    if (kv.Value > 0)
                        pool[kv.Key] = kv.Value;
                return true;
            }
            finally
            {
                path.Remove(itemId);
            }
        }

        /// <summary>
        /// Spends `count` of an item from credits first, then the owned baseline.
        /// </summary>
        private static bool TrySpend(PlayerTrustState state, int itemId, int count)
        {
            int credited = state.Credits.TryGetValue(itemId, out int c) ? c : 0;
            int owned = state.Owned != null && state.Owned.TryGetValue(itemId, out int o) ? o : 0;
            // Rounds already burned in-flight also reduce what can be spent elsewhere
            // (a player can't drop pool ammo that was already loaded and fired).
            state.AmmoSpent.TryGetValue(itemId, out int spent);
            if (owned + credited - spent < count)
                return false;

            int fromCredits = Math.Min(count, credited);
            if (fromCredits > 0)
            {
                int left = credited - fromCredits;
                if (left <= 0) state.Credits.Remove(itemId); else state.Credits[itemId] = left;
                count -= fromCredits;
            }
            if (count > 0 && state.Owned != null)
            {
                int left = owned - count;
                if (left <= 0) state.Owned.Remove(itemId); else state.Owned[itemId] = left;
            }
            return true;
        }

        /// <summary>
        /// Spends `count` of an item, falling back to proving-and-consuming the
        /// ingredients of a fresh craft when the item itself is not on the books.
        /// A crafted-but-never-saved item legitimately shows up as a placement,
        /// drop, or crate deposit before any save could record it; this commits
        /// the ingredient spend immediately so the same materials cannot be
        /// crafted-and-spent twice. Leftovers land back as Owned first
        /// (baseline truth) with the remainder as Credits; ammo already burned
        /// in-flight is reserved out of the crafting pool.
        /// </summary>
        private static bool TrySpendOrCraft(PlayerTrustState state, int itemId, int count)
        {
            if (count <= 0)
                return false;
            if (TrySpend(state, itemId, count))
                return true;

            var pool = state.Owned != null
                ? new Dictionary<int, int>(state.Owned)
                : new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in state.Credits)
            {
                pool.TryGetValue(kv.Key, out int n);
                pool[kv.Key] = checked(n + kv.Value);
            }
            var reserved = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in state.AmmoSpent)
            {
                pool.TryGetValue(kv.Key, out int n);
                int r = Math.Min(n, kv.Value);
                if (r > 0)
                {
                    reserved[kv.Key] = r;
                    pool[kv.Key] = n - r;
                }
            }
            if (!TryExplainByCraft(itemId, count, pool) &&
                !TryExplainByCraft(itemId, count, pool, null, 0, reverseRecipes: true))
                return false;
            foreach (KeyValuePair<int, int> kv in reserved)
            {
                pool.TryGetValue(kv.Key, out int n);
                pool[kv.Key] = checked(n + kv.Value);
            }
            var owned = new Dictionary<int, int>();
            var credits = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in pool)
            {
                int prior = state.Owned != null && state.Owned.TryGetValue(kv.Key, out int n) ? n : 0;
                int keep = Math.Min(prior, kv.Value);
                if (keep > 0)
                    owned[kv.Key] = keep;
                if (kv.Value > keep)
                    credits[kv.Key] = kv.Value - keep;
            }
            state.Owned = owned;
            state.Credits = credits;
            return true;
        }

        /// <summary>
        /// Called after the server persists a block placement. When the new block
        /// lands in a player's own body cells (pillaring up, getting walled in)
        /// their client legitimately overlaps a now-solid cell - mark it disputed
        /// so the embedded check defers instead of reading self-placement as noclip.
        /// </summary>
        public void NotifyBlockPlaced(byte senderId, int x, int y, int z)
        {
            foreach (KeyValuePair<byte, PlayerTrustState> kv in _players)
            {
                PlayerTrustState state = kv.Value;
                if (state == null || !state.HasBaseline || IsExempt(state))
                    continue;

                int px = (int)Math.Floor(state.LastX);
                int py = (int)Math.Floor(state.LastY);
                int pz = (int)Math.Floor(state.LastZ);

                // Bystanders: only exact body overlap counts - being walled in is
                // legit but they never consented to standing inside the block.
                // The placer gets a wider band: position updates lag the edit, so
                // a block placed at their feet during a pillar-jump lands while
                // the server still sees them mid-air above it.
                bool overlaps = kv.Key == senderId
                    ? px == x && pz == z && y >= py - 1 && y <= py + 2
                    : px == x && pz == z && (y == py || y == py + 1);

                if (overlaps)
                    NoteDisputedCell(state, x, y, z);
            }
        }

        /// <summary>
        /// Marks a cell where the client's world view provably differs from the
        /// server's (an edit the server rejected but the client still shows).
        /// Violation-backed rejections stay disputed for the whole session - the
        /// desync is permanent on the client and each dispute cost the sender a
        /// strike. Rate-drop disputes stay short-lived because they're free to
        /// farm and would otherwise let a cheater paint a shielded tunnel.
        /// </summary>
        private static void NoteDisputedCell(PlayerTrustState state, int x, int y, int z, bool longLived = false)
        {
            if (state.DisputedCells.Count >= 512)
                state.DisputedCells.RemoveAt(0);
            state.DisputedCells.Add((x, y, z, longLived ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2)));
        }

        private static bool CellDisputed(PlayerTrustState state, int x, int y, int z)
        {
            DateTime now = DateTime.UtcNow;
            List<(int X, int Y, int Z, DateTime Until)> cells = state.DisputedCells;
            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if (cells[i].Until <= now)
                {
                    cells.RemoveAt(i);
                    continue;
                }
                if (cells[i].X == x && cells[i].Y == y && cells[i].Z == z)
                    return true;
            }
            return false;
        }

        /// <summary>Spends `count` of the first placeable candidate the player can cover.</summary>
        private static bool TrySpendAny(PlayerTrustState state, List<int> candidates, int count)
        {
            foreach (int id in candidates)
            {
                if (TrySpendOrCraft(state, id, count))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the placed block type out of AlterBlockMessage / DigMessage.
        /// Returns false when the message is not a placement (dig = BlockType 0).
        /// </summary>
        private static bool TryReadPlacedBlockType(string typeName, byte[] data, out int blockType)
        {
            blockType = 0;
            if (data == null)
                return false;

            // AlterBlockMessage: [id][BlockLocation:12][BlockType:int32]
            if (typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage" && data.Length >= 17)
            {
                blockType = BitConverter.ToInt32(data, 13);
                return blockType != 0;
            }

            // DigMessage(placing=true) is NOT a placement: InGameHUD.Build sends it
            // alongside the AlterBlock, and its BlockType field names the block the
            // ray CLICKED (the existing neighbor), not the item being placed.
            // Spending on it would burn a phantom item for the click target's type
            // (e.g. placing anything against a rock wall eats one RockBlock).
            // The paired AlterBlockMessage carries the real placement and is the
            // only message that should consume an item here.
            return false;
        }

        /// <summary>
        /// Reads the target cell of an AlterBlockMessage so callers can compare
        /// the write against existing world state (idempotent-restore detection).
        /// </summary>
        private static bool TryReadEditTargetCell(string typeName, byte[] data, out int x, out int y, out int z)
        {
            x = y = z = 0;
            if (data == null)
                return false;

            // AlterBlockMessage: [id][BlockLocation:12][BlockType:int32]
            if (typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage" && data.Length >= 17)
            {
                x = BitConverter.ToInt32(data, 1);
                y = BitConverter.ToInt32(data, 5);
                z = BitConverter.ToInt32(data, 9);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads a block-removal claim: position of the cell being dug plus the
        /// claimed removed type (DigMessage states it; AlterBlock->Empty leaves it 0).
        /// </summary>
        private static bool TryReadDigClaim(
            string typeName, byte[] data,
            out int x, out int y, out int z, out int dugBlockType,
            out float hx, out float hy, out float hz,
            out float nx, out float ny, out float nz)
        {
            x = y = z = dugBlockType = 0;
            hx = hy = hz = nx = ny = nz = 0f;
            if (data == null)
                return false;

            // AlterBlockMessage to Empty: [id][BlockLocation:12][BlockType:int32 == 0]
            if (typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage" && data.Length >= 17 &&
                BitConverter.ToInt32(data, 13) == 0)
            {
                x = BitConverter.ToInt32(data, 1);
                y = BitConverter.ToInt32(data, 5);
                z = BitConverter.ToInt32(data, 9);
                return true;
            }

            // DigMessage digging: [id][Placing:1==0][Location:Vector3][Direction:Vector3][BlockType:int32]
            // Location is the face hit point; the removed cell sits half a block
            // opposite the in-normal. BlockType claims what was removed.
            if (typeName == "DNA.CastleMinerZ.Net.DigMessage" && data.Length >= 30 && data[1] == 0)
            {
                dugBlockType = BitConverter.ToInt32(data, 26);
                hx = BitConverter.ToSingle(data, 2); hy = BitConverter.ToSingle(data, 6); hz = BitConverter.ToSingle(data, 10);
                nx = BitConverter.ToSingle(data, 14); ny = BitConverter.ToSingle(data, 18); nz = BitConverter.ToSingle(data, 22);
                x = (int)Math.Floor(hx - nx * 0.5f);
                y = (int)Math.Floor(hy - ny * 0.5f);
                z = (int)Math.Floor(hz - nz * 0.5f);
                return true;
            }

            return false;
        }

        /// <summary>Effective world block lookup (generated terrain + saved edits); false when unprovable.</summary>
        private bool TryGetWorldBlock(int x, int y, int z, out int blockType)
        {
            blockType = 0;
            TerrainProbe.TryGetSavedBlockTypeDelegate probe = _worldBlockProbe ?? _savedBlockLookup;
            if (probe == null)
                return false;
            try
            {
                return probe(x, y, z, out blockType);
            }
            catch
            {
                blockType = 0;
                return false;
            }
        }

        /// <summary>Safe saved-block lookup; lookup exceptions never propagate into validation.</summary>
        private bool TryGetSavedBlock(int x, int y, int z, out int blockType)
        {
            blockType = 0;
            if (_savedBlockLookup == null)
                return false;
            try
            {
                return _savedBlockLookup(x, y, z, out blockType);
            }
            catch
            {
                blockType = 0;
                return false;
            }
        }

        #region Provenance notifications (called by the world handler)

        /// <summary>Wires the persisted-inventory baseline lookup.</summary>
        public void SetBaselineProvider(Func<ulong, string, Dictionary<int, int>> provider)
        {
            _baselineProvider = provider;
        }

        /// <summary>
        /// Wires persistent kick-count storage. Seeded counts are loaded once
        /// (restart = reload, never a reset); every subsequent kick persists the
        /// whole map. Null load keeps the counter in-memory only.
        /// </summary>
        public void SetKickCountStore(
            Func<Dictionary<ulong, (DateTime FirstUtc, int Count)>> load,
            Action<Dictionary<ulong, (DateTime FirstUtc, int Count)>> save)
        {
            _saveKickCounts = save;
            if (load == null)
                return;
            var seeded = load();
            if (seeded == null)
                return;
            foreach (var kv in seeded)
                _kickCounts[kv.Key] = kv.Value;
        }

        /// <summary>Wires the saved block-type lookup used to verify detonation claims.</summary>
        public void SetSavedBlockLookup(TerrainProbe.TryGetSavedBlockTypeDelegate lookup)
        {
            _savedBlockLookup = lookup;
        }

        /// <summary>Wires the full world block lookup (saved edits + generated terrain).</summary>
        public void SetWorldBlockProbe(TerrainProbe.TryGetSavedBlockTypeDelegate lookup)
        {
            _worldBlockProbe = lookup;
        }

        /// <summary>Server just served this player their stored inventory - it becomes the baseline.</summary>
        public void NotifyInventoryServed(byte senderId, byte[] invBytes)
        {
            if (!ItemPolicy.Ready || invBytes == null)
                return;

            PlayerTrustState state = GetState(senderId, () => null, () => 0UL);
            if (state.BaselineKnown)
                return;

            if (ItemPolicy.TrySummarizeInventory(invBytes, out Dictionary<int, int> totals))
            {
                state.Owned = totals;
                state.BaselineKnown = true;
            }
        }

        /// <summary>Host granted a pickup to this player - credit their inventory.</summary>
        public void NotifyPickupGranted(byte senderId, byte[] itemBytes)
        {
            if (!ItemPolicy.Ready || itemBytes == null || itemBytes.Length == 0)
                return;

            if (!ItemPolicy.TryParseItem(itemBytes, 0, itemBytes.Length, out ItemPolicy.ParsedItem item))
            {
                // Silent credit loss turns into phantom "unearned" violations at
                // the next save - log it so the failure mode is visible.
                _log($"[Trust] pickup grant skipped: item bytes ({itemBytes.Length}) did not parse for sender {senderId}");
                return;
            }

            PlayerTrustState state = GetState(senderId, () => null, () => 0UL);
            state.Credits.TryGetValue(item.Id, out int cur);
            state.Credits[item.Id] = cur + Math.Max(1, item.Stack);
        }

        /// <summary>Player destroyed a crate - credit its contents as expected spill pickups.</summary>
        public void NotifyCrateSpill(byte senderId, List<ItemPolicy.ParsedItem> items)
        {
            if (!ItemPolicy.Ready || items == null)
                return;

            PlayerTrustState state = GetState(senderId, () => null, () => 0UL);
            foreach (ItemPolicy.ParsedItem p in items)
            {
                state.Spill.TryGetValue(p.Id, out int cur);
                state.Spill[p.Id] = cur + p.Stack;
            }
        }

        /// <summary>
        /// Net crate slot transfer gate, called by the world handler AFTER the
        /// packet (checksum, item bytes, round-trip), the crate location, and
        /// the inventory array are all validated, immediately before the slot
        /// is overwritten. An absent slot side is passed as id=0, count=0.
        /// Same-id partial moves settle as a net debit (top-up) or credit
        /// (withdrawal); swaps debit the new item then credit the old one, so a
        /// denied deposit never mints the displaced stack.
        /// </summary>
        /// <returns>False when the transfer must be denied (consume the packet).</returns>
        public bool AllowCrateTransfer(byte senderId, int oldId, int oldCount, int newId, int newCount)
        {
            if (oldId < 0 || oldCount < 0 || newId < 0 || newCount < 0)
                return false;
            if (!ProvenanceApplies)
                return true;
            PlayerTrustState state = GetState(senderId, () => null, () => 0UL);
            if (IsExempt(state))
                return true;
            EnsureInventoryBaseline(state);
            if (!state.BaselineKnown)
                return false;

            int debit = newId == oldId ? Math.Max(0, newCount - oldCount) : newCount;
            int credit = newId == oldId ? Math.Max(0, oldCount - newCount) : oldCount;
            if (debit > 0 && !TrySpendOrCraft(state, newId, debit))
            {
                // Denied either way; only DISTINCT unowned ids count as
                // violations, same desync rule as other item claims.
                if (!NoteDeniedItemClaim(state, newId) ||
                    !DenialCountsAsViolation(ref state.ItemClaimDenials,
                        ItemClaimDenialGrace, ItemClaimDenialWindow))
                    return false;
                return Violation(state, "item",
                    $"deposited {debit}x item {newId} without owning it");
            }
            if (credit > 0)
                NotifyCrateWithdrawal(senderId, oldId, credit);
            return true;
        }

        /// <summary>
        /// A crate slot's previous contents left with the interacting player
        /// (withdrawal or swap-overwrite) - credit it as authorized inflow.
        /// </summary>
        public void NotifyCrateWithdrawal(byte senderId, int itemId, int stack)
        {
            if (!ItemPolicy.Ready || itemId <= 0 || stack <= 0)
                return;

            PlayerTrustState state = GetState(senderId, () => null, () => 0UL);
            state.Credits.TryGetValue(itemId, out int cur);
            state.Credits[itemId] = cur + stack;
        }

        #endregion

        /// <summary>
        /// Whether this message type carries a world target that should be
        /// reach-checked against the sender's last accepted position.
        /// </summary>
        public bool NeedsReachCheck(string typeName)
        {
            return typeName != null && ReachCheckedMessages.Contains(typeName);
        }

        /// <summary>
        /// Distance check for world-mutation messages. A client can only touch the
        /// world near where the server last saw it - no editing across the map.
        /// </summary>
        /// <summary>Per-type reach cap: blast-side effects fly much farther than arm's-reach edits.</summary>
        public float MaxReachFor(string typeName)
        {
            // Vanilla spawns enemies far from the player by design - ambient
            // spawns offset by SpawnRadius + velocity*5 and dragons spawn 750m
            // away. Enemy-death pickups land at the kill site too, so a rifle
            // kill can legitimately claim a drop far from the shooter. Arm's
            // reach would kick everyone the moment an enemy spawns or dies at
            // range; ownership/spawnable checks still bound these claims.
            if (typeName == "DNA.CastleMinerZ.Net.SpawnEnemyMessage" ||
                typeName == "DNA.CastleMinerZ.Net.CreatePickupMessage")
                return _config.TrustSpawnReach;
            return BlastReachMessages.Contains(typeName) ? _config.TrustBlastReach : _config.TrustMaxReach;
        }

        /// <summary>Wires a lookup reporting whether a registered spawner sits at/next to a position.</summary>
        public void SetSpawnerLocator(Func<float, float, float, bool> locator)
        {
            _spawnerLocator = locator;
        }

        private Func<float, float, float, bool> _spawnerLocator;

        // Diagnostic-only shadow combat tracker (CombatSimulationNotes.md).
        // Lazily built on the first accepted message while the flag is on;
        // _shadowDisabled latches after an unexpected fault so a broken
        // tracker can't spam or keep retrying. Never influences enforcement.
        private ShadowCombatTracker _shadowCombat;
        private bool _shadowDisabled;

        private void ObserveShadowMessage(byte senderId, string typeName, byte[] data)
        {
            if (_shadowDisabled)
                return;
            try
            {
                if (_shadowCombat == null)
                {
                    _shadowCombat = new ShadowCombatTracker(
                        _log,
                        _worldBlockProbe ?? _savedBlockLookup,
                        id => TryGetEnemyHealth((byte)id, out float hp) ? (float?)hp : null,
                        id => ItemPolicy.TryGetGunMaxEnemyDamage(id, out float d) ? (float?)d : null);
                }
                _shadowCombat.Terrain = _worldBlockProbe ?? _savedBlockLookup;
                _shadowCombat.ResolveTargetName = ResolveShadowTargetName;
                _shadowCombat.Observe(senderId, typeName, data, DateTime.UtcNow);
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
            catch (Exception ex)
            {
                // Unexpected fault: log once, then disable the diagnostic so
                // it can never disturb or spam the live game path.
                _shadowDisabled = true;
                _shadowCombat = null;
                _log("[ShadowCombat] tracker fault, diagnostics disabled: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves a spawn's named target to an announced, un-kicked player
        /// id for the shadow tracker - vanilla EnemyManager.GetNetworkPlayerByName
        /// compares names with ordinal Equals. Returns the id only on a unique
        /// match; null for none or ambiguous. Never creates player state from
        /// a claimed name.
        /// </summary>
        private byte? ResolveShadowTargetName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            byte? found = null;
            foreach (KeyValuePair<byte, PlayerTrustState> kv in _players)
            {
                PlayerTrustState s = kv.Value;
                if (!s.Announced || s.KickIssued || s.Name == null)
                    continue;
                if (!name.Equals(s.Name, StringComparison.Ordinal))
                    continue;
                if (found.HasValue)
                    return null; // ambiguous - do not attribute
                found = kv.Key;
            }
            return found;
        }

        public bool AllowEditAt(
            byte senderId,
            float targetX,
            float targetY,
            float targetZ,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            return AllowEditAt(senderId, null, null, targetX, targetY, targetZ, resolveName, resolveRemoteId);
        }

        public bool AllowEditAt(
            byte senderId,
            string typeName,
            float targetX,
            float targetY,
            float targetZ,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            return AllowEditAt(senderId, typeName, null, targetX, targetY, targetZ, resolveName, resolveRemoteId);
        }

        public bool AllowEditAt(
            byte senderId,
            string typeName,
            byte[] data,
            float targetX,
            float targetY,
            float targetZ,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            if (senderId == 0)
                return true;

            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            if (IsExempt(state) || !state.HasBaseline)
                return true;

            float maxReach = typeName != null ? MaxReachFor(typeName) : _config.TrustMaxReach;

            // AlterBlock carries the block being set. World-event conversions
            // (spawner on/off/dim, boss markers) legitimately happen at render
            // distance; spawner reward blocks require a real spawner nearby.
            if (typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage" &&
                TryReadPlacedBlockType(typeName, data, out int placedType))
            {
                if (ItemPolicy.IsRemoteAlterBlockType(placedType))
                {
                    maxReach = _config.TrustSpawnReach;
                }
                else if (ItemPolicy.IsLootAlterBlockType(placedType))
                {
                    if (_spawnerLocator == null || !_spawnerLocator(targetX, targetY, targetZ))
                    {
                        NoteDisputedCell(state,
                            (int)Math.Floor(targetX), (int)Math.Floor(targetY), (int)Math.Floor(targetZ),
                            longLived: true);
                        return Violation(state, "item",
                            $"placed loot block type {placedType} with no registered spawner nearby");
                    }
                    maxReach = _config.TrustSpawnReach;
                }
            }

            // Spawner clear-box removals write Empty (the placed-type branch
            // above skips those) and land around the marker, not the player -
            // the far corner is legitimately past arm's reach. AllowMessage
            // already proved the cell against world state; the one-shot entry
            // widens this write's reach without opening a replayable window.
            if (typeName == "DNA.CastleMinerZ.Net.AlterBlockMessage" &&
                state.SpawnerEditCells != null &&
                state.SpawnerEditCells.Remove(PackCell(
                    (int)Math.Floor(targetX), (int)Math.Floor(targetY), (int)Math.Floor(targetZ))))
            {
                maxReach = _config.TrustSpawnReach;
            }

            float dx = targetX - state.LastX;
            float dy = targetY - state.LastY;
            float dz = targetZ - state.LastZ;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= maxReach + StaleReachSlack(state))
                return true;

            // The client may act on its own view of this cell (a dig it thinks
            // succeeded) - world-state checks inside it get the benefit of the
            // doubt for a while so a desynced pocket can't embed-kick them.
            NoteDisputedCell(state,
                (int)Math.Floor(targetX), (int)Math.Floor(targetY), (int)Math.Floor(targetZ),
                longLived: true);

            const string typePrefix = "DNA.CastleMinerZ.Net.";
            string shortType = typeName != null && typeName.StartsWith(typePrefix, StringComparison.Ordinal)
                ? typeName.Substring(typePrefix.Length)
                : typeName ?? "?";
            return Violation(state, "reach", $"{shortType} {dist:F0}m away (max {maxReach:F0}m)");
        }

        /// <summary>
        /// Live-enemy registry: every accepted SpawnEnemyMessage is recorded so
        /// kill/give-up/speed-up claims can only reference enemies the server
        /// actually saw created. EnemyIDs are per-client counters - the same id
        /// is legal from many senders, so entries group by the bare id. Records
        /// persist after a spawner leaves: their zombies keep living on other
        /// clients' sims. FIFO eviction bounds memory.
        /// </summary>
        private sealed class EnemyRecord
        {
            public byte SpawnerId;
            public byte EnemyType; // SpawnEnemyMessage byte 25; 0xFF = unknown
            public DateTime SpawnedUtc;
            public bool InstantCounted;
            public bool BudgetDebited; // this record's HP already charged a killer
        }

        private const int MaxTrackedEnemies = 8192;
        private readonly Dictionary<int, List<EnemyRecord>> _enemiesById =
            new Dictionary<int, List<EnemyRecord>>();
        private readonly Queue<int> _enemyInsertOrder = new Queue<int>();

        private void RegisterEnemySpawn(byte spawnerId, int enemyId, byte enemyType = 0xFF)
        {
            if (!_enemiesById.TryGetValue(enemyId, out List<EnemyRecord> list))
            {
                list = new List<EnemyRecord>();
                _enemiesById[enemyId] = list;
            }
            else if (list.Exists(r => r.SpawnerId == spawnerId))
            {
                return; // same spawn seen on a second path - not a new enemy
            }
            list.Add(new EnemyRecord
            {
                SpawnerId = spawnerId,
                EnemyType = enemyType,
                SpawnedUtc = DateTime.UtcNow
            });
            _enemyInsertOrder.Enqueue(enemyId);

            while (_enemyInsertOrder.Count > MaxTrackedEnemies)
            {
                int oldest = _enemyInsertOrder.Dequeue();
                if (_enemiesById.TryGetValue(oldest, out List<EnemyRecord> l) && l.Count > 0)
                {
                    l.RemoveAt(0);
                    if (l.Count == 0)
                        _enemiesById.Remove(oldest);
                }
            }
        }

        private bool EnemyKnown(int enemyId)
        {
            return _enemiesById.TryGetValue(enemyId, out List<EnemyRecord> list) && list.Count > 0;
        }

        /// <summary>
        /// Canary enemies: server-planted spawns in sealed terrain (deep rock,
        /// skeleton types that give up instead of digging out). Every client
        /// sims them so they sit in each local enemy list - kill-all sweeps
        /// fire claims for every listed id, while no shot/swing/blast can
        /// legitimately reach a sealed mob. A non-exempt kill claim naming
        /// one is fabricated by definition.
        /// </summary>
        private readonly HashSet<int> _canaryEnemyIds = new HashSet<int>();

        /// <summary>Called by the world handler when it broadcasts a canary spawn.</summary>
        internal void RegisterCanaryEnemy(int enemyId, byte enemyType)
        {
            _canaryEnemyIds.Add(enemyId);
            RegisterEnemySpawn(0, enemyId, enemyType);
        }

        /// <summary>Called when the world handler retires/kills a canary.</summary>
        internal void UnregisterCanaryEnemy(int enemyId)
        {
            _canaryEnemyIds.Remove(enemyId);
        }

        private bool IsCanaryEnemy(int enemyId) => _canaryEnemyIds.Contains(enemyId);

        /// <summary>
        /// The youngest live registration for this enemy id, or null. Ids are
        /// per-client counters - a kill claim can't say which spawner's record
        /// it means, so a fresh twin of the id is what a kill gets timed
        /// against (rare collision, absorbed by the grace window).
        /// </summary>
        private EnemyRecord YoungestEnemyRecord(int enemyId)
        {
            if (!_enemiesById.TryGetValue(enemyId, out List<EnemyRecord> list) || list.Count == 0)
                return null;
            EnemyRecord youngest = null;
            foreach (EnemyRecord r in list)
            {
                if (youngest == null || r.SpawnedUtc > youngest.SpawnedUtc)
                    youngest = r;
            }
            return youngest;
        }

        // A kill claim landing this soon after the spawn was registered means
        // the enemy never got played - the "no enemies"/proximity-kill mods
        // wipe whatever spawns near the cheater, whether their own client
        // spawned it or someone else's did. Legit instant kills exist (a blast
        // chain eating a fresh spawn), so only the burst rate strikes; the kill
        // itself is real and still processes normally.
        private const double InstantKillMinAgeSec = 1.25;
        private const int InstantKillGrace = 3;
        private static readonly TimeSpan InstantKillWindow = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Vanilla EnemyType.StartingHealth for a SpawnEnemyMessage type byte
        /// (EnemyTypeEnum -> the computed Types[] table in EnemyType.cs):
        /// zombies 0-17 = 1..17,27 (last is x1.5); archers 18-25 = 1+2.5k;
        /// skeletons 26-49 = 1+0.9k; 50=150, 51=300, 52=70. 53+ never spawns
        /// (Types[] has 53 entries) - unknown returns false so no proof runs.
        /// </summary>
        private static bool TryGetEnemyHealth(byte enemyType, out float hp)
        {
            hp = 0f;
            switch (enemyType)
            {
                case 50: hp = 150f; return true;
                case 51: hp = 300f; return true;
                case 52: hp = 70f; return true;
            }
            if (enemyType < 17) { hp = enemyType + 1f; return true; }
            if (enemyType == 17) { hp = 27f; return true; }
            if (enemyType <= 25) { hp = 1f + (enemyType - 18) * 2.5f; return true; }
            if (enemyType <= 49) { hp = 1f + (enemyType - 26) * 0.9f; return true; }
            return false;
        }

        /// <summary>
        /// Enemy health for the damage-budget debit. Unknown/0xFF types fall
        /// back to a mid-zombie cost (10) instead of skipping: a registered
        /// enemy the server couldn't price still drains the killer's bank,
        /// so one-shot hacks can't hide behind unidentifiable spawns.
        /// </summary>
        private static bool TryGetEnemyHealthOrDefault(byte enemyType, out float hp)
        {
            if (TryGetEnemyHealth(enemyType, out hp))
                return true;
            hp = 10f;
            return true;
        }

        // Vanilla sends each kill claim exactly once - a lag duplicate is a
        // second claim at most. Every-tick mod sweeps (CastleWallsMk2's
        // KillAllMonsters) re-claim the same enemy id dozens of times until the
        // entity leaves the local list, so every claim past the per-enemy grace
        // is evidence; enough of them strike.
        private const int RepeatKillClaimGrace = 3;
        private const int RepeatKillDenialGrace = 6;
        private static readonly TimeSpan RepeatKillDenialWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan KillClaimIdTtl = TimeSpan.FromSeconds(45);
        private const int MaxKillClaimIds = 256;
        // Non-gun kill-claim bound: ~3 kills/s is the melee swing ceiling.
        // 12/4s covers an AoE/TNT multi-kill tick plus lag batching; the
        // 3/30s denial grace absorbs a legit blast-farm burst before strikes.
        private const int MeleeKillClaimLimit = 12;
        private const double MeleeKillClaimWindowSec = 4.0;
        private const int MeleeKillGrace = 3;
        private static readonly TimeSpan MeleeKillDenialWindow = TimeSpan.FromSeconds(30);
        private const int KillIntervalGrace = 4;
        private static readonly TimeSpan KillIntervalWindow = TimeSpan.FromSeconds(30);

        /// <summary>
        /// True when the claimed weapon (or a launcher-family equivalent -
        /// launcher shots report ShotFired held variants while kills claim the
        /// base id) was ever observed in a shot claim this session.
        /// </summary>
        private static bool FiredMatchingGun(PlayerTrustState state, int weaponId)
        {
            if (state.GunsFired.Contains(weaponId))
                return true;
            if (ItemPolicy.Ready &&
                (ItemPolicy.IsRocketLauncherHeldId(weaponId) ||
                 ItemPolicy.IsGrenadeLauncherHeldId(weaponId)))
            {
                foreach (int fired in state.GunsFired)
                {
                    if (ItemPolicy.IsRocketLauncherHeldId(fired) ||
                        ItemPolicy.IsGrenadeLauncherHeldId(fired))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Counts kill-claim evidence for a sender. Runs before the spam guard
        /// so rate-dropped claims still count - the drop only stops the kill
        /// from relaying, not the claim from being evidence. Returns true when
        /// the claim is provably fabricated and must not relay at all.
        /// </summary>
        private bool CheckKillClaimEvidence(PlayerTrustState state, int enemyId, int weaponId = 0, int killerId = -1)
        {
            if (IsExempt(state))
                return false;

            // Canary enemies are server-planted in sealed terrain - nothing
            // legit can reach them, so any claim naming one is fabricated.
            // Kill-all sweeps claim every id in the local list and hit these
            // on their first sweep. One claim = one strike, no grace needed.
            if (IsCanaryEnemy(enemyId))
            {
                Violation(state, "canary", "kill claim on unreachable server-planted enemy");
                return true;
            }

            // Non-gun claimed kills (melee/tool/unknown ids): no wire message
            // exists for swinging at an enemy, so the only honest bound is
            // melee swing speed - nobody lands kills faster than ~3/s
            // sustained. Kill-all sweeps (CastleWallsMk2 KillAllMonsters
            // claims every tick, always with a sword id) blow past it inside
            // a second. Runs before the registry lookup so claims denied
            // downstream still count as evidence.
            if (weaponId <= 0 || !ItemPolicy.TryGetGunEnemyDamage(weaponId, out _))
            {
                DateTime n = DateTime.UtcNow;
                if ((n - state.MeleeKillClaims.Start).TotalSeconds > MeleeKillClaimWindowSec)
                    state.MeleeKillClaims = (n, 1);
                else
                    state.MeleeKillClaims = (state.MeleeKillClaims.Start, state.MeleeKillClaims.Count + 1);

                if (state.MeleeKillClaims.Count > MeleeKillClaimLimit &&
                    DenialCountsAsViolation(ref state.MeleeKillDenials,
                        MeleeKillGrace, MeleeKillDenialWindow))
                {
                    Violation(state, "combat",
                        $"non-gun kill claims at {state.MeleeKillClaims.Count}/{MeleeKillClaimWindowSec}s (weapon {weaponId})");
                }
            }

            DateTime now = DateTime.UtcNow;

            // Gun-claimed kills: two deterministic impossibilities.
            if (weaponId > 0 && ItemPolicy.TryGetGunEnemyDamage(weaponId, out _))
            {
                // Zero-shot kill: GunsFired records claimed shots at parse
                // time, so denied-but-real shots still count - a kill claimed
                // with a gun never observed firing is fabricated outright.
                if (!FiredMatchingGun(state, weaponId))
                {
                    Violation(state, "combat",
                        $"kill claimed with item {weaponId} never observed firing");
                    return true;
                }

                // Single-target guns kill at most one enemy per trigger pull:
                // two claims tighter than the weapon's fire interval can't
                // both be real. Shotgun pellets / launcher splash legitimately
                // multi-kill and are excluded; packet bunching is absorbed by
                // the denial grace.
                if (!ItemPolicy.IsMultiKillGunId(weaponId) &&
                    ItemPolicy.TryGetGunFireInterval(weaponId, out double killInterval))
                {
                    if (state.LastSingleKillUtc != DateTime.MinValue &&
                        (now - state.LastSingleKillUtc).TotalSeconds < killInterval * 0.8 &&
                        DenialCountsAsViolation(ref state.KillIntervalDenials,
                            KillIntervalGrace, KillIntervalWindow))
                    {
                        Violation(state, "combat",
                            $"kill claims tighter than item {weaponId} fire interval {killInterval:F2}s");
                    }
                    state.LastSingleKillUtc = now;
                }
            }

            EnemyRecord youngest = YoungestEnemyRecord(enemyId);
            if (youngest != null && !youngest.InstantCounted)
            {
                double age = (now - youngest.SpawnedUtc).TotalSeconds;
                double minAge = InstantKillMinAgeSec;
                // Claimed weapon is a known gun and the spawn carried a known
                // enemy type: the weapon's per-trigger max enemy damage
                // (TryGetGunMaxEnemyDamage - PumpShotgun 3x, all others 2.5x)
                // gives the physically minimum time-to-kill. Below it the
                // claim is impossible for that weapon alone —
                // damage-hack one-shots land here. Co-damage (teammates,
                // explosions, lava) can also shortcut it, so the result feeds
                // the same grace window instead of insta-striking; the 8 s cap
                // keeps boss-HP enemies (100+ weak hits) out of the proof.
                if (weaponId > 0 && youngest.EnemyType != 0xFF &&
                    ItemPolicy.TryGetGunMaxEnemyDamage(weaponId, out float dmg) &&
                    ItemPolicy.TryGetGunFireInterval(weaponId, out double interval) &&
                    TryGetEnemyHealth(youngest.EnemyType, out float hp) &&
                    dmg > 0f && hp > 0f)
                {
                    double hits = Math.Ceiling(hp / dmg);
                    double ttk = (hits - 1.0) * interval;
                    minAge = ttk > 0.0 ? Math.Min(ttk * 0.75, 8.0) : 0.0;
                }
                if (age >= 0 && age < minAge)
                {
                    youngest.InstantCounted = true;
                    if (DenialCountsAsViolation(ref state.InstantKillDenials,
                            InstantKillGrace, InstantKillWindow))
                    {
                        Violation(state, "item",
                            $"killed enemy {enemyId} {age:F2}s after it spawned");
                    }
                }

                // Damage budget debit: a claimed gun kill spends the victim's
                // real StartingHealth from the KILLER's bank (the KillerID
                // field - only they could have dealt it). Charged once per
                // record; the strike lands on the claimer. Non-gun kills
                // (melee/TNT) skip - those costs aren't modeled.
                //
                // The deficit check is a HEURISTIC, not proof: co-damage from
                // teammates, explosions, and lava never crosses the wire, so
                // a legitimate finisher can spend HP their own shots never
                // dealt. Only a sustained deep deficit counts (grace +
                // DamageDeficitLimit). Every debit is logged so a contested
                // call can be audited instead of hidden.
                if (!youngest.BudgetDebited && killerId > 0 &&
                    ItemPolicy.TryGetGunMaxEnemyDamage(weaponId, out float maxDmg) &&
                    TryGetEnemyHealthOrDefault(youngest.EnemyType, out float killedHp))
                {
                    youngest.BudgetDebited = true;
                    PlayerTrustState killerState = GetState((byte)killerId, null, null);
                    killerState.DamageBudget -= killedHp;
                    if (_config.LogCombat)
                    {
                        string weaponName = ItemPolicy.GunNameForId(weaponId);
                        _log($"[CombatEvidence] {state.Name}({state.RemoteId}): kill claim " +
                             $"enemy {enemyId} type {youngest.EnemyType}, killer {killerId}, " +
                             $"weapon {weaponId}{(weaponName != null ? $" ({weaponName})" : "")}: " +
                             $"debit {killedHp:F0}hp, max {maxDmg:F1}/trigger, " +
                             $"killer bank {killerState.DamageBudget:F1}");
                    }
                    if (killerState.DamageBudget < DamageDeficitLimit &&
                        DenialCountsAsViolation(ref state.DamageDeficitDenials,
                            DamageDeficitGrace, DamageDeficitWindow))
                    {
                        Violation(state, "combat",
                            $"kill claims outpace damage player {killerId} could deal " +
                            $"(heuristic; weapon {weaponId} debited {killedHp:F0}hp " +
                            $"for enemy {enemyId}, bank {killerState.DamageBudget:F1})");
                    }
                }
            }

            if (NoteKillClaim(state, enemyId, now) > RepeatKillClaimGrace &&
                DenialCountsAsViolation(ref state.RepeatKillDenials,
                    RepeatKillDenialGrace, RepeatKillDenialWindow))
            {
                Violation(state, "combat",
                    $"repeated kill claims for enemy {enemyId}");
            }

            return false;
        }

        /// <summary>How many times this sender has claimed a kill for this enemy id.</summary>
        private static int NoteKillClaim(PlayerTrustState state, int enemyId, DateTime now)
        {
            state.KillClaimCounts ??= new Dictionary<int, (int Count, DateTime ExpiryUtc)>();
            int count = 1;
            if (state.KillClaimCounts.TryGetValue(enemyId,
                    out (int Count, DateTime ExpiryUtc) existing) && existing.ExpiryUtc > now)
                count = existing.Count + 1;
            state.KillClaimCounts[enemyId] = (count, now + KillClaimIdTtl);
            if (state.KillClaimCounts.Count > MaxKillClaimIds)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, (int Count, DateTime ExpiryUtc)> kv in state.KillClaimCounts)
                {
                    if (kv.Value.ExpiryUtc <= now)
                        (stale ??= new List<int>()).Add(kv.Key);
                }
                if (stale != null)
                    foreach (int id in stale)
                        state.KillClaimCounts.Remove(id);
            }
            return count;
        }

        /// <summary>
        /// Ids of enemies whose kill was accepted - kept briefly because two
        /// clients can legitimately claim the same kill under lag and the second
        /// claim must not read as a forged id.
        /// </summary>
        private readonly Dictionary<int, DateTime> _deadEnemies = new Dictionary<int, DateTime>();
        private const double DeadEnemyGraceSec = 30.0;
        private const int MaxDeadEnemies = 4096;

        private void DeregisterEnemy(int enemyId)
        {
            if (_enemiesById.TryGetValue(enemyId, out List<EnemyRecord> list) && list.Count > 0)
            {
                list.RemoveAt(0);
                if (list.Count == 0)
                    _enemiesById.Remove(enemyId);
            }
            _deadEnemies[enemyId] = DateTime.UtcNow;
            if (_deadEnemies.Count > MaxDeadEnemies)
            {
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-DeadEnemyGraceSec);
                List<int> stale = null;
                foreach (KeyValuePair<int, DateTime> kv in _deadEnemies)
                {
                    if (kv.Value < cutoff)
                        (stale ??= new List<int>()).Add(kv.Key);
                }
                if (stale != null)
                    foreach (int id in stale)
                        _deadEnemies.Remove(id);
            }
        }

        private bool EnemyRecentlyDead(int enemyId)
        {
            return _deadEnemies.TryGetValue(enemyId, out DateTime deadAt) &&
                   (DateTime.UtcNow - deadAt).TotalSeconds < DeadEnemyGraceSec;
        }

        // Unknown enemy ids: a control claim naming an id the registry never saw
        // spawned is either a SpawnEnemyMessage eaten upstream (FloodGuard
        // blackhole / rate drop) or a forged claim. Recently-dead ids pass;
        // repeat claims for the same unseen id are the same desynced enemy and
        // deny silently; only NEW distinct ids count toward the grace window -
        // a forge spray hits it fast, a blackholed client's few eaten spawns
        // don't. The claim is always denied either way: unregistered enemies
        // can't record kills or dig cells.
        private const int EnemyClaimDenialGrace = 8;
        private static readonly TimeSpan EnemyClaimDenialWindow = TimeSpan.FromSeconds(30);
        private const int MaxUnknownEnemyIds = 64;

        private bool HandleUnknownEnemyClaim(PlayerTrustState state, int enemyId)
        {
            if (EnemyRecentlyDead(enemyId))
                return true;
            if (!NoteUnknownEnemy(state, enemyId))
                return false;
            if (DenialCountsAsViolation(ref state.EnemyClaimDenials,
                    EnemyClaimDenialGrace, EnemyClaimDenialWindow))
            {
                return Violation(state, "combat",
                    $"enemy control claim for unknown enemy id {enemyId}");
            }
            return false;
        }

        // Item ownership claims: same desync shape as enemy ids - a denied or
        // dropped pickup grant leaves a phantom item the client keeps claiming.
        // Only NEW distinct unowned ids count toward violations.
        private const int ItemClaimDenialGrace = 6;
        private static readonly TimeSpan ItemClaimDenialWindow = TimeSpan.FromSeconds(30);
        private const int MaxDeniedItemClaims = 64;
        // Denied-item-claim keys for placements are block types namespaced above
        // the Int16 item-id range so they never collide with real item ids.
        private const int PlacementClaimKeyBit = 0x40000000;

        /// <summary>True when this item id is newly denied (should count), false when already tracked.</summary>
        private static bool NoteDeniedItemClaim(PlayerTrustState state, int itemId)
        {
            state.DeniedItemClaims ??= new Dictionary<int, DateTime>();
            DateTime now = DateTime.UtcNow;
            if (state.DeniedItemClaims.TryGetValue(itemId, out DateTime exp) && exp > now)
                return false;
            state.DeniedItemClaims[itemId] = now + TimeSpan.FromMinutes(2);
            if (state.DeniedItemClaims.Count > MaxDeniedItemClaims)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, DateTime> kv in state.DeniedItemClaims)
                {
                    if (kv.Value <= now)
                        (stale ??= new List<int>()).Add(kv.Key);
                }
                if (stale != null)
                    foreach (int id in stale)
                        state.DeniedItemClaims.Remove(id);
            }
            return true;
        }

        /// <summary>True when this id is newly unseen (should count), false when already tracked.</summary>
        private static bool NoteUnknownEnemy(PlayerTrustState state, int enemyId)
        {
            state.UnknownEnemyIds ??= new Dictionary<int, DateTime>();
            DateTime now = DateTime.UtcNow;
            if (state.UnknownEnemyIds.TryGetValue(enemyId, out DateTime exp) && exp > now)
                return false;
            state.UnknownEnemyIds[enemyId] = now + TimeSpan.FromSeconds(DeadEnemyGraceSec);
            if (state.UnknownEnemyIds.Count > MaxUnknownEnemyIds)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, DateTime> kv in state.UnknownEnemyIds)
                {
                    if (kv.Value <= now)
                        (stale ??= new List<int>()).Add(kv.Key);
                }
                if (stale != null)
                    foreach (int id in stale)
                        state.UnknownEnemyIds.Remove(id);
            }
            return true;
        }

        /// <summary>
        /// Drops all tracked state for a disconnected player id.
        /// </summary>
        public void RemovePlayer(byte playerId)
        {
            _players.Remove(playerId);
            _shadowCombat?.RemovePlayer(playerId);
        }

        #region Movement / PlayerUpdate Enforcement

        private bool CheckPlayerUpdate(
            byte senderId,
            byte[] data,
            Func<string> resolveName,
            Func<ulong> resolveRemoteId)
        {
            if (data == null || data.Length < MinUpdatePayload)
                return true;

            float x = BitConverter.ToSingle(data, 1);
            float y = BitConverter.ToSingle(data, 5);
            float z = BitConverter.ToSingle(data, 9);
            bool dead = (data[15] & DeadFlag) != 0;
            bool reloading = (data[15] & ReloadingFlag) != 0;
            DateTime now = DateTime.UtcNow;

            PlayerTrustState state = GetState(senderId, resolveName, resolveRemoteId);
            if (IsExempt(state))
                return true;

            // Reload cycle edges. The clip only refills when the flag has been
            // held for the held gun's vanilla ReloadTime (x0.8 jitter margin) —
            // mid-clip cancels are legit (fire aborts a reload) so a short flag
            // simply doesn't refill, and a one-frame fake flag gains nothing.
            if (reloading)
            {
                if (!state.ReloadingActive)
                {
                    state.ReloadingActive = true;
                    state.ReloadStartUtc = now;
                    state.ReloadRefilled = false;
                }
                state.LastReloadFlagUtc = now;
                TryCompleteClipReload(state, now);
            }
            else if (state.ReloadingActive)
            {
                state.ReloadingActive = false;
                TryCompleteClipReload(state, now);
            }

            // A kick is already queued for this player - drop the update quietly
            // so post-kick packets can't keep scoring violations or re-kicking.
            if (state.KickIssued)
                return false;

            // Pending lethal-blast exposure: a living player inside an accepted
            // detonation's kill radius must be dead within moments - a dead
            // update clears it, a still-alive update past the window strikes.
            if (!CheckLethalBlastExposure(state, x, y, z, now, dead))
                return false;

            // Position spoofing: ghost/vanish mods pin the replicated position
            // to Vector3.Zero or an astronomical value. Real physics can never
            // produce float-exact all-zero or out-of-world coordinates, so one
            // sighting is already proof - the update stream then stacks strikes
            // fast enough to kick a standing-still ghost in under a second.
            if ((x == 0f && y == 0f && z == 0f) ||
                float.IsNaN(x) || float.IsInfinity(x) || Math.Abs(x) > 1.0e6f ||
                float.IsNaN(y) || float.IsInfinity(y) || Math.Abs(y) > 1.0e6f ||
                float.IsNaN(z) || float.IsInfinity(z) || Math.Abs(z) > 1.0e6f)
            {
                return Violation(state, "ghost", $"spoofed position ({x},{y},{z})");
            }

            // First sighting only establishes a baseline; nothing to compare yet.
            if (!state.HasBaseline)
            {
                ResetEnvironmentTimers(state);
                Accept(state, x, y, z, now, dead);
                return true;
            }

            // Respawn: dead->alive jumps to the spawn point legitimately.
            if (state.WasDead && !dead)
            {
                ResetEnvironmentTimers(state);
                Accept(state, x, y, z, now, dead);
                return true;
            }

            // While dead the player should not move, but position holds at the
            // death point anyway; track it without punishing odd dead updates.
            if (dead)
            {
                ResetEnvironmentTimers(state);
                Accept(state, x, y, z, now, dead);
                return true;
            }

            double dtr = (now - state.LastUpdateUtc).TotalSeconds;
            if (dtr < 0)
                dtr = 0;
            double dt = dtr < 0.05 ? 0.05 : dtr;
            // Timer accrual uses REAL elapsed time, not the floored dt the
            // speed quotient needs: each update in a queued post-stall burst
            // otherwise claimed a 50ms slice it never observed. That let a
            // legit sprint overdraw the stamina pool 3x during a blackhole
            // flush and then stack impossible-time on still-legal frames.
            // The 0.25 cap still bounds stall-boundary deposits; real
            // violations need sustained evidence across many updates.
            double dtc = Math.Min(dtr, 0.25);

            float dx = x - state.LastX;
            float dy = y - state.LastY;
            float dz = z - state.LastZ;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Teleport-class jump: distance alone decides, independent of timing.
            if (dist > _config.TrustTeleportDistance)
                return EvaluateTeleport(state, x, y, z, now, dead, dist);

            if (!_flightAllowed)
            {
                double horiz = Math.Sqrt(dx * dx + dz * dz);
                double horizSpeed = horiz / dt;
                double vertSpeed = dy / dt;

                // Sustained over-speed. Packet jitter can produce one-off spikes, so
                // a burst must persist across several updates before it counts.
                // Speeds beyond double the cap can't be produced by anything
                // legitimate - sustained extremes kick immediately, no counting.
                if (horizSpeed > _config.TrustMaxHorizontalSpeed ||
                    vertSpeed > _config.TrustMaxVerticalSpeed)
                {
                    state.OverSpeedStreak++;
                    if (state.OverSpeedStreak >= 3)
                    {
                        state.OverSpeedStreak = 0;
                        double peak = Math.Max(horizSpeed, vertSpeed);
                        float hardCap = Math.Max(_config.TrustMaxHorizontalSpeed, _config.TrustMaxVerticalSpeed);
                        if (peak > hardCap * 2f)
                        {
                            string target = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
                            IssueKick(state, "speed", $"{peak:F1} m/s", target);
                            _log($"[Trust] {state.Name}: kicked for sustained extreme speed {peak:F1} m/s.");
                            return false;
                        }
                        return Violation(state, "speed", $"{peak:F1} m/s");
                    }
                }
                else
                {
                    state.OverSpeedStreak = 0;
                }

                // Flight: rising for far longer than a vanilla jump while barely
                // moving horizontally. Stairs/slopes always require lateral motion,
                // so a near-stationary climb is unambiguous. Accrued with clamped
                // dt so a stalled update stream can't fabricate the window.
                if (dy > 0.01f && horiz < 0.1)
                {
                    state.RiseSeconds += dtc;
                    if (state.RiseSeconds > _config.TrustFlyGraceSeconds)
                    {
                        state.RiseSeconds = 0;
                        return Violation(state, "fly", "sustained rise without lateral motion");
                    }
                }
                else
                {
                    state.RiseSeconds = 0;
                }

                // Support check: the terrain probe asks the real generated world
                // (plus saved block edits) whether anything occupies space within
                // TrustHoverMaxHeight below the feet. Unsupported airtime accrues
                // while hovering, rising, or gliding slowly - this is what catches
                // flight that mimics stairs or sits still in mid-air. Real falls
                // sink fast and drain the timer; landings reset it.
                if (_supportProbe != null)
                {
                    int scanDepth = (int)Math.Ceiling(_config.TrustHoverMaxHeight) + 2;
                    SupportProbeResult support = _supportProbe(x, y, z, scanDepth);
                    if (support == SupportProbeResult.Supported)
                    {
                        state.AirborneSeconds = 0;
                        state.LastSupportedY = y;
                        state.AboveCeilingSeconds = 0;

                        // Landing: vanilla fall damage is (impactSpeed-18)*0.0667 at
                        // g=-20 -> a ~27m drop is already lethal. 35m survived is
                        // impossible without godmode.
                        if (state.FallPeakY != float.MinValue)
                        {
                            float drop = state.FallPeakY - y;
                            state.FallPeakY = float.MinValue;
                            if (drop > 35f)
                                return Violation(state, "godmode", $"survived {drop:F1}m fall");
                        }
                    }
                    else
                    {
                        if (y > state.FallPeakY)
                            state.FallPeakY = y;

                        if (support == SupportProbeResult.Elevated)
                        {
                            if (vertSpeed > -_config.TrustMinSinkSpeed)
                                state.AirborneSeconds += dtc;
                            else
                                state.AirborneSeconds = Math.Max(0, state.AirborneSeconds - dtr);

                            if (state.AirborneSeconds > _config.TrustMaxAirborneSeconds)
                            {
                                state.AirborneSeconds = 0;
                                return Violation(
                                    state,
                                    "fly",
                                    $"unsupported in air for {_config.TrustMaxAirborneSeconds:F1}s+");
                            }
                        }

                        // Ballistic ceiling: vanilla's best case is a ~5m apex above
                        // the last supported position (10 m/s jump at g=-20, plus a
                        // double jump). Sustained altitude far above that is physically
                        // unreachable - this catches hop-flying that slips the rise check.
                        if (state.LastSupportedY != float.MinValue && y > state.LastSupportedY + 25f)
                        {
                            state.AboveCeilingSeconds += dtc;
                            if (state.AboveCeilingSeconds > 2.0)
                            {
                                state.AboveCeilingSeconds = 0;
                                return Violation(state, "fly", $"altitude {y - state.LastSupportedY:F0}m above supported position");
                            }
                        }
                        else
                        {
                            state.AboveCeilingSeconds = Math.Max(0, state.AboveCeilingSeconds - dtr);
                        }
                    }
                }

                // Noclip: the player must be *buried* - feet, head, and a thick
                // column of cover overhead all inside blocks that physically
                // block players. Requiring six solid cells above the head
                // separates "inside a block" from "on top of one": regenerated
                // terrain can run several blocks high vs the client's surface in
                // terraformed areas, which reads as feet+head solid while the
                // player stands in open air - but a phantom surface is never six
                // cells thick. Unprovable terrain gets the benefit of the doubt,
                // and brief embeds (a door closing on you) clear under the timer.
                int fx = (int)Math.Floor(x), fz = (int)Math.Floor(z);
                if (_worldBlockProbe != null || _savedBlockLookup != null)
                {
                    int fy = (int)Math.Floor(y);
                    // Doors never prove embedding: a closed door is a thin slab
                    // inside its cell, and vanilla lets players occupy the rest.
                    bool SolidAt(int px, int cy, int pz) =>
                        TryGetWorldBlock(px, cy, pz, out int t) &&
                        ItemPolicy.IsPlayerBlockingBlockType(t) &&
                        !ItemPolicy.IsDoorBlockType(t);
                    bool Solid(int cy) => SolidAt(fx, cy, fz);

                    bool feetSolid = Solid(fy);
                    bool headSolid = Solid(fy + 1);
                    bool cover = Solid(fy + 2) && Solid(fy + 3) && Solid(fy + 4) &&
                                 Solid(fy + 5) && Solid(fy + 6) && Solid(fy + 7);

                    // A real burial is surrounded laterally too. A player whose
                    // feet+head sit in a solid column but has an open side is at
                    // a legal edge: brushing a tree trunk while walking leaves,
                    // hugging a cave wall, or a worldgen probe that's off by a
                    // couple cells at the surface. Require most of the 8 lateral
                    // cells (4 neighbors x 2 levels) solid before accumulating.
                    int lateralSolid = 0;
                    if (feetSolid && headSolid && cover)
                    {
                        for (int cy = fy; cy <= fy + 1; cy++)
                        {
                            if (SolidAt(fx + 1, cy, fz)) lateralSolid++;
                            if (SolidAt(fx - 1, cy, fz)) lateralSolid++;
                            if (SolidAt(fx, cy, fz + 1)) lateralSolid++;
                            if (SolidAt(fx, cy, fz - 1)) lateralSolid++;
                        }
                    }

                    bool anyDisputed = false;
                    for (int cy = fy; cy <= fy + 7 && !anyDisputed; cy++)
                        anyDisputed = CellDisputed(state, fx, cy, fz);

                    if (feetSolid && headSolid && cover && lateralSolid >= 6 && !anyDisputed)
                    {
                        state.EmbeddedSeconds += dtc;
                    }
                    else
                    {
                        state.EmbeddedSeconds = 0;
                    }

                    if (state.EmbeddedSeconds > 2.5)
                    {
                        state.EmbeddedSeconds = 0;
                        TryGetWorldBlock(fx, fy, fz, out int feetType);
                        TryGetWorldBlock(fx, fy + 1, fz, out int headType);
                        // How deep the claimed burial runs - tells real noclip
                        // (deep column) from probe surface error (thin cap).
                        int coverRun = 0;
                        for (int cy = fy + 2; cy < fy + 40 && Solid(cy); cy++)
                            coverRun++;
                        return Violation(state, "noclip",
                            $"position inside solid blocks at ({fx},{fy},{fz}) feet={feetType} head={headType} cover={coverRun}");
                    }
                }

                // Godmode proxies: lethal environments must eventually kill. Lava
                // ticks 0.25 dmg per 0.5s (dead in ~2s); drowning kills in ~15s
                // (10s oxygen + 5s bleed-out); sprint drains 0.6 stamina/s against
                // 0.25/s regen so >12s of uninterrupted sprint is impossible.
                if (_worldBlockProbe != null || _savedBlockLookup != null)
                {
                    int fy = (int)Math.Floor(y);
                    if (TryGetWorldBlock(fx, fy, fz, out int envType) &&
                        ItemPolicy.IsLavaBlockType(envType))
                    {
                        state.LavaSeconds += dtc;
                    }
                    else
                    {
                        state.LavaSeconds = 0;
                    }

                    if (state.LavaSeconds > 4.5)
                    {
                        state.LavaSeconds = 0;
                        return Violation(state, "godmode", "sustained lava submersion without dying");
                    }
                }

                // Stamina pool: sprint (8 m/s vs run 4 m/s, threshold sits between)
                // drains 0.6/s from a 1.0 pool; regen is 0.25/s only after a 1s
                // pause. Duty-cycle faking can't beat the pool math - sprinting
                // on an empty pool is impossible in vanilla no matter how the
                // cheat spaces its bursts. In Endurance modes sprint is fully
                // disabled, so the drain empties the pool on the first update.
                // dt is capped so a stalled update stream can't overdraw.
                if (horizSpeed > 6.0)
                {
                    state.StaminaRestSeconds = 0;
                    state.StaminaPool -= _staminaDrainPerSec * Math.Min(dtr, 1.0);
                    if (state.StaminaPool <= 0)
                    {
                        state.ImpossibleSprintSeconds += dtc;
                        if (state.ImpossibleSprintSeconds > 1.5)
                        {
                            state.ImpossibleSprintSeconds = 0;
                            state.StaminaPool = 1.0;
                            return Violation(state, "stamina",
                                "sprinting with exhausted stamina");
                        }
                    }
                }
                else
                {
                    state.ImpossibleSprintSeconds = 0;
                    state.StaminaRestSeconds += dtr;
                    // Vanilla delays regen 1s after last spend, then 0.25/s.
                    if (state.StaminaRestSeconds > 1.0)
                        state.StaminaPool = Math.Min(1.0, state.StaminaPool + 0.25 * dtr);
                }
            }

            Accept(state, x, y, z, now, dead);
            return true;
        }

        private bool EvaluateTeleport(
            PlayerTrustState state,
            float x,
            float y,
            float z,
            DateTime now,
            bool dead,
            double dist)
        {
            if (!_teleportModeAllowed)
            {
                // Mode/difficulty offers no vanilla teleport: operator-only.
                if (ServerCommandRankHelper.HasAccess(state.Rank, _config.TrustTeleportOpRank))
                {
                    ResetEnvironmentTimers(state);
                    Accept(state, x, y, z, now, dead);
                    _log($"[Trust] {state.Name}: operator teleport accepted ({dist:F0}m, mode forbids it for players).");
                    return true;
                }

                return Violation(state, "teleport", $"{dist:F0}m jump in mode that forbids teleporting");
            }

            if (_config.TrustTeleportCooldownSeconds > 0f &&
                state.LastTeleportUtc != DateTime.MinValue &&
                (now - state.LastTeleportUtc).TotalSeconds < _config.TrustTeleportCooldownSeconds)
            {
                double wait = _config.TrustTeleportCooldownSeconds - (now - state.LastTeleportUtc).TotalSeconds;
                // The teleport still happened client-side - rejecting the position
                // leaves the baseline stale so every later update reads as another
                // >30m jump and one cooldown breach cascades into a kick. Count the
                // violation but accept the position; LastTeleportUtc stays at the
                // last legal teleport so repeat breaches keep counting.
                Violation(state, "teleport-rate", $"teleported again {wait:F0}s too early");
                ResetEnvironmentTimers(state);
                Accept(state, x, y, z, now, dead);
                return true;
            }

            state.LastTeleportUtc = now;
            ResetEnvironmentTimers(state);
            Accept(state, x, y, z, now, dead);
            return true;
        }

        #endregion

        #region State / Violations

        private PlayerTrustState GetState(byte senderId, Func<string> resolveName, Func<ulong> resolveRemoteId)
        {
            if (!_players.TryGetValue(senderId, out PlayerTrustState state))
            {
                state = new PlayerTrustState();
                _players[senderId] = state;
            }

            if (!state.IdentityResolved)
            {
                // Lazy resolve: notification paths call GetState without identity
                // delegates, so only lock identity in once something answered.
                ulong rid = SafeInvoke(resolveRemoteId);
                string name = SafeInvoke(resolveName);
                if (rid != 0 || !string.IsNullOrWhiteSpace(name))
                {
                    state.IdentityResolved = true;
                    state.RemoteId = rid;
                    if (!string.IsNullOrWhiteSpace(name))
                        state.Name = name;
                }
            }

            // Rank is refreshed periodically so setrank/op/deop take effect mid-session.
            if ((DateTime.UtcNow - state.RankRefreshedUtc).TotalSeconds > 5)
            {
                state.RankRefreshedUtc = DateTime.UtcNow;
                state.Rank = _rankLookup(state.RemoteId, state.Name);
            }

            return state;
        }

        private bool IsExempt(PlayerTrustState state)
        {
            return ServerCommandRankHelper.HasAccess(state.Rank, _config.TrustExemptMinRank);
        }

        private void Accept(PlayerTrustState state, float x, float y, float z, DateTime now, bool dead)
        {
            state.HasBaseline = true;
            state.LastX = x;
            state.LastY = y;
            state.LastZ = z;
            state.LastUpdateUtc = now;
            state.WasDead = dead;
        }

        /// <summary>Clears environment timers - used on baseline/respawn/death boundaries.</summary>
        private static void ResetEnvironmentTimers(PlayerTrustState state)
        {
            state.FallPeakY = float.MinValue;
            state.LastSupportedY = float.MinValue;
            state.AboveCeilingSeconds = 0;
            state.LavaSeconds = 0;
            state.EmbeddedSeconds = 0;
            state.AirborneSeconds = 0;
            state.StaminaPool = 1.0;
            state.StaminaRestSeconds = 0;
            state.ImpossibleSprintSeconds = 0;
            state.LethalBlastUtc = DateTime.MinValue;
            state.LethalBlastDesc = null;
            state.BlastPendingUntil = DateTime.MinValue;
            state.BlastPendingDesc = null;
        }

        /// <summary>
        /// Records a violation and returns false so the caller drops the packet.
        /// Strikes are bucketed per violation kind: a 120-second clean stretch
        /// resets only that kind's bucket, and kick/ban thresholds compare the
        /// ONE bucket - different kinds never add up to a removal. Once a kick
        /// is already queued later violations are dropped silently.
        /// </summary>
        private bool Violation(PlayerTrustState state, string kind, string detail)
        {
            if (state.KickIssued)
                return false;

            DateTime now = DateTime.UtcNow;
            string bucketKind = kind ?? "unknown";
            if (!state.ViolationBuckets.TryGetValue(
                    bucketKind, out (int Count, DateTime LastUtc) bucket) ||
                (now - bucket.LastUtc).TotalSeconds > 120.0)
            {
                bucket = (0, now);
            }
            bucket.Count++;
            bucket.LastUtc = now;
            state.ViolationBuckets[bucketKind] = bucket;

            // Diagnostic aggregates: lifetime observed strikes this session
            // (a stale bucket keeps its count until the next strike of that
            // kind), not the current enforcement state.
            int total = 0;
            foreach (var kv in state.ViolationBuckets)
                total += kv.Value.Count;
            state.Violations = total;
            state.LastViolationUtc = now;

            // Position/probe-derived kinds and item provenance keep a higher
            // threshold — their evidence is timing-, terrain-, or
            // baseline-desync-sensitive, so flukes deserve more slack.
            // Provable wire-fact kinds (stamina, combat, protocol, malformed,
            // teleport, canary, ghost, godmode, reach) kick at the global
            // default of 5.
            int kickThreshold =
                kind == "fly" || kind == "speed" || kind == "noclip" ||
                kind == "item"
                    ? 10 : _config.TrustViolationsKick;
            _log($"[Trust] {state.Name}: {kind} violation ({detail}) [{bucket.Count}/{kickThreshold}]");

            string target = state.RemoteId != 0UL ? state.RemoteId.ToString() : state.Name;
            if (_config.TrustViolationsBan > 0 && bucket.Count >= _config.TrustViolationsBan)
            {
                state.KickIssued = true;
                QueuePlacementRollback(state);
                _ban(target, $"Banned for cheating: {DescribeKind(kind)}. ({detail})");
                _log($"[Trust] {state.Name}: auto-banned after {bucket.Count} {kind} violations.");
                return false;
            }

            if (bucket.Count >= kickThreshold)
            {
                IssueKick(state, kind, detail, target);
                _log($"[Trust] {state.Name}: kicked after {bucket.Count} {kind} violations.");
            }

            return false;
        }

        /// <summary>
        /// Issues the removal for a violated player: normally a kick, but a
        /// SteamID that has already been kicked TrustKickBan times inside the
        /// window escalates to a persisted ban — rejoining does not reset the
        /// count the way it resets the per-session violation count.
        /// </summary>
        private void IssueKick(PlayerTrustState state, string kind, string detail, string target)
        {
            state.KickIssued = true;
            QueuePlacementRollback(state);

            if (CountsAsBan(state))
            {
                _ban(target, $"Banned for repeated cheating: {DescribeKind(kind)}. ({detail})");
                _log($"[Trust] {state.Name}: auto-banned — kicked {_config.TrustKickBan} times within {_config.TrustKickBanWindowSeconds}s.");
                return;
            }

            _kick(target, $"Kicked for cheating: {DescribeKind(kind)}. ({detail})");
        }

        /// <summary>
        /// Counts this kick against the player's SteamID across sessions.
        /// Returns true when the running count reaches the ban threshold.
        /// </summary>
        private bool CountsAsBan(PlayerTrustState state)
        {
            if (_config.TrustKickBan <= 0 || state.RemoteId == 0UL)
                return false;

            DateTime now = DateTime.UtcNow;
            if (!_kickCounts.TryGetValue(state.RemoteId, out (DateTime FirstUtc, int Count) entry) ||
                (now - entry.FirstUtc).TotalSeconds > _config.TrustKickBanWindowSeconds)
            {
                entry = (now, 0);
            }

            entry.Count++;
            _kickCounts[state.RemoteId] = entry;
            _saveKickCounts?.Invoke(
                new Dictionary<ulong, (DateTime FirstUtc, int Count)>(_kickCounts));
            return entry.Count >= _config.TrustKickBan;
        }

        /// <summary>
        /// Turns a violation kind into player-facing text for kick/ban reasons.
        /// </summary>
        private static string DescribeKind(string kind)
        {
            switch (kind)
            {
                case "noclip": return "moving inside solid blocks";
                case "fly": return "flying is not allowed on this server";
                case "speed": return "moving faster than possible";
                case "teleport": return "teleporting is not allowed on this server";
                case "reach": return "interacting with blocks too far away";
                case "item": return "invalid item or inventory data";
                case "combat": return "impossible combat action";
                case "relay": return "forged network message";
                case "visitor-edit": return "visitors cannot modify the world";
                case "godmode": return "surviving impossible damage";
                case "stamina": return "sprinting beyond possible stamina";
                case "teleport-rate": return "teleporting too often";
                case "host-forgery":
                case "authority": return "forged server-authority message";
                default: return kind ?? "cheating";
            }
        }

        private void WarnVisitor(PlayerTrustState state)
        {
            if ((DateTime.UtcNow - state.LastVisitorWarnUtc).TotalSeconds < 30)
                return;

            state.LastVisitorWarnUtc = DateTime.UtcNow;
            _broadcast($"[Server] {state.Name}: the Visitor rank cannot modify the world.");
        }

        private static string ShortName(string typeName)
        {
            int dot = typeName != null ? typeName.LastIndexOf('.') : -1;
            return dot >= 0 ? typeName.Substring(dot + 1) : (typeName ?? "?");
        }

        private static ulong SafeInvoke(Func<ulong> func)
        {
            try
            {
                return func != null ? func() : 0UL;
            }
            catch
            {
                return 0UL;
            }
        }

        private static string SafeInvoke(Func<string> func)
        {
            try
            {
                return func?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
