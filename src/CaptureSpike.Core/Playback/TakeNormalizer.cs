using System.Diagnostics;
using System.Globalization;
using System.Text;
using CaptureSpike.Core.Capture;

namespace CaptureSpike.Core.Playback;

/// <summary>
/// Normalizes a take's source files so the natural t=0 of each file
/// corresponds to its <see cref="TakeSource.EffectiveOffsetMs"/>.
/// In other words: a take that was "screen +120ms, mic -50ms" becomes
/// a take where screen.mkv starts 120ms later than the original
/// (the first 120ms trimmed) and mic.wav has 50ms of silence
/// prepended. After normalization, all per-source offsets reset to 0
/// and the files are already aligned — playback no longer needs to
/// seek each source independently.
///
/// Output is written to "&lt;name&gt;.normalized.&lt;ext&gt;" alongside the
/// original; the original is left untouched. <see cref="TakeSource.File"/>
/// is updated to point at the new file, the per-source offsets are
/// zeroed. take.json is the only metadata that changes.
///
/// Negative offsets (prepending) require re-encoding. Positive offsets
/// (trimming) use -c copy and are fast. A typical mixed take re-encodes
/// in seconds-to-minutes depending on length and resolution.
/// </summary>
public static class TakeNormalizer
{
    /// <summary>Progress callback: per-source step transitions.</summary>
    /// <param name="sourceId">Which source is being processed.</param>
    /// <param name="step">0..1 progress estimate, or null if unknown.</param>
    /// <param name="message">Human-readable status, e.g. "Trimming 120ms from screen.mkv".</param>
    public delegate void ProgressReporter(string sourceId, double? step, string message);

    public sealed record Result(
        IReadOnlyList<TakeSource> UpdatedSources,
        IReadOnlyList<string> Failed);

