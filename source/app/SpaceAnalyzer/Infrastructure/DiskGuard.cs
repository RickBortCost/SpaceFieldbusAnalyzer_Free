using System.Globalization;

namespace SpaceAnalyzer.App.Infrastructure;

/// <summary>
/// Free space of the disks used by the service (recordings folder and data folder), checked every 10 s. A full disk stops
/// Windows and every program on the PC, so the service keeps a reserve: under the critical threshold the running recording is
/// closed in an orderly way (its files stay readable), the Live history shrinks to leave the reserve free, and imports,
/// conversions and new recordings are refused. Levels: ok, low, critical.
/// </summary>
public static class DiskGuard
{
    /// <summary>Critical under 1 GiB free (SPACE_MIN_FREE_MB), low under 5 GiB: absolute values, a percentage would stop a recording with tens of GB free on a large disk.</summary>
    static long CriticalBytes => Math.Max(256, long.TryParse(Environment.GetEnvironmentVariable("SPACE_MIN_FREE_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : 1024) << 20;
    const long LowBytes = 5L << 30;

    static readonly object Gate = new();
    static readonly Dictionary<string, (string folder, long total, long free, string level)> Disks = new();
    static readonly Dictionary<string, Func<string>> Folders = new();
    static Timer? timer;

    /// <summary>Raised (on the timer thread) when the disk of a watched folder becomes critical: argument is the role (recordings, data).</summary>
    public static event Action<string>? Critical;

    /// <summary>Watches the folder of a role (the function is read at every check, so a changed recordings folder is followed).</summary>
    public static void Watch(string role, Func<string> folder)
    {
        lock (Gate) Folders[role] = folder;
        Sample(role);
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (timer != null) return;
            timer = new Timer(_ => { foreach (var role in Roles()) Sample(role); }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    public static void Stop()
    {
        lock (Gate) { timer?.Dispose(); timer = null; }
    }

    static List<string> Roles() { lock (Gate) return Folders.Keys.ToList(); }

    /// <summary>Level for the given total and free bytes (unknown values count as ok).</summary>
    public static string Classify(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0 || freeBytes < 0) return "ok";
        if (freeBytes < CriticalBytes) return "critical";
        if (freeBytes < LowBytes) return "low";
        return "ok";
    }

    /// <summary>Total and free bytes of the disk holding a folder (-1 when unknown, for example a network share without quota).</summary>
    public static (long total, long free) Measure(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } parent) full = parent;
            var drive = new DriveInfo(full);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException) { return (-1, -1); }
    }

    static void Sample(string role)
    {
        try
        {
            Func<string>? get;
            lock (Gate) Folders.TryGetValue(role, out get);
            if (get == null) return;
            var folder = get();
            var (total, free) = Measure(folder);
            var level = Classify(total, free);
            string previous;
            lock (Gate)
            {
                previous = Disks.TryGetValue(role, out var old) ? old.level : "ok";
                Disks[role] = (folder, total, free, level);
            }
            if (level != previous)
            {
                var message = $"Spazio su disco ({role}, {folder}): {level} (liberi {free >> 20} MB su {total >> 20} MB)";
                if (level == "ok") Log.Info(message); else Log.Warning(message);
            }
            if (level == "critical")
                try { Critical?.Invoke(role); } catch (Exception exc) { Log.Warning("Protezione spazio disco non applicata: " + exc.Message); }
        }
        catch (Exception exc) { Log.Warning("Controllo dello spazio su disco non riuscito: " + exc.Message); }
    }

    /// <summary>
    /// Bytes that can still be written on the disk of a role before it becomes low (long.MaxValue when unknown): optional data
    /// (the Live history) stops there, leaving the rest to recordings.
    /// </summary>
    public static long SpareBeforeLow(string role)
    {
        lock (Gate)
            return Disks.TryGetValue(role, out var disk) && disk.free >= 0 && disk.total > 0 ? Math.Max(0, disk.free - LowBytes) : long.MaxValue;
    }

    public static string Level(string role) { lock (Gate) return Disks.TryGetValue(role, out var disk) ? disk.level : "ok"; }

    public static Dictionary<string, object?> Report()
    {
        lock (Gate)
            return Disks.ToDictionary(k => k.Key, k => (object?)new Dictionary<string, object?>
            {
                ["folder"] = k.Value.folder, ["level"] = k.Value.level, ["total_mb"] = k.Value.total >> 20, ["free_mb"] = k.Value.free >> 20,
            });
    }

    /// <summary>
    /// Refuses an operation that would write about <paramref name="bytes"/> into <paramref name="folder"/> leaving less than the reserve
    /// (measured now, not from the last check).
    /// </summary>
    public static void Require(string folder, long bytes, string what)
    {
        var (total, free) = Measure(folder);
        if (total <= 0 || free < 0) return;
        var reserve = CriticalBytes;
        if (free - Math.Max(0, bytes) < reserve)
            throw new UserError($"Spazio su disco insufficiente per {what} in {folder}: liberi {free >> 20} MB, servono circa {Math.Max(0, bytes) >> 20} MB più {reserve >> 20} MB di riserva per il sistema. Liberare spazio o scegliere un'altra cartella.");
    }
}
