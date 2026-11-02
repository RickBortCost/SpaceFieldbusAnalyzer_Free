using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SpaceAnalyzer.App.Infrastructure;

/// <summary>
/// Physical memory of the PC, checked every 5 s. When it runs low the service compacts its own memory, the Live chart of the
/// page keeps only the recent part (it reads <see cref="Report"/> in /api/values) and opening large files at full resolution is
/// refused, so that the PC stays usable while Record keeps writing. Levels: ok, low, critical.
/// </summary>
public static class MemoryGuard
{
    /// <summary>Low: under 15 % or 1.5 GB of available physical memory; critical: under 8 % or 700 MB.</summary>
    const double LowFraction = .15, CriticalFraction = .08;
    const long LowBytes = 1536L << 20, CriticalBytes = 700L << 20;

    static readonly object Gate = new();
    static Timer? timer;
    static long total, available;
    static string level = "ok";
    static DateTime lastCompaction = DateTime.MinValue;

    public static string Level { get { lock (Gate) return level; } }
    public static long AvailableBytes { get { lock (Gate) return available; } }

    public static void Start()
    {
        lock (Gate)
        {
            if (timer != null) return;
            Sample();
            timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    public static void Stop()
    {
        lock (Gate) { timer?.Dispose(); timer = null; }
    }

    /// <summary>Level for the given total and available bytes (unknown values count as ok).</summary>
    public static string Classify(long totalBytes, long availableBytes)
    {
        if (totalBytes <= 0 || availableBytes < 0) return "ok";
        var fraction = (double)availableBytes / totalBytes;
        if (fraction < CriticalFraction || availableBytes < CriticalBytes) return "critical";
        if (fraction < LowFraction || availableBytes < LowBytes) return "low";
        return "ok";
    }

    static void Sample()
    {
        try
        {
            var (t, a) = Read();
            string previous, next;
            lock (Gate)
            {
                total = t; available = a;
                previous = level;
                level = next = Classify(t, a);
            }
            if (next != previous)
            {
                var message = $"Memoria del PC: {next} (disponibili {a >> 20} MB su {t >> 20} MB; servizio {Process.GetCurrentProcess().WorkingSet64 >> 20} MB)";
                if (next == "ok") Log.Info(message); else Log.Warning(message);
            }
            // Under pressure the service returns the memory it no longer uses (at most once a minute).
            if (next != "ok" && DateTime.UtcNow - lastCompaction > TimeSpan.FromMinutes(1))
            {
                lastCompaction = DateTime.UtcNow;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
        }
        catch (Exception exc) { Log.Warning("Controllo della memoria non riuscito: " + exc.Message); }
    }

    /// <summary>Total and available physical memory of the PC, in bytes (-1 when unknown).</summary>
    static (long total, long available) Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return ((long)status.TotalPhys, (long)status.AvailPhys);
        }
        else if (File.Exists("/proc/meminfo"))
        {
            long Field(string[] lines, string name) =>
                lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal)) is { } line
                && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kb) ? kb * 1024 : -1;
            var lines = File.ReadAllLines("/proc/meminfo");
            return (Field(lines, "MemTotal"), Field(lines, "MemAvailable"));
        }
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }

    public static Dictionary<string, object?> Report()
    {
        lock (Gate)
            return new()
            {
                ["level"] = level, ["total_mb"] = total >> 20, ["available_mb"] = available >> 20,
                ["process_mb"] = Process.GetCurrentProcess().WorkingSet64 >> 20,
            };
    }

    /// <summary>Refuses an operation that would need about <paramref name="bytes"/> more memory than the PC can spare.</summary>
    public static void Require(long bytes, string what)
    {
        long free;
        lock (Gate) free = available;
        if (free > 0 && bytes > free - CriticalBytes)
            throw new UserError($"Memoria del PC insufficiente per {what} (servono circa {bytes >> 20} MB, disponibili {free >> 20} MB): ridurre l'intervallo o il numero di segnali.");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
