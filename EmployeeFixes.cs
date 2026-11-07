using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common.Attributes;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Persistence.Datas;

namespace ItemSafety;

// 1. Safe pickup.
// Vanilla TakeItem takes min(maxMove, source qty, destination capacity) out of the source
// slot, then puts a copy into the employee's pockets without checking they have room.
// NPCInventory.InsertItem refuses an item that doesn't fit, so the items are deleted.
// Here the amount is also capped to the pocket's free capacity. With no room the take is
// skipped, which is the same path vanilla uses when the amount is 0.
[HarmonyPatch(typeof(MoveItemBehaviour), nameof(MoveItemBehaviour.TakeItem))]
public static class SafePickupPatch
{
    private const int NoChange = int.MinValue;

    private static bool Prefix(MoveItemBehaviour __instance, ref int __state)
    {
        __state = NoChange;
        if (!Settings.SafePickup || !Items.IsHost) return true;
        try
        {
            var route = __instance.assignedRoute;
            var template = __instance.itemToRetrieveTemplate;
            var npc = __instance.Npc;
            if (route == null || template == null || npc == null || route.Source == null || npc.Inventory == null) return true;

            var slot = route.Source.GetFirstSlotContainingTemplateItem(template, ITransitEntity.ESlotType.Output);
            if (slot?.ItemInstance == null) return true;

            int amount = __instance.GetAmountToGrab();
            if (amount < 0)
            {
                // Destination already holds more than a full stack (e.g. after a stack-size mod
                // was removed). Vanilla would add items to the source and pass a negative copy on.
                Log.Throttled("neg:" + npc.FullName, 60, $"[pickup] {npc.FullName}: grab amount for {template.ID} is {amount} (destination over its stack limit); skipped", true);
                return false;
            }
            if (amount == 0) return true;

            int room = npc.Inventory.GetCapacityForItem(slot.ItemInstance);
            if (room >= amount) return true;

            if (room <= 0)
            {
                Log.Throttled("full:" + npc.FullName, 60,
                    $"[pickup] {npc.FullName}'s pockets have no room for {template.ID}; skipped taking {amount} so it isn't deleted. Talk to them to empty their pockets.", true);
                return false;
            }

            __state = __instance.maxMoveAmount;
            __instance.maxMoveAmount = room;
            Log.Msg($"[pickup] {npc.FullName}: limited {template.ID} pickup from {amount} to {room} (pocket space)");
        }
        catch (Exception e) { Log.Err($"[pickup] {e}"); }
        return true;
    }

    private static void Postfix(MoveItemBehaviour __instance, int __state)
    {
        if (__state == NoChange) return;
        try { __instance.maxMoveAmount = __state; } catch { }
    }
}

// 2. Safe delivery.
// The PlaceItem coroutine re-checks the destination. If it can't fit, it tries to put the
// items back into the source; if that fails too it logs "Item will be lost" and still
// removes them from the employee's pocket. This patch measures the pocket, destination and
// source around each step of that coroutine; whatever left the pocket without arriving
// anywhere is put back into the pocket.
[HarmonyPatch]
public static class SafeDeliveryPatch
{
    private static PropertyInfo? _thisProp;

    private static MethodBase? TargetMethod()
    {
        var nested = typeof(MoveItemBehaviour).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(t => t.GetCustomAttribute<ObfuscatedNameAttribute>()?.ObfuscatedName?.Contains("<PlaceItem>") == true);
        if (nested == null) { Log.Err("[delivery] PlaceItem coroutine type not found; safe delivery disabled"); return null; }
        _thisProp = nested.GetProperty("__4__this");
        return nested.GetMethod("MoveNext");
    }

    private static bool Prepare() => TargetMethod() != null;

    public sealed class State
    {
        public MoveItemBehaviour B = null!;
        public NPC Npc = null!;
        public TransitRoute Route = null!;
        public ItemInstance Copy = null!;
        public int Pocket, Dest, Src;
    }

