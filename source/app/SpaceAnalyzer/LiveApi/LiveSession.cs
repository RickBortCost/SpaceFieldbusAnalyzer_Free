using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.Engine.Core;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>
/// One temporary acquisition requested through the API: the samples of its signals, thinned to the requested period, are sent as
/// "samples" messages on a WebSocket that SFA opens towards streamTarget.url. Lives only in memory; ends on DELETE, at the TTL or
/// when the stream target stays unreachable. Lost connections are retried (samples of the gap are not kept, as the specification allows).
/// </summary>
public sealed class LiveSession
{
    public const int SchemaVersion = 1;
    const int MaxQueued = 200_000, MaxPerMessage = 5000;

    sealed class SignalState(LiveSignal signal)
    {
        public LiveSignal Signal { get; } = signal;
        public double? LastSent;
        public string? LastQuality;
        public bool InError;
        public double? LastT;
        public object? LastValue;
        public long Sent;
    }

    readonly record struct Outgoing(SignalState Signal, double T, object? Value, string Quality);

    readonly object gate = new();
    readonly Dictionary<string, List<SignalState>> byNode = new();
    readonly List<SignalState> signals;
    readonly Queue<Outgoing> queue = new();
    readonly Queue<Action<Utf8JsonWriter>> events = new();
    readonly LinkedList<string> log = new();
    readonly CancellationTokenSource stop = new();
    readonly Func<LiveApiSettings> settings;
    readonly Func<string, string> sourceState;
    readonly Action<LiveSession> finished;
    Task? runner;
    Timer? ttl;
    string? stopReason;

    public string SessionId { get; } = "sfa-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    public LiveApiRequest Request { get; }
    public string Client { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; }
    public DateTime? EndedAt { get; private set; }
    public string Status { get; private set; } = "starting";
    public bool StreamConnected { get; private set; }
    public long Sequence { get; private set; }
    public long SamplesSent { get; private set; }
    public long Dropped { get; private set; }
    public long Reconnects { get; private set; }
    public string LastError { get; private set; } = "";
    public string? EndReason { get; private set; }

    public LiveSession(LiveApiRequest request, string client, Func<LiveApiSettings> settings, Func<string, string> sourceState, Action<LiveSession> finished)
    {
        Request = request; Client = client; this.settings = settings; this.sourceState = sourceState; this.finished = finished;
        ExpiresAt = CreatedAt.AddSeconds(request.TtlSeconds);
        signals = request.Signals.Select(s => new SignalState(s)).ToList();
        foreach (var s in signals)
        {
            if (!byNode.TryGetValue(s.Signal.NodeId, out var list)) byNode[s.Signal.NodeId] = list = new();
            list.Add(s);
        }
    }

    public IEnumerable<string> NodeIds => byNode.Keys;
    public bool Ended => EndedAt != null;

