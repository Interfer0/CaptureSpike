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
        // The graph also synthesizes missing streams: anullsrc
        // for video files that have no audio, color for audio
        // files that have no video. These synthesized streams
        // have arbitrary labels ([ias0], [ivs1]) and are NOT
        // consumed by xstack/amix — they're there to make
        // ffmpeg happy about the graph's stream topology.
        // (Confirmed via the mpv source: see
        // github.com/mpv-player/mpv/pull/5965 — the playback
        // chain init looks for [vo]/[ao] specifically, and
        // a graph with [v]/[a] outputs produces no playback
        // stream.)
        var totalFiles = allVideos.Count + allAudios.Count;
        for (var i = 0; i < totalFiles; i++)
        {
            var isVideo = i < allVideos.Count;
            if (isVideo)
            {
                // Video file: real stream is vid{fileIndex+1}.
                // No real audio; synthesize silent.
                sb.Append(CultureInfo.InvariantCulture,
                    $"anullsrc=channel_layout=stereo:sample_rate=48000[ias{i}];");
            }
            else
            {
                // Audio file: real stream is aid{audioOrdinal+1}.
                // No real video; synthesize black.
                sb.Append(CultureInfo.InvariantCulture,
                    $"color=size=640x360:rate=30:color=black[ivs{i}];");
            }
        }
        if (allVideos.Count > 0)
        {
            const int cellW = 640;
            const int cellH = 360;
            // vid1, vid2, ... for video files in order.
            for (var i = 0; i < allVideos.Count; i++)
            {
                var v = allVideos[i];
                var ms = v.OffsetMs - minOffsetMs;
                var inputLabel = $"vid{i + 1}";
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{inputLabel}]setpts=PTS+{Sec(ms)}/TB,scale={cellW}:{cellH}[{PLabel(i)}];");
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
            // Output label is [vo] (mpv's required video-out).
            sb.Append(CultureInfo.InvariantCulture,
                $"{inputs}xstack=inputs={allVideos.Count}:layout={layoutSb}[vo];");
        }

        // Audio: aid1, aid2, ... for audio files in order.
        // mpv numbers aid separately from vid: the first
        // audio file in the file list is aid1 even if there
        // are video files before it.
        if (allAudios.Count > 0)
        {
            for (var i = 0; i < allAudios.Count; i++)
            {
                var a = allAudios[i];
                var ms = a.OffsetMs - minOffsetMs;
                var inputLabel = $"aid{i + 1}";
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{inputLabel}]adelay=delays={ms:F0}:all=1,aresample=48000[{AOutLabel(i)}];");
            }
            if (allAudios.Count == 1)
                // Output label is [ao] (mpv's required audio-out).
                sb.Append(CultureInfo.InvariantCulture, $"[{AOutLabel(0)}]anull[ao];");
            else
            {
                var inputs = string.Join("", Enumerable.Range(0, allAudios.Count).Select(i => $"[{AOutLabel(i)}]"));
                sb.Append(CultureInfo.InvariantCulture,
                    $"{inputs}amix=inputs={allAudios.Count}:normalize=0[ao];");
            }
        }

        // If only audio, lavfi still needs a video output. Produce
        // a black frame. Without this mpv errors with "no video".
        if (allVideos.Count == 0)
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
