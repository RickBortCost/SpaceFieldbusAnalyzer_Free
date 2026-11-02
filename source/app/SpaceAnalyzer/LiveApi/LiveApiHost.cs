using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SpaceAnalyzer.App.Config;
using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.App.Web;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>
/// Network side of the Live Acquisition API: a listener separate from the interface (which stays on 127.0.0.1), on the port and
/// address of the settings, with the API key. Also hosts the built-in test receiver used by the monitor page to check the whole
/// chain (REST → acquisition → outbound WebSocket) on the PC itself.
/// </summary>
public sealed class LiveApiHost(LiveApiService service, TestReceiver receiver)
{
    WebApplication? app;
    readonly SemaphoreSlim gate = new(1, 1);
    public string Error { get; private set; } = "";
    public int? ListeningPort { get; private set; }
    public string? ListeningAddress { get; private set; }

    public async Task Restart()
    {
        await gate.WaitAsync();
        try
        {
            if (app != null)
            {
                await app.StopAsync(TimeSpan.FromSeconds(3));
                await app.DisposeAsync();
                app = null; ListeningPort = null; ListeningAddress = null;
            }
            var settings = service.Settings;
            var built = Build(settings);
            try
            {
                await built.StartAsync();
                app = built;
                ListeningPort = settings.Port; ListeningAddress = settings.Bind;
                Error = "";
                Log.Info($"Live Acquisition API in ascolto su {settings.Bind}:{settings.Port}");
            }
            catch (Exception exc) when (exc is IOException or Microsoft.AspNetCore.Connections.AddressInUseException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                await built.DisposeAsync();
                Error = $"Porta {settings.Port} su {settings.Bind} non disponibile: {exc.Message}";
                Log.Warning("Live Acquisition API: " + Error);
            }
        }
        finally { gate.Release(); }
    }

    public async Task Stop()
    {
        await gate.WaitAsync();
        try
        {
            if (app != null) { await app.StopAsync(TimeSpan.FromSeconds(3)); await app.DisposeAsync(); app = null; }
        }
        finally { gate.Release(); }
    }

    /// <summary>Addresses an external system can use (one per IPv4 address of the PC, or the configured one).</summary>
    public Dictionary<string, object?> Describe()
    {
        var urls = new List<object?>();
        if (ListeningPort is int port)
        {
            var addresses = ListeningAddress is "0.0.0.0" or "::" or null ? LocalAddresses() : [ListeningAddress];
            urls.AddRange(addresses.Select(a => (object?)$"http://{a}:{port}/api/v1/live-sessions"));
        }
        return new() { ["listening"] = ListeningPort != null, ["port"] = (long?)ListeningPort, ["address"] = ListeningAddress, ["error"] = Error, ["urls"] = urls, ["host"] = Environment.MachineName };
    }

