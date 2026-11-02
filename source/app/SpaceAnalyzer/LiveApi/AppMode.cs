using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.App.Runtime;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>
/// Operating mode chosen at start: "sfa" (complete interface) or "api" (Live Acquisition API service with a light monitor page,
/// no SFA interface loaded). It stays fixed until the application is closed. Preset with --mode sfa|api or SPACE_MODE.
/// </summary>
public static class AppMode
{
    static readonly object Gate = new();
    static string current = "";
    static LiveApiMode? api;

    public static string Current { get { lock (Gate) return current; } }
    public static LiveApiMode? Api { get { lock (Gate) return api; } }

    public static string? Requested(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--mode") return args[i + 1];
        return Environment.GetEnvironmentVariable("SPACE_MODE") is { Length: > 0 } env ? env : null;
    }

    /// <summary>Sets the mode once; choosing it again is accepted, a different one needs a restart.</summary>
    public static async Task Choose(string mode, AppRuntime rt)
    {
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("sfa" or "api")) throw new UserError("Modalità non valida: sfa oppure api");
        LiveApiMode? start = null;
        lock (Gate)
        {
            if (current == mode) return;
            if (current.Length > 0) throw new UserError("Modalità già scelta: chiudere e riavviare l'applicazione per cambiarla");
            current = mode;
            if (mode == "api") api = start = new LiveApiMode(rt);
        }
        Log.Info(mode == "api" ? "Modalità Live Acquisition API" : "Modalità Space Fieldbus Analyzer");
        if (start != null) await start.Host.Restart();
    }

    public static async Task Shutdown()
    {
        LiveApiMode? running;
        lock (Gate) running = api;
        if (running != null) await running.DisposeAsync();
    }
}

public sealed class LiveApiMode : IAsyncDisposable
{
    public LiveApiService Service { get; }
    public TestReceiver Receiver { get; } = new();
    public LiveApiHost Host { get; }

    public LiveApiMode(AppRuntime rt)
    {
        Service = new LiveApiService(rt);
        Host = new LiveApiHost(Service, Receiver);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await Host.Stop();
    }
}