    private static void Prefix(object __instance, ref State? __state)
    {
        __state = null;
        if (!Settings.SafeDelivery || !Items.IsHost) return;
        try
        {
            var b = _thisProp?.GetValue(__instance) as MoveItemBehaviour;
            if (b == null || b.grabbedAmount <= 0) return;
            var npc = b.Npc;
            var route = b.assignedRoute;
            var template = b.itemToRetrieveTemplate;
            if (npc?.Inventory == null || route == null || template == null) return;

            var inPocket = npc.Inventory.GetFirstIdenticalItem(template, null);
            if (inPocket == null) return;
            var copy = inPocket.GetCopy(-1);

            __state = new State
            {
                B = b, Npc = npc, Route = route, Copy = copy,
                Pocket = Items.Count(npc.Inventory.ItemSlots, copy),
                Dest = Items.Count(route.Destination?.InputSlots, copy),
                Src = Items.Count(route.Source?.OutputSlots, copy),
            };
        }
        catch (Exception e) { Log.Err($"[delivery] {e}"); __state = null; }
    }

    private static void Postfix(State? __state)
    {
        if (__state == null) return;
        try
        {
            var s = __state;
            int pocket = Items.Count(s.Npc.Inventory.ItemSlots, s.Copy);
            int removed = s.Pocket - pocket;
            if (removed <= 0) return;

            int arrived = (Items.Count(s.Route.Destination?.InputSlots, s.Copy) - s.Dest)
                        + (Items.Count(s.Route.Source?.OutputSlots, s.Copy) - s.Src);
            int lost = removed - Math.Max(0, arrived);
            if (lost <= 0) return;

            s.Npc.Inventory.InsertItem(s.Copy.GetCopy(lost), true);
            Log.Warn($"[delivery] {s.Npc.FullName}: {lost}x {s.Copy.ID} couldn't go into '{SafeName(s.Route.Destination)}' or back to '{SafeName(s.Route.Source)}'. " +
                     "The game would have deleted it; kept it in their pockets instead.");
        }
        catch (Exception e) { Log.Err($"[delivery] {e}"); }
    }

    private static string SafeName(ITransitEntity? e)
    {
        try { return e?.Name ?? "?"; } catch { return "?"; }
    }
}

// 3. Restore carried items on load.
// The save keeps an active move's template and grabbed quantity. Load restores the route
// and grabbedAmount but never puts the items back in the pocket, so the employee walks to
// the source and takes a second lot; the first lot is gone. Put it back.
[HarmonyPatch(typeof(MoveItemBehaviour), nameof(MoveItemBehaviour.Load))]
public static class RestoreCarryPatch
{
    private static void Postfix(MoveItemBehaviour __instance, MoveItemData moveItemData)
    {
        if (!Settings.RestoreCarry || !Items.IsHost) return;
        try
        {
            int grabbed = moveItemData?.GrabbedItemQuantity ?? 0;
            var template = __instance.itemToRetrieveTemplate;
            if (grabbed <= 0 || template == null) return;

            var npc = __instance.Npc;
            if (npc?.Inventory == null) { Log.Warn("[restore] employee has no inventory yet; skipped"); return; }

            var items = template.GetCopy(grabbed);
            int have = Items.Count(npc.Inventory.ItemSlots, items);
            int missing = grabbed - have;
            if (missing <= 0) return;

            npc.Inventory.InsertItem(template.GetCopy(missing), true);
            int now = Items.Count(npc.Inventory.ItemSlots, items);
            if (now - have >= missing)
                Log.Msg($"[restore] {npc.FullName}: gave back {missing}x {template.ID} they were carrying when the game saved");
            else
                Log.Warn($"[restore] {npc.FullName}: only {now - have} of {missing}x {template.ID} fit back into their pockets");
        }
        catch (Exception e) { Log.Err($"[restore] {e}"); }
    }
}
