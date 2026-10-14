// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core;
using CaptureSpike.Core.Capture;
using CaptureSpike.Core.Playback;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests that the playback graph honours ManualOffsetMs.
///
/// TakeSource.EffectiveOffsetMs is the documented contract: "playback
/// uses manual if set, else recorded", and AGENTS.md names it as such.
/// TakeNormalizer already read it. FilterGraphBuilder did not - it read
/// the raw recorded OffsetMs everywhere, so the offset control in the
/// UI changed the status text and the normalizer's trim but not the
/// playback graph at all. The user moved a slider and nothing happened,
/// on every platform.
///
/// ManualOffsetTests covers the EffectiveOffsetMs property itself. That
/// was the gap: the property was correct and correct, and nothing
/// checked that anything consumed it.
/// </summary>
public sealed class ManualOffsetGraphTests
{
    private static List<TakeSource> TwoVideosAndTwoAudios(
        double screenMs, double? screenManual = null)
    {
        return
        [
            new() { Id = "screen", Kind = SourceKind.Screen, File = "s.mkv",
                    OffsetMs = screenMs, ManualOffsetMs = screenManual },
            new() { Id = "cam", Kind = SourceKind.Webcam, File = "c.mkv", OffsetMs = 50 },
            new() { Id = "mic", Kind = SourceKind.Microphone, File = "m.wav", OffsetMs = 30 },
            new() { Id = "sys", Kind = SourceKind.SystemAudio, File = "sa.wav", OffsetMs = 80 },
        ];
    }

    [Fact]
    public void BuildTakeGraph_UsesManualOffset_WhenSet()
    {
        // Recorded 100ms, but the user nudged it to 300ms. With
        // normalization against the smallest source (mic @ 30ms)
        // the screen pad should be 300-30 = 270ms, not 100-30 = 70ms.
        var graph = FilterGraphBuilder.BuildTakeGraph(
            TwoVideosAndTwoAudios(screenMs: 100, screenManual: 300));

        Assert.Contains("PTS+0.270/TB", graph);
        // And must not still be emitting the recorded value.
        Assert.DoesNotContain("PTS+0.070/TB", graph);
    }

    [Fact]
    public void BuildTakeGraph_UsesRecordedOffset_WhenManualNull()
    {
        var graph = FilterGraphBuilder.BuildTakeGraph(
            TwoVideosAndTwoAudios(screenMs: 100, screenManual: null));

        Assert.Contains("PTS+0.070/TB", graph);
    }

    [Fact]
    public void BuildTakeGraph_NormalizesAgainstTheEarliestEffectiveOffset()
    {
        // The baseline itself has to come from EffectiveOffsetMs, not
        // OffsetMs. If the earliest source carries a manual offset the
        // whole graph would shift, because the minimum is subtracted
        // from every other track.
        var sources = new List<TakeSource>
        {
            new() { Id = "screen", Kind = SourceKind.Screen, File = "s.mkv",
                    OffsetMs = 10, ManualOffsetMs = 500 },
            new() { Id = "cam", Kind = SourceKind.Webcam, File = "c.mkv", OffsetMs = 900 },
        };

        var graph = FilterGraphBuilder.BuildTakeGraph(sources);

        // min is 500 (manual), so cam is 900-500 = 400ms and screen
        // is the origin at 0.
        Assert.Contains("PTS+0.400/TB", graph);
        Assert.Contains("PTS+0.000/TB", graph);
    }

    [Fact]
    public void BuildTakeGraph_AppliesManualOffsetToAudioToo()
    {
        // adelay is driven by the same minimum, so a manual offset on
        // an audio source has to move its delay too.
        var sources = new List<TakeSource>
        {
            new() { Id = "screen", Kind = SourceKind.Screen, File = "s.mkv", OffsetMs = 0 },
            new() { Id = "mic", Kind = SourceKind.Microphone, File = "m.wav",
                    OffsetMs = 20, ManualOffsetMs = 520 },
        };

        var graph = FilterGraphBuilder.BuildTakeGraph(sources);

        // mic is the earliest by effective offset (520), so screen is
        // the origin. If the graph still used OffsetMs the minimum
        // would be 0 and mic would be delayed by 20ms instead of 0.
        Assert.DoesNotContain("adelay=delays=20", graph);
    }
}