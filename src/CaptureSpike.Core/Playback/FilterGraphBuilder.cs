// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Globalization;
using System.Text;

namespace CaptureSpike.Core.Playback;

/// <summary>
/// Builds mpv <c>--lavfi-complex</c> filter graphs from a take.
///
/// Input files are always passed to mpv in <see cref="Take.Sources"/> order,
/// so mpv's track labels are stable: vid1..N for the video sources and
/// aid1..N for the audio sources, in take order. Muted tracks stay in the
/// input list but are left out of the graph, which keeps the labels stable
/// across mute/unmute rebuilds.
///
/// Track offsets are normalized by subtracting the smallest offset so
/// playback starts promptly; relative alignment is preserved.
/// </summary>
public static class FilterGraphBuilder
{
    public static string Build(Project project, Scene scene, Take take, ISet<string>? mutedSourceIds = null)
    {
        var muted = mutedSourceIds ?? new HashSet<string>();
        var allVideos = take.Sources.Where(s => s.Kind.IsVideo()).ToList();
        var allAudios = take.Sources.Where(s => s.Kind.IsAudio()).ToList();
        var videos = allVideos.Where(s => !muted.Contains(s.Id)).ToList();
        var audios = allAudios.Where(s => !muted.Contains(s.Id)).ToList();

        if (videos.Count == 0 && audios.Count == 0)
            throw new InvalidOperationException("Take has no unmuted sources to play.");

        var live = videos.Concat(audios).ToList();
        var minOffsetMs = take.Sources.Min(s => s.OffsetMs);

        var sb = new StringBuilder();

        // ----- video: base layer full-bleed, the rest overlaid per scene layout -----
        if (videos.Count > 0)
        {
            var first = videos[0];
            sb.Append(CultureInfo.InvariantCulture,
                $"[vid{VidIndex(allVideos, first)}]setpts=PTS+{Sec(first.OffsetMs - minOffsetMs)}/TB,scale={project.OutputWidth}:{project.OutputHeight}[vbase];");

            var composed = "[vbase]";
            for (var i = 1; i < videos.Count; i++)
            {
                var v = videos[i];
                var layout = LayoutFor(project, scene, v.Id, i);
                var w = (int)Math.Round(layout.Width * project.OutputWidth);
                var h = (int)Math.Round(layout.Height * project.OutputHeight);
                var x = (int)Math.Round(layout.X * project.OutputWidth);
                var y = (int)Math.Round(layout.Y * project.OutputHeight);
                sb.Append(CultureInfo.InvariantCulture,
                    $"[vid{VidIndex(allVideos, v)}]setpts=PTS+{Sec(v.OffsetMs - minOffsetMs)}/TB,scale={w}:{h}[vpip{i}];");
                sb.Append($"{composed}[vpip{i}]overlay={x}:{y}[vout{i}];");
                composed = $"[vout{i}]";
            }
            sb.Append($"{composed}null[vo];");
        }

        // ----- audio: delay each track, then mix -----
        if (audios.Count > 0)
        {
            for (var i = 0; i < audios.Count; i++)
            {
                var a = audios[i];
                var ms = (int)Math.Round(a.OffsetMs - minOffsetMs);
                sb.Append($"[aid{AidIndex(allAudios, a)}]adelay=delays={ms}:all=1,aresample=48000[a{i}];");
            }
            if (audios.Count == 1)
                sb.Append($"[aid{AidIndex(allAudios, audios[0])}]adelay=delays={(int)Math.Round(audios[0].OffsetMs - minOffsetMs)}:all=1,aresample=48000[ao];");
            else
                sb.Append($"[a0]{string.Concat(Enumerable.Range(1, audios.Count - 1).Select(i => $"[a{i}]"))}amix=inputs={audios.Count}:normalize=0[ao];");
        }

        return sb.ToString().TrimEnd(';');
    }

