using System.Text.Json.Nodes;
using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.App.LiveApi;
using SpaceAnalyzer.Engine.Core;
using Xunit;

namespace SpaceAnalyzer.Tests;

/// <summary>Live Acquisition API: validazione delle richieste, traduzione degli indirizzi, impostazioni, diradamento dei campioni.</summary>
public class LiveApiTests
{
    static readonly HashSet<string> S7 = ["s7"];

    static JsonObject Body(Action<JsonObject>? change = null)
    {
        var body = (JsonObject)JsonNode.Parse("""
        {"requestId":"test-8f21","sourceId":"PLC01","signals":[{"signalId":"pyrometer-entry-stand-1","address":"DB200.DBD56","dataType":"REAL","unit":"°C"}],
         "samplingPeriodMs":250,"ttlSeconds":1800,"streamTarget":{"protocol":"websocket","url":"ws://TEST-PC:8080/api/live-streams/test-8f21"}}
        """)!;
        change?.Invoke(body);
        return body;
    }

    static LiveApiSettings Settings()
    {
        var s = new LiveApiSettings();
        s.Apply(new JsonObject { ["allowed_stream_targets"] = new JsonArray("TEST-PC"), ["source_aliases"] = new JsonObject { ["PLC01"] = "s7" } });
        return s;
    }

    static string Code(Action action) => Assert.Throws<LiveApiException>(action).Code;

    [Fact]
    public void TheRequestOfTheSpecificationIsAccepted()
    {
        var r = LiveApiRequest.Parse(Body(), Settings(), S7, "classic");
        Assert.Equal("test-8f21", r.RequestId);
        Assert.Equal("s7", r.Protocol);
        Assert.Equal("s7:DB200.DBD56:REAL", Assert.Single(r.Signals).NodeId);
        Assert.Equal("°C", r.Signals[0].Unit);
        Assert.Equal(250, r.RequestedSamplingMs);
        Assert.Equal(250, r.EffectiveSamplingMs);
        Assert.Equal(1800, r.TtlSeconds);
        Assert.Equal("test-pc", r.StreamUrl.Host);
    }

    [Fact]
    public void FasterThanTheMinimumIsRaisedAndReported()
    {
        var r = LiveApiRequest.Parse(Body(b => b["samplingPeriodMs"] = 2), Settings(), S7, "classic");
        Assert.Equal(2, r.RequestedSamplingMs);
        Assert.Equal(10, r.EffectiveSamplingMs);
    }

    [Theory]
    [InlineData("DB1.DBX0.0", "BOOL", "s7:DB1.DBX0.0:BOOL")]
    [InlineData("DB1.DBB2", "BYTE", "s7:DB1.DBB2:BYTE")]
    [InlineData("DB1.DBW4", "WORD", "s7:DB1.DBW4:WORD")]
    [InlineData("DB1.DBD8", "DWORD", "s7:DB1.DBD8:DWORD")]
    [InlineData("DB1.DBW4", "INT", "s7:DB1.DBW4:INT")]
    [InlineData("db1.dbd8", "dint", "s7:DB1.DBD8:DINT")]
    [InlineData("MW10", "INT", "s7:MW10:INT")]
    public void S7AddressesOfTheSpecification(string address, string type, string node) =>
        Assert.Equal(node, LiveApiRequest.Parse(Body(b => { b["signals"]![0]!["address"] = address; b["signals"]![0]!["dataType"] = type; }), Settings(), S7, "classic").Signals[0].NodeId);

