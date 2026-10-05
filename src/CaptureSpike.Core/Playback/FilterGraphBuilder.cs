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
                var layout = LayoutFor(scene, v.Id, i);
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

    private static int VidIndex(List<TakeSource> allVideos, TakeSource s) => allVideos.IndexOf(s) + 1;
    private static int AidIndex(List<TakeSource> allAudios, TakeSource s) => allAudios.IndexOf(s) + 1;

    private static string Sec(double ms) =>
        (ms / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

    private static (double X, double Y, double Width, double Height) LayoutFor(Scene scene, string sourceId, int videoOrdinal)
    {
        var src = scene.Sources.FirstOrDefault(s => s.Id == sourceId && s.Kind.IsVideo());
        if (src is not null) return (src.X, src.Y, src.Width, src.Height);
        // Take has a video source the scene doesn't know: stack as PiP.
        return videoOrdinal switch
        {
            _ => (0.72, 0.70 - 0.28 * ((videoOrdinal - 1) % 3), 0.26, 0.26),
        };
    }
}