    /// <summary>
    /// Builds the mpv <c>--lavfi-complex</c> filter graph that
    /// the single-mpv tiled-take player uses. The single-mpv
    /// approach gives one audio clock for every source — no
    /// per-process drift between separate mpv instances. The
    /// video sources are laid out in a 2-column grid (more on
    /// one row when there are 3+); the audio sources are
    /// delay-aligned to the take's earliest first-data and then
    /// amixed. Muted sources are excluded from the graph.
    /// </summary>
    public static string BuildTakeGraph(
        IReadOnlyList<TakeSource> sources,
        ISet<string>? mutedSourceIds = null)
    {
        var muted = mutedSourceIds ?? new HashSet<string>();
        var all = sources.ToList();
        var live = all.Where(s => !muted.Contains(s.Id)).ToList();
        if (live.Count == 0)
            throw new InvalidOperationException("Take has no unmuted sources to play.");
        // Keep the timeline origin stable across mute/unmute rebuilds. If
        // the earliest source is muted, the remaining tracks must not shift.
        var minOffsetMs = sources.Min(s => s.OffsetMs);

        var sb = new StringBuilder();
        var allVideos = all.Where(s => s.Kind.IsVideo()).ToList();
        var allAudios = all.Where(s => s.Kind.IsAudio()).ToList();
        var videos = allVideos.Where(s => !muted.Contains(s.Id)).ToList();
        var audios = allAudios.Where(s => !muted.Contains(s.Id)).ToList();

        // mpv's --lavfi-complex=GRAPH has its own input label
        // scheme that is DIFFERENT from ffmpeg's. mpv labels
        // each input file's streams sequentially by stream
        // type: vid1, vid2, ... for video (in the order
        // files are listed), and aid1, aid2, ... for audio
        // (in the order files are listed, separately).
        // An audio-only file gets an aid label but no vid
        // label. The graph must address inputs by these
        // labels, NOT by [0:v], [1:a] like ffmpeg does.
        //
        // The TiledTakePlayer passes files in the order
        // [video0, video1, ..., audio0, audio1, ...] so:
        //   video0.mkv -> vid1
        //   video1.mkv -> vid2
        //   audio0.wav -> aid1 (no vid, because no video stream)
        //   audio1.wav -> aid2
        //
        // Output labels must be [vo] for video and [ao] for
        // audio. mpv's playback chain init looks for these
        // specific labels to know what streams to set up —
        // using [v]/[a] makes mpv think there are no output
        // streams and it produces no file-loaded event.
        //
        // Only the requested media streams belong in this graph. Missing
        // stream types are not represented by disconnected synthetic sources:
        // mpv loads these files as external tracks, and the graph outputs
        // [vo]/[ao] directly describe the streams it should play.
        if (videos.Count > 0)
        {
            const int cellW = 640;
            const int cellH = 360;
            // vid1, vid2, ... for video files in order.
            for (var i = 0; i < videos.Count; i++)
            {
                var v = videos[i];
                var ms = v.OffsetMs - minOffsetMs;
                var inputLabel = $"vid{allVideos.IndexOf(v) + 1}";
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{inputLabel}]setpts=PTS+{Sec(ms)}/TB,scale={cellW}:{cellH}[{PLabel(i)}];");
            }
            // Stack the cells: xstack=inputs=N:layout=0_0|w0_0|w0_h0|...
            // We use the explicit grid by giving each cell its
            // position. xstack supports 'layout' as a pipe-
            // separated list of positions. With 2 columns and
            // arbitrary rows we just enumerate the layout.
            var layoutSb = new StringBuilder();
            for (var i = 0; i < videos.Count; i++)
            {
                var col = i % 2;
                var row = i / 2;
                if (i > 0) layoutSb.Append('|');
                layoutSb.Append(CultureInfo.InvariantCulture, $"{col * cellW}_{row * cellH}");
            }
            // FFmpeg's xstack requires at least two inputs. For a
            // single video, pass the scaled stream through directly.
            if (videos.Count == 1)
            {
                sb.Append($"[{PLabel(0)}]null[vo];");
            }
            else
            {
                var inputs = string.Join("", Enumerable.Range(0, videos.Count).Select(i => $"[{PLabel(i)}]"));
                // Output label is [vo] (mpv's required video-out).
                sb.Append(CultureInfo.InvariantCulture,
                    $"{inputs}xstack=inputs={videos.Count}:layout={layoutSb}[vo];");
            }
        }

        // Audio: aid1, aid2, ... for audio files in order.
        // mpv numbers aid separately from vid: the first
        // audio file in the file list is aid1 even if there
        // are video files before it.
        if (audios.Count > 0)
        {
            for (var i = 0; i < audios.Count; i++)
            {
                var a = audios[i];
                var ms = a.OffsetMs - minOffsetMs;
                var inputLabel = $"aid{allAudios.IndexOf(a) + 1}";
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{inputLabel}]adelay=delays={ms:F0}:all=1,aresample=48000[{AOutLabel(i)}];");
            }
            if (audios.Count == 1)
                // Output label is [ao] (mpv's required audio-out).
                sb.Append(CultureInfo.InvariantCulture, $"[{AOutLabel(0)}]anull[ao];");
            else
            {
                var inputs = string.Join("", Enumerable.Range(0, audios.Count).Select(i => $"[{AOutLabel(i)}]"));
                sb.Append(CultureInfo.InvariantCulture,
                    $"{inputs}amix=inputs={audios.Count}:normalize=0[ao];");
            }
        }

        // If only audio, lavfi still needs a video output. Produce
        // a black frame. Without this mpv errors with "no video".
        if (videos.Count == 0)
        {
            sb.Append("color=black:size=640x360:rate=30[vo];");
        }

        return sb.ToString().TrimEnd(';');
    }

    private static int VidIndex(List<TakeSource> allVideos, TakeSource s) => allVideos.IndexOf(s) + 1;
    private static int AidIndex(List<TakeSource> allAudios, TakeSource s) => allAudios.IndexOf(s) + 1;

    // Short, stable labels for the lavfi graph so the graph
    // itself is readable in mpv's verbose output.
    private static string VLabel(int i) => $"v{i}";
    private static string PLabel(int i) => $"vp{i}";
    private static string ALabel(int i) => $"a{i}";
    private static string AOutLabel(int i) => $"ao{i}";

    private static string Sec(double ms) =>
        (ms / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

    private static (double X, double Y, double Width, double Height) LayoutFor(
        Project project, Scene scene, string takeSourceId, int videoOrdinal)
    {
        var sceneRef = scene.Sources.FirstOrDefault(s => s.SourceId == takeSourceId);
        var palette = project.Sources.FirstOrDefault(p => p.Id == takeSourceId);
        if (sceneRef is not null && palette is not null && palette.Kind.IsVideo())
            return (sceneRef.X, sceneRef.Y, sceneRef.Width, sceneRef.Height);
        // Take has a video source the scene doesn't know: stack as PiP.
        return (0.72, 0.70 - 0.28 * ((videoOrdinal - 1) % 3), 0.26, 0.26);
    }
}
