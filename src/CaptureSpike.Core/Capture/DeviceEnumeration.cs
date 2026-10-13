// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CaptureSpike.Core.Capture;

/// <summary>One selectable capture device: a screen, camera, microphone, etc.</summary>
/// <param name="Id">Stable identifier passed to the capturer (the source's Device).</param>
/// <param name="Label">Human-readable label shown in the picker.</param>
public sealed record DeviceOption(string Id, string Label);

/// <summary>A monitor's geometry in the virtual desktop.</summary>
public sealed record MonitorGeometry(string Name, int Width, int Height, int X, int Y, bool Primary)
{
    /// <summary>Opaque device id stored on the source, e.g. "1920x1080+1920+0".</summary>
    public string ToDeviceId() => $"{Width}x{Height}+{X}+{Y}";
    public string ToLabel() => $"{Name} ({Width}x{Height}){(Primary ? " · primary" : "")}";
}

/// <summary>
/// Pure parsers for device-enumeration output. Public so they are unit-testable.
/// </summary>
public static class DeviceParsers
{
    private static readonly Regex GeometryPattern =
        new(@"^(\d+)x(\d+)\+(\d+)\+(\d+)$", RegexOptions.Compiled);

    /// <summary>Prefix used by
    /// <see cref="DeviceEnumeration.GetWindows"/> to mark
    /// a device id as a literal HWND (vs a legacy
    /// plain-title entry from before WGC support was
    /// added).</summary>
    public const string HwndPrefix = "hwnd:";

    /// <summary>True if the device id is in the
    /// HWND form (case-insensitive — accepts both
    /// "hwnd:" and "HWND:").</summary>
    public static bool IsHwndDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        return deviceId.StartsWith(HwndPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parse the "hwnd:0xABCD" form. Returns
    /// false if the id isn't in that form or the hex
    /// portion doesn't parse.</summary>
    public static bool TryParseHwnd(string? deviceId, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!IsHwndDeviceId(deviceId)) return false;
        // Strip the case-insensitive "hwnd:" / "HWND:"
        // prefix, then any leading "0x" / "0X". The hex
        // literal remains; NumberStyles.HexNumber parses
        // it as base 16.
        var raw = deviceId!.Trim();
        if (raw.StartsWith(HwndPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(HwndPrefix.Length).Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(2);
        if (!long.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        hwnd = new IntPtr(parsed);
        return true;
    }

    /// <summary>Parses a "WxH+X+Y" device id back into a geometry.</summary>
    public static bool TryParseGeometry(string? deviceId, out MonitorGeometry geometry)
    {
        geometry = null!;
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var m = GeometryPattern.Match(deviceId.Trim());
        if (!m.Success) return false;
        geometry = new MonitorGeometry("", int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            int.Parse(m.Groups[4].Value), Primary: false);
        return true;
    }

    /// <summary>
    /// Parses <c>ffmpeg -list_devices true -f dshow -i dummy</c> stderr.
    /// </summary>
    /// <param name="kindTag">"(video)" or "(audio)".</param>
    public static IReadOnlyList<string> ParseDShowDevices(string output, string kindTag)
    {
        // Lines look like: [dshow @ 0000023a] "USB Camera" (video)
        var names = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(kindTag, StringComparison.OrdinalIgnoreCase)) continue;
            var q1 = line.IndexOf('"');
            if (q1 < 0) continue;
            var q2 = line.IndexOf('"', q1 + 1);
            if (q2 < 0) continue;
            var name = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Parses <c>xrandr --listmonitors</c> output. Primary first.</summary>
    public static IReadOnlyList<MonitorGeometry> ParseXrandrMonitors(string output)
    {
        // " 0: +*HDMI-1 1920/510x1080/290+0+0  HDMI-1"
        var list = new List<MonitorGeometry>();
        var pattern = new Regex(
            @"^\s*\d+:\s+(?<conn>\S+)\s+(?<w>\d+)/\d+x(?<h>\d+)/\d+\+(?<x>\d+)\+(?<y>\d+)\s+(?<name>\S+)",
            RegexOptions.Compiled);
        foreach (var line in output.Split('\n'))
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;
            list.Add(new MonitorGeometry(
                m.Groups["name"].Value,
                int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value),
                int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value),
                m.Groups["conn"].Value.Contains('*')));
        }
        return list.OrderByDescending(g => g.Primary).ToList();
    }

