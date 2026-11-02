using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SpaceAnalyzer.App.Infrastructure;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>
/// Settings of the Live Acquisition API (live_api.json in the data folder): listening port, API key, authorized stream targets,
/// source aliases and limits. Only settings: the live sessions themselves are never saved.
/// </summary>
public sealed class LiveApiSettings
{
    public int Port { get; set; } = 5180;
    /// <summary>Address the API listens on: "0.0.0.0" (every network card) or one address of the PC.</summary>
    public string Bind { get; set; } = "0.0.0.0";
    public bool RequireKey { get; set; } = true;
    public string ApiKey { get; set; } = NewKey();
    /// <summary>Hosts (names or addresses) the API may open a WebSocket to; the PC itself is always allowed (built-in test receiver).</summary>
    public List<string> AllowedStreamTargets { get; set; } = new();
    /// <summary>sourceId of the requests → protocol of the saved connection (e.g. PLC01 → s7); the protocol names are always accepted.</summary>
    public Dictionary<string, string> SourceAliases { get; set; } = new();
    public int MaxSessions { get; set; } = 8;
    public int MaxSignals { get; set; } = 64;
    public int MinSamplingMs { get; set; } = 10;
    public int MaxTtlSeconds { get; set; } = 86400;
    public int ReconnectDelayMs { get; set; } = 1000;
    public int ReconnectMaxAttempts { get; set; } = 10;
    public int ReconnectTimeoutSeconds { get; set; } = 120;
    /// <summary>Interval of the "samples" messages (each carries the samples read meanwhile).</summary>
    public int BatchMs { get; set; } = 100;

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string FilePath(string dataDir) => Path.Combine(dataDir, "live_api.json");

    public static LiveApiSettings Load(string dataDir)
    {
        var settings = new LiveApiSettings();
        var path = FilePath(dataDir);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
            {
                settings.Apply(saved, partial: true);
                if (saved["api_key"] == null) settings.Save(dataDir);   // the generated key must stay the same across restarts
            }
            else settings.Save(dataDir);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or UserError or InvalidOperationException)
        {
            Log.Warning("Impostazioni Live Acquisition API non lette, uso i valori predefiniti: " + exc.Message);
        }
        return settings;
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        Py.AtomicPlain(FilePath(dataDir), ToPlain(includeKey: true));
    }

    /// <summary>Validated update from the monitor page (or the saved file when <paramref name="partial"/>).</summary>
    public void Apply(JsonObject data, bool partial = false)
    {
        int Int(string key, int current, int min, int max)
        {
            if (data[key] is null) return current;
            if (!LiveApiRequest.TryNumber(data[key], out var d) || d != Math.Floor(d) || d < min || d > max)
                throw new UserError($"{key}: valore intero tra {min} e {max}");
            return (int)d;
        }
        Port = Int("port", Port, 1024, 65535);
        MaxSessions = Int("max_sessions", MaxSessions, 1, 64);
        MaxSignals = Int("max_signals", MaxSignals, 1, 256);
        MinSamplingMs = Int("min_sampling_ms", MinSamplingMs, 1, 60000);
        MaxTtlSeconds = Int("max_ttl_seconds", MaxTtlSeconds, 10, 7 * 86400);
        ReconnectDelayMs = Int("reconnect_delay_ms", ReconnectDelayMs, 100, 60000);
        ReconnectMaxAttempts = Int("reconnect_max_attempts", ReconnectMaxAttempts, 0, 1000);
        ReconnectTimeoutSeconds = Int("reconnect_timeout_seconds", ReconnectTimeoutSeconds, 1, 3600);
        BatchMs = Int("batch_ms", BatchMs, 20, 5000);
        if (data["bind"] is JsonValue bind)
        {
            var text = bind.ToString().Trim();
            if (!IPAddress.TryParse(text, out _)) throw new UserError("Indirizzo di ascolto non valido (ad esempio 0.0.0.0)");
            Bind = text;
        }
        if (data["require_key"] is JsonValue require && require.TryGetValue<bool>(out var r)) RequireKey = r;
        if (data["api_key"] is JsonValue key)
        {
            var text = key.ToString().Trim();
            if (text.Length < 16 || text.Length > 200 || text.Any(char.IsWhiteSpace)) throw new UserError("Chiave API: da 16 a 200 caratteri, senza spazi");
            ApiKey = text;
        }
        if (data["allowed_stream_targets"] is JsonArray targets)
        {
            var list = targets.Select(t => (t?.ToString() ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 64 || list.Any(t => t.Length > 253 || t.Contains('/') || t.Any(char.IsWhiteSpace)))
                throw new UserError("Host autorizzati: nomi o indirizzi senza schema né percorso (massimo 64)");
            AllowedStreamTargets = list;
        }
        if (data["source_aliases"] is JsonObject aliases)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, protocol) in aliases)
            {
                var name = alias.Trim();
                var target = (protocol?.ToString() ?? "").Trim().ToLowerInvariant();
                if (name.Length is 0 or > 64) throw new UserError("Alias sorgente: da 1 a 64 caratteri");
                if (!Engine.Sources.SourceController.Protocols.Contains(target)) throw new UserError($"Alias {name}: protocollo {target} non valido (opcua, ads, mqtt, s7)");
                map[name] = target;
            }
            SourceAliases = map;
        }
        if (!partial && RequireKey && ApiKey.Length < 16) throw new UserError("Chiave API mancante");
    }

    public Dictionary<string, object?> ToPlain(bool includeKey) => new()
    {
        ["port"] = (long)Port, ["bind"] = Bind, ["require_key"] = RequireKey, ["api_key"] = includeKey ? ApiKey : null,
        ["allowed_stream_targets"] = AllowedStreamTargets.Cast<object?>().ToList(),
        ["source_aliases"] = SourceAliases.ToDictionary(k => k.Key, k => (object?)k.Value),
        ["max_sessions"] = (long)MaxSessions, ["max_signals"] = (long)MaxSignals, ["min_sampling_ms"] = (long)MinSamplingMs,
        ["max_ttl_seconds"] = (long)MaxTtlSeconds, ["reconnect_delay_ms"] = (long)ReconnectDelayMs, ["reconnect_max_attempts"] = (long)ReconnectMaxAttempts,
        ["reconnect_timeout_seconds"] = (long)ReconnectTimeoutSeconds, ["batch_ms"] = (long)BatchMs,
    };

    public LiveApiSettings Clone()
    {
        var copy = new LiveApiSettings();
        copy.Apply((JsonObject)JsonNode.Parse(Py.DumpsPlain(ToPlain(true)))!, partial: true);
        return copy;
    }
}
