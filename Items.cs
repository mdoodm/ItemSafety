using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using SlotList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemSlot>;

namespace ItemSafety;

public static class Items
{
    public static bool IsHost
    {
        get { try { return InstanceFinder.IsServer; } catch { return false; } }
    }

    // Total quantity of items in `slots` that stack with `like` (same item, same quality).
    public static int Count(SlotList? slots, ItemInstance like)
    {
        if (slots == null || like == null) return 0;
        int n = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var it = slots[i]?.ItemInstance;
            if (it != null && it.Quantity > 0 && it.CanStackWith(like, false)) n += it.Quantity;
        }
        return n;
    }

    public static string Describe(ItemInstance? it)
    {
        if (it == null || it.Quantity <= 0) return "empty";
        var q = it.TryCast<QualityItemInstance>();
        return q != null ? $"{it.ID} x{it.Quantity} ({q.Quality})" : $"{it.ID} x{it.Quantity}";
    }

    public static string Who(NetworkConnection? conn)
    {
        if (conn == null) return "host (direct)";
        try
        {
            if (conn.IsLocalClient) return "host/employee";
            string name = "?";
            var players = Player.PlayerList;
            if (players != null)
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p != null && p.Connection != null && p.Connection.ClientId == conn.ClientId) { name = p.PlayerName; break; }
                }
            return $"player '{name}' (client {conn.ClientId})";
        }
        catch { return "unknown"; }
    }

    public static bool IsRemote(NetworkConnection? conn)
    {
        try { return conn != null && !conn.IsLocalClient; } catch { return false; }
    }
}