    [Fact]
    public void TheTypeIsNotInferredFromTheAddress()
    {
        Assert.Equal("signal_address_invalid", Code(() => LiveApiRequest.Parse(Body(b => b["signals"]![0]!["dataType"] = "BOOL"), Settings(), S7, "classic")));
        Assert.Equal("signal_address_invalid", Code(() => LiveApiRequest.Parse(Body(b => b["signals"]![0]!["address"] = "DB200.DBD56:REAL"), Settings(), S7, "classic")));
        Assert.Equal("signal_type_unsupported", Code(() => LiveApiRequest.Parse(Body(b => b["signals"]![0]!["dataType"] = "FLOAT"), Settings(), S7, "classic")));
        Assert.Equal("signal_address_invalid", Code(() => LiveApiRequest.Parse(Body(), Settings(), S7, "udp")));
        Assert.Equal("s7:sym:Motore.Velocita", LiveApiRequest.Parse(Body(b => b["signals"]![0]!["address"] = "Motore.Velocita"), Settings(), S7, "symbolic").Signals[0].NodeId);
    }

    [Fact]
    public void OtherProtocolsUseTheirOwnAddresses()
    {
        var all = new HashSet<string> { "opcua", "ads", "mqtt", "s7" };
        Assert.Equal("ads:MAIN.Wave", LiveApiRequest.Parse(Body(b => { b["sourceId"] = "ads"; b["signals"]![0]!["address"] = "MAIN.Wave"; }), Settings(), all, "classic").Signals[0].NodeId);
        Assert.Equal("ns=2;s=Temperature", LiveApiRequest.Parse(Body(b => { b["sourceId"] = "opcua"; b["signals"]![0]!["address"] = "ns=2;s=Temperature"; }), Settings(), all, "classic").Signals[0].NodeId);
        Assert.Equal("mqtt:plant/motor#/load", LiveApiRequest.Parse(Body(b => { b["sourceId"] = "MQTT"; b["signals"]![0]!["address"] = "plant/motor#/load"; }), Settings(), all, "classic").Signals[0].NodeId);
    }