    static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) list.Add(a.Address.ToString());
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        if (!list.Contains("127.0.0.1")) list.Add("127.0.0.1");
        return list.Distinct().OrderBy(a => a == "127.0.0.1" ? 1 : 0).ToList();
    }

    static Task WriteError(HttpContext ctx, LiveApiException exc) => Http.Write(ctx, new Dictionary<string, object?>
    {
        ["status"] = "error",
        ["error"] = new Dictionary<string, object?> { ["code"] = exc.Code, ["message"] = exc.Message, ["retryable"] = exc.Retryable },
    }, exc.HttpStatus);

    bool Authorized(HttpContext ctx)
    {
        var settings = service.Settings;
        if (!settings.RequireKey) return true;
        var supplied = ctx.Request.Headers["X-Api-Key"].ToString();
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (supplied.Length == 0 && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = authorization[7..].Trim();
        var expected = Encoding.UTF8.GetBytes(settings.ApiKey);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    WebApplication Build(LiveApiSettings settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Parse(settings.Bind), settings.Port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        builder.Services.Configure<KestrelServerOptions>(o => o.AllowSynchronousIO = false);
        builder.Services.AddRoutingCore();
        var web = builder.Build();
        web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        web.Use(async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var client = ctx.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "?";
            ctx.Items["client"] = client;
            ctx.Response.OnStarting(() => { ctx.Response.Headers.CacheControl = "no-store"; ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; return Task.CompletedTask; });
            var path = ctx.Request.Path.Value ?? "";
            var receiverPath = path.StartsWith("/api/v1/test-receiver/", StringComparison.Ordinal);
            if (receiverPath)
            {
                // Only SFA itself streams to the built-in receiver.
                if (ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
                await next();
                return;
            }
            try
            {
                if (path.StartsWith("/api/v1/live-sessions", StringComparison.Ordinal) && !Authorized(ctx))
                    throw new LiveApiException("unauthorized", "Chiave API mancante o non valida (intestazione X-Api-Key o Authorization: Bearer)", 401);
                await next();
                if (!ctx.Response.HasStarted && ctx.Response.StatusCode == 404 && ctx.GetEndpoint() == null)
                    throw new LiveApiException("not_found", "Percorso non previsto dalla Live Acquisition API", 404);
                if (!ctx.Response.HasStarted && ctx.Response.StatusCode == 405)
                    throw new LiveApiException("invalid_request", "Metodo non ammesso per questo percorso", 405);
            }
            catch (LiveApiException exc) when (!ctx.Response.HasStarted)
            {
                ctx.Items["code"] = exc.Code;
                await WriteError(ctx, exc);
            }
            catch (Exception exc) when (!ctx.Response.HasStarted)
            {
                Log.Error("Live Acquisition API: richiesta " + path, exc);
                ctx.Items["code"] = "internal_error";
                await WriteError(ctx, new LiveApiException("internal_error", "Errore interno: " + exc.Message, 500, retryable: true));
            }
            finally
            {
                if (path != "/api/v1/health")
                    service.Record(client, ctx.Request.Method, path, ctx.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        ctx.Items["code"] as string, ctx.Items["requestId"] as string);
            }
        });

        web.MapGet("/api/v1/health", ctx => Http.Write(ctx, new Dictionary<string, object?>
        {
            ["status"] = "ok", ["app"] = Defaults.AppName, ["version"] = Defaults.Version, ["mode"] = "live-api", ["keyRequired"] = service.Settings.RequireKey,
        }));
        web.MapPost("/api/v1/live-sessions", async ctx =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (System.Text.Json.JsonException) { throw LiveApiException.Invalid("Corpo JSON della richiesta non valido"); }
            ctx.Items["requestId"] = (body as JsonObject)?["requestId"] is JsonValue r && r.TryGetValue<string>(out var id) ? id : null;
            var session = await service.Create(body, (string)ctx.Items["client"]!);
            var result = session.Describe();
            result.Remove("streamConnected");
            await Http.Write(ctx, result, 201);
        });
        web.MapGet("/api/v1/live-sessions", ctx => Http.Write(ctx, new Dictionary<string, object?>
        {
            ["sessions"] = service.Active().Select(s => (object?)s.Describe()).ToList(),
        }));
        web.MapGet("/api/v1/live-sessions/{id}", (HttpContext ctx, string id) =>
        {
            var session = service.Find(id) ?? throw new LiveApiException("session_not_found", "Sessione non trovata: " + id, 404);
            ctx.Items["requestId"] = session.Request.RequestId;
            return Http.Write(ctx, session.Describe());
        });
        web.MapDelete("/api/v1/live-sessions/{id}", async (HttpContext ctx, string id) =>
        {
            ctx.Items["requestId"] = service.Find(id)?.Request.RequestId;
            if (!await service.Delete(id)) throw new LiveApiException("session_not_found", "Sessione non trovata: " + id, 404);
            ctx.Response.StatusCode = 204;
        });
        web.Map("/api/v1/test-receiver/{id}", async (HttpContext ctx, string id) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await receiver.Receive(id, socket, ctx.RequestAborted);
        });
        return web;
    }
}

