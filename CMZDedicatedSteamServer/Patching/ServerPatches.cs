/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2025 RussDev7, unknowghost0
This file is part of https://github.com/RussDev7/CMZDedicatedServers - see LICENSE for details.
*/

using System;
using System.Reflection;
using HarmonyLib;

namespace CMZDedicatedSteamServer
{
    /// <summary>
    /// Central runtime patch bootstrap for the dedicated server host.
    ///
    /// Purpose:
    /// - Creates the Harmony instance used by this project.
    /// - Applies all Harmony patches found in the executing assembly.
    /// - Installs individual headless-safety patches on demand (GPS factory).
    ///
    /// Notes:
    /// - Patch application is guarded so it only happens once per process.
    /// - This class does not remove or re-apply patches after initialization.
    /// - The Harmony ID should remain stable so patch ownership is consistent.
    /// </summary>
    internal static class ServerPatches
    {
        #region Fields

        /// <summary>
        /// Cached Harmony instance for this host process.
        ///
        /// Notes:
        /// - Null means no patch has been installed yet.
        /// - Created lazily by ApplyAllPatches or InstallHeadlessGpsFactory,
        ///   whichever runs first.
        /// </summary>
        private static Harmony _harmony;

        /// <summary>True once PatchAll has scanned the executing assembly.</summary>
        private static bool _allPatchesApplied;

        /// <summary>True once the GPS CreateItem prefix has been installed.</summary>
        private static bool _gpsFactoryInstalled;

        // Reflected members cached at install time for the GPS factory prefix.
        private static Type _gpsItemType;
        private static FieldInfo _gpsBaseClassField;
        private static FieldInfo _gpsStackField;
        private static FieldInfo _gpsHealthField;
        private static FieldInfo _gpsNameField;

        #endregion

        #region Patch Bootstrap

        /// <summary>
        /// Applies all Harmony patches declared in the current assembly.
        ///
        /// Purpose:
        /// - Initializes Harmony once.
        /// - Scans the executing assembly for patch classes/attributes.
        ///
        /// Notes:
        /// - Safe to call multiple times; subsequent calls are ignored.
        /// - Logic is intentionally minimal so startup behavior stays predictable.
        /// </summary>
        public static void ApplyAllPatches()
        {
            if (_allPatchesApplied)
                return;

            _harmony ??= new Harmony("cmz.serverhost.runtime");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            _allPatchesApplied = true;
        }

        /// <summary>
        /// Installs a prefix on GPSItemClass.CreateItem(int) so the headless server
        /// can construct GPS items without the vanilla constructor, which opens a
        /// PCKeyboardInputScreen against CastleMinerZGame.Instance.
        /// </summary>
        /// <param name="gameAssembly">The loaded game assembly.</param>
        /// <returns>True when the prefix is installed (including repeat calls).</returns>
        /// <remarks>
        /// The produced GPSItem is serialization-only: Read/Write touch the base
        /// fields plus _pointToLocation/_GPSname, all of which are initialized by
        /// the prefix (coordinates naturally remain Vector3.Zero). No render or
        /// input paths are ever invoked on it. Client installs are untouched.
        /// </remarks>
        internal static bool InstallHeadlessGpsFactory(Assembly gameAssembly)
        {
            if (_gpsFactoryInstalled)
                return true;

            _gpsItemType = gameAssembly.GetType("DNA.CastleMinerZ.Inventory.GPSItem")
                ?? throw new MissingMemberException("DNA.CastleMinerZ.Inventory.GPSItem");

            Type inventoryItemType = gameAssembly.GetType("DNA.CastleMinerZ.Inventory.InventoryItem")
                ?? throw new MissingMemberException("DNA.CastleMinerZ.Inventory.InventoryItem");

            Type gpsItemClassType = gameAssembly.GetType("DNA.CastleMinerZ.Inventory.GPSItemClass")
                ?? throw new MissingMemberException("DNA.CastleMinerZ.Inventory.GPSItemClass");

            _gpsBaseClassField = inventoryItemType.GetField("_class",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(inventoryItemType.FullName, "_class");

            _gpsStackField = inventoryItemType.GetField("_stackCount",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(inventoryItemType.FullName, "_stackCount");

            _gpsHealthField = inventoryItemType.GetField("ItemHealthLevel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(inventoryItemType.FullName, "ItemHealthLevel");

            _gpsNameField = _gpsItemType.GetField("_GPSname",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(_gpsItemType.FullName, "_GPSname");

            MethodInfo createItem = gpsItemClassType.GetMethod(
                "CreateItem",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(int)],
                null)
                ?? throw new MissingMemberException(gpsItemClassType.FullName, "CreateItem");

            MethodInfo prefix = typeof(ServerPatches).GetMethod(
                nameof(CreateHeadlessGpsItem),
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMemberException(nameof(ServerPatches), nameof(CreateHeadlessGpsItem));

            _harmony ??= new Harmony("cmz.serverhost.runtime");
            _harmony.Patch(createItem, prefix: new HarmonyMethod(prefix));
            _gpsFactoryInstalled = true;
            return true;
        }

        /// <summary>
        /// Replaces GPSItemClass.CreateItem: builds an uninitialized GPSItem and
        /// fills only the serialization-relevant fields. Stack/health/name are
        /// overwritten by Read for wire items; new items read as stack-1
        /// "Alpha" at the origin.
        /// </summary>
        private static bool CreateHeadlessGpsItem(object __instance, int stackCount, ref object __result)
        {
            object item = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_gpsItemType);
            _gpsBaseClassField.SetValue(item, __instance);
            _gpsStackField.SetValue(item, stackCount);
            _gpsHealthField.SetValue(item, 1f);
            _gpsNameField.SetValue(item, "Alpha");
            __result = item;
            return false;
        }

        #endregion
    }
}
