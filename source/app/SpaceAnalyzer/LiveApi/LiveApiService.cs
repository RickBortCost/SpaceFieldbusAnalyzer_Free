using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceAnalyzer.App.Config;
using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.App.Runtime;
using SpaceAnalyzer.Engine.Acquisition;
using SpaceAnalyzer.Engine.Core;
using SpaceAnalyzer.Engine.Sources;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>
/// Live Acquisition API mode: temporary sessions requested by external systems, read with the same drivers and acquisition engine
/// as the SFA interface (own SourceController and AcquisitionEngine, no Live buffers, no recording). Sources come from the saved
/// SFA connection and are opened only while a session needs them; nothing of the sessions is saved.
/// </summary>
public sealed class LiveApiService : IAsyncDisposable
{
    readonly AppRuntime rt;
    readonly AcquisitionEngine acquisition;
    readonly SourceController sources;
    readonly SemaphoreSlim sourceLock = new(1, 1);
    readonly object gate = new();
    readonly Dictionary<string, LiveSession> sessions = new();
    readonly LinkedList<LiveSession> ended = new();
    readonly LinkedList<Dictionary<string, object?>> requests = new();
    volatile LiveSession[] active = [];
    long revision, totalRequests, totalSessions;
    bool closed;

    public LiveApiSettings Settings { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public LiveApiService(AppRuntime rt)
    {
        this.rt = rt;
        Settings = LiveApiSettings.Load(rt.Paths.DataDir);
        acquisition = new AcquisitionEngine((kind, connection, details) => rt.Events.EmitEngine("live_api_" + kind, connection, details));
        acquisition.Forward = Distribute;
        sources = new SourceController(acquisition, (kind, connection, details) => rt.Events.EmitEngine("live_api_" + kind, connection, details), new RecordGate());
    }

    public void UpdateSettings(JsonObject data)
    {
        var next = Settings.Clone();
        next.Apply(data);
        next.Save(rt.Paths.DataDir);
        Settings = next;
        Log.Info("Live Acquisition API: impostazioni aggiornate");
    }

    public string RegenerateKey()
    {
        var next = Settings.Clone();
        next.ApiKey = LiveApiSettings.NewKey();
        next.Save(rt.Paths.DataDir);
        Settings = next;
        Log.Info("Live Acquisition API: nuova chiave generata");
        return next.ApiKey;
    }

    /// <summary>Every REST call, for the monitor page (last 300).</summary>
    public void Record(string client, string method, string path, int status, double ms, string? code, string? requestId)
    {
        lock (gate)
        {
            totalRequests++;
            requests.AddFirst(new Dictionary<string, object?>
            {
                ["at"] = LiveSession.Iso(DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds), ["client"] = client, ["method"] = method,
                ["path"] = path, ["status"] = (long)status, ["ms"] = Math.Round(ms, 1), ["code"] = code, ["requestId"] = requestId,
            });
            while (requests.Count > 300) requests.RemoveLast();
        }
    }

    bool Distribute(double t, List<Sample> rows, int period, Generation? generation)
    {
        foreach (var session in active) session.Offer(t, rows, period);
        return true;
    }

    string SourceState(string protocol) => sources.Sessions.TryGetValue(protocol, out var s) ? s.State : "disconnected";

    JsonElement EngineConfig(JsonObject cfg)
    {
        var copy = (JsonObject)cfg.DeepClone();
        copy["_client_cert"] = rt.Paths.CertFile;
        copy["_client_key"] = rt.Paths.KeyFile;
        copy["_server_cert"] = rt.Paths.ServerCertFile;
        // The period comes from the sessions (the shortest requested), not from the intervals of the interface.
        copy.Remove("source_intervals");
        using var doc = JsonDocument.Parse(copy.ToJsonString());
        return doc.RootElement.Clone();
    }

    void UpdateSelection()
    {
        LiveSession[] current;
        lock (gate) current = active = sessions.Values.Where(s => !s.Ended).ToArray();
        var ids = current.SelectMany(s => s.NodeIds).Distinct().ToList();
        var period = current.Length > 0 ? current.Min(s => s.Request.EffectiveSamplingMs) : 500;
        sources.Select(ids, period, new Generation(Interlocked.Increment(ref revision), 1));
    }

    /// <summary>POST /api/v1/live-sessions.</summary>
    public async Task<LiveSession> Create(JsonNode? body, string client)
    {
        var cfg = rt.Config.Load();
        var configured = Validation.ConfiguredSources(cfg);
        var request = LiveApiRequest.Parse(body, Settings, configured, cfg["s7_mode"].Str() ?? "classic");
        if (MemoryGuard.Level == "critical")
            throw new LiveApiException("acquisition_failed", "Memoria del PC quasi esaurita: nuove sessioni rifiutate", 503, retryable: true);
        LiveSession session;
        lock (gate)
        {
            if (closed) throw new LiveApiException("internal_error", "Servizio in chiusura", 503, retryable: true);
            if (sessions.Values.Any(s => !s.Ended && s.Request.RequestId == request.RequestId))
                throw new LiveApiException("invalid_request", "requestId già usato da una sessione attiva: " + request.RequestId, 409);
            if (sessions.Values.Count(s => !s.Ended) >= Settings.MaxSessions)
                throw new LiveApiException("session_limit_reached", $"Raggiunto il limite di {Settings.MaxSessions} sessioni contemporanee", 429, retryable: true);
            session = new LiveSession(request, client, () => Settings, SourceState, Finished);
            sessions[session.SessionId] = session;
            totalSessions++;
        }
        await sourceLock.WaitAsync();
        try
        {
            UpdateSelection();
            if (!sources.Sessions.ContainsKey(request.Protocol))
            {
                if (request.Protocol == "opcua") rt.Certs.EnsureClient(cfg["application_uri"].Str());
                var error = await sources.Add(EngineConfig(cfg), request.Protocol, TimeSpan.FromSeconds(20));
                if (error != null)
                {
                    lock (gate) sessions.Remove(session.SessionId);
                    UpdateSelection();
                    Log.Warning($"Live Acquisition API: sorgente {request.Protocol} non disponibile: {error}");
                    throw new LiveApiException("source_unavailable", $"Sorgente {request.SourceId} ({request.Protocol}) non raggiungibile: {error}", 503, retryable: true);
                }
                Log.Info($"Live Acquisition API: sorgente {request.Protocol} connessa");
                SystemTuning.SetAcquiring(true);
            }
        }
        finally { sourceLock.Release(); }
        session.Start();
        Log.Info($"Live Acquisition API: sessione {session.SessionId} ({request.RequestId}) da {client}, {request.Signals.Count} segnali, {request.EffectiveSamplingMs} ms");
        return session;
    }

    void Finished(LiveSession session)
    {
        lock (gate)
        {
            sessions.Remove(session.SessionId);
            ended.AddFirst(session);
            while (ended.Count > 50) ended.RemoveLast();
        }
        Log.Info($"Live Acquisition API: sessione {session.SessionId} terminata ({session.EndReason})");
        _ = Task.Run(Release);
    }

    /// <summary>Closes the sources no session uses any more (resources are freed when the last session ends).</summary>
    async Task Release()
    {
        await sourceLock.WaitAsync();
        try
        {
            UpdateSelection();
            HashSet<string> needed;
            lock (gate) needed = sessions.Values.Select(s => s.Request.Protocol).ToHashSet();
            foreach (var protocol in sources.Sessions.Keys.Where(p => !needed.Contains(p)).ToList())
            {
                await sources.Remove(protocol);
                Log.Info($"Live Acquisition API: sorgente {protocol} chiusa (nessuna sessione attiva)");
            }
            if (sources.Sessions.Count == 0) SystemTuning.SetAcquiring(false);
        }
        catch (Exception exc) { Log.Error("Live Acquisition API: chiusura sorgenti", exc); }
        finally { sourceLock.Release(); }
    }

    public LiveSession? Find(string sessionId)
    {
        lock (gate) return sessions.TryGetValue(sessionId, out var s) ? s : null;
    }

    public List<LiveSession> Active()
    {
        lock (gate) return sessions.Values.ToList();
    }

    /// <summary>DELETE: stops the session and waits (up to 5 s) for stream_stopped and the closing of the socket.</summary>
    public async Task<bool> Delete(string sessionId, string reason = "requested")
    {
        var session = Find(sessionId);
        if (session == null) return false;
        session.Stop(reason);
        await Task.WhenAny(session.Completion, Task.Delay(5000));
        return true;
    }

    public Dictionary<string, object?> Monitor(Dictionary<string, object?> listener)
    {
        List<LiveSession> current, past;
        List<Dictionary<string, object?>> log;
        long total, created;
        lock (gate) { current = sessions.Values.ToList(); past = ended.ToList(); log = requests.ToList(); total = totalRequests; created = totalSessions; }
        var cfg = rt.Config.Load();
        return new Dictionary<string, object?>
        {
            ["listener"] = listener, ["settings"] = Settings.ToPlain(includeKey: true),
            ["configured_sources"] = Validation.ConfiguredSources(cfg).Where(SourceController.Protocols.Contains).Cast<object?>().ToList(),
            ["s7_mode"] = cfg["s7_mode"].Str() ?? "classic",
            ["sources"] = sources.Sessions.ToDictionary(k => k.Key, k => (object?)new Dictionary<string, object?>
            {
                ["state"] = k.Value.State, ["error"] = k.Value.Error, ["received"] = k.Value.Received, ["reconnections"] = k.Value.Reconnections,
                ["period_ms"] = (long)k.Value.Period,
            }),
            ["sessions"] = current.Select(s => (object?)s.Monitor()).ToList(),
            ["ended"] = past.Select(s => (object?)s.Monitor()).ToList(),
            ["requests"] = log.Cast<object?>().ToList(), ["total_requests"] = total, ["total_sessions"] = created,
            ["memory"] = MemoryGuard.Report(), ["started_at"] = LiveSession.Iso(StartedAt.Subtract(DateTime.UnixEpoch).TotalMilliseconds),
        };
    }

    public async ValueTask DisposeAsync()
    {
        List<LiveSession> current;
        lock (gate) { closed = true; current = sessions.Values.ToList(); }
        foreach (var s in current) s.Stop("service_stopped");
        await Task.WhenAny(Task.WhenAll(current.Select(s => s.Completion)), Task.Delay(5000));
        await sources.Stop();
        acquisition.Close();
    }
}
