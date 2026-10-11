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
        var minOffsetMs = live.Min(s => s.OffsetMs);

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
        var live = sources.Where(s => !muted.Contains(s.Id)).ToList();
        if (live.Count == 0)
            throw new InvalidOperationException("Take has no unmuted sources to play.");
        var minOffsetMs = live.Min(s => s.OffsetMs);

        var sb = new StringBuilder();
        var allVideos = live.Where(s => s.Kind.IsVideo()).ToList();
        var allAudios = live.Where(s => s.Kind.IsAudio()).ToList();

        // mpv's --lavfi-complex=GRAPH exposes each input file's
        // streams by FILE POSITION: [0:v], [1:v], etc. for the
        // first file's video, second file's video, etc. The
        // TiledTakePlayer passes files to mpv in the order
        // [video0, video1, ..., audio0, audio1, ...] so the
        // graph can label videos by their video-list index
        // and audios by their audio-list offset.
        //
        // Video indices: 0..N-1 (videos go first in the file
        // list). Audio indices: N..N+M-1 where N = videos.Count
        // and M = audios.Count.
        //
        // ffmpeg's lavfi rejects graphs where a referenced
        // stream doesn't exist on the file (e.g. [0:a] on a
        // video-only file). For every file, we provide BOTH
        // [i:v] and [i:a] — using the file's stream when
        // present, and a synthesized fallback (anullsrc for
        // audio, color for video) when it's not. This way the
        // graph always has both streams to feed downstream
        // filters, and xstack/amix can take the real streams
        // they need.
        // All-video file count N; all-audio file count M.
        // Total files = N + M.
        // File order = [v0, v1, ..., vN-1, a0, a1, ..., aM-1]
        // Stream labels:
        //   video file at position i contributes [i:v] (real)
        //                              and [i:a] (anullsrc, silent)
        //   audio file at position N+j contributes [N+j:a] (real)
        //                                 and [N+j:v] (color, black)
        var totalFiles = allVideos.Count + allAudios.Count;
        for (var i = 0; i < totalFiles; i++)
        {
            var isVideo = i < allVideos.Count;
            // For every file: ensure both :v and :a exist.
            // Use a re-labeled stream so downstream filters
            // (xstack, amix) can pick the right one.
            if (isVideo)
            {
                // Video file: [i:v] is the real video,
                // [i:a] is silent. We label the silent
                // stream as [ias{i}] so it's not picked up
                // by amix unless we want it to be.
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{i}:a]anullsrc=channel_layout=stereo:sample_rate=48000[ias{i}];");
            }
            else
            {
                // Audio file: [i:a] is the real audio,
                // [i:v] is black. Label the black stream
                // as [ivs{i}] (silent video) so it can be
                // picked up by xstack if needed.
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{i}:v]color=size=640x360:rate=30:color=black[ivs{i}];");
            }
        }
        if (allVideos.Count > 0)
        {
            const int cellW = 640;
            const int cellH = 360;
            for (var i = 0; i < allVideos.Count; i++)
            {
                var v = allVideos[i];
                var ms = v.OffsetMs - minOffsetMs;
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{i}:v]setpts=PTS+{Sec(ms)}/TB,scale={cellW}:{cellH}[{PLabel(i)}];");
            }
            // Stack the cells: xstack=inputs=N:layout=0_0|w0_0|w0_h0|...
            // We use the explicit grid by giving each cell its
            // position. xstack supports 'layout' as a pipe-
            // separated list of positions. With 2 columns and
            // arbitrary rows we just enumerate the layout.
            var layoutSb = new StringBuilder();
            for (var i = 0; i < allVideos.Count; i++)
            {
                var col = i % 2;
                var row = i / 2;
                if (i > 0) layoutSb.Append('|');
                layoutSb.Append(CultureInfo.InvariantCulture, $"{col * cellW}_{row * cellH}");
            }
            var inputs = string.Join("", Enumerable.Range(0, allVideos.Count).Select(i => $"[{PLabel(i)}]"));
            sb.Append(CultureInfo.InvariantCulture,
                $"{inputs}xstack=inputs={allVideos.Count}:layout={layoutSb}[v];");
        }

        // Audio indices: N..N+M-1 (audios come after videos in
        // the file list, so [N:a] is the first audio file).
        if (allAudios.Count > 0)
        {
            var videoCount = allVideos.Count;
            for (var i = 0; i < allAudios.Count; i++)
            {
                var a = allAudios[i];
                var fileIndex = videoCount + i;
                var ms = a.OffsetMs - minOffsetMs;
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{fileIndex}:a]adelay=delays={ms:F0}:all=1,aresample=48000[{AOutLabel(i)}];");
            }
            if (allAudios.Count == 1)
                sb.Append(CultureInfo.InvariantCulture, $"[{AOutLabel(0)}]anull[a];");
            else
            {
                var inputs = string.Join("", Enumerable.Range(0, allAudios.Count).Select(i => $"[{AOutLabel(i)}]"));
                sb.Append(CultureInfo.InvariantCulture,
                    $"{inputs}amix=inputs={allAudios.Count}:normalize=0[a];");
            }
        }

        // If only audio, lavfi still needs a video output. Produce
        // a black frame. Without this mpv errors with "no video".
        if (allVideos.Count == 0)
        {
            sb.Append("color=black:size=640x360:rate=30[v];");
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
