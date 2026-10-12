// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Diagnostics;

namespace CaptureSpike.Core;

/// <summary>
/// Opens a file or folder in the OS's default handler:
///   Linux:    xdg-open
///   macOS:    open
///   Windows:  Process.Start with UseShellExecute (explorer / associated app)
///
/// Pure helper, no UI dependencies. Never throws — returns false on
/// spawn failure so callers can surface the path in their own UI.
/// </summary>
public static class OpenInOs
{
    /// <summary>
    /// Default opener — uses xdg-open / open / Windows shell based on
    /// <see cref="OperatingSystem"/>. Returns true on a successful
    /// Process.Start, false if the spawn failed.
    /// </summary>
    public static bool Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = false });
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                // UseShellExecute=true is required for verbs like "explore"
                // and for plain paths to launch associated apps.
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
