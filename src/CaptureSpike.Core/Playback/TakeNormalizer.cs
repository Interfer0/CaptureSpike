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
/// prepended. After normalization, the per-source OffsetMs and
/// ManualOffsetMs in take.json are PRESERVED (not zeroed) — the
/// offset is baked into the on-disk content, so re-applying it via
/// the JSON would double the effect. The files are already
/// aligned — playback no longer needs to
/// seek each source independently. Unnormalizing (restoring the
/// original File pointer) leaves the take exactly as it was.
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

        // Probe every input's natural duration up front. The pad
        // target for the whole take is the LONGEST input. With
        // parallel recording, sources are usually the same length
        // (within a few ms) — but a slow-starting mic, a screen
        // capture that started late, or a tap that took a moment
        // to begin can leave one source a few hundred ms short.
        // Without a common target, each normalized file keeps its
        // own input length and the user can't mux the take without
        // hand-padding. With the longest as target, we pad the
        // shorter ones to match — no fabrication, no truncation.
        var targetSeconds = await ResolveTargetDurationAsync(ffmpeg, take, takeDir, ct);

        // Compute the trim anchor: the take-time at which every
        // normalized file should begin. We start with the take's
        // record-time offsets (EffectiveOffsetMs) and then refine
        // for audio sources by detecting the first NON-SILENT
        // sample, not just the first-data time. The recorder's
        // first-data event fires on the first audio buffer
        // callback — which on WASAPI happens within a few ms of
        // capture start but well before the user starts talking.
        // Without this refinement, an audio file that opened
        // with 800ms of room-tone silence would still be trimmed
        // to start at its first-data time, leaving a visible
        // 800ms gap between sources after normalize. With the
        // first-non-silent detection, the trim anchor is the
        // latest "actual sound" across the take — every file's
        // t=0 lines up with real audio content.
        //
        // Returns the per-source first-non-silent offset
        // (in ms, relative to the take's t0). For video, this is
        // just EffectiveOffsetMs (cameras don't buffer silence).
        // For audio, it's the first time the audio exceeds the
        // silence threshold, measured from the file's own t=0
        // and translated back to take-time using EffectiveOffsetMs.
        var perSourceFirstNonSilentMs = await ResolveFirstNonSilentMsAsync(ffmpeg, take, takeDir, ct);
        var maxFirstNonSilentMs = perSourceFirstNonSilentMs.Values.DefaultIfEmpty(0).Max();
        // Diagnostic: surface the per-source first-non-silent
        // times so the user can see what each source was
        // detected at, and what the trim anchor ended up being.
        // Helps confirm the right sources went into the max
        // calculation (e.g. a source whose first-non-silent is
        // 0 because the file is all silence shouldn't lower
        // the max).
        foreach (var src in take.Sources)
        {
            if (perSourceFirstNonSilentMs.TryGetValue(src.Id, out var ms))
            {
                progress?.Invoke(src.Id, null,
                    $"First-non-silent: {ms:F0}ms (record-time offset was {src.EffectiveOffsetMs:F0}ms).");
            }
        }
        // Diagnostic: surface the computed max so the user can
        // see what value the code decided on.
        progress?.Invoke("", null,
            $"Normalize plan: trim {maxFirstNonSilentMs:F0}ms (max first-non-silent), pad to {targetSeconds:F2}s (longest input).");
        // maxOffsetMs (the old variable) is replaced by
        // maxFirstNonSilentMs in the per-source trim below. The
        // old field stays as maxOffsetMs so the rest of the
        // function is unchanged.
        var maxOffsetMs = maxFirstNonSilentMs;

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

            try
            {
                // Trim the file so its content starts at the same
                // take-time across every source in the take. The
                // amount trimmed is the MAX offset across the take
                // (not this source's own offset). A source that
                // started earlier (smaller offset) loses that much
                // extra silence/no-data to the trim; a source that
                // started exactly at maxOffset keeps its full first
                // data. After trim, every file's t=0 corresponds to
                // the same take-time, and the player can play from
                // t=0 with no per-source seek — all content aligns.
                var trimMs = maxOffsetMs;
                if (trimMs < 0.5)
                {
                    // maxOffset is 0 (or all sources are missing).
                    // Just copy the file as-is to .normalized.
                    progress?.Invoke(src.Id, 0.0,
                        $"{src.File} no trim needed; copying to .normalized.");
                    File.Copy(inputPath, outputPath, overwrite: true);
                }
                else
                {
                    progress?.Invoke(src.Id, 0.0,
                        $"Trimming {trimMs:F0}ms from {src.File} (align to take's max offset).");
                    var args = BuildTrimArgs(inputPath, outputPath, trimMs / 1000.0,
                        src.Kind.IsVideo());
                    var exitCode = await RunFfmpegAsync(ffmpeg, args, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException(
                            $"ffmpeg trim exited with code {exitCode} for {src.Id}.");
                }

                // Pad/trim the .normalized output to the take's
                // common target length (the longest input across
                // every source in the take, computed once at the
                // start). Using a common target means every file
                // in the take ends up the same length so the user
                // can mux them without hand-padding.
                var inputDuration = await ProbeDurationAsync(ffmpeg, inputPath, ct);
                if (Math.Abs(inputDuration - targetSeconds) > 0.01)
                {
                    progress?.Invoke(src.Id, 0.9,
                        $"Aligning length: {inputDuration:F2}s → {targetSeconds:F2}s.");
                    // Log the input audio format BEFORE the pad so
                    // we can confirm the pad preserved it.
                    var inputFmt = await ProbeAudioFormatAsync(ffmpeg, inputPath, ct);
                    progress?.Invoke(src.Id, 0.85,
                        $"Input format: {inputFmt}.");
                    await PadToDurationAsync(ffmpeg, outputPath, targetSeconds, ct);
                    // Diagnostic: confirm the pad hit the target
                    // AND preserved the input's bit rate. If the
                    // output format differs, surface that too.
                    var afterDuration = await ProbeDurationAsync(ffmpeg, outputPath, ct);
                    var outputFmt = await ProbeAudioFormatAsync(ffmpeg, outputPath, ct);
                    progress?.Invoke(src.Id, 0.95,
                        $"Output format: {outputFmt}.");
                    if (Math.Abs(afterDuration - targetSeconds) > 0.05)
                    {
                        progress?.Invoke(src.Id, 0.95,
                            $"Pad did not reach target: actual {afterDuration:F2}s, expected {targetSeconds:F2}s.");
                    }
                }
                else
                {
                    // This source's input is already the longest
                    // in the take — but the trim/prepend might
                    // have made the output slightly different
                    // (e.g. keyframe snap on video). Probe the
                    // output and pad if it's short of the target.
                    var afterDuration = await ProbeDurationAsync(ffmpeg, outputPath, ct);
                    if (Math.Abs(afterDuration - targetSeconds) > 0.05)
                    {
                        progress?.Invoke(src.Id, 0.9,
                            $"Aligning length (longest source): {afterDuration:F2}s → {targetSeconds:F2}s.");
                        await PadToDurationAsync(ffmpeg, outputPath, targetSeconds, ct);
                    }
                }

                // Sanity check: did ffmpeg actually produce a non-empty output?
                var outputBytes = new FileInfo(outputPath).Length;
                if (outputBytes < 100)
                    throw new InvalidOperationException(
                        $"Normalized output is suspiciously small ({outputBytes} bytes).");

                // Build a new TakeSource pointing at the new file.
                // IMPORTANT: keep the original OffsetMs and
                // ManualOffsetMs. The on-disk content has the
                // offset baked in (trimmed or prepended) so the
                // effective t=0 of the file is now aligned with
                // the take's t=0. We just don't repeat the offset
                // in take.json. That way Unnormalize (which
                // restores the original File pointer) leaves the
                // take exactly as it was — the user doesn't have
                // to re-apply any offsets.
                updated.Add(new TakeSource
                {
                    Id = src.Id,
                    Kind = src.Kind,
                    File = Path.GetFileName(outputPath),
                    OffsetMs = src.OffsetMs,
                    ManualOffsetMs = src.ManualOffsetMs,
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
    /// Trim the start by exactly N seconds. For audio (no
    /// keyframes) this is a stream copy and is fast and exact.
    /// For video, -c copy with -ss before -i is a keyframe
    /// snap (the output starts at the previous keyframe, not
    /// exactly N seconds in — so the trimmed file is offset
    /// from the requested position by up to one keyframe
    /// interval). That offset is in the file's CONTENT, so the
    /// player can't fix it by skipping the per-source seek —
    /// the audio and video would still be desynced by that
    /// amount. We re-encode the trimmed portion for video
    /// files to get an exact cut. Slow but correct.
    /// </summary>
    private static string[] BuildTrimArgs(string input, string output, double trimSeconds,
        bool isVideo)
    {
        var secStr = FormatSeconds(trimSeconds);
        if (isVideo)
        {
            // -ss AFTER -i: output seek. ffmpeg decodes from the
            // start, throws away frames until the seek point, and
            // re-encodes from there. Exact, no keyframe snap.
            // Default libx264 / aac re-encoding so the output
            // mux is straightforward.
            return new[]
            {
                "-y",
                "-i", input,
                "-ss", secStr,
                // Video: re-encode from the seek point so the
                // output is exactly at the requested offset (no
                // keyframe snap).
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast",
                // Audio: STREAM COPY the audio track rather than
                // re-encoding to AAC. AAC frames are 1024 samples
                // (21.33ms at 48kHz), so re-encoding would round
                // the audio's t=0 to the nearest frame boundary —
                // up to 21ms off from the exact seek point. Stream
                // copy keeps the audio sample-accurate. The mux
                // (mkv) handles video and audio with different
                // codecs natively.
                "-c:a", "copy",
                "-movflags", "+faststart",
                output,
            };
        }
        // ffmpeg -y -ss <secs> -i <input> -c copy <output>
        return new[]
        {
            "-y",
            "-ss", secStr,
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
    /// Probe a file's first audio stream and return a short
    /// human-readable description of the format (codec, sample
    /// rate, channel count, bit rate). Used to verify the pad
    /// re-encode preserved the input's format. Returns
    /// "no audio stream" if there's no audio.
    /// </summary>
    private static async Task<string> ProbeAudioFormatAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            try { await stderrTask.ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        // ffprobe-style output:
        //   Stream #0:0: Audio: pcm_f32le ([3][0][0][0] / 0x0003), 16000 Hz, mono, flt, 512 kb/s
        var audioLine = stderr.ToString().Split('\n')
            .FirstOrDefault(l => l.Contains("Audio:", StringComparison.Ordinal));
        if (audioLine is null) return "no audio stream";
        return audioLine.Trim().Length > 200
            ? audioLine.Trim()[..200] + "…"
            : audioLine.Trim();
    }

    /// <summary>
    /// Probe a file's first audio stream and return the codec
    /// name (e.g. "pcm_s16le", "pcm_f32le"). Returns
    /// "pcm_s16le" as a safe default if the probe fails or
    /// there's no audio.
    /// </summary>
    private static async Task<string> ProbeAudioCodecAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            try { await stderrTask.ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        // The codec name is the first field after "Audio:" in the
        // stream line, e.g. "Audio: pcm_f32le ([3][0][0][0] / 0x0003), 16000 Hz, ..."
        var audioLine = stderr.ToString().Split('\n')
            .FirstOrDefault(l => l.Contains("Audio:", StringComparison.Ordinal));
        if (audioLine is null) return "pcm_s16le";
        var m = System.Text.RegularExpressions.Regex.Match(audioLine, @"Audio:\s*(\S+)");
        if (!m.Success) return "pcm_s16le";
        // Strip the codec tag in parens ("pcm_s16le ([1][0][0][0] / 0x0001)")
        // and any trailing comma so the codec name is clean.
        var codec = m.Groups[1].Value.TrimEnd(',', ' ');
        // ffprobe shows "pcm_f32le" for 32-bit float WAV. Other
        // common values: pcm_s16le, pcm_s24le, pcm_s32le, mp3,
        // aac, opus. Pass through whatever ffmpeg reports.
        return codec;
    }

    /// <summary>
    /// Probe every source file in the take and return the longest
    /// input duration. That's the pad target for every source in
    /// the take — the longer ones are kept as-is, the shorter ones
    /// are padded with silence (or black for video) to match.
    /// If all probes fail, returns 0 (each source pads to its own
    /// length, the prior behaviour).
    /// </summary>
    private static async Task<double> ResolveTargetDurationAsync(string ffmpeg, Take take, string takeDir, CancellationToken ct)
    {
        double max = 0;
        foreach (var src in take.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(takeDir, src.File);
            if (!File.Exists(path)) continue;
            try
            {
                var d = await ProbeDurationAsync(ffmpeg, path, ct);
                if (d > max) max = d;
            }
            catch
            {
                // Skip on probe failure — keep what we have from
                // the others rather than aborting the whole take.
            }
        }
        return max;
    }

    /// <summary>
    /// Return the largest EffectiveOffsetMs across the take's
    /// sources. Used as the common trim amount so the content of
    /// every normalized file corresponds to the same take-time
    /// window. If all sources are missing, returns 0.
    /// </summary>
    private static Task<double> ResolveMaxOffsetMsAsync(Take take)
    {
        double max = 0;
        foreach (var src in take.Sources)
        {
            var o = src.EffectiveOffsetMs;
            if (o > max) max = o;
        }
        return Task.FromResult(max);
    }

    /// <summary>
    /// Per-source first-non-silent offset (in ms, relative to
    /// the take's t0). For video, returns EffectiveOffsetMs
    /// (cameras don't buffer silence — first frame IS first
    /// data). For audio, runs ffmpeg's silencedetect filter to
    /// find the first time the audio exceeds a noise floor, then
    /// translates that to take-time using the take's t0 and the
    /// source's EffectiveOffsetMs. The result is the take-time
    /// at which "actual sound" begins for each source. The
    /// maximum of these is the trim anchor: every normalized
    /// file is trimmed back to that take-time, so all files'
    /// t=0 lines up with real audio content (not first-data
    /// silence).
    /// </summary>
    private static async Task<Dictionary<string, double>> ResolveFirstNonSilentMsAsync(
        string ffmpeg, Take take, string takeDir, CancellationToken ct)
    {
        var result = new Dictionary<string, double>(take.Sources.Count);
        foreach (var src in take.Sources)
        {
            ct.ThrowIfCancellationRequested();
            // Video: first frame is first data. No refinement.
            if (src.Kind.IsVideo())
            {
                result[src.Id] = src.EffectiveOffsetMs;
                continue;
            }
            // Audio: detect first non-silent sample in the file.
            var path = Path.Combine(takeDir, src.File);
            if (!File.Exists(path))
            {
                result[src.Id] = src.EffectiveOffsetMs;
                continue;
            }
            try
            {
                var firstNonSilentFileMs = await ProbeFirstNonSilentAsync(ffmpeg, path, ct)
                    .ConfigureAwait(false);
                // Translate file-time to take-time:
                //   take-time = file-time + (take.t0 - source.firstDataAt)
                //            = file-time + EffectiveOffsetMs
                //   Wait, EffectiveOffsetMs is (firstDataAt - t0), so
                //   a POSITIVE offset means the source started AFTER t0.
                //   file-time 0 corresponds to take-time EffectiveOffsetMs.
                //   So take-time = file-time + EffectiveOffsetMs.
                var takeTimeMs = firstNonSilentFileMs + src.EffectiveOffsetMs;
                result[src.Id] = takeTimeMs;
            }
            catch
            {
                // On failure, fall back to the record-time offset.
                // Better to normalize at a slightly-wrong anchor
                // than to fail the whole take.
                result[src.Id] = src.EffectiveOffsetMs;
            }
        }
        return result;
    }

    /// <summary>
    /// Run ffmpeg with silencedetect to find the first
    /// non-silent timestamp (in ms, from file t=0). Returns 0
    /// if the entire file is silent or detection fails — that's
    /// the right answer for "no audible sound" since we want the
    /// file to start at its first-data time in that case.
    /// </summary>
    private static async Task<double> ProbeFirstNonSilentAsync(
        string ffmpeg, string path, CancellationToken ct)
    {
        // silencedetect flags:
        //   noise=-30dB  — anything quieter than -30dB is "silence".
        //                    -30dB is conservative enough to catch
        //                    breathing and room tone but not so
        //                    aggressive it flags quiet speech.
        //   d=0.05       — minimum 50ms of non-silence to count
        //                    as a sound (suppresses sub-frame
        //                    blips from noise floor).
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add("silencedetect=noise=-30dB:d=0.05");
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
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            try { await stderrTask.ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        // silencedetect prints lines like:
        //   [silencedetect @ 0x...] silence_start: 0.123
        //   [silencedetect @ 0x...] silence_end: 1.234 | silence_duration: 1.111
        //   [silencedetect @ 0x...] silence_start: 5.678
        // The first silence_end is the first non-silent time.
        // If there's no silence_end (file is all sound from t=0),
        // the first non-silent time is 0.
        var m = System.Text.RegularExpressions.Regex.Match(
            stderr.ToString(), @"silence_end:\s*(\d+(?:\.\d+)?)");
        if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return seconds * 1000.0;
        return 0;
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
        // ffmpeg picks the output muxer from the file extension, so
        // the temp file must keep the input's extension (.wav,
        // .mkv, .mp4, ...) or ffmpeg returns EINVAL — "Unable to
        // choose an output format for '*.padtmp'". Strip the
        // original extension, append .padtmp.<ext> so ffmpeg can
        // mux the output, and File.Move at the end will swap it
        // back over the canonical .normalized.<ext> path.
        var origExt = Path.GetExtension(path);
        var tempPath = Path.ChangeExtension(path, null) + ".padtmp" + origExt;
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
        var probeOutput = probeStderr.ToString();
        var hasVideo = probeOutput.Contains("Video:", StringComparison.Ordinal);
        // Extract the input's sample rate, channel count, and
        // audio codec so the pad re-encode preserves the
        // original bit depth and bit rate. The user's NAudio
        // recordings come out as 32-bit float (pcm_f32le) on
        // some Windows setups; if the pad hard-codes 16-bit
        // (pcm_s16le) the bit rate is exactly halved.
        int? inputSampleRate = null;
        int? inputChannels = null;
        var srMatch = System.Text.RegularExpressions.Regex.Match(
            probeOutput, @"(\d+)\s*Hz");
        if (srMatch.Success && int.TryParse(srMatch.Groups[1].Value, out var sr))
            inputSampleRate = sr;
        var chMatch = System.Text.RegularExpressions.Regex.Match(
            probeOutput, @"Hz\s*,\s*(\d+)\s*channels");
        if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, out var ch))
            inputChannels = ch;
        var codecMatch = System.Text.RegularExpressions.Regex.Match(
            probeOutput, @"Audio:\s*(\S+)");
        // The first match is the codec name; trim trailing
        // comma/space in case the codec tag had a parens block.
        var inputCodec = codecMatch.Success
            ? codecMatch.Groups[1].Value.TrimEnd(',', ' ').TrimEnd(')', ' ')
            : "pcm_s16le";
        // Strip the (rare) codec tag in parens that ffmpeg
        // sometimes appends, e.g. "pcm_f32le ([3][0][0][0] /
        // 0x0003)". If the codec name still has a '(' we just
        // take the first token.
        if (inputCodec.Contains('('))
            inputCodec = inputCodec.Split('(')[0].Trim();

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
        // Match the input's audio format (sample rate, channel
        // count, AND codec) so the pad re-encode doesn't
        // silently halve the bit rate by downgrading
        // 32-bit float to 16-bit PCM. Force the same sample
        // rate and channel count as the input; use the input's
        // codec (typically pcm_s16le or pcm_f32le) so the bit
        // rate is preserved exactly.
        if (inputSampleRate is not null) { args.Add("-ar"); args.Add(inputSampleRate.Value.ToString(CultureInfo.InvariantCulture)); }
        if (inputChannels is not null) { args.Add("-ac"); args.Add(inputChannels.Value.ToString(CultureInfo.InvariantCulture)); }
        args.Add("-c:a"); args.Add(inputCodec);
        args.Add(tempPath);

        try
        {
            var stderr = new StringBuilder();
            var exitCode = await RunFfmpegAsync(ffmpeg, args.ToArray(), ct, stderr);
            if (exitCode != 0)
            {
                // Surface ffmpeg's last error line so the user
                // can see what the filter actually rejected.
                var tail = stderr.ToString();
                var lastLines = tail.Length > 2000
                    ? tail[^2000..]
                    : tail;
                throw new InvalidOperationException(
                    $"ffmpeg pad exited with code {exitCode}. Last ffmpeg output: {lastLines.Trim()}");
            }
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
    /// Throws OperationCanceledException if the token fires. Stderr is
    /// captured but truncated to ~64KB; on non-zero exit the full
    /// captured stderr is available via <paramref name="stderrOut"/>
    /// for diagnostic messages.
    /// </summary>
    private static async Task<int> RunFfmpegAsync(string ffmpeg, string[] args, CancellationToken ct, StringBuilder? stderrOut = null)
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
        var stderr = stderrOut ?? new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                stderr.AppendLine(line);
                // Cap only the in-memory copy. If a caller passed
                // their own StringBuilder, leave it uncapped so
                // they can see the full ffmpeg error.
                if (ReferenceEquals(stderrOut, null) && stderr.Length > 64 * 1024)
                    stderr.Length = 64 * 1024;
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