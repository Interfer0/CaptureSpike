// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Collections.Generic;
using System.Diagnostics;

namespace CaptureSpike.Core;

/// <summary>
/// Tracks every ffmpeg / mpv / pactl child process we spawn so
/// they can be force-killed when the app exits. Process
/// disposal (via using statements) handles the happy path,
/// but if a runaway process is left running (e.g. an ffmpeg
/// hung on an unbounded anullsrc) it can write to disk long
/// after our normal cleanup. The user reported a 75GB WAV
/// being created from a runaway ffmpeg — this registry is
/// the safety net.
///
/// Core (this class) holds the registry. App.axaml.cs
/// subscribes to ShutdownRequested and calls KillAll to
/// terminate anything still running.
/// </summary>
public static class ChildProcessRegistry
{
    private static readonly object Lock = new();
    private static readonly List<Process> Tracked = new();

    public static void Track(Process p)
    {
        lock (Lock) Tracked.Add(p);
    }

    public static void Untrack(Process p)
    {
        lock (Lock) Tracked.Remove(p);
    }

    public static void KillAll()
    {
        Process[] procs;
        lock (Lock) procs = Tracked.ToArray();
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        lock (Lock) Tracked.Clear();
    }
}