    /// <summary>
    /// Normalize every source in the take. Updates <see cref="TakeSource.File"/>
    /// to the new file name and zeroes the offsets. Does not write take.json
    /// — the caller does that after merging the updated sources back.
    /// </summary>
    public static async Task<Result> NormalizeAsync(
        string takeDir,
        Take take,
        ProgressReporter? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = ToolPaths.Resolve("ffmpeg");
        var updated = new List<TakeSource>(take.Sources.Count);
        var failed = new List<string>();

        foreach (var src in take.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(takeDir, src.File);
            if (!File.Exists(inputPath))
            {
                failed.Add($"{src.Id}: source file missing at {inputPath}");
                continue;
            }

            // Skip already-normalized files (idempotent: running twice is a no-op).
            if (src.File.Contains(".normalized."))
            {
                updated.Add(src);
                progress?.Invoke(src.Id, 1.0, $"{src.File} is already normalized; skipping.");
                continue;
            }

            // Build the output path: <basename>.normalized<ext>.
            var dir = Path.GetDirectoryName(inputPath) ?? takeDir;
            var name = Path.GetFileNameWithoutExtension(inputPath);
            var ext = Path.GetExtension(inputPath);
            var outputPath = Path.Combine(dir, $"{name}.normalized{ext}");

            // Same path for input and output? shouldn't happen, but bail safely.
            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                failed.Add($"{src.Id}: computed output path equals input path; refusing to overwrite.");
                continue;
            }

            var effectiveMs = src.EffectiveOffsetMs;
            try
            {
                if (Math.Abs(effectiveMs) < 0.5)
                {
                    // Effectively zero. Just copy the file as-is to .normalized.
                    progress?.Invoke(src.Id, 0.0,
                        $"{src.File} offset is 0; copying to .normalized.");
                    File.Copy(inputPath, outputPath, overwrite: true);
                }
                else if (effectiveMs > 0)
                {
                    // Trim the start: ffmpeg -ss <secs> -i input -c copy output
                    progress?.Invoke(src.Id, 0.0,
                        $"Trimming {effectiveMs:F0}ms from {src.File}.");
                    var args = BuildTrimArgs(inputPath, outputPath, effectiveMs / 1000.0);
                    var exitCode = await RunFfmpegAsync(ffmpeg, args, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException(
                            $"ffmpeg trim exited with code {exitCode} for {src.Id}.");
                }
                else
                {
                    // Prepend: |effectiveMs| of silence/black at the start.
                    var secs = -effectiveMs / 1000.0;
                    progress?.Invoke(src.Id, 0.0,
                        $"Prepending {secs * 1000:F0}ms to {src.File}.");
                    var args = BuildPrependArgs(inputPath, outputPath, secs);
                    var exitCode = await RunFfmpegAsync(ffmpeg, args, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException(
                            $"ffmpeg prepend exited with code {exitCode} for {src.Id}.");
                }

                // Pad/trim the .normalized output to match the input's
                // natural length. Without this, a positive offset produces
                // an output shorter than the input (e.g. trim 200ms off a
                // 9s clip → 8.8s), which the user can't mux back into a
                // 9s video. The original is left untouched so we can probe
                // its duration even if the offset was applied to a file
                // that no longer matches the original length.
                var inputDuration = await ProbeDurationAsync(ffmpeg, inputPath, ct);
                var normalizedDuration = await ProbeDurationAsync(ffmpeg, outputPath, ct);
                if (Math.Abs(normalizedDuration - inputDuration) > 0.01)
                {
                    progress?.Invoke(src.Id, 0.9,
                        $"Aligning length: {normalizedDuration:F2}s → {inputDuration:F2}s.");
                    await PadToDurationAsync(ffmpeg, outputPath, inputDuration, ct);
                }

                // Sanity check: did ffmpeg actually produce a non-empty output?
                var outputBytes = new FileInfo(outputPath).Length;
                if (outputBytes < 100)
                    throw new InvalidOperationException(
                        $"Normalized output is suspiciously small ({outputBytes} bytes).");

                // Build a new TakeSource pointing at the new file with zeroed offsets.
                updated.Add(new TakeSource
                {
                    Id = src.Id,
                    Kind = src.Kind,
                    File = Path.GetFileName(outputPath),
                    OffsetMs = 0,
                    ManualOffsetMs = null,
                    Width = src.Width,
                    Height = src.Height,
                });
                progress?.Invoke(src.Id, 1.0,
                    $"Normalized {src.Id} → {Path.GetFileName(outputPath)} ({outputBytes:N0} bytes).");
            }
            catch (Exception ex)
            {
                failed.Add($"{src.Id}: {ex.Message}");
                // Best-effort cleanup of partial output.
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }

        return new Result(updated, failed);
    }

    /// <summary>
    /// Fast trim using stream copy. -ss BEFORE -i = input seek (fast,
    /// but may snap to keyframes; close enough for our purposes since
    /// the offset was hand-tuned by the user).
    /// </summary>
    private static string[] BuildTrimArgs(string input, string output, double trimSeconds)
    {
        // ffmpeg -y -ss <secs> -i <input> -c copy <output>
        return new[]
        {
            "-y",
            "-ss", FormatSeconds(trimSeconds),
            "-i", input,
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            output,
        };
    }

    /// <summary>
    /// Prepend N seconds of silence (audio) and black (video) at the
    /// start. Requires re-encoding. Uses a filter graph so we don't
    /// need to know the input's resolution/audio rate ahead of time —
    /// we override them via the lavfi sources and let ffmpeg do the
    /// up/down conversion. This is the slowest case.
    /// </summary>
    private static string[] BuildPrependArgs(string input, string output, double seconds)
    {
        // ffmpeg -y \
        //   -f lavfi -i "anullsrc=channel_layout=stereo:sample_rate=48000" -t <s> \
        //   -f lavfi -i "color=black:s=1280x720:r=30" -t <s> \
        //   -i <input> \
        //   -filter_complex "[1:v][2:v]concat=n=2:v=1:a=0[v];[0:a][2:a]concat=n=2:v=0:a=1[a]" \
        //   -map "[v]" -map "[a]" -c:v libx264 -pix_fmt yuv420p -preset ultrafast -c:a aac \
        //   -movflags +faststart <output>
        var secStr = FormatSeconds(seconds);
        return new[]
        {
            "-y",
            "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000", "-t", secStr,
            "-f", "lavfi", "-i", "color=black:s=1280x720:r=30", "-t", secStr,
            "-i", input,
            "-filter_complex", "[1:v][2:v]concat=n=2:v=1:a=0[v];[0:a][2:a]concat=n=2:v=0:a=1[a]",
            "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast",
            "-c:a", "aac",
            "-shortest", // don't extend past input
            "-movflags", "+faststart",
            output,
        };
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Run ffmpeg to read a file's duration, parse the "Duration: HH:MM:SS.MS"
    /// line from stderr. Throws on failure or unparseable output. Used
    /// to learn the input's natural length so the normalized output
    /// can be padded/trimmed back to that length — otherwise trim
    /// makes the output shorter than the video and the user can't
    /// mux the files together.
    /// </summary>
    private static async Task<double> ProbeDurationAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -i <input> -f null -  asks ffmpeg to demux the file and
        // throw the result away, but still print the container's
        // metadata to stderr.
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                stderr.AppendLine(line);
        }, ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        try { await stderrTask.ConfigureAwait(false); } catch { }
        // Expect exit code 1 (ffmpeg "fails" to encode to /dev/null)
        // but still prints the metadata. Don't check the exit code
        // — what matters is the Duration line.

        var m = System.Text.RegularExpressions.Regex.Match(
            stderr.ToString(), @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
        if (!m.Success)
            throw new InvalidOperationException(
                $"Could not parse duration from ffmpeg output for {input}.");
        var hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    /// <summary>
    /// Pad/trim a file in place to exactly <paramref name="targetSeconds"/>.
    /// Pads audio with silence (apad) and video with cloned last
    /// frame (tpad); truncates with -t. Works for audio-only files
    /// (apad only) and video files (apad + tpad). Writes to a
    /// temp file, then atomically moves into place.
    /// </summary>
    private static async Task PadToDurationAsync(string ffmpeg, string path, double targetSeconds, CancellationToken ct)
    {
        var tempPath = path + ".padtmp";
        var secStr = FormatSeconds(targetSeconds);
        // We branch on whether the input has a video stream. A
        // single filter graph that applies both tpad and apad
        // works for video files but fails (exit -22 on some
        // ffmpeg builds) when the input is audio-only because
        // the tpad filter requires a [0:v] stream that doesn't
        // exist. So: probe the input, then build args that
        // only touch the streams that are actually present.
        var probePsi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        probePsi.ArgumentList.Add("-i");
        probePsi.ArgumentList.Add(path);
        var probe = Process.Start(probePsi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var probeStderr = new StringBuilder();
        var probeTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await probe.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                probeStderr.AppendLine(line);
        }, ct);
        await probe.WaitForExitAsync(ct).ConfigureAwait(false);
        try { await probeTask.ConfigureAwait(false); } catch { }
        var hasVideo = probeStderr.ToString().Contains("Video:", StringComparison.Ordinal);

        var args = new List<string> { "-y", "-i", path };
        // Audio: pad to target. apad's whole_dur pads if shorter,
        // no-op if longer; the -t below truncates any overshoot.
        args.Add("-af"); args.Add($"apad=whole_dur={secStr}");
        if (hasVideo)
        {
            // Video: tpad with clone stop mode fills missing
            // frames with the last frame.
            args.Add("-vf"); args.Add($"tpad=stop_mode=clone:stop_duration={secStr}");
            args.Add("-c:v"); args.Add("libx264");
            args.Add("-pix_fmt"); args.Add("yuv420p");
            args.Add("-preset"); args.Add("ultrafast");
            args.Add("-movflags"); args.Add("+faststart");
        }
        // Truncate to the target in case of any overshoot.
        args.Add("-t"); args.Add(secStr);
        // Match input codec: pcm for wav. Don't specify a video
        // codec for audio-only files (we'd hit "video encoder not
        // found" or similar when there's no video stream).
        args.Add("-c:a"); args.Add("pcm_s16le");
        args.Add(tempPath);

        try
        {
            var exitCode = await RunFfmpegAsync(ffmpeg, args.ToArray(), ct);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg pad exited with code {exitCode}.");
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Run ffmpeg with the given args, capture stderr, return exit code.
    /// Throws OperationCanceledException if the token fires.
    /// </summary>
    private static async Task<int> RunFfmpegAsync(string ffmpeg, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        // Drain stderr asynchronously; if it grows huge that's fine.
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                stderr.AppendLine(line);
                if (stderr.Length > 64 * 1024) stderr.Length = 64 * 1024; // cap
            }
        }, ct);

        await using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        try { await stderrTask.ConfigureAwait(false); } catch { }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        return process.ExitCode;
    }
}