    /// <summary>
    /// Parses <c>pactl list sources</c>; monitor sources are excluded.
    /// </summary>
    public static IReadOnlyList<DeviceOption> ParsePactlSources(string output) =>
        ParsePactlNodes(output, "Source #", (name, desc) =>
            name.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase)
                ? null : new DeviceOption(name, desc));

    /// <summary>
    /// Parses <c>pactl list sinks</c>; ids are "&lt;sink&gt;.monitor" for loopback.
    /// </summary>
    public static IReadOnlyList<DeviceOption> ParsePactlSinks(string output) =>
        ParsePactlNodes(output, "Sink #", (name, desc) =>
            new DeviceOption(name + ".monitor", $"{desc} (system audio)"));

    private static IReadOnlyList<DeviceOption> ParsePactlNodes(
        string output, string header, Func<string, string, DeviceOption?> map)
    {
        var options = new List<DeviceOption>();
        string? name = null, desc = null;
        void Flush()
        {
            if (name is not null)
            {
                var option = map(name, desc ?? name);
                if (option is not null) options.Add(option);
            }
            name = desc = null;
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(header, StringComparison.Ordinal)) Flush();
            else if (line.StartsWith("Name:", StringComparison.Ordinal))
                name = line["Name:".Length..].Trim();
            else if (line.StartsWith("Description:", StringComparison.Ordinal))
                desc = line["Description:".Length..].Trim();
        }
        Flush();
        return options;
    }
}

/// <summary>
/// Lists the real capture devices for a source kind on this machine.
/// Never throws: failures yield an empty list (the UI falls back to "Default").
/// </summary>
public static class DeviceEnumeration
{
    public static IReadOnlyList<DeviceOption> GetDevices(SourceKind kind, string ffmpeg)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsDevices.Get(kind, ffmpeg);
            if (OperatingSystem.IsLinux()) return LinuxDevices.Get(kind);
        }
        catch { }
        return Array.Empty<DeviceOption>();
    }

#if WINDOWS
    private static class WindowsDevices
    {
        internal static IReadOnlyList<DeviceOption> Get(SourceKind kind, string ffmpeg) =>
            kind switch
            {
                SourceKind.Screen => GetMonitors(),
                SourceKind.Window => GetWindows(),
                SourceKind.Webcam => ListDShow(ffmpeg, "(video)"),
                SourceKind.Microphone => ListAudioEndpoints(NAudio.CoreAudioApi.DataFlow.Capture),
                SourceKind.SystemAudio => ListAudioEndpoints(NAudio.CoreAudioApi.DataFlow.Render),
                _ => Array.Empty<DeviceOption>(),
            };

        // ----- monitors (no WinForms dependency) -----

        private delegate bool MonitorEnumProc(
            IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX lpmi);

        private static IReadOnlyList<DeviceOption> GetMonitors()
        {
            var geoms = new List<MonitorGeometry>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hmon, IntPtr _, ref RECT _, IntPtr _) =>
                {
                    var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(hmon, ref mi))
                    {
                        geoms.Add(new MonitorGeometry(mi.szDevice,
                            mi.rcMonitor.right - mi.rcMonitor.left,
                            mi.rcMonitor.bottom - mi.rcMonitor.top,
                            mi.rcMonitor.left, mi.rcMonitor.top,
                            (mi.dwFlags & 1) != 0)); // MONITORINFOF_PRIMARY
                    }
                    return true;
                }, IntPtr.Zero);
            var options = new List<DeviceOption>();
            var n = 0;
            foreach (var g in geoms.OrderByDescending(g => g.Primary))
            {
                n++;
                options.Add(new DeviceOption(g.ToDeviceId(),
                    $"Display {n} ({g.Width}x{g.Height}){(g.Primary ? " · primary" : "")}"));
            }
            return options;
        }

        // ----- top-level windows -----

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, char[] lpString, int nMaxCount);
        // GetClassNameW: used to disambiguate windows with
        // the same title (e.g. two "Untitled - Notepad" windows
        // for different .txt files; their class names differ).
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, char[] lpString, int nMaxCount);

        /// <summary>
        /// Enumerate all top-level visible windows for the
        /// "Add Source" / device-picker dropdown. Each
        /// entry's <see cref="DeviceOption.Id"/> is
        /// <c>"hwnd:0xHHHHHHHH"</c> — the literal HWND
        /// value formatted as hex with a "hwnd:" prefix
        /// (so the capturer can distinguish HWNDs from
        /// legacy plain-title entries).
        /// </summary>
        private static IReadOnlyList<DeviceOption> GetWindows()
        {
            // We keep a list of (hwnd, title, class) tuples,
            // then dedupe by HWND at the end. (Multiple windows
            // with the same title and class is possible if
            // a single process opens them — e.g. an MDI
            // app. We keep all of them and rely on the user
            // to pick the right one. The display label
            // shows the class for disambiguation when
            // titles collide.)
            var seen = new List<(IntPtr Hwnd, string Title, string Class)>();
            EnumWindows((hwnd, _) =>
            {
                if (seen.Count >= 100) return false;
                if (!IsWindowVisible(hwnd)) return true;
                var len = GetWindowTextLengthW(hwnd);
                if (len == 0 || len > 256) return true;
                var titleBuf = new char[len + 1];
                GetWindowTextW(hwnd, titleBuf, titleBuf.Length);
                var title = new string(titleBuf, 0, len).Trim();
                if (title.Length == 0) return true;

                // Get the window class for disambiguation.
                var classBuf = new char[256];
                var classLen = GetClassNameW(hwnd, classBuf, classBuf.Length);
                var className = classLen > 0
                    ? new string(classBuf, 0, classLen)
                    : "";

                seen.Add((hwnd, title, className));
                return true;
            }, IntPtr.Zero);

            // Find windows with duplicate titles so we can
            // append the class name to disambiguate them.
            var duplicates = seen
                .GroupBy(s => s.Title)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(s => s.Hwnd))
                .ToHashSet();

            return seen.Select(s =>
            {
                // HWND formatted as "hwnd:0xHHHH" (uppercase,
                // no leading zeros, 8 hex digits). 64-bit
                // HWNDs on 64-bit Windows would need 16
                // digits, but capturing a 32-bit window
                // from a 64-bit process is the common case.
                // We use the lower 32 bits which is what
                // WGC's CreateFromWindowHandle takes on
                // x64 Windows.
                var id = $"hwnd:0x{s.Hwnd.ToInt64():x}";
                var label = duplicates.Contains(s.Hwnd)
                    ? $"{s.Title}  ({s.Class})"
                    : s.Title;
                return new DeviceOption(id, label);
            }).ToList();
        }

        // ----- DirectShow cameras -----

        private static IReadOnlyList<DeviceOption> ListDShow(string ffmpeg, string kindTag)
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
            try { p?.WaitForExit(15000); } catch { }
            return DeviceParsers.ParseDShowDevices(output, kindTag)
                .Select(n => new DeviceOption(n, n)).ToList();
        }

        // ----- WASAPI audio endpoints -----

        private static IReadOnlyList<DeviceOption> ListAudioEndpoints(
            NAudio.CoreAudioApi.DataFlow flow)
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var options = new List<DeviceOption>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow,
                         NAudio.CoreAudioApi.DeviceState.Active))
            {
                try { options.Add(new DeviceOption(d.ID, d.FriendlyName)); }
                finally { d.Dispose(); }
            }
            return options;
        }
    }
