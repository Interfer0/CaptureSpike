// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core.Capture;

/// <summary>Thrown when a source can't be captured (missing device, bad args...).</summary>
public sealed class CaptureException(string sourceId, string message) : Exception(message)
{
    public string SourceId { get; } = sourceId;
}

/// <summary>
/// Records one source into one file. Implementations measure when the first
/// real data arrives so the take can align tracks by offset instead of
/// assuming every source started at t0.
/// </summary>
public interface ISourceCapturer : IAsyncDisposable
{
    string SourceId { get; }
    SourceKind Kind { get; }
    string OutputFile { get; }
    DateTimeOffset? FirstDataAt { get; }

    /// <summary>
    /// Starts capture. Throws <see cref="CaptureException"/> if the source
    /// fails fast (e.g. no such device) instead of recording silence.
    /// </summary>
    Task StartAsync(DateTimeOffset t0, CancellationToken ct);

    /// <summary>Stops capture and finalizes the output file.</summary>
    Task StopAsync(CancellationToken ct);
}
