using System.Text.Json;
using CaptureSpike.Core;
using Xunit;

public sealed class ManualOffsetTests
{
    [Fact]
    public void EffectiveOffset_UsesManual_WhenSet()
    {
        var s = new TakeSource { OffsetMs = 123.4, ManualOffsetMs = 200.0 };
        Assert.Equal(200.0, s.EffectiveOffsetMs);
    }

    [Fact]
    public void EffectiveOffset_FallsBackToRecorded_WhenManualNull()
    {
        var s = new TakeSource { OffsetMs = 123.4, ManualOffsetMs = null };
        Assert.Equal(123.4, s.EffectiveOffsetMs);
    }

    [Fact]
    public void ManualOffset_RoundTrips_ThroughJson()
    {
        var s = new TakeSource
        {
            Id = "mic",
            Kind = SourceKind.Microphone,
            File = "mic.wav",
            OffsetMs = 123.4,
            ManualOffsetMs = 150.0,
        };
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<TakeSource>(json)!;
        Assert.Equal(150.0, back.ManualOffsetMs);
        Assert.Equal(123.4, back.OffsetMs);
        Assert.Equal(150.0, back.EffectiveOffsetMs);
    }

    [Fact]
    public void ManualOffset_Omitted_WhenNull()
    {
        var s = new TakeSource { Id = "mic", Kind = SourceKind.Microphone, File = "mic.wav", OffsetMs = 100 };
        var json = JsonSerializer.Serialize(s,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        // With null-ignoring options, manualOffsetMs should not appear.
        Assert.DoesNotContain("manualOffsetMs", json, StringComparison.OrdinalIgnoreCase);
    }
}
