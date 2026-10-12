// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Runtime.InteropServices;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Locates bundled external tools (ffmpeg, mpv, ffprobe).
/// Order: explicit override, then the app's own tools/&lt;rid&gt;/ folder,
/// then PATH. Binaries are gitignored; see scripts/fetch-tools.*.
/// </summary>
public static class ToolPaths
{
    public static string Resolve(string name, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath)) return overridePath;
            throw new FileNotFoundException($"Configured {name} path does not exist: {overridePath}");
        }

        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}"),
        };
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", $"{os}-{rid}", exe);
        if (File.Exists(bundled)) return bundled;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"'{name}' was not found. Install it and put it on PATH, or bundle it under tools/{os}-{rid}/ (see scripts/fetch-tools.*).");
    }
}