#else
    private static class WindowsDevices
    {
        internal static IReadOnlyList<DeviceOption> Get(SourceKind kind, string ffmpeg) =>
            Array.Empty<DeviceOption>();
    }
#endif

    private static class LinuxDevices
    {
        internal static IReadOnlyList<DeviceOption> Get(SourceKind kind) => kind switch
        {
            SourceKind.Screen => GetMonitors(),
            SourceKind.Window => Array.Empty<DeviceOption>(), // per-window capture is v2
            SourceKind.Webcam => GetCameras(),
            SourceKind.Microphone => GetPulseSources(),
            SourceKind.SystemAudio => GetPulseSinks(),
            _ => Array.Empty<DeviceOption>(),
        };

        private static string Run(string file, string args, int timeoutMs = 8000)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Couldn't start {file}.");
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException($"{file} timed out.");
            }
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"{file} exited with code {p.ExitCode}.");
            return output;
        }

        private static IReadOnlyList<DeviceOption> GetMonitors()
        {
            var output = Run("xrandr", "--listmonitors");
            return DeviceParsers.ParseXrandrMonitors(output)
                .Select(g => new DeviceOption(g.ToDeviceId(), g.ToLabel()))
                .ToList();
        }

        private static IReadOnlyList<DeviceOption> GetCameras()
        {
            var options = new List<DeviceOption>();
            string[] devs;
            try { devs = Directory.GetFiles("/dev", "video*"); }
            catch { return options; }
            foreach (var dev in devs)
            {
                string label;
                try
                {
                    label = File.ReadAllText(
                        $"/sys/class/video4linux/{Path.GetFileName(dev)}/name").Trim();
                    if (label.Length == 0) label = Path.GetFileName(dev);
                }
                catch { label = Path.GetFileName(dev); }
                options.Add(new DeviceOption(dev, $"{label} ({dev})"));
            }
            return options.OrderBy(o => o.Id).ToList();
        }

        private static IReadOnlyList<DeviceOption> GetPulseSources()
        {
            try { return DeviceParsers.ParsePactlSources(Run("pactl", "list sources")); }
            catch { return Array.Empty<DeviceOption>(); }
        }

        private static IReadOnlyList<DeviceOption> GetPulseSinks()
        {
            try { return DeviceParsers.ParsePactlSinks(Run("pactl", "list sinks")); }
            catch { return Array.Empty<DeviceOption>(); }
        }
    }
}
