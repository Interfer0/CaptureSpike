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
    /// <param name="silenceThresholdDb">dB below which audio is
    /// considered silence. Default -30. Lower (more negative)
    /// = stricter; higher (less negative) = more sensitive.</param>
    /// <param name="silenceMinDurationSec">Minimum duration of
    /// audio above the threshold before it counts as a sound
    /// event. Default 0.05s.</param>
    public static async Task<Result> NormalizeAsync(
        string takeDir,
        Take take,
        ProgressReporter? progress = null,
        double silenceThresholdDb = -30.0,
        double silenceMinDurationSec = 0.05,
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
        // Compute the prepend amount per source. The user's
        // instruction (paraphrased): "tack empty space onto
        // the beginning of the files so they match the
        // longest file. Don't try to detect silence." So we
        // pick the longest-duration file as the anchor and
        // prepend (maxDuration - thisDuration) of silence to
        // every shorter file. After the prepend, every file
        // ends at the same wall-clock moment (the take's stop
        // time).
        //
        // This is intentionally simple. No silencedetect,
        // no offset math, no end-trim. If a file's actual
        // content is shorter than its duration (e.g. trailing
        // silence), that's preserved — the user is going to
        // import to Resolve and trim there if they want.
        var perSourceDurationMs = await ProbePerSourceDurationAsync(ffmpeg, take, takeDir, ct);
        var maxDurationMs = perSourceDurationMs.Values.DefaultIfEmpty(0).Max();
        // Diagnostic: per-source prepend amounts.
        foreach (var src in take.Sources)
        {
            if (perSourceDurationMs.TryGetValue(src.Id, out var dur))
            {
                var prependMs = maxDurationMs - dur;
                progress?.Invoke(src.Id, null,
                    $"Duration: {dur / 1000.0:F2}s; prepend {prependMs:F0}ms of silence to align with longest source.");
            }
        }
        progress?.Invoke("", null,
            $"Normalize plan: prepend silence so every source ends at the same wall-clock time (longest: {maxDurationMs / 1000.0:F2}s).");
        // SilenceThresholdDb and SilenceMinDurationSec are
        // accepted parameters but not used here — the user's
        // latest direction is to skip the silence detector
        // entirely. We still pass them through so the public
        // API doesn't change.

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
            if (src.File.Contains(".normalized.") ||
                Path.GetFileName(src.File).StartsWith("NORM-", StringComparison.Ordinal))
            {
                updated.Add(src);
                progress?.Invoke(src.Id, 1.0, $"{src.File} is already normalized; skipping.");
                continue;
            }

            // Build the output path: NORM-<basename><ext>. The
            // NORM- prefix sorts to the top in Resolve's file
            // browser, making it easy to select only the
            // normalized variants of a take. The original
            // file is left untouched so the user can
            // un-normalize to restore the unprocessed take.
            var dir = Path.GetDirectoryName(inputPath) ?? takeDir;
            var name = Path.GetFileNameWithoutExtension(inputPath);
            var ext = Path.GetExtension(inputPath);
            var outputPath = Path.Combine(dir, $"NORM-{name}{ext}");

            // Same path for input and output? shouldn't happen, but bail safely.
            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                failed.Add($"{src.Id}: computed output path equals input path; refusing to overwrite.");
                continue;
            }

            try
            {
                // Per-source prepend: tack silence onto the
                // beginning so every file's end is at the
                // take's stop wall-clock time. The longest
                // file is the anchor (no change). Shorter
                // files get (maxDuration - thisDuration) of
                // silence prepended.
                var sourceDurationMs = perSourceDurationMs.TryGetValue(src.Id, out var d)
                    ? d : 0;
                var prependMs = Math.Max(0, maxDurationMs - sourceDurationMs);
                if (prependMs < 0.5)
                {
                    // Already the longest source. Copy
                    // as-is to NORM-.
                    progress?.Invoke(src.Id, 0.0,
                        $"{src.File} is the longest source; copying to NORM-.");
                    File.Copy(inputPath, outputPath, overwrite: true);
                }
                else
                {
                    // Prepend (prependMs) of silence/black to
                    // the start. For audio we use anullsrc +
                    // concat; for video we use color +
                    // overlay. The end of the file is
                    // unchanged so all files end at the
                    // take's stop wall-clock time.
                    var prependSec = prependMs / 1000.0;
                    progress?.Invoke(src.Id, 0.0,
                        $"Prepending {prependMs:F0}ms of silence to {src.File} (to align with longest source).");
                    var args = BuildPrependArgs(inputPath, outputPath, prependSec,
                        src.Kind.IsVideo());
                    var exitCode = await RunFfmpegAsync(ffmpeg, args, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException(
                            $"ffmpeg prepend exited with code {exitCode} for {src.Id}.");
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
    /// Trim the END of a file by setting the output duration
    /// to <paramref name="durationSec"/>. (Currently unused —
    /// the user opted for prepend-only alignment — but kept
    /// here in case silence-detection is re-introduced
    /// later.) -t stops the output at the requested duration.
    /// For audio-only files we can stream-copy; for video we
    /// re-encode to land exactly on the last audible sample.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0051", Justification = "Currently unused; kept for future re-introduction of trailing-silence detection.")]
    private static string[] BuildEndTrimArgs(string input, string output, double durationSec,
        bool isVideo)
    {
        var secStr = FormatSeconds(durationSec);
        if (isVideo)
        {
            // Re-encode so the output ends exactly at the
            // last audible sample. -t is an output option so
            // it applies after the decoder/encoder pipeline,
            // not as a stream-copy shortcut.
            return new[]
            {
                "-y",
                "-i", input,
                "-t", secStr,
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast",
                "-c:a", "copy",
                "-movflags", "+faststart",
                output,
            };
        }
        // Audio-only: -c copy is exact.
        return new[]
        {
            "-y",
            "-i", input,
            "-t", secStr,
            "-c", "copy",
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
    /// <summary>
    /// Build the ffmpeg args to prepend <paramref name="seconds"/>
    /// of silence (audio) and black (video) to the start of the
    /// file. Uses a single-input filter chain (`adelay` for
    /// audio, `tpad` for video) so ffmpeg can process the file
    /// in a single pass and terminates when the input ends —
    /// no concat, no second input, no infinite source filters
    /// that can hang the muxer.
    ///
    /// For video sources the input's resolution is preserved
    /// (tpad inherits the input's size, no up/down scale).
    /// </summary>
    private static string[] BuildPrependArgs(string input, string output, double seconds,
        bool isVideo)
    {
        var secStr = FormatSeconds(seconds);
        // Probe the input for what streams it actually has.
        // A screen recording typically has video only
        // (no audio); a mic recording has audio only.
        // Passing -af on a video-only input or -vf on an
        // audio-only input is what ffmpeg was rejecting
        // with exit code -22 (EINVAL) on Take 13's
        // SC-Screen2. Probe first; only apply the filter
        // that matches the actual stream.
        var inputStreams = ProbeStreamsAsync(input).GetAwaiter().GetResult();
        var hasVideo = inputStreams.HasVideo;
        var hasAudio = inputStreams.HasAudio;
        if (isVideo && !hasVideo && hasAudio)
        {
            // Caller asked for video path but the file is
            // audio-only. Demote to audio path.
            isVideo = false;
        }

        if (isVideo)
        {
            // tpad adds frames at the start of the video.
            // start_mode=clone:0 clones the first input
            // frame for the prepended duration. The output
            // looks like a static hold of whatever was on
            // screen at the start of the recording followed
            // by the original video.
            //
            // -c:v libx264 re-encodes because tpad changes
            // the number of frames; stream copy wouldn't
            // work for the same reason.
            var args = new List<string>
            {
                "-y",
                "-i", input,
                "-vf", $"tpad=start_mode=clone:0:start_duration={secStr}",
            };
            if (hasAudio)
            {
                // adelay shifts the input audio by
                // <seconds> ms. all=1 applies the same
                // delay to every channel.
                args.Add("-af");
                args.Add($"adelay=delays={FormatAdelayMs(seconds)}:all=1");
            }
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            args.Add("-preset");
            args.Add("ultrafast");
            if (hasAudio)
            {
                args.Add("-c:a");
                args.Add("aac");
            }
            args.Add("-shortest");
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add(output);
            return args.ToArray();
        }
        // Audio-only: delay the input by <seconds> so its
        // first sample lands at seconds instead of 0. The
        // output is just the input with a prepended silence
        // region. No second input, no concat, no infinite
        // source filters — ffmpeg can process this in a
        // single pass and the output duration is exactly
        // (seconds + inputDuration).
        return new[]
        {
            "-y",
            "-i", input,
            "-af", $"adelay=delays={FormatAdelayMs(seconds)}:all=1",
            "-c:a", "pcm_s16le",
            output,
        };
    }

    /// <summary>
    /// Cache of stream detection results so we don't probe
    /// the same file twice in a single normalize pass.
    /// </summary>
    private static readonly Dictionary<string, (bool HasVideo, bool HasAudio)> _streamProbeCache = new();

    private static async Task<(bool HasVideo, bool HasAudio)> ProbeStreamsAsync(string input)
    {
        if (_streamProbeCache.TryGetValue(input, out var cached)) return cached;
        // Probe with ffprobe — fast (just metadata, no decode).
        // ffprobe isn't on the public ToolPaths API; ffmpeg's
        // -i on a /dev/null output gives us a stderr dump we
        // can parse. The alternative would be ffprobe
        // (sharper parsing) but we already have ffmpeg in the
        // path via ToolPaths. Use it with the existing
        // ProbeDurationAsync pattern.
        var ffmpeg = ToolPaths.Resolve("ffmpeg");
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
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        var (hasVideo, hasAudio) = (false, false);
        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.");
            var stderr = new StringBuilder();
            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    stderr.AppendLine(line);
            });
            // No cancellation here — stream probe is fast.
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await stderrTask.ConfigureAwait(false); } catch { }
            var s = stderr.ToString();
            // ffmpeg's stderr for -i <file> -f null - looks like:
            //   Stream #0:0: Video: h264 ...
            //   Stream #0:1: Audio: pcm_s16le ...
            hasVideo = s.Contains("Video:", StringComparison.Ordinal);
            hasAudio = s.Contains("Audio:", StringComparison.Ordinal);
        }
        catch
        {
            // Probe failed. Default to assuming both — the
            // downstream filter will error if that's wrong,
            // and we'll surface it.
            hasVideo = true;
            hasAudio = true;
        }
        _streamProbeCache[input] = (hasVideo, hasAudio);
        return (hasVideo, hasAudio);
    }

    /// <summary>
    /// Convert a seconds value to milliseconds (rounded to
    /// integer) for ffmpeg's adelay filter. adelay takes
    /// delays in milliseconds, one per channel, separated
    /// by '|'. With all=1, the same delay applies to every
    /// channel.
    /// </summary>
    private static string FormatAdelayMs(double seconds) =>
        ((int)Math.Round(seconds * 1000.0)).ToString(CultureInfo.InvariantCulture);

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
    /// Probe each take source's media file and return its
    /// duration in milliseconds. Used by the prepend pass:
    /// the longest-duration file is the anchor; shorter
    /// files get (max - thisDuration) of silence prepended
    /// so every file ends at the same wall-clock time.
    /// </summary>
    private static async Task<Dictionary<string, double>> ProbePerSourceDurationAsync(
        string ffmpeg, Take take, string takeDir, CancellationToken ct)
    {
        var result = new Dictionary<string, double>(take.Sources.Count);
        foreach (var src in take.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(takeDir, src.File);
            if (!File.Exists(path)) continue;
            try
            {
                var seconds = await ProbeDurationAsync(ffmpeg, path, ct).ConfigureAwait(false);
                result[src.Id] = seconds * 1000.0;
            }
            catch
            {
                // Skip on probe failure; that source's
                // prepend falls back to 0 (no prepend).
            }
        }
        return result;
    }

    /// <summary>
    /// Probe each take source's media file and return:
    ///   - duration: total file length in ms.
    ///   - lastNonSilentMs: time (ms from file t=0) of the
    ///     last sample above the silence threshold, or
    ///     duration if the file is all sound.
    /// Used to compute the trim anchor at the END of each
    /// file. The user's insight: "if a source ends with dead
    /// space, that's the part that needs trimming, not the
    /// beginning." A file whose last sound is 5 minutes
    /// before its actual end has 5 minutes of trailing
    /// silence we should drop.
    /// </summary>
    private static async Task<Dictionary<string, (double DurationMs, double LastNonSilentMs)>>
        ProbePerSourceEndAsync(
        string ffmpeg, Take take, string takeDir,
        double silenceThresholdDb, double silenceMinDurationSec,
        CancellationToken ct)
    {
        var result = new Dictionary<string, (double, double)>(take.Sources.Count);
        foreach (var src in take.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(takeDir, src.File);
            if (!File.Exists(path)) continue;
            try
            {
                var dur = await ProbeDurationAsync(ffmpeg, path, ct).ConfigureAwait(false);
                var durMs = dur * 1000.0;
                // For video, last non-silent = end of file
                // (videos don't have a notion of "silence";
                // every frame is content).
                double lastNonSilentMs;
                if (src.Kind.IsVideo())
                {
                    lastNonSilentMs = durMs;
                }
                else
                {
                    lastNonSilentMs = await ProbeLastNonSilentAsync(ffmpeg, path,
                        silenceThresholdDb, silenceMinDurationSec, ct)
                        .ConfigureAwait(false);
                }
                result[src.Id] = (durMs, lastNonSilentMs);
            }
            catch
            {
                // Skip on probe failure.
            }
        }
        return result;
    }

    /// <summary>
    /// Run ffmpeg with silencedetect to find the LAST
    /// non-silent timestamp (in ms, from file t=0). Returns
    /// 0 if the entire file is silent (rare; we then trim
    /// everything). Returns the file duration (caller's job
    /// to substitute dur) if detection fails.
    /// </summary>
    private static async Task<double> ProbeLastNonSilentAsync(
        string ffmpeg, string path,
        double silenceThresholdDb, double silenceMinDurationSec,
        CancellationToken ct)
    {
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
        psi.ArgumentList.Add($"silencedetect=noise={silenceThresholdDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}dB:d={silenceMinDurationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
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
        // silencedetect prints pairs of (silence_start,
        // silence_end). silence_start = beginning of a
        // silence run. silence_end = end of a silence run
        // (= sound resumes). After the LAST silence_end the
        // file is "sound" until the end. So the last non-
        // silent time is the LAST silence_end value. If the
        // file is all sound from t=0 (no silence_end line),
        // the last non-silent time is the file duration —
        // we return 0 and the caller uses duration as
        // fallback.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stderr.ToString(), @"silence_end:\s*(\d+(?:\.\d+)?)");
        if (matches.Count > 0)
        {
            var last = matches[matches.Count - 1];
            if (double.TryParse(last.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return seconds * 1000.0;
        }
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
        // Track so the app can kill a runaway ffmpeg on shutdown.
        // Without this, an ffmpeg hung on an unbounded source
        // (anullsrc with concat that ignores -shortest, for
        // example) would keep writing to the output file long
        // after the user closed the app. The user reported a
        // 75GB WAV being created this way.
        ChildProcessRegistry.Track(process);

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
        // Process completed normally. Untrack so the registry
        // doesn't accumulate dead references over the session.
        ChildProcessRegistry.Untrack(process);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        return process.ExitCode;
    }
}