    [Fact]
    public void ErrorsHaveTheCodesOfTheSpecification()
    {
        var s = Settings();
        Assert.Equal("source_not_found", Code(() => LiveApiRequest.Parse(Body(b => b["sourceId"] = "PLC99"), s, S7, "classic")));
        Assert.Equal("source_not_found", Code(() => LiveApiRequest.Parse(Body(b => b["sourceId"] = "ads"), s, S7, "classic")));   // protocol not in the saved connection
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(Body(b => b.Remove("ttlSeconds")), s, S7, "classic")));
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(Body(b => b["ttlSeconds"] = 999999), s, S7, "classic")));
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(Body(b => b["signals"] = new JsonArray()), s, S7, "classic")));
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(Body(b => ((JsonArray)b["signals"]!).Add(b["signals"]![0]!.DeepClone())), s, S7, "classic")));
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(JsonNode.Parse("[]"), s, S7, "classic")));
        Assert.Equal("sampling_period_unsupported", Code(() => LiveApiRequest.Parse(Body(b => b["samplingPeriodMs"] = 0), s, S7, "classic")));
        Assert.Equal("sampling_period_unsupported", Code(() => LiveApiRequest.Parse(Body(b => b["samplingPeriodMs"] = 12.5), s, S7, "classic")));
        Assert.Equal("invalid_stream_target", Code(() => LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "ws://OTHER-PC:8080/x"), s, S7, "classic")));
        Assert.Equal("invalid_stream_target", Code(() => LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "http://TEST-PC:8080/x"), s, S7, "classic")));
        Assert.Equal("invalid_stream_target", Code(() => LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "ws://user:pw@TEST-PC/x"), s, S7, "classic")));
        Assert.Equal("invalid_stream_target", Code(() => LiveApiRequest.Parse(Body(b => b["streamTarget"]!["protocol"] = "mqtt"), s, S7, "classic")));
        var tooMany = new JsonArray(Enumerable.Range(0, 65).Select(i => (JsonNode?)new JsonObject { ["signalId"] = "s" + i, ["address"] = "DB1.DBD" + i * 4, ["dataType"] = "REAL" }).ToArray());
        Assert.Equal("invalid_request", Code(() => LiveApiRequest.Parse(Body(b => b["signals"] = tooMany), s, S7, "classic")));
        // The PC itself is always an allowed target (built-in test receiver); wss is accepted.
        Assert.NotNull(LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "ws://127.0.0.1:5180/api/v1/test-receiver/x"), s, S7, "classic"));
        Assert.NotNull(LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "wss://test-pc/x"), s, S7, "classic"));
    }

    [Fact]
    public void SettingsAreValidatedAndKeptWithTheirKey()
    {
        using var dir = new TempDir();
        {
            var paths = dir.Path;
            var first = LiveApiSettings.Load(paths);
            Assert.True(first.ApiKey.Length >= 16);
            Assert.True(File.Exists(LiveApiSettings.FilePath(paths)));
            Assert.Equal(first.ApiKey, LiveApiSettings.Load(paths).ApiKey);   // the generated key survives a restart
            var s = first.Clone();
            Assert.Throws<UserError>(() => s.Apply(new JsonObject { ["port"] = 80 }));
            Assert.Throws<UserError>(() => s.Apply(new JsonObject { ["bind"] = "pc" }));
            Assert.Throws<UserError>(() => s.Apply(new JsonObject { ["source_aliases"] = new JsonObject { ["PLC01"] = "profinet" } }));
            Assert.Throws<UserError>(() => s.Apply(new JsonObject { ["allowed_stream_targets"] = new JsonArray("http://pc/x") }));
            Assert.Throws<UserError>(() => s.Apply(new JsonObject { ["api_key"] = "short" }));
            s.Apply(new JsonObject { ["port"] = 6000, ["allowed_stream_targets"] = new JsonArray("TEST-PC", "test-pc", "10.0.0.5"), ["max_sessions"] = 2 });
            s.Save(paths);
            var loaded = LiveApiSettings.Load(paths);
            Assert.Equal(6000, loaded.Port);
            Assert.Equal(["TEST-PC", "10.0.0.5"], loaded.AllowedStreamTargets);
            Assert.Equal(2, loaded.MaxSessions);
        }
    }

    [Fact]
    public void SamplesAreThinnedAndQualityChangesAlwaysPass()
    {
        var request = LiveApiRequest.Parse(Body(b => b["streamTarget"]!["url"] = "ws://127.0.0.1:1/x"), Settings(), S7, "classic");
        var session = new LiveSession(request, "test", () => new LiveApiSettings(), _ => "connected", _ => { });
        Sample Row(object? value, string quality = "Good") => new() { NodeId = "s7:DB200.DBD56:REAL", Value = value, Good = quality == "Good", Quality = quality };
        // Not connected yet: the last values are tracked for the monitor but nothing is queued.
        session.Offer(1000, [Row(1.0)], 10);
        var monitor = session.Monitor();
        var signal = (Dictionary<string, object?>)((List<object?>)monitor["signals"]!)[0]!;
        Assert.Equal(1.0, signal["lastValue"]);
        Assert.Equal("good", signal["lastQuality"]);
        session.Offer(1100, [Row(2.0)], 10);   // inside the 250 ms period: skipped
        signal = (Dictionary<string, object?>)((List<object?>)session.Monitor()["signals"]!)[0]!;
        Assert.Equal(1.0, signal["lastValue"]);
        session.Offer(1150, [Row(null, "Bad")], 10);   // a change of quality passes at once, without a value
        signal = (Dictionary<string, object?>)((List<object?>)session.Monitor()["signals"]!)[0]!;
        Assert.Null(signal["lastValue"]);
        Assert.Equal("bad", signal["lastQuality"]);
        session.Offer(1245, [Row(3.0)], 10);
        signal = (Dictionary<string, object?>)((List<object?>)session.Monitor()["signals"]!)[0]!;
        Assert.Equal(3.0, signal["lastValue"]);
        session.Offer(1300, [new Sample { NodeId = "s7:DB1.DBD0:REAL", Value = 9.0, Good = true, Quality = "Good" }], 10);   // another signal: ignored
        Assert.Equal(3.0, ((Dictionary<string, object?>)((List<object?>)session.Monitor()["signals"]!)[0]!)["lastValue"]);
    }
}
