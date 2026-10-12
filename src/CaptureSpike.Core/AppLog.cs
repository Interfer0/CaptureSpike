// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core;

/// <summary>
/// On-disk companion to the in-app log panel. Every line that goes into
/// the UI's LogLines also lands here, plus every app-level event that
/// doesn't reach the panel (unhandled exceptions, startup banners).
///
/// All files live in <see cref="LogDirectory"/>:
///   - session-YYYYMMDD-HHMMSS.log     most recent session transcript
///   - crash.log                        append-only tail of crashes
///
/// The directory is created on first use; it never throws if it can't be
/// written (a logging system that bricks the app is worse than no logs).
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    /// <summary>
    /// Folder containing all session logs and crash.log. Guaranteed to
    /// exist after construction. Mutable only by the test seam below.
    /// </summary>
    public static string LogDirectory => LogDirectoryField;
    private static string LogDirectoryField = MakeDefaultDirectory();

    private AppLog(string sessionFile)
    {
        SessionFile = sessionFile;
        try
        {
            _writer = new StreamWriter(new FileStream(
                sessionFile, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null;
        }
    }

    /// <summary>File path for this session's transcript.</summary>
    public string SessionFile { get; }

    /// <summary>The currently-open session log, or null if disk init failed.</summary>
    public static AppLog? Current { get; private set; }

    /// <summary>
    /// Override <see cref="LogDirectory"/> for tests. Pass null to reset
    /// to the real path. The test assembly is the only caller (see
    /// InternalsVisibleTo on Core).
    /// </summary>
    internal static void SetLogDirectoryForTest(string? dir)
    {
        if (dir is null)
            LogDirectoryField = MakeDefaultDirectory();
        else
        {
            System.IO.Directory.CreateDirectory(dir);
            LogDirectoryField = dir;
        }
    }

    /// <summary>
    /// Test-only factory that constructs an <see cref="AppLog"/> against
    /// a specific file path without touching the global
    /// <see cref="Current"/> handle. Tests that need precise isolation
    /// use this so they don't race other tests through shared static state.
    /// </summary>
    internal static AppLog OpenAt(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return new AppLog(filePath);
    }

    /// <summary>
    /// Open a session log. Subsequent <see cref="WriteLine"/> calls land
    /// here. Idempotent — calling twice returns the existing handle.
    /// </summary>
    public static AppLog Open()
    {
        var existing = Current;
        if (existing is not null) return existing;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(LogDirectory, $"session-{stamp}.log");
        var log = new AppLog(file);
        Current = log;
        try
        {
            log.WriteLine($"--- session opened {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} ---");
        }
        catch { /* never let logging throw outward */ }
        return log;
    }

    /// <summary>
    /// Append a line to the session log. Adds a timestamp prefix that
    /// matches the in-app panel format so the file and the panel stay in
    /// sync. Never throws.
    /// </summary>
    public void WriteLine(string message)
    {
        if (_writer is null) return;
        var stamped = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (_gate)
        {
            try { _writer.WriteLine(stamped); }
            catch { /* disk full, network drive gone — give up silently */ }
        }
    }

    /// <summary>
    /// Append a crash entry. Slightly different prefix (CRASH) so it's
    /// easy to grep in the file even if the in-app panel was cleared.
    /// Also mirrored to crash.log in the same directory as this session's
    /// log file (cross-session diagnosis without losing the per-session
    /// context).
    /// </summary>
    public void WriteCrash(Exception ex)
    {
        if (_writer is null) return;
        var stamped = $"CRASH {DateTime.Now:HH:mm:ss} {ex}";
        lock (_gate)
        {
            try { _writer.WriteLine(stamped); }
            catch { }
        }
        try
        {
            // crash.log sits next to the session log, not in the
            // hard-coded LogDirectory. This way tests that scope
            // SessionFile to a temp dir see crash.log in that same
            // temp dir, and production crashes are still grouped with
            // the session that reported them.
            var crashFile = Path.Combine(
                Path.GetDirectoryName(SessionFile) ?? LogDirectory, "crash.log");
            File.AppendAllText(crashFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}] {ex}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.WriteLine($"--- session closed {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} ---"); }
            catch { }
            try { _writer?.Dispose(); }
            catch { }
            _writer = null;
        }
    }

    private static string MakeDefaultDirectory()
    {
        // ~/.config/CaptureSpike/logs on Linux, %APPDATA%/CaptureSpike/logs on Windows.
        // Matches the convention AppSettings and App.axaml.cs already use.
        var baseDir = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(baseDir, "CaptureSpike", "logs");
        try { System.IO.Directory.CreateDirectory(dir); }
        catch { /* fall through; LogDirectory property still exposed */ }
        return dir;
    }
}
