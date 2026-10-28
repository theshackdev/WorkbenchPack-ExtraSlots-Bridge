using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace TheShack.WorkbenchPackExtraSlotsBridge
{
    // Shack Workbench Pack's backpack is a utility item, so wearing it takes the belt's slot. Extra Slots has an API
    // for custom equipment slots; this adds a "Backpack" slot that only takes the workbench backpack.
    //
    // The backpack gets an item type of its own that no vanilla slot takes (the way shudnal's HipLantern gives its
    // lantern a slot). The game still equips it: EquipItem runs all its checks (broken, swimming, attacking ...), finds
    // no vanilla slot for the type and returns true, and this puts it in the Backpack slot. So:
    //   - right-click, or wearing it after packing it up, fills the Backpack slot and the belt stays on;
    //   - it is really equipped (m_equipped), which is what Workbench Pack looks for: the bench on your back, Tab
    //     crafting, the build radius and the stonecutter work as they do in the utility slot;
    //   - the model on your back comes from a number on the player's ZDO (as with vanilla's utility slot), so other
    //     players who have this bridge see it too.
    // The forge backpack is carried in the bag and isn't touched.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(WorkbenchPackGuid)]
    [BepInDependency(ExtraSlotsGuid)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.workbenchpack_extraslots_bridge";
        public const string PluginName = "Workbench Pack Extra Slots Bridge";
        public const string PluginVersion = "1.0.0";
        private const string WorkbenchPackGuid = "theshack.workbenchpack";
        private const string ExtraSlotsGuid = "shudnal.ExtraSlots";

        // Workbench Pack's names for the backpack (Content.ItemName and Content.Token).
        private const string ItemName = "ShackWorkbenchBackpack";
        private const string Token = "$shack_benchpack";
        private const string SlotId = "ShackWorkbenchBackpack";
        private static readonly int BackKey = "theshack.workbenchpack_extraslots_bridge.back".GetStableHashCode();

        internal static ManualLogSource Log;
        private static ConfigEntry<bool> slotOn;
        private static ConfigEntry<string> slotName;
        private static ConfigEntry<int> typeNumber;
        private static ConfigEntry<bool> wearAfterPackUp;
        private static ItemDrop.ItemData.ItemType slotType;
        private static bool typeSet;

        private static readonly Action<Humanoid> SetupEquipment =
            AccessTools.MethodDelegate<Action<Humanoid>>(AccessTools.Method(typeof(Humanoid), "SetupEquipment"));
        private static readonly Action<Humanoid, ItemDrop.ItemData, float> DrainDurability =
            AccessTools.MethodDelegate<Action<Humanoid, ItemDrop.ItemData, float>>(
                AccessTools.Method(typeof(Humanoid), "DrainEquipedItemDurability"));
        private static readonly Func<VisEquipment, int, int, int, List<GameObject>> AttachArmor =
            AccessTools.MethodDelegate<Func<VisEquipment, int, int, int, List<GameObject>>>(
                AccessTools.Method(typeof(VisEquipment), "AttachArmor"));
        private static readonly Action<VisEquipment> UpdateLodgroup =
            AccessTools.MethodDelegate<Action<VisEquipment>>(AccessTools.Method(typeof(VisEquipment), "UpdateLodgroup"));
        private static readonly AccessTools.FieldRef<VisEquipment, ZNetView> VisNview =
            AccessTools.FieldRefAccess<VisEquipment, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<VisEquipment, LODGroup> VisLod =
            AccessTools.FieldRefAccess<VisEquipment, LODGroup>("m_lodGroup");

        private void Awake()
        {
            Log = Logger;
            slotOn = Config.Bind("General", "Backpack slot", true,
                "Give the workbench backpack its own Extra Slots equipment slot, so wearing it leaves the belt on. " +
                "Off: it's a utility item again, as without this bridge. Restart the game after changing it.");
            slotName = Config.Bind("General", "Slot name", "Backpack", "The slot's name in the equipment panel.");
            typeNumber = Config.Bind("General", "Item type number", 57,
                "The backpack's own item type, which no vanilla slot takes. Change it only if another mod uses the same " +
                "number (HipLantern uses 56). Restart the game after changing it.");
            if (!slotOn.Value)
            {
                Log.LogInfo("Backpack slot is off; the workbench backpack stays a utility item.");
                return;
            }
            if (!AddSlot())
            {
                return;
            }
            slotType = (ItemDrop.ItemData.ItemType)typeNumber.Value;
            ItemManager.OnItemsRegistered += SetType;

            Harmony harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            // Packing up wears the backpack only if the utility slot is free; with this slot the belt doesn't matter.
            Type placedBench = AccessTools.TypeByName("TheShack.WorkbenchPack.PlacedBench");
            MethodInfo give = placedBench == null ? null : AccessTools.Method(placedBench, "Give");
            Type settings = AccessTools.TypeByName("TheShack.WorkbenchPack.Settings");
            wearAfterPackUp = settings == null ? null : AccessTools.Field(settings, "WearAfterPackUp")?.GetValue(null) as ConfigEntry<bool>;
            if (give != null && wearAfterPackUp != null)
            {
                harmony.Patch(give, prefix: new HarmonyMethod(typeof(Plugin), nameof(GivePrefix)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(GivePostfix)));
            }
            else
            {
                Log.LogWarning("This Workbench Pack version is laid out differently; after packing up, put the backpack " +
                    "in the Backpack slot yourself (right-click it).");
            }
            Log.LogInfo($"{PluginName} {PluginVersion} ready (slot \"{slotName.Value}\", item type {typeNumber.Value}).");
        }

        // Its own method, so an Extra Slots without this API fails here and not when Awake is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AddSlot()
        {
            try
            {
                if (ExtraSlots.API.AddSlot(SlotId, () => slotName.Value, IsOurs, () => slotOn.Value))
                {
                    return true;
                }
                Log.LogWarning("Extra Slots has no room for another custom slot; the bridge does nothing.");
            }
            catch (Exception e)
            {
                Log.LogWarning("This Extra Slots version has no custom slot API the bridge knows; it does nothing. " + e.Message);
            }
            return false;
        }

        // The item data of every backpack shares the prefab's SharedData, so setting the type there covers them all.
        private static void SetType()
        {
            GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(ItemName) : null;
            ItemDrop drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!drop)
            {
                Log.LogWarning("No workbench backpack in the game; the Backpack slot stays empty.");
                return;
            }
            drop.m_itemData.m_shared.m_itemType = slotType;
            typeSet = true;
        }

        private static bool IsOurs(ItemDrop.ItemData item) => item?.m_shared != null && item.m_shared.m_name == Token;

        private static bool IsSlotItem(ItemDrop.ItemData item) => typeSet && IsOurs(item) && item.m_shared.m_itemType == slotType;

        // The backpack each character wears in the slot.
        private class Worn
        {
            public ItemDrop.ItemData Item;
        }

        private static readonly ConditionalWeakTable<Humanoid, Worn> worn = new ConditionalWeakTable<Humanoid, Worn>();

        private static ItemDrop.ItemData GetWorn(Humanoid humanoid) =>
            humanoid && worn.TryGetValue(humanoid, out Worn w) ? w.Item : null;

        private static void SetWorn(Humanoid humanoid, ItemDrop.ItemData item) => worn.GetOrCreateValue(humanoid).Item = item;

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.IsEquipable))]
        private static class ItemData_IsEquipable
        {
            private static void Postfix(ItemDrop.ItemData __instance, ref bool __result)
            {
                __result = __result || IsSlotItem(__instance);
            }
        }

        // EquipItem returns true once all its checks pass, even though no vanilla slot took the type.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Humanoid_EquipItem
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool triggerEquipEffects, bool __result)
            {
                if (!__result || !IsSlotItem(item))
                {
                    return;
                }
                ItemDrop.ItemData old = GetWorn(__instance);
                if (old != null && old != item)
                {
                    __instance.UnequipItem(old, triggerEquipEffects);
                }
                SetWorn(__instance, item);
                item.m_equipped = true;
                SetupEquipment(__instance);
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        private static class Humanoid_UnequipItem
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item)
            {
                if (item != null && GetWorn(__instance) == item)
                {
                    SetWorn(__instance, null);
                    SetupEquipment(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipAllItems))]
        private static class Humanoid_UnequipAllItems
        {
            private static void Postfix(Humanoid __instance)
            {
                __instance.UnequipItem(GetWorn(__instance), triggerEquipEffects: false);
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsItemEquiped))]
        private static class Humanoid_IsItemEquiped
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                __result = __result || item != null && GetWorn(__instance) == item;
            }
        }

        // Vanilla counts the utility item in the equipment weight; the slot does the same.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetEquipmentWeight))]
        private static class Humanoid_GetEquipmentWeight
        {
            private static void Postfix(Humanoid __instance, ref float __result)
            {
                ItemDrop.ItemData item = GetWorn(__instance);
                if (item != null)
                {
                    __result += item.m_shared.m_weight;
                }
            }
        }

        // Vanilla takes off a worn item that has broken (the backpack doesn't drain, so only one worn down by use).
        [HarmonyPatch(typeof(Humanoid), "UpdateEquipment")]
        private static class Humanoid_UpdateEquipment
        {
            private static void Postfix(Humanoid __instance, float dt)
            {
                ItemDrop.ItemData item = __instance.IsPlayer() ? GetWorn(__instance) : null;
                if (item != null && item.m_shared.m_useDurability)
                {
                    DrainDurability(__instance, item, dt);
                }
            }
        }

        // Moved to a chest, dropped, set down, left in a tombstone: no longer worn.
        [HarmonyPatch(typeof(Inventory), "Changed")]
        private static class Inventory_Changed
        {
            private static void Prefix(Inventory __instance)
            {
                Player player = Player.m_localPlayer;
                ItemDrop.ItemData item = player ? GetWorn(player) : null;
                if (item != null && __instance == player.GetInventory() && !__instance.ContainsItem(item))
                {
                    SetWorn(player, null);
                    SetupEquipment(player);
                }
            }
        }

        private static void GivePrefix(Player player, out List<ItemDrop.ItemData> __state)
        {
            __state = player ? player.GetInventory().GetAllItems().Where(IsOurs).ToList() : null;
        }

        private static void GivePostfix(Player player, bool forge, List<ItemDrop.ItemData> __state)
        {
            if (forge || __state == null || !player || !wearAfterPackUp.Value || GetWorn(player) != null)
            {
                return;
            }
            ItemDrop.ItemData item = player.GetInventory().GetAllItems()
                .FirstOrDefault(i => IsSlotItem(i) && !i.m_equipped && !__state.Contains(i));
            if (item != null)
            {
                // The game refuses a broken one, which then stays in the bag.
                player.EquipItem(item);
            }
        }

        // The model on the back: the wearer's client writes the backpack's prefab hash to the player's ZDO, and every
        // client draws what the ZDO says (the menu's preview player has no ZDO and uses the local value).
        private class Back
        {
            public int Local;
            public int Shown;
            public List<GameObject> Instances;
        }

        private static readonly ConditionalWeakTable<VisEquipment, Back> backs = new ConditionalWeakTable<VisEquipment, Back>();

        [HarmonyPatch(typeof(Humanoid), "SetupVisEquipment")]
        private static class Humanoid_SetupVisEquipment
        {
            private static void Postfix(Humanoid __instance, VisEquipment visEq)
            {
                if (!visEq)
                {
                    return;
                }
                ItemDrop.ItemData item = GetWorn(__instance);
                int hash = item?.m_dropPrefab ? item.m_dropPrefab.name.GetStableHashCode() : 0;
                Back back = backs.GetOrCreateValue(visEq);
                if (back.Local == hash)
                {
                    return;
                }
                back.Local = hash;
                ZNetView nview = VisNview(visEq);
                if (nview && nview.IsValid() && nview.IsOwner())
                {
                    nview.GetZDO().Set(BackKey, hash);
                }
            }
        }

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        private static class VisEquipment_UpdateEquipmentVisuals
        {
            private static void Postfix(VisEquipment __instance)
            {
                ZNetView nview = VisNview(__instance);
                ZDO zdo = nview && nview.IsValid() ? nview.GetZDO() : null;
                backs.TryGetValue(__instance, out Back back);
                int hash = zdo != null ? zdo.GetInt(BackKey) : back?.Local ?? 0;
                if ((back?.Shown ?? 0) == hash)
                {
                    return;
                }
                back = back ?? backs.GetOrCreateValue(__instance);
                if (back.Instances != null)
                {
                    LODGroup lod = VisLod(__instance);
                    foreach (GameObject instance in back.Instances)
                    {
                        if (lod)
                        {
                            Utils.RemoveFromLodgroup(lod, instance);
                        }
                        UnityEngine.Object.Destroy(instance);
                    }
                    back.Instances = null;
                }
                back.Shown = hash;
                // Workbench Pack's own AttachArmor patch sizes the model and puts the bench on it.
                if (hash != 0)
                {
                    back.Instances = AttachArmor(__instance, hash, -1, 0);
                }
                UpdateLodgroup(__instance);
            }
        }
    }
}
