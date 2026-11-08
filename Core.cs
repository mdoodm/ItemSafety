using ItemSafety;
using MelonLoader;

[assembly: MelonInfo(typeof(Core), "ItemSafety", Core.Version, "mdoodm")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ItemSafety;

// Host-side fixes for the base game deleting items (Schedule I 0.4.7, Il2Cpp).
// Only the host (or a solo player) needs it. Every fix checks InstanceFinder.IsServer.
public class Core : MelonMod
{
    public const string Version = "0.1.0";

    public override void OnInitializeMelon()
    {
        Settings.Init();
        LogBackup.BackupPreviousPlayerLog();
        Log.Warn($@"ItemSafety {Version} is an ALPHA test build. It changes how employees and cauldrons handle items. Back up your saves first (%USERPROFILE%\AppData\LocalLow\TVGS\Schedule I\Saves) and report anything odd.");
        Log.Msg($"ItemSafety {Version} loaded. Pickup={Settings.SafePickup} Delivery={Settings.SafeDelivery} " +
                $"RestoreCarry={Settings.RestoreCarry} CauldronQuality={Settings.CauldronQuality} SlotLog={Settings.LogCauldronSlots}");
        var patched = HarmonyInstance.GetPatchedMethods().Select(m => $"{m.DeclaringType?.Name}.{m.Name}").OrderBy(n => n).ToList();
        Log.Msg($"Patched {patched.Count} methods: {string.Join(", ", patched)}");
    }
}

public static class Settings
{
    private static MelonPreferences_Entry<bool>? _pickup, _delivery, _restore, _quality, _slotLog, _backup;

    public static bool SafePickup => _pickup?.Value ?? true;
    public static bool SafeDelivery => _delivery?.Value ?? true;
    public static bool RestoreCarry => _restore?.Value ?? true;
    public static bool CauldronQuality => _quality?.Value ?? true;
    public static bool LogCauldronSlots => _slotLog?.Value ?? true;
    public static bool BackupPlayerLog => _backup?.Value ?? true;

    public static void Init()
    {
        var c = MelonPreferences.CreateCategory("ItemSafety", "ItemSafety");
        _pickup = c.CreateEntry("SafeEmployeePickup", true, "Safe employee pickup",
            "Employees only take what fits in their pockets, so a full pocket can't delete items.");
        _delivery = c.CreateEntry("SafeEmployeeDelivery", true, "Safe employee delivery",
            "If a delivery can't go into the destination or back to the source, the employee keeps it instead of it being deleted.");
        _restore = c.CreateEntry("RestoreEmployeeCarry", true, "Restore carried items on load",
            "After loading a save, employees get back the items they were carrying when it was saved.");
        _quality = c.CreateEntry("CauldronQualityFix", true, "Cauldron quality fix",
            "Don't start a cook whose cocaine base can't stack with the output; merge at the lower quality instead of deleting a finished batch.");
        _slotLog = c.CreateEntry("LogCauldronSlots", true, "Log cauldron slot changes",
            "Log every cauldron slot change with who caused it. Flags suspicious drops from joined players.");
        _backup = c.CreateEntry("BackupPlayerLog", true, "Back up Player.log",
            "Copy the previous session's Player-prev.log to UserData/ItemSafety/Logs at startup (keeps 15).");
    }
}

public static class Log
{
    public static void Msg(string s) => MelonLogger.Msg(s);
    public static void Warn(string s) => MelonLogger.Warning(s);
    public static void Err(string s) => MelonLogger.Error(s);

    private static readonly Dictionary<string, float> _last = new();

    // Logs at most once per `seconds` per key, so a stuck employee doesn't flood the log.
    public static void Throttled(string key, float seconds, string s, bool warn = false)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_last.TryGetValue(key, out var t) && now - t < seconds) return;
        _last[key] = now;
        if (warn) Warn(s); else Msg(s);
    }
}

public static class LogBackup
{
    public static void BackupPreviousPlayerLog()
    {
        if (!Settings.BackupPlayerLog) return;
        try
        {
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "TVGS", "Schedule I", "Player-prev.log");
            if (!File.Exists(src)) return;
            string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "ItemSafety", "Logs");
            Directory.CreateDirectory(dir);
            string stamp = File.GetLastWriteTime(src).ToString("yyyyMMdd-HHmmss");
            string dst = Path.Combine(dir, $"Player-{stamp}.log");
            if (!File.Exists(dst)) File.Copy(src, dst);
            foreach (var old in new DirectoryInfo(dir).GetFiles("Player-*.log").OrderByDescending(f => f.Name).Skip(15))
                old.Delete();
            Log.Msg($"Backed up previous Player.log to {dst}");
        }
        catch (Exception e) { Log.Warn($"Player.log backup failed: {e.Message}"); }
    }
}
