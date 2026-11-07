using HarmonyLib;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Serializing;
using Il2CppFishNet.Transporting;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.ObjectScripts;

namespace ItemSafety;

// 6. Cauldron quality.
// HasOutputSpace checks room for 10 cocaine base of the *default* quality, not the quality
// this cook will make. If the output holds another quality, the finished batch can't stack,
// OutputSlot.AddItem refuses it, and the leaves and petrol (taken at the start) are lost.
[HarmonyPatch]
public static class CauldronQualityPatches
{
    private const int BatchSize = 10; // FinishCookOperation adds 10 cocaine base

    // Same order RemoveIngredients uses: last slot first, 20 leaves, lowest quality wins.
    public static EQuality? PredictQuality(Cauldron c)
    {
        var slots = c.IngredientSlots;
        if (slots == null) return null;
        int need = 20;
        EQuality? min = null;
        for (int i = slots.Length - 1; i >= 0 && need > 0; i--)
        {
            var it = slots[i]?.ItemInstance;
            if (it == null || it.Quantity <= 0) continue;
            need -= Math.Min(it.Quantity, need);
            var q = it.TryCast<QualityItemInstance>();
            if (q != null && (min == null || q.Quality < min)) min = q.Quality;
        }
        return min;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.HasOutputSpace))]
    private static void HasOutputSpace_Postfix(Cauldron __instance, ref bool __result)
    {
        if (!__result || !Settings.CauldronQuality || !Items.IsHost) return;
        try
        {
            var output = __instance.OutputSlot?.ItemInstance?.TryCast<QualityItemInstance>();
            if (output == null || output.Quantity <= 0) return;
            var q = PredictQuality(__instance);
            if (q == null || q == output.Quality) return;
            __result = false;
            Log.Throttled("q:" + __instance.GUID, 120,
                $"[cauldron {Short(__instance)}] not starting: output holds {output.Quality} base and this cook would make {q}. Empty the output first.");
        }
        catch (Exception e) { Log.Err($"[cauldron] {e}"); }
    }

    // Runs on every peer; only the host adds the output (it checks IsServer itself).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcLogic___FinishCookOperation_2166136261))]
    private static void Finish_Prefix(Cauldron __instance)
    {
        if (!Items.IsHost) return;
        try
        {
            var slot = __instance.OutputSlot;
            var output = slot?.ItemInstance?.TryCast<QualityItemInstance>();
            Log.Msg($"[cauldron {Short(__instance)}] cook finished: adding {BatchSize} base ({__instance.InputQuality}); output was {Items.Describe(slot?.ItemInstance)}");
            if (!Settings.CauldronQuality || output == null || output.Quantity <= 0) return;

            var made = __instance.InputQuality;
            if (output.Quality == made) return;

            if (output.Quantity + BatchSize > output.StackLimit)
            {
                Log.Warn($"[cauldron {Short(__instance)}] finished batch ({made}) can't fit next to {output.Quantity} {output.Quality} base; the game will delete it");
                return;
            }

            var low = made < output.Quality ? made : output.Quality;
            if (output.Quality != low)
            {
                var lowered = output.GetCopy(-1).TryCast<QualityItemInstance>();
                if (lowered == null) return;
                lowered.SetQuality(low);
                slot!.SetStoredItem(lowered, false);
            }
            __instance.InputQuality = low;
            Log.Warn($"[cauldron {Short(__instance)}] output held {output.Quality} base and the batch is {made}; merged as {low} instead of deleting the batch");
        }
        catch (Exception e) { Log.Err($"[cauldron] {e}"); }
    }

    public static string Short(Cauldron c)
    {
        try { return c.GUID.ToString().Substring(0, 8); } catch { return "?"; }
    }
}

// 7 (and 5, log only). Who changes cauldron slots.
// A joined player's game sends "set slot to N" from its own copy, not "remove 1". The host
// applies it as-is. This logs every change on the host with the sender so a wiped stack can
// be traced. Nothing is blocked yet.
[HarmonyPatch]
public static class CauldronSlotLog
{
    [ThreadStatic] private static NetworkConnection? _sender;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcReader___Server_SetItemSlotQuantity_1692629761))]
    private static void ReadQty_Prefix(NetworkConnection conn) => _sender = conn;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcReader___Server_SetItemSlotQuantity_1692629761))]
    private static void ReadQty_Postfix() => _sender = null;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcReader___Server_SetStoredInstance_2652194801))]
    private static void ReadStored_Prefix(NetworkConnection conn) => _sender = conn;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcReader___Server_SetStoredInstance_2652194801))]
    private static void ReadStored_Postfix() => _sender = null;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcLogic___SetItemSlotQuantity_1692629761))]
    private static void SetQty_Prefix(Cauldron __instance, int itemSlotIndex, int quantity)
    {
        if (!Settings.LogCauldronSlots || !Items.IsHost) return;
        try
        {
            var slot = SlotAt(__instance, itemSlotIndex);
            if (slot == null) return;
            int old = slot.Quantity;
            if (old == quantity) return;
            var sender = _sender;
            string line = $"[cauldron {CauldronQualityPatches.Short(__instance)}] {SlotName(__instance, slot, itemSlotIndex)}: {old} -> {quantity} " +
                          $"({slot.ItemInstance?.ID ?? "?"}) by {Items.Who(sender)}{(__instance.RemainingCookTime > 0 ? $", cooking {__instance.RemainingCookTime} min left" : "")}";

            bool suspicious = Items.IsRemote(sender) && Same(slot, __instance.LiquidSlot) && old - quantity > 1;
            if (suspicious) Log.Warn(line + " | SUSPICIOUS: petrol dropped by more than one cook uses");
            else Log.Msg(line);
        }
        catch (Exception e) { Log.Err($"[slotlog] {e}"); }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Cauldron), nameof(Cauldron.RpcLogic___SetStoredInstance_2652194801))]
    private static void SetStored_Prefix(Cauldron __instance, int itemSlotIndex, ItemInstance instance)
    {
        if (!Settings.LogCauldronSlots || !Items.IsHost) return;
        try
        {
            var slot = SlotAt(__instance, itemSlotIndex);
            if (slot == null) return;
            string before = Items.Describe(slot.ItemInstance), after = Items.Describe(instance);
            if (before == after) return;
            var sender = _sender;
            string line = $"[cauldron {CauldronQualityPatches.Short(__instance)}] {SlotName(__instance, slot, itemSlotIndex)}: {before} -> {after} by {Items.Who(sender)}";
            bool suspicious = Items.IsRemote(sender) && Same(slot, __instance.LiquidSlot) && (instance == null || instance.Quantity <= 0) && slot.Quantity > 1;
            if (suspicious) Log.Warn(line + " | SUSPICIOUS: petrol cleared");
            else Log.Msg(line);
        }
        catch (Exception e) { Log.Err($"[slotlog] {e}"); }
    }

    private static bool Same(ItemSlot? a, ItemSlot? b) => a != null && b != null && a.Pointer == b.Pointer;

    private static ItemSlot? SlotAt(Cauldron c, int i)
    {
        var slots = c.ItemSlots;
        return slots != null && i >= 0 && i < slots.Count ? slots[i] : null;
    }

    private static string SlotName(Cauldron c, ItemSlot slot, int i)
    {
        if (Same(slot, c.LiquidSlot)) return "petrol";
        if (Same(slot, c.OutputSlot)) return "output";
        var ing = c.IngredientSlots;
        if (ing != null)
            for (int k = 0; k < ing.Length; k++)
                if (Same(ing[k], slot)) return $"leaves {k + 1}";
        return $"slot {i}";
    }
}
