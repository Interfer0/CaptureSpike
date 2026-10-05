using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Builds one <see cref="ISourceCapturer"/> per palette source for the current
/// OS. Set CAPTURESPIKE_SYNTHETIC=1 to record generated test signals instead
/// of real devices (for headless CI/dev).
/// </summary>
public static class CaptureFactory
{
    public static bool Synthetic =>
        Environment.GetEnvironmentVariable("CAPTURESPIKE_SYNTHETIC") == "1";

    public static ISourceCapturer Create(Project project, ProjectSource source, string outputFile, string ffmpeg)
    {
        if (Synthetic)
            return new FfmpegCapturer(source.Id, source.Kind, outputFile, ffmpeg, SyntheticArgs(project, source, outputFile));

        if (OperatingSystem.IsWindows()) return CreateWindows(project, source, outputFile, ffmpeg);
        if (OperatingSystem.IsLinux()) return CreateLinux(project, source, outputFile, ffmpeg);
        throw new PlatformNotSupportedException($"Capture is not supported on {RuntimeInformation.OSDescription}.");
    }

    // ----- shared argument pieces -----

    private static List<string> BaseArgs() =>
        new() { "-hide_banner", "-loglevel", "warning", "-progress", "pipe:1", "-nostats" };

    private static List<string> VideoOutArgs(Project project, string outputFile) =>
        new()
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-r", project.Fps.ToString(),
            "-y", outputFile,
        };

    private static List<string> AudioOutArgs(string outputFile) =>
        new() { "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-y", outputFile };

    private static List<string> SyntheticArgs(Project project, ProjectSource source, string outputFile)
    {
        var args = BaseArgs();
        if (source.Kind.IsVideo())
        {
            // -re paces the generated signal at wall-clock rate, like a real device.
            args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30" });
            args.AddRange(VideoOutArgs(project, outputFile));
        }
        else
        {
            args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" });
            args.AddRange(AudioOutArgs(outputFile));
        }
        return args;
    }

    // ----- Linux (ffmpeg everywhere) -----

    private static ISourceCapturer CreateLinux(Project project, ProjectSource source, string outputFile, string ffmpeg)
    {
        var args = BaseArgs();
        switch (source.Kind)
        {
            case SourceKind.Screen:
            {
                args.AddRange(new[] { "-f", "x11grab", "-framerate", project.Fps.ToString() });
                var disp = DisplayName();
                if (DeviceParsers.TryParseGeometry(source.Device, out var geo))
                    args.AddRange(new[] { "-video_size", $"{geo.Width}x{geo.Height}", "-i", $"{disp}+{geo.X},{geo.Y}" });
                else
                    args.AddRange(new[] { "-i", disp });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            }
            case SourceKind.Window:
                // True per-window capture on X11 needs the window geometry (v2).
                args.AddRange(new[] { "-f", "x11grab", "-framerate", project.Fps.ToString(), "-i", DisplayName() });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            case SourceKind.Webcam:
                args.AddRange(new[] { "-f", "v4l2", "-framerate", "30", "-i", source.Device ?? "/dev/video0" });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            case SourceKind.Microphone:
                args.AddRange(new[] { "-f", "pulse", "-i", source.Device ?? "default" });
                args.AddRange(AudioOutArgs(outputFile));
                break;
            case SourceKind.SystemAudio:
                args.AddRange(new[] { "-f", "pulse", "-i",
                    string.IsNullOrWhiteSpace(source.Device) ? PulseMonitorSource() : source.Device });
                args.AddRange(AudioOutArgs(outputFile));
                break;
            default:
                throw new NotSupportedException($"Unsupported source kind: {source.Kind}");
        }
        return new FfmpegCapturer(source.Id, source.Kind, outputFile, ffmpeg, args);
    }

    private static string DisplayName() =>
        Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } d ? d : ":0.0";

    private static string PulseMonitorSource()
    {
        try
        {
            var psi = new ProcessStartInfo("pactl", "get-default-sink")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var sink = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(sink)) return sink + ".monitor";
        }
        catch { }
        throw new CaptureException("system",
            "Could not determine the PulseAudio default sink (is pactl installed?). " +
            "Set the scene source's device to a '<sink>.monitor' name as a workaround.");
    }

    // ----- Windows (ffmpeg video, NAudio audio) -----

    private static ISourceCapturer CreateWindows(Project project, ProjectSource source, string outputFile, string ffmpeg)
    {
#if WINDOWS
        if (source.Kind is SourceKind.Microphone or SourceKind.SystemAudio)
            return new NAudioCapturer(source.Id, source.Kind, outputFile,
                loopback: source.Kind == SourceKind.SystemAudio, deviceId: source.Device);
#else
        if (source.Kind is SourceKind.Microphone or SourceKind.SystemAudio)
            throw new PlatformNotSupportedException("Windows audio capture needs the Windows build of this app.");
#endif
        var args = BaseArgs();
        switch (source.Kind)
        {
            case SourceKind.Screen:
            {
                args.AddRange(new[] { "-f", "gdigrab", "-framerate", project.Fps.ToString() });
                if (DeviceParsers.TryParseGeometry(source.Device, out var geo))
                    args.AddRange(new[] { "-offset_x", geo.X.ToString(), "-offset_y", geo.Y.ToString(),
                                          "-video_size", $"{geo.Width}x{geo.Height}" });
                args.AddRange(new[] { "-i", "desktop" });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            }
            case SourceKind.Window:
                if (string.IsNullOrWhiteSpace(source.Device))
                    throw new CaptureException(source.Id,
                        "Window capture needs a window title in the source's device setting.");
                args.AddRange(new[] { "-f", "gdigrab", "-framerate", project.Fps.ToString(), "-i", $"title={source.Device}" });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            case SourceKind.Webcam:
                args.AddRange(new[] { "-f", "dshow", "-framerate", "30", "-i",
                    $"video={source.Device ?? FirstDShowVideoDevice(ffmpeg)}" });
                args.AddRange(VideoOutArgs(project, outputFile));
                break;
            default:
                throw new NotSupportedException($"Unsupported source kind: {source.Kind}");
        }
        return new FfmpegCapturer(source.Id, source.Kind, outputFile, ffmpeg, args);
    }

    private static string FirstDShowVideoDevice(string ffmpeg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var output = p?.StandardError.ReadToEnd() ?? "";
        try { p?.WaitForExit(10000); } catch { }
        var first = DeviceParsers.ParseDShowDevices(output, "(video)").FirstOrDefault();
        if (first is not null) return first;
        throw new CaptureException("webcam",
            "No DirectShow video devices found. Plug in a camera or set the source's device to its DirectShow name.");
    }
}
