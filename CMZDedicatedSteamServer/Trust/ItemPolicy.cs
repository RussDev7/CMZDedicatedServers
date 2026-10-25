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
    /// Wire-level legitimacy checks for client-supplied InventoryItem payloads.
    ///
    /// The server stores inventory bytes verbatim, so a modified client could upload
    /// forged items (unobtainables, absurd stacks, teleport items in modes that ban
    /// teleporting). This validates every serialized item slot instead of trusting.
    ///
    /// Wire model (from the decompiled sources):
    ///   InventoryItem.Write  = int16 id | int16 stack | float32 health
    ///   GunInventoryItem     = base + int32 RoundsInClip
    ///   GPSItem              = base + Vector3(12) + string
    ///   BlockInventoryItem   = base + Vector3(12) when block is SpawnPointBasic or
    ///                          TeleportStation, plus string for TeleportStation
    ///   PlayerInventory.Save = "PINV" | int32 version(4) | 32 slots |
    ///                          trays | teleport stations | spawn teleport
    /// </summary>
    internal static class ItemPolicy
    {
        // Item ids whose payload appends an int32 clip count (all GunInventoryItem subclasses).
        private static readonly string[] GunItemNames =
        {
            "AssultRifle", "BloodStoneAssultRifle", "DiamondAssultRifle", "GoldAssultRifle",
            "Pistol", "BloodStonePistol", "DiamondPistol", "GoldPistol",
            "PumpShotgun", "BloodStonePumpShotgun", "DiamondPumpShotgun", "GoldPumpShotgun",
            "SMGGun", "BloodStoneSMGGun", "DiamondSMGGun", "GoldSMGGun",
            "LMGGun", "BloodStoneLMGGun", "DiamondLMGGun", "GoldLMGGun",
            "BoltActionRifle", "BloodStoneBoltActionRifle", "DiamondBoltActionRifle", "GoldBoltActionRifle",
            "CopperSpaceAssultRifle", "DiamondSpaceAssultRifle", "GoldSpaceAssultRifle", "IronSpaceAssultRifle",
            "CopperSpacePistol", "DiamondSpacePistol", "GoldSpacePistol", "IronSpacePistol",
            "CopperSpacePumpShotgun", "DiamondSpacePumpShotgun", "GoldSpacePumpShotgun", "IronSpacePumpShotgun",
            "CopperSpaceSMGGun", "DiamondSpaceSMGGun", "GoldSpaceSMGGun", "IronSpaceSMGGun",
            "CopperSpaceBoltActionRifle", "DiamondSpaceBoltActionRifle", "GoldSpaceBoltActionRifle", "IronSpaceBoltActionRifle",
            "LaserDrill", "PrecisionLaser", "BasicGrenadeLauncher",
            "RocketLauncher", "RocketLauncherGuided", "RocketLauncherShotFired", "RocketLauncherGuidedShotFired",
        };

        // Held items that can legitimately produce a FireRocketMessage: the
        // launchers themselves plus their ShotFired stand-ins (vanilla swaps
        // the reported held item to the ShotFired id once a rocket is spent).
        private static readonly string[] RocketLauncherHeldNames =
        {
            "RocketLauncher", "RocketLauncherGuided",
            "RocketLauncherShotFired", "RocketLauncherGuidedShotFired",
        };

        // Held items that can legitimately produce a launcher-fired Sticky
        // GrenadeMessage: the rocket launchers send it on equip to show the
        // loaded barrel grenade, and the ShotFired ids cover a just-spent one.
        private static readonly string[] GrenadeLauncherHeldNames =
        {
            "RocketLauncher", "RocketLauncherGuided", "BasicGrenadeLauncher",
            "RocketLauncherShotFired", "RocketLauncherGuidedShotFired",
        };

        // Item ids whose payload appends Vector3 + string (GPSItem + teleport station).
        private static readonly string[] VectorStringItemNames = { "GPS", "TeleportGPS", "TeleportStation" };

        // Item ids whose payload appends Vector3 only (spawn point blocks).
        private static readonly string[] VectorOnlyItemNames = { "SpawnBasic" };

        // Vanilla InventoryItem.IsValid() rejects these outright in every context.
        private static readonly string[] AlwaysBannedItemNames = { "BloodStoneBullets", "SpaceRock" };

        // The only item vanilla permits at zero stack (respawn beacon internals).
        private static readonly string[] ZeroStackItemNames = { "TeleportStation" };

        // Items enemies drop directly (EnemyManager) - legal non-player pickups
        // even though they are not blocks and may not be in the loot table.
        private static readonly string[] EnemyDropItemNames =
        {
            // Enemy + dragon drops (BaseZombie.CreatePickup + SpawnDragonPickups).
            // WoodBlock is the most common surface-enemy drop (70% of the
            // y>=-40 branch); without it a legit zombie kill flags as forged.
            "ExplosivePowder", "Copper", "Iron", "Gold", "Diamond", "BloodStoneBlock",
            "WoodBlock",
            // Mined-resource drops (Explosive.GetHarvestedItem): ore blocks yield
            // non-block resource items, so they are not covered by BlockClasses.
            "CopperOre", "IronOre", "GoldOre", "Coal",
        };

        // InventoryItemIDs -> BlockTypeEnum pairs from the vanilla registration.
        // Anything placeable as a block maps here; placing debits the item.
        private static readonly (string item, string block)[] BlockItemPairs =
        {
            ("WoodBlock", "Wood"), ("RockBlock", "Rock"), ("SandBlock", "Sand"), ("DirtBlock", "Dirt"),
            ("LogBlock", "Log"), ("LanternBlock", "Lantern"), ("BloodStoneBlock", "BloodStone"),
            ("SpaceRock", "SpaceRock"), ("SpaceRockInventory", "SpaceRockInventory"),
            ("IronWall", "IronWall"), ("CopperWall", "CopperWall"), ("GoldenWall", "GoldenWall"),
            ("DiamondWall", "DiamondWall"), ("Crate", "Crate"), ("Snow", "Snow"), ("TNT", "TNT"),
            ("Ice", "Ice"), ("C4", "C4"), ("Slime", "Slime"),
            ("GlassWindowWood", "GlassBasic"), ("GlassWindowIron", "GlassIron"),
            ("GlassWindowGold", "GlassStrong"), ("GlassWindowDiamond", "GlassMystery"),
            ("StoneContainer", "CrateStone"), ("CopperContainer", "CrateCopper"),
            ("IronContainer", "CrateIron"), ("GoldContainer", "CrateGold"),
            ("DiamondContainer", "CrateDiamond"), ("BloodstoneContainer", "CrateBloodstone"),
            ("LanternFancyBlock", "LanternFancy"),
            ("Door", "NormalLowerDoor"), ("IronDoor", "StrongLowerDoor"),
            ("DiamondDoor", "StrongLowerDoor"), ("TechDoor", "StrongLowerDoor"),
            ("SpawnBasic", "SpawnPointBasic"), ("TeleportStation", "TeleportStation"),
        };

        // Items that provide teleporting; banned when the mode/difficulty hides the Teleport menu.
        private static readonly string[] TeleportItemNames = { "TeleportGPS", "TeleportStation" };

        // BlockTypeEnum -> InventoryItemIDs dig outputs that are NOT the block's
        // own item: InventoryItem.CreatesWhenDug / PickInventoryItem specials.
        // Blocks absent here drop only their BlockClasses item (or nothing).
        private static readonly (string block, string item)[] DigSpecialPairs =
        {
            ("Grass", "DirtBlock"), ("SurfaceLava", "RockBlock"), ("SpaceRock", "SpaceRockInventory"),
            ("GoldOre", "GoldOre"), ("IronOre", "IronOre"), ("CopperOre", "CopperOre"),
            ("CoalOre", "Coal"), ("DiamondOre", "Diamond"),
            ("IronWall", "IronWall"), ("CopperWall", "CopperWall"),
            ("GoldenWall", "GoldenWall"), ("DiamondWall", "DiamondWall"),
        };

        // Explosive.GetHarvestedItem: blast-removed cells yield ONLY these
        // resource items - terrain blocks destroyed by TNT/C4 drop nothing.
        private static readonly (string block, string item)[] ExplosionHarvestPairs =
        {
            ("GoldOre", "GoldOre"), ("IronOre", "IronOre"), ("CopperOre", "CopperOre"),
            ("CoalOre", "Coal"), ("DiamondOre", "Diamond"),
            ("IronWall", "IronWall"), ("CopperWall", "CopperWall"),
            ("GoldenWall", "GoldenWall"), ("DiamondWall", "DiamondWall"),
        };

        private const int InventorySlotCount = 32;
        private const int InventoryVersionCurrent = 4;
        private const int MaxTrayCount = 8;
        private const int MaxTraySlots = 32;
        private const int MaxTeleportStations = 256;
        private const int MaxItemNameLength = 64;

        private static int _maxItemId = -1;
        private static HashSet<int> _gunIds = new HashSet<int>();
        private static HashSet<int> _rocketLauncherHeldIds = new HashSet<int>();
        private static HashSet<int> _grenadeLauncherHeldIds = new HashSet<int>();
        private static HashSet<int> _vectorStringIds = new HashSet<int>();
        private static HashSet<int> _vectorOnlyIds = new HashSet<int>();
        // Per-slot stack ceilings beyond the blanket config cap: ammo/casings
        // legitimately stack to 5000 (InventoryItem.cs per-slot limit), while
        // guns, tools, and GPS-type items never stack past 1 per slot.
        private static HashSet<int> _bulkStackIds = new HashSet<int>();
        private static HashSet<int> _singleStackIds = new HashSet<int>();
        private static HashSet<int> _alwaysBannedIds = new HashSet<int>();
        private static HashSet<int> _zeroStackIds = new HashSet<int>();
        private static HashSet<int> _teleportIds = new HashSet<int>();
        private static HashSet<int> _worldSpawnIds = new HashSet<int>();
        private static HashSet<int> _enemyDropIds = new HashSet<int>();
        private static HashSet<int> _lootIds = new HashSet<int>();
        private static HashSet<int> _lootBlockIds = new HashSet<int>();
        private static HashSet<int> _facingBlockIds = new HashSet<int>();
        private static Dictionary<int, int> _blockFacing = new Dictionary<int, int>();
        private static Dictionary<int, int> _parentBlockType = new Dictionary<int, int>();
        private static Dictionary<int, int> _digSpecials = new Dictionary<int, int>();
        private static Dictionary<int, int> _explosionHarvest = new Dictionary<int, int>();
        /// <summary>
        /// Spawn-marker and boss-marker block types that vanilla converts via
        /// AlterBlock at render distance (SpawnBlockView light/dim/off toggles,
        /// HellFloorBiome writes). These carry no inventory mapping and are
        /// useless for a player to fake-place, so a large reach cap is safe.
        /// </summary>
        private static readonly string[] RemoteAlterBlockNames =
        {
            "EnemySpawnOn", "EnemySpawnOff", "EnemySpawnDim",
            "EnemySpawnRareOn", "EnemySpawnRareOff", "EnemySpawnRareDim",
            "EnemySpawnAltar",
            "AlienSpawnOn", "AlienSpawnOff", "AlienSpawnDim",
            "AlienHordeOn", "AlienHordeOff", "AlienHordeDim",
            "HellSpawnOn", "HellSpawnOff", "HellSpawnDim",
            "BossSpawnOn", "BossSpawnOff", "BossSpawnDim",
        };

        /// <summary>
        /// Reward blocks a spawner converts to when defeated. Valuable when
        /// broken, so remote claims additionally require a registered spawner
        /// at or next to the claimed position.
        /// </summary>
        private static readonly string[] LootAlterBlockNames =
        {
            "LootBlock", "LuckyLootBlock",
        };

        private static Dictionary<int, List<int>> _blockTypeToItemIds = new Dictionary<int, List<int>>();
        // gunId -> (ammoItemId, clipCapacity); ammoItemId -> largest clip among its guns;
        // grenadeType byte -> thrown grenade item id. From the game's own class registry.
        private static Dictionary<int, KeyValuePair<int, int>> _gunAmmo = new Dictionary<int, KeyValuePair<int, int>>();
        private static HashSet<int> _gunAmmoItemIds = new HashSet<int>();
        // Shotguns (5 pellets per shell) and launchers (splash): a single
        // trigger pull can legitimately kill more than one enemy, so
        // kill-interval checks must skip them.
        private static HashSet<int> _multiKillGunIds = new HashSet<int>();
        // Pump shotguns alone: every pellet stacks skeleton/Felguard weakness
        // (1.5x) with the headshot (2x) for a 3x per-pellet bound, so their
        // per-trigger max enemy damage is dmg*3, not the global 2.5x.
        private static HashSet<int> _pumpShotgunIds = new HashSet<int>();
        // gunId -> the enum name it resolved to (log/diagnostic display only).
        private static Dictionary<int, string> _gunIdToName = new Dictionary<int, string>();
        private static Dictionary<int, int> _ammoMaxClip = new Dictionary<int, int>();
        private static Dictionary<int, int> _grenadeTypeToItem = new Dictionary<int, int>();
        // gunId -> vanilla CoolDownTime seconds (the *InventoryItemClass ctors
        // in InventoryItem.cs registration). Rapid-fire mods shorten/zero the
        // client-side cooldown; the wire can't lie about how often claims arrive.
        private static Dictionary<int, double> _gunFireInterval = new Dictionary<int, double>();
        private static Dictionary<int, float> _gunEnemyDamage = new Dictionary<int, float>();
        private static Dictionary<int, int> _gunClipCapacity = new Dictionary<int, int>();
        private static Dictionary<int, double> _gunReloadSec = new Dictionary<int, double>();
        private static HashSet<int> _remoteAlterBlockIds = new HashSet<int>();
        private static HashSet<int> _lootAlterBlockIds = new HashSet<int>();
        private static Type _blockTypeEnumType;
        private static Type _itemIdEnumType;
        private static int _tntBlockType = -1;
        private static int _c4BlockType = -1;
        private static int _surfaceLavaBlockType = -1;
        private static int _deepLavaBlockType = -1;
        private static HashSet<int> _undiggableBlockIds = new HashSet<int>();
        private static HashSet<int> _playerBlockingIds = new HashSet<int>();
        private static HashSet<int> _doorBlockIds = new HashSet<int>();
        private static Dictionary<int, List<RecipeInfo>> _recipesByResult = new Dictionary<int, List<RecipeInfo>>();
        private static bool _initialized;

        /// <summary>
        /// Malformed means the stream could not be parsed at all - the packet is
        /// dropped but NOT counted as a violation, since a modeling gap on our side
        /// is a possible cause. InvalidItem means a confirmed rules violation.
        /// </summary>
        internal enum ItemVerdict
        {
            Ok,
            InvalidItem,
            Malformed,
            /// <summary>Same unexplained claim as an already-penalized save: drop silently.</summary>
            DeniedRepeat,
            /// <summary>Correlation miss within the desync grace: deny the write, no violation.</summary>
            DeniedDesync
        }

        /// <summary>A parsed (id, stack) pair from a serialized item.</summary>
        internal struct ParsedItem
        {
            public int Id;
            public int Stack;
        }

        /// <summary>
        /// One crafting recipe in result -> ingredients form for provenance checks.
        /// </summary>
        internal sealed class RecipeInfo
        {
            public int ResultCount;
            public List<KeyValuePair<int, int>> Ingredients = new List<KeyValuePair<int, int>>();
        }

        /// <summary>False when the item-id enum could not be resolved; checks then skip.</summary>
        public static bool Ready { get; private set; }

        /// <summary>True when the recipe table loaded; crafting reconciliation is possible.</summary>
        public static bool RecipesReady { get; private set; }

        // Ammo/casing items whose per-slot ceiling is the vanilla 5000 instead
        // of the blanket resource cap (InventoryItem.cs per-slot limit).
        private static readonly string[] BulkStackItemNames =
        {
            "BrassCasing", "IronCasing", "GoldCasing", "DiamondCasing",
            "Bullets", "IronBullets", "GoldBullets", "DiamondBullets", "BloodStoneBullets", "LaserBullets", "RocketAmmo"
        };

        /// <summary>
        /// True when the item payload appends an int32 clip count after the base
        /// wire form (every GunInventoryItem subclass). Name-based so it also
        /// works before Initialize resolves the id sets.
        /// </summary>
        internal static bool HasGunPayload(string itemName) => Array.IndexOf(GunItemNames, itemName) >= 0;

        // Guns, vector-string items, and handheld tools never stack past 1 per
        // slot in vanilla - same classification the _singleStackIds set uses.
        private static bool IsSingleStackItemName(string name)
        {
            if (name == null)
                return false;
            if (HasGunPayload(name) || Array.IndexOf(VectorStringItemNames, name) >= 0)
                return true;
            return name.EndsWith("PickAxe") || name.EndsWith("Spade") || name.EndsWith("Axe") ||
                   name.EndsWith("Knife") || name.EndsWith("LaserSword") || name.StartsWith("Chainsaw") ||
                   name == "Compass" || name == "Clock";
        }

        /// <summary>
        /// MaxStackCount a headless registry class must advertise so
        /// InventoryItem.IsValid() accepts the same stacks the policy does:
        /// 5000 for ammo/casings, 1 for single-stack items, 999 otherwise.
        /// Name-based; usable before Initialize runs.
        /// </summary>
        internal static int HeadlessStackLimit(string itemName)
        {
            if (Array.IndexOf(BulkStackItemNames, itemName) >= 0)
                return 5000;
            if (IsSingleStackItemName(itemName))
                return 1;
            return 999;
        }

        /// <summary>
        /// The reflected InventoryItemIDs enum, retained for display lookups
        /// (PlayerInspection.DescribeItem).
        /// </summary>
        internal static Type ItemIdEnumType => _itemIdEnumType;

        /// <summary>
        /// Resolves the InventoryItemIDs enum into numeric id sets and pulls the
        /// real recipe book + loot table + block map out of the game assembly.
        /// Called once during world init after the item registry is populated.
        /// </summary>
        public static void Initialize(
            Type inventoryItemIdsType,
            Type blockTypeEnumType,
            System.Reflection.Assembly gameAsm,
            Action<string> log)
        {
            if (_initialized)
                return;
            _initialized = true;
            _log = log ?? (_ => { });

            try
            {
                if (inventoryItemIdsType == null || !inventoryItemIdsType.IsEnum)
                {
                    _log("[Trust] ItemPolicy disabled: InventoryItemIDs enum not found.");
                    return;
                }

                foreach (object v in Enum.GetValues(inventoryItemIdsType))
                {
                    int id = Convert.ToInt32(v);
                    if (id > _maxItemId)
                        _maxItemId = id;
                }
                _maxItemId++; // ids are 0..max inclusive

                _itemIdEnumType = inventoryItemIdsType;

                _blockTypeEnumType = blockTypeEnumType != null && blockTypeEnumType.IsEnum ? blockTypeEnumType : null;

                if (_blockTypeEnumType != null)
                {
                    _remoteAlterBlockIds = ResolveIds(_blockTypeEnumType, RemoteAlterBlockNames);
                    _lootAlterBlockIds = ResolveIds(_blockTypeEnumType, LootAlterBlockNames);
                    _tntBlockType = ResolveId(_blockTypeEnumType, "TNT");
                    _c4BlockType = ResolveId(_blockTypeEnumType, "C4");
                    _surfaceLavaBlockType = ResolveId(_blockTypeEnumType, "SurfaceLava");
                    _deepLavaBlockType = ResolveId(_blockTypeEnumType, "DeepLava");
                }

                LoadBlockTypeFlags(gameAsm);

                _gunIds = ResolveIds(inventoryItemIdsType, GunItemNames);
                _rocketLauncherHeldIds = ResolveIds(inventoryItemIdsType, RocketLauncherHeldNames);
                _grenadeLauncherHeldIds = ResolveIds(inventoryItemIdsType, GrenadeLauncherHeldNames);
                _vectorStringIds = ResolveIds(inventoryItemIdsType, VectorStringItemNames);
                _vectorOnlyIds = ResolveIds(inventoryItemIdsType, VectorOnlyItemNames);
                _alwaysBannedIds = ResolveIds(inventoryItemIdsType, AlwaysBannedItemNames);
                _zeroStackIds = ResolveIds(inventoryItemIdsType, ZeroStackItemNames);
                _teleportIds = ResolveIds(inventoryItemIdsType, TeleportItemNames);

                _bulkStackIds = ResolveIds(inventoryItemIdsType, BulkStackItemNames);
                _singleStackIds = new HashSet<int>(_gunIds);
                _singleStackIds.UnionWith(_vectorStringIds);
                foreach (object value in Enum.GetValues(inventoryItemIdsType))
                {
                    string name = Enum.GetName(inventoryItemIdsType, value);
                    if (IsSingleStackItemName(name))
                        _singleStackIds.Add(Convert.ToInt32(value));
                }

                BuildBlockMaps(inventoryItemIdsType, blockTypeEnumType);
                LoadBlockClassMap(gameAsm);
                LoadGunAmmoMap(inventoryItemIdsType, gameAsm);
                foreach (List<int> ids in _blockTypeToItemIds.Values)
                    _worldSpawnIds.UnionWith(ids);
                _enemyDropIds = ResolveIds(inventoryItemIdsType, EnemyDropItemNames);
                _worldSpawnIds.UnionWith(_enemyDropIds);

                Ready = true;
                _log($"[Trust] ItemPolicy enabled: {_maxItemId} item ids, " +
                     $"{_gunIds.Count} gun ids, {_teleportIds.Count} teleport-gated ids, " +
                     $"{_worldSpawnIds.Count} world-spawnable ids.");
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy disabled: " + ex.GetType().Name + " " + ex.Message);
            }

            // Metadata extraction is best-effort; Ready stays true even if these fail.
            LoadLootIds(gameAsm);
            LoadRecipes(gameAsm);
        }

        private static Action<string> _log = _ => { };

        private static int ResolveId(Type enumType, string name)
        {
            try
            {
                return Convert.ToInt32(Enum.Parse(enumType, name));
            }
            catch
            {
                _log("[Trust] ItemPolicy: enum value missing for " + name);
                return -1;
            }
        }

        private static HashSet<int> ResolveIds(Type enumType, string[] names)
        {
            var set = new HashSet<int>();
            foreach (string name in names)
            {
                try
                {
                    set.Add(Convert.ToInt32(Enum.Parse(enumType, name)));
                }
                catch
                {
                    _log("[Trust] ItemPolicy: enum value missing for " + name);
                }
            }
            return set;
        }

        /// <summary>
        /// Validates a standalone serialized InventoryItem blob (pickup spawns, crate writes).
        /// Reads only the fixed header plus known per-class extras.
        /// </summary>
        public static ItemVerdict ValidateItemBytes(
            byte[] data,
            int offset,
            int length,
            bool teleportAllowed,
            int maxStack,
            out string reason)
        {
            reason = null;
            if (data == null || offset < 0 || length < 8 || offset + length > data.Length)
            {
                reason = "item payload truncated";
                return ItemVerdict.Malformed;
            }

            try
            {
                using var ms = new MemoryStream(data, offset, length, writable: false);
                using var reader = new BinaryReader(ms);
                if (!ReadItem(reader, teleportAllowed, maxStack, out reason, out _))
                    return ItemVerdict.InvalidItem;

                if (ms.Position != ms.Length)
                {
                    reason = "trailing bytes after item payload";
                    return ItemVerdict.Malformed;
                }

                return ItemVerdict.Ok;
            }
            catch (Exception ex)
            {
                reason = "item payload unreadable: " + ex.GetType().Name;
                return ItemVerdict.Malformed;
            }
        }

        /// <summary>
        /// Validates a full PlayerInventory.Save blob (the invBytes section of an
        /// InventoryStoreOnServerMessage). Structural parse failure rejects the store
        /// so a forged stream cannot hide items past the parser.
        /// </summary>
        public static ItemVerdict ValidateInventoryBlob(
            byte[] inv,
            bool teleportAllowed,
            int maxStack,
            out string reason)
        {
            return ParseInventoryBlob(inv, teleportAllowed, maxStack, null, out reason);
        }

        /// <summary>
        /// Reads one serialized item exactly as InventoryItem.Create + subclass Read
        /// would, then applies the trust rules. Emits the (id, stack) pair.
        /// </summary>
        private static bool ReadItem(
            BinaryReader reader,
            bool teleportAllowed,
            int maxStack,
            out string reason,
            out ParsedItem item)
        {
            reason = null;
            item = default;

            short id = reader.ReadInt16();
            if (id < 0 || id >= _maxItemId)
            {
                reason = "unknown item id " + id;
                return false;
            }

            int stack = reader.ReadInt16();
            float health = reader.ReadSingle(); // ItemHealthLevel: durability fraction 0..1

            if (_gunIds.Contains(id))
            {
                int rounds = reader.ReadInt32(); // RoundsInClip
                // "Unsafe Clip Size" territory: a gun can never hold more rounds
                // than the largest clip on its ammo pool, and never negative.
                if (rounds < 0 ||
                    (TryGetGunAmmo(id, out _, out int clipCap) && rounds > clipCap))
                {
                    reason = $"impossible clip size {rounds} for gun item {id}";
                    return false;
                }
            }
            else if (_vectorStringIds.Contains(id))
            {
                SkipVector3(reader);
                ReadBoundedString(reader);
            }
            else if (_vectorOnlyIds.Contains(id))
            {
                SkipVector3(reader);
            }

            item = new ParsedItem { Id = id, Stack = stack };

            if (_alwaysBannedIds.Contains(id))
            {
                reason = "banned item id " + id;
                return false;
            }

            if (!teleportAllowed && _teleportIds.Contains(id))
            {
                reason = "teleport item " + id + " forbidden in this mode";
                return false;
            }

            if (stack < 0)
            {
                reason = "negative stack for item " + id;
                return false;
            }

            // Zero-stack is only legal for TeleportStation (vanilla IsValid rule).
            if (stack == 0 && !_zeroStackIds.Contains(id))
            {
                reason = "zero-stack item " + id;
                return false;
            }

            // Per-slot stack limit: guns/tools/GPS items cap at 1 (several
            // slots of stack-1 items are legal, one slot of 2 is not), ammo and
            // casings cap at the vanilla 5000, everything else uses the
            // configured blanket cap.
            int slotLimit = _singleStackIds.Contains(id) ? 1 : _bulkStackIds.Contains(id) ? 5000 : maxStack;
            if (stack > slotLimit)
            {
                reason = $"oversized stack {stack} for item {id} (slot limit {slotLimit})";
                return false;
            }

            // "Unsafe Health" territory: ItemHealthLevel is a 0..1 durability
            // fraction - vanilla assigns exactly 1f on create and only ever
            // decrements it (item dies at <=0), so anything above 1 is forged.
            // NaN is an infinite-durability exploit client-side (NaN <= 0 is
            // false, so the item never breaks) and Infinity is out of range too.
            if (float.IsNaN(health) || float.IsInfinity(health) ||
                health < 0f || health > 1.0f)
            {
                reason = $"impossible health {health} for item {id}";
                return false;
            }

            return true;
        }

        private static void SkipVector3(BinaryReader reader)
        {
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
        }

        /// <summary>
        /// Reads a BinaryWriter string with a hard length cap so a forged stream
        /// cannot allocate or scan unbounded memory.
        /// </summary>
        private static void ReadBoundedString(BinaryReader reader)
        {
            int len = Read7BitInt(reader);
            if (len < 0 || len > MaxItemNameLength)
                throw new InvalidDataException("item string length " + len);
            if (reader.ReadBytes(len).Length != len)
                throw new EndOfStreamException();
        }

        private static int Read7BitInt(BinaryReader reader)
        {
            int value = 0;
            int shift = 0;
            byte b;
            do
            {
                b = reader.ReadByte();
                value |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0 && shift < 35);
            return value;
        }

        #region Provenance metadata

        /// <summary>BlockTypeEnum value -> item ids that can place it (multiple doors share one type).</summary>
        public static bool TryGetPlacementItems(int blockType, out List<int> itemIds)
        {
            return _blockTypeToItemIds.TryGetValue(blockType, out itemIds);
        }

        /// <summary>Item ids vanilla game code legitimately spawns as world pickups.</summary>
        public static bool IsWorldSpawnable(int itemId)
        {
            return _worldSpawnIds.Contains(itemId);
        }

        /// <summary>Item ids enemies/dragons drop on death (BaseZombie, SpawnDragonPickups).</summary>
        public static bool IsEnemyDropItem(int itemId)
        {
            return _enemyDropIds.Contains(itemId);
        }

        /// <summary>
        /// True when drop-correlation tables loaded. When false the pickup gate
        /// degrades to the broad world-spawnable check rather than flagging every
        /// mined pickup as forged.
        /// </summary>
        public static bool HasDropTables =>
            _blockTypeToItemIds.Count > 0 || _digSpecials.Count > 0 || _lootIds.Count > 0;

        /// <summary>
        /// Item ids a dig of this block type may legitimately produce, mirroring
        /// InventoryItem.CreatesWhenDug: the block's own BlockClasses item (looked
        /// up by ParentBlockType like CreateBlockItem does), plus per-tool special
        /// outputs (ores->resources), plus the loot table for loot blocks.
        /// </summary>
        public static void CollectDigDropIds(int blockType, HashSet<int> output)
        {
            int parent = _parentBlockType.TryGetValue(blockType, out int p) ? p : blockType;
            if (_blockTypeToItemIds.TryGetValue(parent, out List<int> blockItems))
            {
                foreach (int id in blockItems)
                    output.Add(id);
            }
            if (_digSpecials.TryGetValue(blockType, out int special))
                output.Add(special);
            if (_lootBlockIds.Contains(blockType))
            {
                foreach (int id in _lootIds)
                    output.Add(id);
            }
        }

        /// <summary>Item ids a blast-removed cell may produce (Explosive.GetHarvestedItem).</summary>
        public static void CollectExplosionDropIds(int blockType, HashSet<int> output)
        {
            if (_explosionHarvest.TryGetValue(blockType, out int id))
                output.Add(id);
        }

        /// <summary>True when ANY blasted block can yield this item (Explosive.GetHarvestedItem).</summary>
        public static bool IsExplosionHarvestItem(int itemId)
        {
            return _explosionHarvest.ContainsValue(itemId);
        }

        /// <summary>True when this item can roll out of the loot table (PossibleLootType).</summary>
        public static bool IsLootDropItem(int itemId)
        {
            return _lootIds.Contains(itemId);
        }

        /// <summary>True when this block type rolls the loot table when destroyed (BlockType.ShouldDropLoot).</summary>
        public static bool IsLootBlockType(int blockType)
        {
            return _lootBlockIds.Contains(blockType);
        }

        /// <summary>
        /// Block type is face-attached (torch-on-wall style). Attached blocks
        /// destroyed as blast/dig dependents drop their block item even though
        /// they were never dug directly.
        /// </summary>
        public static bool IsFacingBlockType(int blockType)
        {
            return _facingBlockIds.Contains(blockType);
        }

        /// <summary>The face a dependent block attaches to (0-5), when it is one.</summary>
        public static bool TryGetBlockFacing(int blockType, out int facing)
        {
            return _blockFacing.TryGetValue(blockType, out facing);
        }

        /// <summary>True when the id resolves to a real enum member in the game assembly.</summary>
        public static bool IsKnownItemId(int id)
        {
            return Ready && id >= 0 && id < _maxItemId;
        }

        /// <summary>True when the id is a gun-type item (carries clip data on the wire).</summary>
        public static bool IsGunId(int id)
        {
            return Ready && _gunIds.Contains(id);
        }

        /// <summary>True when the id is an ammunition item pooled across gun types.</summary>
        public static bool IsGunAmmoId(int id)
        {
            return Ready && _gunAmmoItemIds.Contains(id);
        }

        /// <summary>
        /// True for shotgun (5 pellets) and launcher (splash) ids - one trigger
        /// pull can legitimately kill more than one enemy.
        /// </summary>
        public static bool IsMultiKillGunId(int id)
        {
            return Ready && _multiKillGunIds.Contains(id);
        }

        /// <summary>True when holding this item can legitimately produce a FireRocketMessage.</summary>
        public static bool IsRocketLauncherHeldId(int id)
        {
            return Ready && _rocketLauncherHeldIds.Contains(id);
        }

        /// <summary>True when holding this item can legitimately produce a launcher-fired Sticky grenade.</summary>
        public static bool IsGrenadeLauncherHeldId(int id)
        {
            return Ready && _grenadeLauncherHeldIds.Contains(id);
        }

        /// <summary>True when the block type exists in the vanilla BlockType enum (modded blocks do not).</summary>
        public static bool IsKnownBlockType(int blockType)
        {
            return Ready && _blockTypeEnumType != null && Enum.IsDefined(_blockTypeEnumType, blockType);
        }

        /// <summary>
        /// Spawn/boss marker types vanilla converts remotely via AlterBlock
        /// (spawner light/dim/off toggles, biome writes). Harmless to fake.
        /// </summary>
        public static bool IsRemoteAlterBlockType(int blockType)
        {
            return _remoteAlterBlockIds.Contains(blockType);
        }

        /// <summary>
        /// Reads the game's static BlockType table: CanBeDug=false blocks can never be
        /// legitimately removed (bedrock), BlockPlayer=true blocks are solid enough
        /// that a claimed player position inside one means noclip.
        /// </summary>
        private static void LoadBlockTypeFlags(System.Reflection.Assembly gameAsm)
        {
            try
            {
                Type blockType = gameAsm?.GetType("DNA.CastleMinerZ.Terrain.BlockType");
                var table = blockType?.GetField(
                        "_blockTypes",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    ?.GetValue(null) as System.Collections.IEnumerable;
                if (table == null)
                {
                    _log("[Trust] ItemPolicy: BlockType table not found; dig/noclip flags unknown.");
                    return;
                }

                var typeField = blockType.GetField("_type", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var dugField = blockType.GetField("CanBeDug", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var blocksField = blockType.GetField("BlockPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var parentField = blockType.GetField("ParentBlockType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var facingField = blockType.GetField("Facing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (typeField == null || dugField == null || blocksField == null)
                    return;

                foreach (object entry in table)
                {
                    if (entry == null)
                        continue;
                    object typeValue = typeField.GetValue(entry);
                    int id = Convert.ToInt32(typeValue);
                    string typeName = typeValue?.ToString() ?? string.Empty;
                    if (typeName.IndexOf("Door", StringComparison.Ordinal) >= 0)
                        _doorBlockIds.Add(id);
                    if (typeName == "LootBlock" || typeName == "LuckyLootBlock")
                        _lootBlockIds.Add(id);
                    if ((bool)dugField.GetValue(entry) == false)
                        _undiggableBlockIds.Add(id);
                    if ((bool)blocksField.GetValue(entry))
                        _playerBlockingIds.Add(id);
                    if (parentField != null)
                    {
                        object parentValue = parentField.GetValue(entry);
                        if (parentValue != null)
                            _parentBlockType[id] = Convert.ToInt32(parentValue);
                    }
                    if (facingField != null)
                    {
                        object facingValue = facingField.GetValue(entry);
                        // BlockFace.NUM_FACES (6) = unattached; a real face = dependent block.
                        if (facingValue != null && Convert.ToInt32(facingValue) < 6)
                        {
                            int face = Convert.ToInt32(facingValue);
                            _facingBlockIds.Add(id);
                            _blockFacing[id] = face;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy: block flag scan failed - " + ex.GetType().Name);
            }
        }

        /// <summary>Block type can never be legitimately dug/removed (bedrock, air, markers).</summary>
        public static bool IsUndiggableBlockType(int blockType)
        {
            return _undiggableBlockIds.Contains(blockType);
        }

        /// <summary>Block type physically blocks players - a player position inside one means noclip.</summary>
        public static bool IsPlayerBlockingBlockType(int blockType)
        {
            return _playerBlockingIds.Contains(blockType);
        }

        /// <summary>
        /// Door block types (all open/closed/normal/strong variants). A closed
        /// door is a thin slab inside its cell, not a full cube - a player can
        /// legitimately stand in a door cell, so doors never prove embedding.
        /// </summary>
        public static bool IsDoorBlockType(int blockType)
        {
            return _doorBlockIds.Contains(blockType);
        }

        /// <summary>Lava block types (SurfaceLava/DeepLava) - sustained submersion is always lethal.</summary>
        public static bool IsLavaBlockType(int blockType)
        {
            return blockType == _surfaceLavaBlockType || blockType == _deepLavaBlockType;
        }

        /// <summary>
        /// Placed explosive block types (TNT/C4) - the only blocks a client may
        /// legitimately claim to detonate via DetonateExplosiveMessage.
        /// </summary>
        public static bool IsExplosiveBlockType(int blockType)
        {
            if (!Ready || _blockTypeEnumType == null)
                return false;
            return blockType == _tntBlockType || blockType == _c4BlockType;
        }

        /// <summary>
        /// Spawner reward blocks (LootBlock/LuckyLootBlock). Valuable - remote
        /// claims must be next to a registered spawner.
        /// </summary>
        public static bool IsLootAlterBlockType(int blockType)
        {
            return _lootAlterBlockIds.Contains(blockType);
        }

        /// <summary>Recipes whose result is the given item id.</summary>
        public static List<RecipeInfo> GetRecipesFor(int itemId)
        {
            return _recipesByResult.TryGetValue(itemId, out List<RecipeInfo> list) ? list : null;
        }

        /// <summary>
        /// Parses a validated item blob and returns just the (id, stack) pairs.
        /// Used by the provenance layer to snapshot inventories and pickups.
        /// </summary>
        public static bool TryParseItem(byte[] data, int offset, int length, out ParsedItem item)
        {
            item = default;
            if (data == null || length < 8 || offset < 0 || offset + length > data.Length)
                return false;

            try
            {
                using var ms = new MemoryStream(data, offset, length, writable: false);
                using var reader = new BinaryReader(ms);
                if (!ReadItem(reader, teleportAllowed: true, maxStack: int.MaxValue, out _, out ParsedItem parsed))
                    return false;
                if (ms.Position != ms.Length)
                    return false;
                item = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parses a full PINV blob into per-item (id, stack) totals.
        /// Returns false when the blob does not match the vanilla format.
        /// </summary>
        public static bool TrySummarizeInventory(byte[] inv, out Dictionary<int, int> totals)
        {
            totals = null;
            if (inv == null)
                return false;

            var items = new List<ParsedItem>();
            var verdict = ParseInventoryBlob(inv, true, int.MaxValue, items, out _);
            if (verdict != ItemVerdict.Ok)
                return false;

            totals = new Dictionary<int, int>();
            foreach (ParsedItem p in items)
            {
                totals.TryGetValue(p.Id, out int cur);
                totals[p.Id] = cur + p.Stack;
            }
            return true;
        }

        /// <summary>
        /// Like ValidateInventoryBlob but also emits every parsed item so the
        /// caller can reconcile inventory deltas.
        /// </summary>
        public static ItemVerdict ParseInventoryBlob(
            byte[] inv,
            bool teleportAllowed,
            int maxStack,
            List<ParsedItem> items,
            out string reason)
        {
            reason = null;
            if (inv == null || inv.Length < 16)
            {
                reason = "inventory blob too small";
                return ItemVerdict.Malformed;
            }

            try
            {
                using var ms = new MemoryStream(inv, writable: false);
                using var reader = new BinaryReader(ms);

                if (reader.ReadString() != "PINV")
                {
                    reason = "bad inventory magic";
                    return ItemVerdict.Malformed;
                }

                if (reader.ReadInt32() != InventoryVersionCurrent)
                {
                    reason = "unexpected inventory version";
                    return ItemVerdict.Malformed;
                }

                for (int i = 0; i < InventorySlotCount; i++)
                {
                    if (!ReadSlotCollect(reader, teleportAllowed, maxStack, items, out reason, out ItemVerdict v))
                        return v;
                }

                int trayCount = reader.ReadInt32();
                if (trayCount < 0 || trayCount > MaxTrayCount)
                {
                    reason = "invalid tray count " + trayCount;
                    return ItemVerdict.Malformed;
                }
                for (int t = 0; t < trayCount; t++)
                {
                    int slots = reader.ReadInt32();
                    if (slots < 0 || slots > MaxTraySlots)
                    {
                        reason = "invalid tray slot count " + slots;
                        return ItemVerdict.Malformed;
                    }
                    for (int i = 0; i < slots; i++)
                    {
                        if (!ReadSlotCollect(reader, teleportAllowed, maxStack, items, out reason, out ItemVerdict v))
                            return v;
                    }
                }

                int stations = reader.ReadInt32();
                if (stations < 0 || stations > MaxTeleportStations)
                {
                    reason = "invalid teleport station count " + stations;
                    return ItemVerdict.Malformed;
                }
                for (int i = 0; i < stations; i++)
                {
                    if (!ReadSlotCollect(reader, teleportAllowed, maxStack, items, out reason, out ItemVerdict v))
                        return v;
                }

                // InventorySpawnPointTeleport: optional trailing item.
                if (reader.ReadBoolean())
                {
                    if (!ReadItem(reader, teleportAllowed, maxStack, out reason, out ParsedItem spawnItem))
                        return ItemVerdict.InvalidItem;
                    items?.Add(spawnItem);
                }

                if (ms.Position != ms.Length)
                {
                    reason = "trailing bytes after inventory payload";
                    return ItemVerdict.Malformed;
                }

                return ItemVerdict.Ok;
            }
            catch (Exception ex)
            {
                reason = "inventory parse failed: " + ex.GetType().Name;
                return ItemVerdict.Malformed;
            }
        }

        private static bool ReadSlotCollect(
            BinaryReader reader,
            bool teleportAllowed,
            int maxStack,
            List<ParsedItem> items,
            out string reason,
            out ItemVerdict verdict)
        {
            reason = null;
            verdict = ItemVerdict.Ok;
            if (!reader.ReadBoolean())
                return true;

            if (ReadItem(reader, teleportAllowed, maxStack, out reason, out ParsedItem item))
            {
                items?.Add(item);
                return true;
            }

            verdict = ItemVerdict.InvalidItem;
            return false;
        }

        /// <summary>
        /// Resolves the baked (item,block) name pairs into id maps against the
        /// live enums. Missing enum names are dropped, never fatal.
        /// </summary>
        private static void BuildBlockMaps(Type itemIdsType, Type blockTypeEnumType)
        {
            if (blockTypeEnumType == null || !blockTypeEnumType.IsEnum)
                return;

            foreach ((string item, string block) in BlockItemPairs)
            {
                try
                {
                    int itemId = Convert.ToInt32(Enum.Parse(itemIdsType, item));
                    int blockType = Convert.ToInt32(Enum.Parse(blockTypeEnumType, block));
                    if (!_blockTypeToItemIds.TryGetValue(blockType, out List<int> list))
                    {
                        list = new List<int>();
                        _blockTypeToItemIds[blockType] = list;
                    }
                    list.Add(itemId);
                }
                catch
                {
                }
            }

            ResolvePairs(itemIdsType, blockTypeEnumType, DigSpecialPairs, _digSpecials);
            ResolvePairs(itemIdsType, blockTypeEnumType, ExplosionHarvestPairs, _explosionHarvest);
        }

        /// <summary>Resolves (blockTypeName -> itemName) pairs into int maps; skips unknown names.</summary>
        private static void ResolvePairs(
            Type itemIdsType,
            Type blockTypeEnumType,
            (string block, string item)[] pairs,
            Dictionary<int, int> output)
        {
            foreach ((string block, string item) in pairs)
            {
                try
                {
                    output[Convert.ToInt32(Enum.Parse(blockTypeEnumType, block))] =
                        Convert.ToInt32(Enum.Parse(itemIdsType, item));
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Reads BlockInventoryItemClass.BlockClasses - the game's authoritative
        /// block type -&gt; item class map, populated when the headless inventory
        /// registry initializes. Covers every minable block (ores, coal, diamond,
        /// walls, terrain) instead of only the hand-listed placeable items.
        /// </summary>
        private static void LoadBlockClassMap(System.Reflection.Assembly gameAsm)
        {
            if (gameAsm == null)
                return;

            try
            {
                Type bic = gameAsm.GetType("DNA.CastleMinerZ.Inventory.BlockInventoryItemClass");
                var field = bic?.GetField("BlockClasses",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (!(field?.GetValue(null) is System.Collections.IDictionary map) || map.Count == 0)
                    return;

                int added = 0;
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    int blockType = Convert.ToInt32(entry.Key);
                    object idObj = ReadMember(entry.Value, "ID");
                    if (idObj == null)
                        continue;
                    int itemId = Convert.ToInt32(idObj);

                    if (!_blockTypeToItemIds.TryGetValue(blockType, out List<int> list))
                    {
                        list = new List<int>();
                        _blockTypeToItemIds[blockType] = list;
                    }
                    if (!list.Contains(itemId))
                    {
                        list.Add(itemId);
                        added++;
                    }
                }

                _log($"[Trust] ItemPolicy: block class map added {added} block->item links.");
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy block class map failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Maps each gun id to its ammo item id by the vanilla registration
        /// pattern (InventoryItem.Initalize is unusable headless - it needs a
        /// ContentManager). The pairings are fixed in vanilla: *Space* guns burn
        /// LaserBullets, LaserDrill/PrecisionLaser burn GoldBullets, launchers
        /// burn RocketAmmo, and material prefixes pick the tier below
        /// (BloodStone->Diamond, Diamond->Gold, Gold->Iron, base->Bullets).
        /// The clip column is the largest ClipCapacity among guns sharing the
        /// pool - it bounds the hidden "already loaded" grace, not per-gun.
        /// </summary>
        private static void LoadGunAmmoMap(Type inventoryItemIdsType, System.Reflection.Assembly gameAsm)
        {
            try
            {
                foreach (int gunId in _gunIds)
                {
                    string name = Enum.GetName(inventoryItemIdsType, gunId);
                    double interval = FireIntervalForGunName(name);
                    if (interval > 0.0)
                        _gunFireInterval[gunId] = interval;
                    float enemyDamage = EnemyDamageForGunName(name);
                    if (enemyDamage > 0f)
                        _gunEnemyDamage[gunId] = enemyDamage;
                    int clipCap = ClipCapacityForGunName(name);
                    if (clipCap > 0)
                        _gunClipCapacity[gunId] = clipCap;
                    double reloadSec = ReloadSecondsForGunName(name);
                    if (reloadSec > 0.0)
                        _gunReloadSec[gunId] = reloadSec;
                    if (name != null)
                    {
                        _gunIdToName[gunId] = name;
                        if (name.EndsWith("PumpShotgun"))
                            _pumpShotgunIds.Add(gunId);
                        if (name.Contains("PumpShotgun") || name.Contains("Launcher"))
                            _multiKillGunIds.Add(gunId);
                    }
                    string ammoName = name == null ? null : AmmoNameForGun(name);
                    if (ammoName == null)
                        continue;
                    int ammoId = ResolveId(inventoryItemIdsType, ammoName);
                    if (ammoId <= 0)
                        continue;
                    int clip = ClipAllowanceForAmmo(ammoName);
                    _gunAmmo[gunId] = new KeyValuePair<int, int>(ammoId, clip);
                    _ammoMaxClip[ammoId] = clip;
                    _gunAmmoItemIds.Add(ammoId);
                }

                // GrenadeType wire byte -> item id: HE=Grenade, Sticky=StickyGrenade.
                Type grenadeEnum = gameAsm?.GetType("DNA.CastleMinerZ.GrenadeTypeEnum");
                if (grenadeEnum != null)
                {
                    foreach (var pair in new[] {
                        new { Type = "HE", Item = "Grenade" },
                        new { Type = "Sticky", Item = "StickyGrenade" } })
                    {
                        int t = ResolveId(grenadeEnum, pair.Type);
                        int i = ResolveId(inventoryItemIdsType, pair.Item);
                        if (t >= 0 && i > 0)
                            _grenadeTypeToItem[t] = i;
                    }
                    // Sticky is the launcher-fired round: the launcher classes
                    // send GrenadeMessage(Sticky) - even on equip to show the
                    // loaded barrel grenade - instead of consuming a thrown
                    // grenade item.
                    LauncherGrenadeType = ResolveId(grenadeEnum, "Sticky");
                }

                _log($"[Trust] ItemPolicy: ammo map {_gunAmmo.Count} guns, " +
                     $"{_grenadeTypeToItem.Count} grenade types.");
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy gun/ammo map failed: " + ex.Message);
            }
        }

        private static string AmmoNameForGun(string gunName)
        {
            if (gunName.Contains("Launcher"))
                return "RocketAmmo";
            if (gunName == "LaserDrill" || gunName == "PrecisionLaser")
                return "GoldBullets";

            bool isGun = gunName.EndsWith("AssultRifle") || gunName.EndsWith("PumpShotgun") ||
                         gunName.EndsWith("SMGGun") || gunName.EndsWith("LMGGun") ||
                         gunName.EndsWith("BoltActionRifle") || gunName.EndsWith("Pistol");
            if (!isGun)
                return null;

            if (gunName.Contains("Space"))
                return "LaserBullets";
            if (gunName.StartsWith("BloodStone"))
                return "DiamondBullets";
            if (gunName.StartsWith("Diamond"))
                return "GoldBullets";
            if (gunName.StartsWith("Gold"))
                return "IronBullets";
            return "Bullets";
        }

        /// <summary>
        /// Vanilla CoolDownTime seconds per gun family (the *InventoryItemClass
        /// ctors): Space SMG/AR/pistol autos 0.06, laser rifle 0.26, laser
        /// shotgun 0.19, drill/precision 0.05, conventional AR/pistol 0.10,
        /// LMG 0.112, pump shotgun and launchers 1.0. Semi-automatics are
        /// click-bound in vanilla (bolt rifle's cooldown is ~3ms) so they get
        /// a 0.05 floor - no human sustains 20 clicks/s.
        /// </summary>
        private static double FireIntervalForGunName(string gunName)
        {
            if (gunName == null)
                return -1.0;
            if (gunName.Contains("Launcher"))
                return 1.0;
            if (gunName == "LaserDrill" || gunName == "PrecisionLaser")
                return 0.05;
            if (gunName.Contains("PumpShotgun"))
                return gunName.Contains("Space") ? 0.19 : 1.0;
            if (gunName.Contains("BoltActionRifle"))
                return gunName.Contains("Space") ? 0.26 : 0.05;
            if (gunName.Contains("Space"))
            {
                if (gunName.EndsWith("SMGGun") || gunName.EndsWith("AssultRifle") ||
                    gunName.EndsWith("Pistol"))
                    return 0.06;
                return -1.0;
            }
            if (gunName.EndsWith("LMGGun"))
                return 0.112;
            if (gunName.EndsWith("AssultRifle") || gunName.EndsWith("Pistol"))
                return 0.10;
            return -1.0;
        }

        /// <summary>
        /// Vanilla per-hit enemy damage for a gun name, straight from the
        /// RegisterItemClass table in InventoryItem.cs. Space guns ignore
        /// material tier; conventional guns scale Iron->Gold->Diamond->BloodStone.
        /// Shotgun shells spawn 5 pellets each at full bulletDamage, so the
        /// per-trigger value is 5x (all-pellets-hit is the generous bound).
        /// </summary>
        private static float EnemyDamageForGunName(string gunName)
        {
            if (gunName == null)
                return -1f;
            if (gunName.Contains("Launcher"))
                return 100f;
            if (gunName == "LaserDrill")
                return 0.2f;
            if (gunName == "PrecisionLaser")
                return 25f;
            if (gunName.Contains("Space"))
            {
                if (gunName.EndsWith("BoltActionRifle") || gunName.EndsWith("AssultRifle"))
                    return 15f;
                if (gunName.EndsWith("SMGGun") || gunName.EndsWith("Pistol"))
                    return 10f;
                if (gunName.EndsWith("PumpShotgun"))
                    return 50f;
                return -1f;
            }
            float tier = gunName.Contains("BloodStone") ? 4f :
                         gunName.Contains("Diamond") ? 2f :
                         gunName.Contains("Gold") ? 1f : 0f;
            if (gunName.EndsWith("AssultRifle") || gunName.EndsWith("BoltActionRifle"))
                return tier switch { 0f => 0.5f, 1f => 2.5f, 2f => 6f, _ => 12f };
            if (gunName.EndsWith("Pistol"))
                return tier switch { 0f => 0.3f, 1f => 1f, 2f => 4f, _ => 8f };
            if (gunName.EndsWith("PumpShotgun"))
                return (tier switch { 0f => 0.3f, 1f => 1f, 2f => 4f, _ => 8f }) * 5f;
            return -1f;
        }

        /// <summary>Vanilla per-trigger enemy damage for a gun id (shotguns count all 5 pellets).</summary>
        public static bool TryGetGunEnemyDamage(int gunId, out float damage)
        {
            return _gunEnemyDamage.TryGetValue(gunId, out damage);
        }

        /// <summary>The enum name a gun id resolved to, or null (log display only).</summary>
        internal static string GunNameForId(int gunId)
        {
            return _gunIdToName.TryGetValue(gunId, out string name) ? name : null;
        }

        /// <summary>
        /// Most enemy damage one trigger pull of this gun can ever deal:
        /// per-trigger bulletDamage x the best vanilla crit stack. Skeletons
        /// and Felguard take weakness 1.5x ON TOP of the headshot 2x from each
        /// pellet, so pump shotguns bound at 3x; every other gun/enemy pair
        /// tops out at the 2.5x headshot bound.
        /// </summary>
        public static bool TryGetGunMaxEnemyDamage(int gunId, out float damage)
        {
            if (!_gunEnemyDamage.TryGetValue(gunId, out damage))
                return false;
            damage *= _pumpShotgunIds.Contains(gunId) ? 3f : 2.5f;
            return true;
        }

        /// <summary>
        /// Vanilla tracer ballistics for the diagnostic shadow model
        /// (TracerManager.Tracer: head/tail velocity = dir*Velocity, life =
        /// FlightTime). Only ordinary bullet guns are modelable - lasers,
        /// launchers, and space weapons travel differently and are rejected.
        /// speed = muzzle velocity in m/s; lifetime = seconds of flight.
        /// </summary>
        public static bool TryGetGunBallistics(int gunId, out float speed, out float lifetime)
        {
            speed = 0f;
            lifetime = 0f;
            string gunName = GunNameForId(gunId);
            if (gunName == null ||
                gunName.Contains("Space") || gunName.Contains("Laser") ||
                gunName.Contains("Precision") || gunName.Contains("Launcher"))
                return false;
            if (gunName.EndsWith("Pistol")) { speed = 75f; lifetime = 1f; return true; }
            if (gunName.EndsWith("PumpShotgun")) { speed = 50f; lifetime = 0.4f; return true; }
            if (gunName.EndsWith("BoltActionRifle")) { speed = 150f; lifetime = 2f; return true; }
            if (gunName.EndsWith("AssultRifle")) { speed = 100f; lifetime = 2f; return true; }
            if (gunName.EndsWith("SMGGun") || gunName.EndsWith("LMGGun")) { speed = 100f; lifetime = 1f; return true; }
            return false;
        }

        /// <summary>
        /// Vanilla ClipCapacity per gun name (subclass ctor values in the
        /// Inventory folder): pistol/bolt rifle 8, pump shotgun 6, AR 30,
        /// SMG 20, LMG 100, launchers 1, drills 200/10, space guns 7-30.
        /// </summary>
        private static int ClipCapacityForGunName(string gunName)
        {
            if (gunName == null)
                return -1;
            if (gunName.Contains("Launcher"))
                return 1;
            if (gunName == "LaserDrill")
                return 200;
            if (gunName == "PrecisionLaser")
                return 10;
            if (gunName.Contains("Space"))
            {
                if (gunName.EndsWith("AssultRifle")) return 30;
                if (gunName.EndsWith("SMGGun")) return 20;
                if (gunName.EndsWith("Pistol")) return 7;
                if (gunName.EndsWith("BoltActionRifle")) return 10;
                if (gunName.EndsWith("PumpShotgun")) return 8;
                return -1;
            }
            if (gunName.EndsWith("LMGGun")) return 100;
            if (gunName.EndsWith("SMGGun")) return 20;
            if (gunName.EndsWith("AssultRifle")) return 30;
            if (gunName.EndsWith("BoltActionRifle")) return 8;
            if (gunName.EndsWith("Pistol")) return 8;
            if (gunName.EndsWith("PumpShotgun")) return 6;
            return -1;
        }

        /// <summary>Vanilla rounds-per-clip for a gun id.</summary>
        public static bool TryGetGunClipCapacity(int gunId, out int capacity)
        {
            return _gunClipCapacity.TryGetValue(gunId, out capacity);
        }

        /// <summary>
        /// Vanilla reload duration per gun name (ReloadTime in the subclass
        /// ctors): pistol 1.63 s, bolt 2.4, shotgun/launchers ~0.57, AR 3.0,
        /// SMG 2.0, LMG 9.7, space 1.25-3.0.
        /// </summary>
        private static double ReloadSecondsForGunName(string gunName)
        {
            if (gunName == null)
                return -1.0;
            if (gunName.Contains("Launcher"))
                return 0.567;
            if (gunName == "LaserDrill" || gunName == "PrecisionLaser")
                return 3.0;
            if (gunName.Contains("Space"))
            {
                if (gunName.EndsWith("AssultRifle")) return 2.5;
                if (gunName.EndsWith("SMGGun")) return 2.05;
                if (gunName.EndsWith("Pistol")) return 1.25;
                if (gunName.EndsWith("BoltActionRifle")) return 2.95;
                if (gunName.EndsWith("PumpShotgun")) return 0.57;
                return -1.0;
            }
            if (gunName.EndsWith("LMGGun")) return 9.7;
            if (gunName.EndsWith("SMGGun")) return 2.0;
            if (gunName.EndsWith("AssultRifle")) return 3.0;
            if (gunName.EndsWith("BoltActionRifle")) return 2.4;
            if (gunName.EndsWith("Pistol")) return 1.63;
            if (gunName.EndsWith("PumpShotgun")) return 0.567;
            return -1.0;
        }

        /// <summary>Vanilla reload duration (seconds) for a gun id.</summary>
        public static bool TryGetGunReloadSeconds(int gunId, out double seconds)
        {
            return _gunReloadSec.TryGetValue(gunId, out seconds);
        }

        /// <summary>Vanilla minimum seconds between shots for a gun id.</summary>
        public static bool TryGetGunFireInterval(int gunId, out double seconds)
        {
            return _gunFireInterval.TryGetValue(gunId, out seconds);
        }

        private static int ClipAllowanceForAmmo(string ammoName)
        {
            switch (ammoName)
            {
                case "GoldBullets": return 200;  // LaserDrill
                case "RocketAmmo": return 1;
                case "LaserBullets": return 30;  // LaserAR
                default: return 100;             // LMG clips are the biggest conventional
            }
        }

        /// <summary>Ammo item id + clip capacity consumed by a gun id.</summary>
        public static bool TryGetGunAmmo(int gunId, out int ammoItemId, out int clipCapacity)
        {
            ammoItemId = 0;
            clipCapacity = 0;
            if (_gunAmmo.TryGetValue(gunId, out KeyValuePair<int, int> kv))
            {
                ammoItemId = kv.Key;
                clipCapacity = kv.Value;
                return true;
            }
            return false;
        }

        /// <summary>Largest clip among guns sharing an ammo pool (grace bound).</summary>
        public static bool TryGetAmmoMaxClip(int ammoItemId, out int clipCapacity)
        {
            return _ammoMaxClip.TryGetValue(ammoItemId, out clipCapacity);
        }

        /// <summary>GrenadeType wire byte -> the item consumed per throw.</summary>
        public static bool TryGetGrenadeItem(int grenadeType, out int itemId)
        {
            return _grenadeTypeToItem.TryGetValue(grenadeType, out itemId);
        }

        /// <summary>
        /// The GrenadeType a launcher fires (Sticky) - this one does not debit a
        /// grenade item when the held item is a launcher.
        /// </summary>
        public static int LauncherGrenadeType = -1;

        /// <summary>
        /// Reads the vanilla loot tables (PossibleLootType.GetLootSource/GetEnemySource)
        /// and adds every drop item id to the world-spawnable set.
        /// </summary>
        private static void LoadLootIds(System.Reflection.Assembly gameAsm)
        {
            if (gameAsm == null)
                return;

            try
            {
                Type lootType = gameAsm.GetType("DNA.CastleMinerZ.Inventory.PossibleLootType");
                if (lootType == null)
                    return;

                int before = _worldSpawnIds.Count;
                foreach (string methodName in new[] { "GetLootSource" })
                {
                    var mi = lootType.GetMethod(methodName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (mi == null)
                        continue;

                    if (!(mi.Invoke(null, null) is Array arr))
                        continue;

                    foreach (object entry in arr)
                    {
                        object idObj = ReadMember(entry, "itemID");
                        if (idObj == null)
                            continue;
                        int id = Convert.ToInt32(idObj);
                        if (id > 0)
                        {
                            _worldSpawnIds.Add(id);
                            _lootIds.Add(id);
                        }
                    }
                }

                _log($"[Trust] ItemPolicy: loot table added {_worldSpawnIds.Count - before} spawnable ids.");
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy loot table load failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reads the static Recipe.CookBook list: each Recipe's Result +
        /// Ingredients are InventoryItem objects (id via ItemClass.ID, count via
        /// StackCount). Requires the placeholder item registry to be initialized.
        /// </summary>
        private static void LoadRecipes(System.Reflection.Assembly gameAsm)
        {
            if (gameAsm == null)
                return;

            try
            {
                Type recipeType = gameAsm.GetType("DNA.CastleMinerZ.Inventory.Recipe");
                if (recipeType == null)
                    return;

                object cookBook =
                    recipeType.GetField("CookBook",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        ?.GetValue(null)
                    ?? recipeType.GetProperty("CookBook",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        ?.GetValue(null);

                if (!(cookBook is System.Collections.IEnumerable recipes))
                {
                    _log("[Trust] ItemPolicy: Recipe.CookBook not found; crafting reconciliation off.");
                    return;
                }

                int count = 0;
                foreach (object recipe in recipes)
                {
                    object result = ReadMember(recipe, "Result");
                    object ingredients = ReadMember(recipe, "Ingredients");
                    if (result == null || !(ingredients is System.Collections.IEnumerable ingList))
                        continue;

                    if (!TryReadItemIdAndCount(result, out int resultId, out int resultCount))
                        continue;

                    var info = new RecipeInfo { ResultCount = Math.Max(1, resultCount) };
                    bool bad = false;
                    foreach (object ing in ingList)
                    {
                        if (ing == null)
                            continue;
                        if (!TryReadItemIdAndCount(ing, out int ingId, out int ingCount))
                        {
                            bad = true;
                            break;
                        }
                        info.Ingredients.Add(new KeyValuePair<int, int>(ingId, Math.Max(1, ingCount)));
                    }
                    if (bad)
                        continue;

                    if (!_recipesByResult.TryGetValue(resultId, out List<RecipeInfo> list))
                    {
                        list = new List<RecipeInfo>();
                        _recipesByResult[resultId] = list;
                    }
                    list.Add(info);
                    count++;
                }

                RecipesReady = count > 0;
                _log($"[Trust] ItemPolicy: {count} crafting recipes loaded" +
                     (RecipesReady ? "." : "; crafting reconciliation off."));
            }
            catch (Exception ex)
            {
                _log("[Trust] ItemPolicy recipe load failed: " + ex.Message);
            }
        }

        /// <summary>Reads (ItemClass.ID, StackCount) off a reflected InventoryItem.</summary>
        private static bool TryReadItemIdAndCount(object item, out int id, out int count)
        {
            id = 0;
            count = 0;
            try
            {
                object cls = ReadMember(item, "ItemClass");
                object idObj = ReadMember(cls, "ID");
                object countObj = ReadMember(item, "StackCount");
                if (idObj == null || countObj == null)
                    return false;
                id = Convert.ToInt32(idObj);
                count = Convert.ToInt32(countObj);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null)
                return null;
            Type t = obj.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            return t.GetProperty(name, flags)?.GetValue(obj)
                ?? t.GetField(name, flags)?.GetValue(obj);
        }

        #endregion
    }
}