/// <summary>
/// WebSocket server inside SFA that plays the external system for the monitor page: it records what arrives (messages, sequence
/// gaps, last samples per signal) so that the operator can check the API on the Edge PC without another PC.
/// </summary>
public sealed class TestReceiver
{
    sealed class Stream
    {
        public long Messages, Samples, Gaps, LastSequence;
        public bool Connected;
        public string? Started, Stopped;
        public readonly Dictionary<string, LinkedList<object?[]>> Points = new();
        public readonly LinkedList<string> Log = new();
    }

    readonly object gate = new();
    readonly Dictionary<string, Stream> streams = new();

    public async Task Receive(string id, WebSocket socket, CancellationToken ct)
    {
        Stream stream;
        lock (gate)
        {
            if (!streams.TryGetValue(id, out stream!)) streams[id] = stream = new Stream();
            while (streams.Count > 20) streams.Remove(streams.Keys.First(k => k != id));
            stream.Connected = true;
            Note(stream, "WebSocket connesso da SFA");
        }
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                Handle(stream, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception exc) when (exc is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            lock (gate) { stream.Connected = false; Note(stream, "WebSocket chiuso"); }
        }
    }

    static void Note(Stream stream, string text)
    {
        stream.Log.AddLast(DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + text);
        while (stream.Log.Count > 40) stream.Log.RemoveFirst();
    }

    void Handle(Stream stream, string text)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { json = null; }
        lock (gate)
        {
            stream.Messages++;
            var type = json?["type"]?.ToString();
            switch (type)
            {
                case "stream_started":
                    stream.Started = json!["sessionId"]?.ToString();
                    stream.LastSequence = 0;
                    Note(stream, "stream_started ricevuto");
                    break;
                case "stream_stopped":
                    stream.Stopped = json!["reason"]?.ToString();
                    Note(stream, "stream_stopped ricevuto: " + stream.Stopped);
                    break;
                case "signal_status":
                    Note(stream, $"signal_status {json!["signalId"]}: {json["status"]} {json["error"]?["message"]}");
                    break;
                case "samples":
                    var sequence = json!["sequence"]?.GetValue<long>() ?? 0;
                    if (stream.LastSequence > 0 && sequence != stream.LastSequence + 1) { stream.Gaps++; Note(stream, $"Sequenza discontinua: {stream.LastSequence} → {sequence}"); }
                    if (stream.Samples == 0) Note(stream, "Primo campione ricevuto");
                    stream.LastSequence = sequence;
                    foreach (var s in json["samples"] as JsonArray ?? [])
                    {
                        stream.Samples++;
                        var signal = s?["signalId"]?.ToString() ?? "";
                        if (!stream.Points.TryGetValue(signal, out var points)) stream.Points[signal] = points = new();
                        var value = s?["value"] is JsonValue v && v.TryGetValue<double>(out var d) ? (object?)d : s?["value"] is JsonValue b && b.TryGetValue<bool>(out var flag) ? (flag ? 1.0 : 0.0) : null;
                        points.AddLast([s?["timestamp"]?.ToString(), value, s?["quality"]?.ToString()]);
                        while (points.Count > 2000) points.RemoveFirst();
                    }
                    break;
                default:
                    Note(stream, "Messaggio non riconosciuto");
                    break;
            }
        }
    }

    public Dictionary<string, object?> Describe(string id)
    {
        lock (gate)
        {
            if (!streams.TryGetValue(id, out var s)) return new() { ["connected"] = false, ["messages"] = 0L, ["samples"] = 0L, ["points"] = new Dictionary<string, object?>(), ["log"] = new List<object?>() };
            return new()
            {
                ["connected"] = s.Connected, ["messages"] = s.Messages, ["samples"] = s.Samples, ["gaps"] = s.Gaps, ["sequence"] = s.LastSequence,
                ["started"] = s.Started, ["stopped"] = s.Stopped,
                ["points"] = s.Points.ToDictionary(k => k.Key, k => (object?)k.Value.Select(p => (object?)p.ToList()).ToList()),
                ["log"] = s.Log.Cast<object?>().ToList(),
            };
        }
    }

    public void Clear(string id) { lock (gate) streams.Remove(id); }
}