    public static string Iso(double ms) => DateTime.UnixEpoch.AddTicks((long)Math.Round(ms * 10_000)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    void Note(string text)
    {
        lock (gate)
        {
            log.AddLast(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text);
            while (log.Count > 60) log.RemoveFirst();
        }
    }

    public void Start()
    {
        Note($"Sessione accettata da {Client}: {signals.Count} segnali, {Request.EffectiveSamplingMs} ms, TTL {Request.TtlSeconds} s");
        // The TTL counts from the creation (expiresAt of the response), not from the end of the source connection.
        var remaining = ExpiresAt - DateTime.UtcNow;
        ttl = new Timer(_ => Stop("expired"), null, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        runner = Task.Run(Run);
    }

    /// <summary>Ends the session (requested, expired, service_stopped): stream_stopped is sent if the socket is open.</summary>
    public void Stop(string reason)
    {
        lock (gate)
        {
            if (stopReason != null) return;
            stopReason = reason;
        }
        stop.Cancel();
    }

    public Task Completion => runner ?? Task.CompletedTask;

    /// <summary>
    /// Samples from the acquisition engine (any protocol, any signal): the ones of this session are thinned to its period, and a
    /// change between valid and failed reading becomes a signal_status event. Read errors are sent as "bad" samples without a value,
    /// never replaced with the last valid value.
    /// </summary>
    public void Offer(double t, List<Sample> rows, int acquisitionPeriod)
    {
        if (Ended) return;
        var tolerance = Math.Max(0, acquisitionPeriod) / 2.0;
        lock (gate)
        {
            foreach (var row in rows)
            {
                if (!byNode.TryGetValue(row.NodeId, out var states)) continue;
                var quality = row.QualityOrDerived.ToLowerInvariant() switch { "good" => "good", "uncertain" => "uncertain", _ => "bad" };
                var value = quality == "bad" ? null : Plain(row.Value);
                if (quality != "bad" && value == null) quality = "bad";
                var failed = quality == "bad" && (row.Error != null || row.Status != null && row.Status != "Good");
                foreach (var s in states)
                {
                    if (failed != s.InError)
                    {
                        s.InError = failed;
                        var error = failed ? (row.Error ?? row.Status ?? "Lettura non riuscita") : null;
                        var code = failed ? (sourceState(Request.Protocol) == "connected" ? "acquisition_failed" : "source_unavailable") : null;
                        QueueStatus(s.Signal.SignalId, failed ? "error" : "ok", code, error, t);
                        if (failed) Note($"{s.Signal.SignalId}: {error}");
                    }
                    var due = s.LastSent == null || t - s.LastSent >= Request.EffectiveSamplingMs - tolerance || s.LastQuality != quality;
                    if (!due) continue;
                    s.LastSent = t; s.LastQuality = quality; s.LastT = t; s.LastValue = value;
                    if (!StreamConnected) continue;   // a lost connection does not keep samples (§24)
                    if (queue.Count >= MaxQueued) { Dropped++; continue; }
                    queue.Enqueue(new Outgoing(s, t, value, quality));
                }
            }
        }
    }

    static object? Plain(object? value) => value switch
    {
        null => null,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? (double)f : null,
        bool b => b,
        string s => s,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? (long)u : (double)u,
        decimal m => (double)m,
        _ => value.ToString(),
    };

    void QueueStatus(string signalId, string status, string? code, string? message, double t)
    {
        while (events.Count >= 1000) events.Dequeue();
        events.Enqueue(w =>
        {
            w.WriteString("type", "signal_status");
            Ids(w);
            w.WriteString("signalId", signalId);
            w.WriteString("status", status);
            if (code != null)
            {
                w.WriteStartObject("error");
                w.WriteString("code", code);
                w.WriteString("message", message);
                w.WriteEndObject();
            }
            w.WriteString("timestamp", Iso(t));
        });
    }

    void Ids(Utf8JsonWriter w)
    {
        w.WriteString("requestId", Request.RequestId);
        w.WriteString("sessionId", SessionId);
    }

    async Task Run()
    {
        var token = stop.Token;
        var attempts = 0;
        DateTime? lostSince = null;
        var delay = settings().ReconnectDelayMs;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Status = "connecting";
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                try
                {
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(10));
                        await socket.ConnectAsync(Request.StreamUrl, connect.Token);
                    }
                    Note($"WebSocket connesso a {Request.StreamUrl}");
                    lock (gate) { queue.Clear(); foreach (var signal in signals) signal.LastSent = null; }
                    await Send(socket, Started, token);
                    StreamConnected = true;
                    Status = "streaming";
                    attempts = 0; lostSince = null; delay = settings().ReconnectDelayMs;
                    Note("stream_started inviato");
                    await Pump(socket, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (StreamConnected && socket.State == WebSocketState.Open) await Close(socket);
                    break;
                }
                catch (Exception exc) when (exc is WebSocketException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException)
                {
                    LastError = exc.InnerException?.Message is { Length: > 0 } inner ? exc.Message + " (" + inner + ")" : exc.Message;
                    Note((StreamConnected ? "WebSocket perso: " : "Connessione WebSocket non riuscita: ") + LastError);
                }
                if (StreamConnected) Reconnects++;
                StreamConnected = false;
                lock (gate) queue.Clear();
                var s = settings();
                lostSince ??= DateTime.UtcNow;
                attempts++;
                if (attempts > s.ReconnectMaxAttempts || DateTime.UtcNow - lostSince > TimeSpan.FromSeconds(s.ReconnectTimeoutSeconds))
                {
                    lock (gate) stopReason ??= "stream_target_unavailable";
                    Note("Destinazione non raggiungibile: sessione terminata");
                    break;
                }
                Status = "connecting";
                try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
                delay = Math.Min(delay * 2, 30_000);
            }
        }
        catch (Exception exc)
        {
            LastError = exc.Message;
            lock (gate) stopReason ??= "internal_error";
            Log.Error("Live Acquisition API: sessione " + SessionId, exc);
        }
        finally
        {
            ttl?.Dispose();
            StreamConnected = false;
            EndReason = stopReason ?? "requested";
            Status = EndReason switch { "requested" or "service_stopped" => "stopped", "expired" => "expired", _ => "error" };
            EndedAt = DateTime.UtcNow;
            Note("Sessione terminata: " + EndReason);
            finished(this);
        }
    }

    void Started(Utf8JsonWriter w)
    {
        w.WriteString("type", "stream_started");
        Ids(w);
        w.WriteNumber("schemaVersion", SchemaVersion);
        w.WriteNumber("samplingPeriodMs", Request.EffectiveSamplingMs);
        w.WriteNumber("requestedSamplingPeriodMs", Request.RequestedSamplingMs);
        w.WriteStartArray("signals");
        foreach (var s in signals)
        {
            w.WriteStartObject();
            w.WriteString("signalId", s.Signal.SignalId);
            w.WriteString("dataType", s.Signal.DataType);
            if (s.Signal.Unit != null) w.WriteString("unit", s.Signal.Unit);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>Sends the queued samples every batch interval until the socket closes or the session ends.</summary>
    async Task Pump(ClientWebSocket socket, CancellationToken token)
    {
        // The consumer may close the socket: a pending receive notices it (its messages are ignored).
        var receive = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        });
        _ = receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        while (true)
        {
            var wait = Task.Delay(settings().BatchMs, token);
            if (await Task.WhenAny(wait, receive) == receive)
            {
                if (receive.IsFaulted) throw receive.Exception!.InnerException ?? receive.Exception;
                throw new WebSocketException("chiuso dalla destinazione");
            }
            await wait;   // throws when the session is stopped
            await Flush(socket, token);
        }
    }

    async Task Flush(ClientWebSocket socket, CancellationToken token)
    {
        while (true)
        {
            Action<Utf8JsonWriter>? evt;
            lock (gate) evt = events.Count > 0 ? events.Dequeue() : null;
            if (evt == null) break;
            await Send(socket, evt, token);
        }
        while (true)
        {
            List<Outgoing> batch;
            lock (gate)
            {
                if (queue.Count == 0) return;
                batch = new List<Outgoing>(Math.Min(queue.Count, MaxPerMessage));
                while (batch.Count < MaxPerMessage && queue.Count > 0) batch.Add(queue.Dequeue());
            }
            var sequence = Sequence + 1;
            await Send(socket, w =>
            {
                w.WriteString("type", "samples");
                Ids(w);
                w.WriteNumber("sequence", sequence);
                w.WriteStartArray("samples");
                foreach (var o in batch)
                {
                    w.WriteStartObject();
                    w.WriteString("signalId", o.Signal.Signal.SignalId);
                    w.WriteString("timestamp", Iso(o.T));
                    w.WritePropertyName("value");
                    switch (o.Value)
                    {
                        case null: w.WriteNullValue(); break;
                        case double d: w.WriteNumberValue(d); break;
                        case long l: w.WriteNumberValue(l); break;
                        case bool b: w.WriteBooleanValue(b); break;
                        default: w.WriteStringValue(o.Value.ToString()); break;
                    }
                    w.WriteString("quality", o.Quality);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }, token);
            Sequence = sequence;
            lock (gate) foreach (var o in batch) o.Signal.Sent++;
            SamplesSent += batch.Count;
        }
    }

    async Task Close(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Flush(socket, timeout.Token);
            var reason = stopReason ?? "requested";
            await Send(socket, w =>
            {
                w.WriteString("type", "stream_stopped");
                Ids(w);
                w.WriteString("reason", reason);
                w.WriteString("timestamp", Iso(DateTime.UtcNow));
            }, timeout.Token);
            Note("stream_stopped inviato (" + reason + ")");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
        }
        catch (Exception exc) when (exc is WebSocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            Note("Chiusura WebSocket non completata: " + exc.Message);
        }
    }

    static async Task Send(ClientWebSocket socket, Action<Utf8JsonWriter> body, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, token);
    }

    /// <summary>State for GET /api/v1/live-sessions/{id}.</summary>
    public Dictionary<string, object?> Describe() => new()
    {
        ["requestId"] = Request.RequestId, ["sessionId"] = SessionId, ["status"] = Status,
        ["createdAt"] = Iso(CreatedAt), ["expiresAt"] = Iso(ExpiresAt),
        ["requestedSamplingPeriodMs"] = (long)Request.RequestedSamplingMs, ["effectiveSamplingPeriodMs"] = (long)Request.EffectiveSamplingMs,
        ["streamConnected"] = StreamConnected,
    };

    /// <summary>Everything the monitor page shows.</summary>
    public Dictionary<string, object?> Monitor()
    {
        lock (gate)
        {
            var d = Describe();
            d["sourceId"] = Request.SourceId; d["protocol"] = Request.Protocol; d["client"] = Client; d["streamTarget"] = Request.StreamUrl.ToString();
            d["sequence"] = Sequence; d["samplesSent"] = SamplesSent; d["dropped"] = Dropped; d["reconnects"] = Reconnects; d["lastError"] = LastError;
            d["endReason"] = EndReason; d["endedAt"] = EndedAt is DateTime e ? Iso(e) : null; d["ttlSeconds"] = (long)Request.TtlSeconds;
            d["signals"] = signals.Select(s => (object?)new Dictionary<string, object?>
            {
                ["signalId"] = s.Signal.SignalId, ["address"] = s.Signal.Address, ["dataType"] = s.Signal.DataType, ["unit"] = s.Signal.Unit,
                ["nodeId"] = s.Signal.NodeId, ["lastTimestamp"] = s.LastT is double t ? Iso(t) : null, ["lastValue"] = s.LastValue,
                ["lastQuality"] = s.LastQuality, ["inError"] = s.InError, ["sent"] = s.Sent,
            }).ToList();
            d["log"] = log.Cast<object?>().ToList();
            return d;
        }
    }
}
