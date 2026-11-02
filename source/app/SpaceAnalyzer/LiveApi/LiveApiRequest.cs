using System.Net;
using System.Text.Json.Nodes;
using SpaceAnalyzer.Engine.Drivers.S7;

namespace SpaceAnalyzer.App.LiveApi;

/// <summary>Expected error of the Live Acquisition API: returned as {"status":"error","error":{code,message,retryable}}.</summary>
public sealed class LiveApiException(string code, string message, int httpStatus, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
    public bool Retryable { get; } = retryable;

    public static LiveApiException Invalid(string message) => new("invalid_request", message, 400);
}

public sealed record LiveSignal(string SignalId, string Address, string DataType, string? Unit, string NodeId);

/// <summary>A validated POST /api/v1/live-sessions: signals translated into the node ids of the SFA drivers.</summary>
public sealed record LiveApiRequest(string RequestId, string SourceId, string Protocol, List<LiveSignal> Signals, int RequestedSamplingMs,
    int EffectiveSamplingMs, int TtlSeconds, Uri StreamUrl)
{
    /// <summary>Types accepted in dataType (S7 decodes them; for the other protocols they describe the value).</summary>
    public static readonly string[] DataTypes =
        ["BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT", "REAL", "LREAL", "TIME", "STRING"];

    static string Text(JsonObject body, string key, int max, bool required = true)
    {
        var node = body[key];
        if (node == null) return required ? throw LiveApiException.Invalid(key + " mancante") : "";
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) throw LiveApiException.Invalid(key + ": testo atteso");
        text = text.Trim();
        if (required && text.Length == 0) throw LiveApiException.Invalid(key + " vuoto");
        if (text.Length > max) throw LiveApiException.Invalid($"{key}: massimo {max} caratteri");
        if (text.Any(char.IsControl)) throw LiveApiException.Invalid(key + ": caratteri di controllo non ammessi");
        return text;
    }

    /// <summary>A JSON number, whatever its .NET backing (parsed text or a value built in code).</summary>
    public static bool TryNumber(JsonNode? node, out double number)
    {
        number = double.NaN;
        return node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    static double Number(JsonObject body, string key)
    {
        if (!TryNumber(body[key], out var number)) throw LiveApiException.Invalid(key + ": numero atteso");
        return number;
    }

    /// <summary>Hosts of the PC itself: always allowed (built-in test receiver).</summary>
    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    /// <param name="configured">Protocols of the saved SFA connection.</param>
    /// <param name="s7Mode">S7 mode of the saved connection (classic, symbolic, udp).</param>
    public static LiveApiRequest Parse(JsonNode? json, LiveApiSettings settings, IReadOnlySet<string> configured, string s7Mode)
    {
        if (json is not JsonObject body) throw LiveApiException.Invalid("Corpo JSON della richiesta non valido");
        var requestId = Text(body, "requestId", 128);
        var sourceId = Text(body, "sourceId", 64);
        var protocol = settings.SourceAliases.TryGetValue(sourceId, out var alias) ? alias : sourceId.ToLowerInvariant();
        if (!Engine.Sources.SourceController.Protocols.Contains(protocol) || !configured.Contains(protocol))
            throw new LiveApiException("source_not_found", $"Sorgente {sourceId} non configurata in Space Fieldbus Analyzer", 404);

        if (body["signals"] is not JsonArray list || list.Count == 0) throw LiveApiException.Invalid("signals: almeno un segnale");
        if (list.Count > settings.MaxSignals)
            throw LiveApiException.Invalid($"signals: massimo {settings.MaxSignals} segnali per sessione");
        var signals = new List<LiveSignal>();
        foreach (var item in list)
        {
            if (item is not JsonObject s) throw LiveApiException.Invalid("signals: oggetti attesi");
            var signalId = Text(s, "signalId", 128);
            var address = Text(s, "address", 256);
            var dataType = Text(s, "dataType", 32).ToUpperInvariant();
            var unit = Text(s, "unit", 32, required: false);
            if (signals.Any(x => x.SignalId == signalId)) throw LiveApiException.Invalid("signalId duplicato: " + signalId);
            var baseType = dataType.StartsWith("STRING[", StringComparison.Ordinal) && dataType.EndsWith(']') ? "STRING" : dataType;
            if (!DataTypes.Contains(baseType))
                throw new LiveApiException("signal_type_unsupported", $"Tipo {dataType} non supportato (tipi: {string.Join(", ", DataTypes)})", 400);
            signals.Add(new LiveSignal(signalId, address, dataType, unit.Length > 0 ? unit : null, NodeId(protocol, s7Mode, address, dataType)));
        }

        var period = Number(body, "samplingPeriodMs");
        if (period != Math.Floor(period) || period < 1 || period > 3_600_000)
            throw new LiveApiException("sampling_period_unsupported", "samplingPeriodMs: intero da 1 a 3600000", 400);
        var effective = (int)Math.Max(period, settings.MinSamplingMs);

        if (body["ttlSeconds"] == null) throw LiveApiException.Invalid("ttlSeconds obbligatorio");
        var ttl = Number(body, "ttlSeconds");
        if (ttl != Math.Floor(ttl) || ttl < 1 || ttl > settings.MaxTtlSeconds)
            throw LiveApiException.Invalid($"ttlSeconds: intero da 1 a {settings.MaxTtlSeconds}");

        if (body["streamTarget"] is not JsonObject target) throw new LiveApiException("invalid_stream_target", "streamTarget mancante", 400);
        var kind = Text(target, "protocol", 32).ToLowerInvariant();
        if (kind != "websocket") throw new LiveApiException("invalid_stream_target", "streamTarget.protocol: è supportato solo websocket", 400);
        var url = Text(target, "url", 2048);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new LiveApiException("invalid_stream_target", "streamTarget.url: indirizzo ws:// o wss:// senza credenziali", 400);
        if (!IsLoopback(uri.Host) && !settings.AllowedStreamTargets.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            throw new LiveApiException("invalid_stream_target", $"Destinazione {uri.Host} non autorizzata: aggiungerla agli host autorizzati di Space Fieldbus Analyzer", 400);

        return new LiveApiRequest(requestId, sourceId, protocol, signals, (int)period, effective, (int)ttl, uri);
    }

    /// <summary>Address and type of the request → node id of the SFA driver of that protocol (validated as the driver does).</summary>
    public static string NodeId(string protocol, string s7Mode, string address, string dataType)
    {
        static LiveApiException Bad(string message) => new("signal_address_invalid", message, 400);
        switch (protocol)
        {
            case "s7":
                if (s7Mode == "udp") throw Bad("S7 in modalità UDP: i dati arrivano dal PLC e non hanno indirizzi; usare la modalità classica o simbolica");
                if (s7Mode == "symbolic")
                {
                    var symbol = "s7:sym:" + address;
                    try { S7Types.ParseNode(symbol); } catch (ArgumentException exc) { throw Bad(exc.Message); }
                    return symbol;
                }
                if (address.Contains(':')) throw Bad("address senza tipo: il tipo va in dataType (ad esempio DB200.DBD56 e REAL)");
                try { return S7Types.ParseNode("s7:" + address.ToUpperInvariant() + ":" + dataType).NodeId; }
                catch (ArgumentException exc) { throw Bad($"{address} non può essere acquisito come {dataType}: {exc.Message}"); }
            case "ads":
                if (address.Any(char.IsWhiteSpace)) throw Bad("Simbolo ADS senza spazi (ad esempio MAIN.Temperatura)");
                return address.StartsWith("ads:", StringComparison.Ordinal) ? address : "ads:" + address;
            case "mqtt":
                return address.StartsWith("mqtt:", StringComparison.Ordinal) ? address : "mqtt:" + address;
            default:
                if (address.StartsWith("ads:", StringComparison.Ordinal) || address.StartsWith("s7:", StringComparison.Ordinal) || address.StartsWith("mqtt:", StringComparison.Ordinal))
                    throw Bad("NodeId OPC UA atteso (ad esempio ns=2;s=Temperatura)");
                return address;
        }
    }
}
