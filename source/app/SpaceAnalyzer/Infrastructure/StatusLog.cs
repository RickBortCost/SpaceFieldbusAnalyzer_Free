using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpaceAnalyzer.App.Infrastructure;

/// <summary>
/// Periodic status of the service in SFA_stato_*.log (every 60 s, SPACE_STATUS_LOG_SECONDS from 5 to 3600): connections, acquisition
/// queues, recording, memory, disk, threads. A missing interval (PC asleep or hibernated, clock changed, service blocked) is reported in
/// the error log. Also forwards the acquisition events (registro eventi) to the text log, at most 20 per kind and minute.
/// </summary>
public static class StatusLog
{
    static readonly object Gate = new();
    static Timer? timer;
    static Func<Dictionary<string, object?>>? build;
    static TimeSpan interval;
    static DateTime? lastWall;
    static long lastMono;
    static readonly DateTime Started = DateTime.UtcNow;
    static readonly Dictionary<string, (DateTime minute, int count, int dropped)> Rates = new();

    public static TimeSpan Interval
    {
        get
        {
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("SPACE_STATUS_LOG_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 60;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
        }
    }

    public static void Start(Func<Dictionary<string, object?>> snapshot)
    {
        lock (Gate)
        {
            if (timer != null) return;
            build = snapshot;
            interval = Interval;
            timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), interval);
        }
        Log.Info($"Log di stato ogni {interval.TotalSeconds:0} s nella cartella {Log.Folder ?? "(non disponibile)"}");
    }

    public static void Stop()
    {
        lock (Gate) { timer?.Dispose(); timer = null; }
        Tick();   // last status at shutdown
    }

    /// <summary>Process figures for the status line.</summary>
    public static Dictionary<string, object?> Process()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return new()
        {
            ["uptime_s"] = (long)(DateTime.UtcNow - Started).TotalSeconds, ["working_set_mb"] = p.WorkingSet64 >> 20, ["private_mb"] = p.PrivateMemorySize64 >> 20,
            ["gc_heap_mb"] = GC.GetTotalMemory(false) >> 20, ["gc_gen0"] = (long)GC.CollectionCount(0), ["gc_gen1"] = (long)GC.CollectionCount(1),
            ["gc_gen2"] = (long)GC.CollectionCount(2), ["threads"] = (long)p.Threads.Count, ["handles"] = (long)p.HandleCount,
            ["cpu_s"] = (long)p.TotalProcessorTime.TotalSeconds,
        };
    }

    static void Tick()
    {
        Func<Dictionary<string, object?>>? snapshot;
        lock (Gate) snapshot = build;
        if (snapshot == null) return;
        var wall = DateTime.UtcNow;
        var mono = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            if (lastWall is DateTime previous)
            {
                var wallGap = wall - previous;
                var monoGap = Stopwatch.GetElapsedTime(lastMono, mono);
                if (wallGap > interval * 3)
                    Log.Warning(string.Create(CultureInfo.InvariantCulture,
                        $"Stato non registrato per {wallGap.TotalSeconds:0} s (atteso ogni {interval.TotalSeconds:0} s; tempo monotono {monoGap.TotalSeconds:0} s): PC in sospensione o ibernazione, servizio bloccato o orologio cambiato"));
                else if ((wallGap - monoGap).Duration() > TimeSpan.FromSeconds(30))
                    Log.Warning(string.Create(CultureInfo.InvariantCulture, $"Orologio del PC spostato di {(wallGap - monoGap).TotalSeconds:0} s tra due stati"));
            }
            lastWall = wall; lastMono = mono;
        }
        try { Log.Status(Py.DumpsPlain(snapshot())); }
        catch (Exception exc) { Log.Warning("Stato non registrato: " + exc.Message); }
    }

    static readonly Regex Problem = new("overflow|error|fail|gap|disconnect|stopped|lost|backwards|skipped|disk_full|unavailable", RegexOptions.Compiled);

    /// <summary>An acquisition event in the text log (warnings for the ones that signal a problem), rate-limited per kind.</summary>
    public static void Event(string kind, string? connectionId, object? details)
    {
        var now = DateTime.UtcNow;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        int dropped;
        lock (Gate)
        {
            var (at, count, skipped) = Rates.TryGetValue(kind, out var r) ? r : (minute, 0, 0);
            if (at != minute) { dropped = skipped; (at, count, skipped) = (minute, 0, 0); } else dropped = 0;
            if (count >= 20) { Rates[kind] = (at, count, skipped + 1); return; }
            Rates[kind] = (at, count + 1, skipped);
        }
        if (dropped > 0) Log.Warning($"Evento {kind}: altri {dropped} eventi nel minuto precedente non riportati qui (sono nel registro eventi)");
        var text = Py.DumpsPlain(details);
        if (text.Length > 2000) text = text[..2000] + "…";
        var message = "Evento " + kind + (connectionId != null ? " [" + connectionId + "]" : "") + " " + text;
        if (Problem.IsMatch(kind)) Log.Warning(message); else Log.Info(message);
    }
}
